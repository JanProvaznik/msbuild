# MSBuild Server Default-Enable Investigation

## Background

Triggered by VMR build failure (Pipelines run 20260428.5):
```
Unhandled exception: System.TimeoutException: The operation has timed out.
   at System.IO.Pipes.NamedPipeClientStream.ConnectInternal(...)
   at Microsoft.Build.BackEnd.NodeProviderOutOfProcBase.TryConnectToPipeStream(...)
   at Microsoft.Build.Experimental.MSBuildClient.TryConnectToServer(Int32 timeoutMilliseconds)
   at Microsoft.Build.Experimental.MSBuildClient.Execute(CancellationToken cancellationToken)
   at Microsoft.Build.CommandLine.MSBuildClientApp.Execute(...)
   at Microsoft.Build.CommandLine.MSBuildApp.Main(String[] args)
   at Microsoft.DotNet.Cli.Utils.MSBuildForwardingAppWithoutLogging.ExecuteInProc(String[] arguments)
```
- Failure is **deterministic across multiple verticals** while building **fsharp** in the VMR.
- VMR build had MSBUILDUSESERVER on; the client failed to handshake/connect to the server within the timeout.

## Goals

1. Investigate making the MSBuild server **enabled by default** for all builds (both standalone and through `dotnet`).
2. Investigate **MSBuild server as implied by `-mt`** (multithreaded) — how the in-process worker model interacts with server reuse and what shared state must be cleared.
3. Find and document **all known blockers**: tasks/code paths that have process-lifetime caches, statics, or environment dependencies that don't survive across server reuse.
4. Reproduce / root-cause the **fsharp VMR timeout**.
5. Document mitigations and (if rich enough rationale) prototype them on a branch.

## Key entry points (current code)

- Server enable point: `src/MSBuild/XMake.cs:312-323` — gated by `Environment.GetEnvironmentVariable("MSBUILDUSESERVER") == "1"` AND `CanRunServerBasedOnCommandLineSwitches(args)`.
- Env var name: `src/Framework/Traits.cs:136` — `MSBUILDUSESERVER`.
- Client side: `src/Build/BackEnd/Client/MSBuildClient.cs`.
- Server node: `src/Build/BackEnd/Node/OutOfProcServerNode.cs`.
- Tests: `src/MSBuild.UnitTests/MSBuildServer_Tests.cs`.

## Investigation Threads

(filled in by sub-agents below)

### Thread A — Current architecture & enablement gating

- Enablement today is opt-in: `XMake.cs:312-323` checks `Environment.GetEnvironmentVariable(Traits.UseMSBuildServerEnvVarName) == "1"` plus `!Traits.Instance.EscapeHatches.EnsureStdOutForChildNodesIsPrimaryStdout` and `CanRunServerBasedOnCommandLineSwitches(args)` before routing to `MSBuildClientApp.Execute`; `Traits.UseMSBuildServerEnvVarName` is `MSBUILDUSESERVER` in `src\Framework\Traits.cs`.
- `CanRunServerBasedOnCommandLineSwitches` in `XMake.cs:346-388` disables server for `/help`, `/version`, `/nodemode`, binary log inputs, or `/nodeReuse:false`; it also falls back on parse exceptions. `/preprocess` is a recognized switch (`src\MSBuild\CommandLine\CommandLineSwitches.cs:272`) but is **not** one of the server-disable checks — gap worth filing.
- Client connect path: `MSBuildClient` derives `_pipeName` from `OutOfProcServerNode.GetPipeName(_handshake)` (`MSBuildClient.cs:121-143`), where the handshake uses `NodeModeHelper` + `XMakeAttributes.GetCurrentMSBuildArchitecture()` (`MSBuildClient.cs:522-525`) and the pipe name is `MSBuildServer-{handshake.ComputeHash()}` (`OutOfProcServerNode.cs:166-170`). The handshake salt is `GetHashCode($"{MSBUILDNODEHANDSHAKESALT}{toolsDirectory}")` with `toolsDirectory ??= BuildEnvironmentHelper.Instance.MSBuildToolsDirectoryRoot` (`Handshake.cs:73-86`); there is **no temp-path input** in this branch, so the known issue #13594 temp-dir addition (memory) applies only to worker-node handshakes. **Asymmetry to flag.**
- Connection/retry behavior: `Execute` probes running/busy mutexes, launches if needed, then calls `TryConnectToServer(20_000)` for cold start or `1_000` for hot start (`MSBuildClient.cs:161-189` and `238-287`). `TryConnectToServer` loops until timeout, calling `NodeProviderOutOfProcBase.TryConnectToPipeStream(...)`, and retries on non-timeout handshake failures by recreating the pipe stream (`MSBuildClient.cs:599-634`); failure sets `MSBuildClientExitType.UnableToConnect` and logs `Failed to connect to server: {result.ErrorMessage}`.
- Server launch: `TryLaunchServer` spawns via `NodeLauncher.Start(new NodeLaunchData(_msbuildLocation, "/nologo /nodemode:8"))` (`MSBuildClient.cs:428-473`), and `NodeLauncher.Start` always clears `MSBUILDUSESERVER` for child processes (`NodeLauncher.cs:46-51`, `294-312`). `NodeLauncher` preserves child env overrides and on Windows calls `CreateProcess` with the assembled command line (`NodeLauncher.cs:157-212`).
- Server readiness: `OutOfProcServerNode.Run` constructs a `ServerNodeHandshake`, opens the running mutex, then calls `_nodeEndpoint.Listen(this)` on a pipe named from the handshake (`OutOfProcServerNode.cs:94-133`); the server is considered ready once the named-pipe endpoint is listening and the mutex/pipe handshake can complete. The node advertises liveness through the running/busy mutexes (`OutOfProcServerNode.cs:166-173`).
- Per-request reset: before each build, the server sets cwd from `command.StartupDirectory`, applies the client env block (`CommunicationsUtilities.SetEnvironment(command.BuildProcessEnvironment)`), refreshes `Traits` from env, sets current cultures, rewrites `BuildParameters.StartupDirectory`, updates `ConsoleConfiguration.Provider`, and redirects `Console.Out/Console.Error` for the build (`OutOfProcServerNode.cs:365-445`). It does reset cwd back to `CurrentMSBuildToolsDirectory` after each build (`OutOfProcServerNode.cs:442-452`) and on shutdown (`249-260`), but it does **not** reset all process-global state (e.g. `BuildManager.DefaultBuildManager`, static caches, console encoding/global env beyond the explicit `SetEnvironment`, or arbitrary task statics). **→ Thread G has the deep-dive.**
- Idle/lifetime: the server lives until the client sends `NodeBuildComplete(false)` during shutdown or until the server decides not to reuse after a build (`MSBuildClient.cs:479-484`, `OutOfProcServerNode.cs:320-339`, `348-453`). It also self-terminates on cancel (`HandleBuildCancel` calls `BuildManager.DefaultBuildManager.CancelAllSubmissions()`) or when another server with the same handshake is already active (`OutOfProcServerNode.cs:324-335`).
- User-facing connection diagnostics: connect failures trace `Failed to connect to server: ...` and map to `UnableToConnect` (`MSBuildClient.cs:625-628`); shutdown connect failure traces `Client cannot connect to idle server to shut it down.` (`MSBuildClient.cs:258-263`). Launch/busy state failures also log `Server is busy...` and `UnknownServerState` paths (`MSBuildClient.cs:176-198`, `428-450`).

### Thread B — Known issues, PRs, and discussions in dotnet/msbuild and dotnet/sdk re: server

Survey of GitHub issues, PRs, and discussions across dotnet/msbuild, dotnet/sdk, and dotnet/dotnet (VMR)
regarding MSBuild server reliability and default-enablement blockers. **17 high-signal items found.**

#### Confirmed Blockers / Open

1. **[dotnet/msbuild #13315](https://github.com/dotnet/msbuild/issues/13315)** — **OPEN** (milestone: .NET 10) — *"Execute Restore tasks in the TaskHost node in /mt mode or when msbuild server is on"*. Core blocker: NuGet's `RestoreTask` holds static singletons (`PluginManager`, `EnvironmentVariableWrapper`) that assume one invocation per process. Workaround PR #13660 below. **→ See Thread C.**
2. **[dotnet/msbuild #12246](https://github.com/dotnet/msbuild/issues/12246)** — **OPEN** — *"Create and document the lifetime and correct usage of static members in Tasks"*. Root-cause design issue: no formal contract for Task static-singleton lifetimes. Requires MSBuild to expose `IsMultiThreaded`/`IsServerMode` flags so task authors can detect these modes.
3. **[dotnet/msbuild #9379](https://github.com/dotnet/msbuild/issues/9379)** — **OPEN** (Epic, milestone .NET 10) — *"MSBuild Server — enable for all dotnet CLI-based builds"*. Top-level epic. Updated 2025-08-19 with team plan: **.NET 10 GA**: mitigate NuGet auth via out-of-proc RestoreTask routing + dogfood in primary dotnet-org repos. **.NET 10.0.200**: enable server by default at the SDK level. Mirrors `/mt` enablement plan. Defer rationale: NuGet auth risk + insufficient preview runway.
4. **[dotnet/msbuild #11358](https://github.com/dotnet/msbuild/issues/11358)** — **OPEN** (Task, sub of #9379) — *"Enable MSBuild Server for .NET SDK by default in .NET 10"*. Concrete action item to flip `DOTNET_CLI_USE_MSBUILD_SERVER=1` in SDK wrapper logic.
5. **[dotnet/msbuild #13604](https://github.com/dotnet/msbuild/issues/13604)** — **OPEN** (Area: Server, 2026-04-24) — *"Validate MSBuild server in VMR validation pipeline"*. Created by @AR-May **one day after** the VMR fsharp timeout that triggered this investigation. Tracks: run VMR with server on, find issues beyond RestoreTask, resolve, then switch VMR validation to server by default. **This is the direct tracking issue for our triggering failure.**
6. **[dotnet/msbuild #9692](https://github.com/dotnet/msbuild/issues/9692)** — **OPEN** — *"[Feature Request]: MSBuild Server Identifier"*. Multi-clone interference: clones sharing the same MSBuild binary path collide on pipe name and can shut down each other's server. Request: add `SharedId` to handshake. Blocks multi-clone CI setups.

#### Active PRs (Open, addressing blockers)

7. **[dotnet/msbuild PR #13660](https://github.com/dotnet/msbuild/pull/13660)** — **OPEN** (2026-04-30) — *"Workaround: route NuGet RestoreTask to transient TaskHost in server or mt modes"*. Fixes #13315. Routes `NuGet.Build.Tasks.RestoreTask` to a transient (non-sidecar, `nodeReuse=false`) TaskHost when `WasLaunchedInMSBuildServerMode` OR `/mt`. Introduces sidecar env var `_MSBUILDORIGINALUSESERVER` because original PR #13649 failed — `MSBUILDUSESERVER` is stripped by `NodeLauncher.DisableMSBuildServer` before workers see it. **→ See Thread C.**

#### Mitigated / Closed (Historical bugs — useful background)

8. **[dotnet/msbuild #7993](https://github.com/dotnet/msbuild/issues/7993)** — **CLOSED 2022-10** — Mutex `IOException` in `TryLaunchServer` leaked as unhandled exception instead of falling back; fixed by wrapping in existing try/catch.
9. **[dotnet/msbuild #12580](https://github.com/dotnet/msbuild/issues/12580)** — **CLOSED/not_planned 2026-01** — `ObjectDisposedException` in `OutOfProcServerNode.RedirectConsoleWriter.WriteLine` during `dotnet pack -graph`. Server-only. Closed not planned.
10. **[dotnet/msbuild #13534](https://github.com/dotnet/msbuild/issues/13534)** — **CLOSED 2026-04** — `FormatException` in `OutOfProcServerNode.HandleBuildCancel` (stray `}` in trace format). Crashed on cancel with debug logging.
11. **[dotnet/msbuild #13188](https://github.com/dotnet/msbuild/issues/13188)** — **CLOSED 2026-03** — `-mt` intermittent crash *"Results for configuration X were not retrieved from node Y"*. Race in scheduler. **Manifested building dotnet/dotnet VMR (F# repo) on high-core CI**, parent #11801. Likely fixed via scheduler hardening.
12. **[dotnet/msbuild PR #13649](https://github.com/dotnet/msbuild/pull/13649)** — **CLOSED/superseded 2026-04-30** — first RestoreTask routing attempt; broke because `Traits.Instance.UseMSBuildServer` is always `0` in worker (env var stripped). Replaced by #13660.
13. **[dotnet/msbuild PR #13175](https://github.com/dotnet/msbuild/pull/13175)** — **MERGED 2026-03** — App Host Support for MSBuild. Prerequisite for stable server identity; adds `DotnetHostEnvironmentHelper` to propagate `DOTNET_ROOT` to child nodes.

#### Discussions / Proposals

14. **[dotnet/msbuild #11801](https://github.com/dotnet/msbuild/issues/11801)** — **OPEN** (Epic, .NET 10) — Parent epic for `/mt`. Explicitly defers both server and MT default-on to 10.0.200, citing risk of stale Task state, partial-migration perf regressions, and insufficient testing runway.
15. **[dotnet/msbuild PR #11383](https://github.com/dotnet/msbuild/pull/11383)** — **MERGED 2025-05** — Out-of-proc RAR node lifecycle. Establishes the routing pattern that PR #13660 reuses for RestoreTask.

#### Customer / VMR Reports

16. **[dotnet/dotnet #1015](https://github.com/dotnet/dotnet/issues/1015)** — **CLOSED/duplicate 2025-06** — VMR orchestrator runs `dotnet build-server shutdown` between repo builds; (a) doesn't reach toolset-package compilers, (b) parallel VMR builds shutting down mid-flight cause failures. 24 comments on VMR server-lifecycle management.
17. **[dotnet/dotnet #5391](https://github.com/dotnet/dotnet/issues/5391)** — **OPEN 2026-03** — *"Ubuntu2404_Ubuntu_BuildTests_x64: Razor file-lock race"* (~47% of PR failures). Mitigations explicitly include checking whether **stale MSBuild server processes hold file locks**. Live example of the persistent-process-handle risk class.

#### Key Relationships / Cross-References

- Thread C covers PR #13660 / issue #13315 in depth.
- The VMR fsharp `NamedPipeClientStream.ConnectInternal` timeout that triggered this investigation is tracked by **#13604**.
- Team's official roadmap: NuGet auth workaround + VMR validation in **.NET 10 GA** → SDK default-on in **.NET 10.0.200** (#9379, #11801).
- The env var `MSBUILDUSESERVER` is **stripped from worker child processes** by `NodeLauncher.DisableMSBuildServer` — key subtlety that broke PR #13649 and is worked around by PR #13660 with the `_MSBUILDORIGINALUSESERVER` sidecar.

### Thread C — RestoreTask short-lived TaskHost & related task-static-cache problems

#### Canonical Issue: [dotnet/msbuild#13315](https://github.com/dotnet/msbuild/issues/13315) — "Execute Restore tasks in the TaskHost node in /mt mode or when msbuild server is on"

- State: **open**, milestone .NET 10, assigned to @OvesN, filed 2026-03-02 by @AR-May
- Parent: [dotnet/msbuild#12246](https://github.com/dotnet/msbuild/issues/12246) — "Create and document the lifetime and correct usage of static members in Tasks" (open, @baronfel + @rainersigwald)

#### Open PR: [dotnet/msbuild#13660](https://github.com/dotnet/msbuild/pull/13660) — "Workaround: route NuGet RestoreTask to transient TaskHost in server or mt modes"

- **Open, NOT merged** (`mergeable_state: blocked`), filed 2026-04-30 by @OvesN, reviewer @AR-May
- 8 files changed, +398 / -4, 8 commits
- Outstanding review comment from @JanProvaznik: prefer `FrozenDictionary` / `const string` over `string[]` for `s_knownProblematicTaskNames` in `TaskRouter.cs`

#### Root Cause

NuGet's `RestoreTask` (`NuGet.Build.Tasks.RestoreTask`) holds two process-lifetime static singletons that assume one invocation per process:
1. `PluginManager` — `NuGet.Protocol/Plugins/PluginManager.cs` (NuGet/NuGet.Client)
2. `EnvironmentVariableWrapper` — `NuGet.Common/EnvironmentVariableWrapper.cs`

Two MSBuild modes break this assumption:
- **MSBuild Server** (`MSBUILDUSESERVER=1`): one process services many back-to-back builds → statics **leak between builds**
- **`/mt`**: RestoreTask was already routed to a TaskHost for thread-safety, but to a long-lived **sidecar** (reused across the build) → statics **leak between projects** in the same build

#### Prior Failed Attempt (commit `f670784a53`)

Detected server via `Traits.Instance.UseMSBuildServer` (reads `MSBUILDUSESERVER`) — never fires because:
1. `NodeLauncher.DisableMSBuildServer` (`src/Build/BackEnd/Components/Communications/NodeLauncher.cs:299-319`) zeroes `MSBUILDUSESERVER` before spawning the Server child (recursion guard)
2. `OutOfProcServerNode.HandleServerNodeBuildCommand` overwrites the server's env from the client snapshot, which doesn't include MSBuild internals

#### Proposed Fix Shape (PR #13660) — entirely MSBuild-side

**1. Sidecar env var `_MSBUILDORIGINALUSESERVER`** (`Traits.OriginalUseMSBuildServerEnvVarName`, `src/Framework/Traits.cs:532-542`):

```csharp
internal const string OriginalUseMSBuildServerEnvVarName = "_MSBUILDORIGINALUSESERVER";
internal readonly bool WasLaunchedInMSBuildServerMode =
    Environment.GetEnvironmentVariable(OriginalUseMSBuildServerEnvVarName) == "1";
```

`NodeLauncher.DisableMSBuildServer` stashes the original `MSBUILDUSESERVER` value into `_MSBUILDORIGINALUSESERVER` before zeroing it. `OutOfProcServerNode.HandleServerNodeBuildCommand` re-applies it after the env-snapshot restore so it survives `CommunicationsUtilities.SetEnvironment(...)`.

**2. Allow-list `TaskRouter.IsKnownProblematicTask`** (`src/Build/BackEnd/Components/RequestBuilder/TaskRouter.cs:95-130`):

```csharp
private static readonly string[] s_knownProblematicTaskNames =
[
    "NuGet.Build.Tasks.RestoreTask",
];
```

**3. Routing in `AssemblyTaskFactory.CreateTaskInstance`** (`src/Build/Instance/TaskFactories/AssemblyTaskFactory.cs:443-469`):

```csharp
bool forceTransientTaskHost = false;
if (_loadedType?.Type != null && TaskRouter.IsKnownProblematicTask(_loadedType.Type))
{
    bool isMultiThreaded = buildComponentHost?.BuildParameters?.MultiThreaded == true;
    bool isServerMode = Traits.Instance.WasLaunchedInMSBuildServerMode;
    if (isMultiThreaded || isServerMode)
    {
        useTaskFactory = true;
        forceTransientTaskHost = true;
    }
}
bool useSidecarTaskHost = !forceTransientTaskHost && !(_factoryIdentityParameters.TaskHostFactoryExplicitlyRequested ?? false);
```

When `forceTransientTaskHost=true`, the spawned `MSBuild.exe` TaskHost exits after `Execute()` (`nodeReuse=false`), wiping all statics. Confirmed in binlog: two `dotnet restore` invocations show **different `ProcessId`** in the `TaskHost details for task "RestoreTask"` log lines.

**Diagnostic logging** added in `TaskHostTask.Execute` (`src/Build/Instance/TaskFactories/TaskHostTask.cs`):
```
TaskHost details for task "RestoreTask": ProcessId=<N>, ParentProcessId=<M>, NewNodeContext=True, IsSidecar=False, NodeReuseEffective=False.
```

#### Why It Can't Trivially Be Fixed in NuGet Code

- The proper fix per #12246 — make `RestoreTask` thread-safe, clear/re-init statics at lifecycle points, possibly opt into TaskHost via `UsingTask Runtime/Architecture` — is "lengthy" per @baronfel.
- @jankratochvilcz (2026-04-21 sync): "This unblocks other MT work streams. Without a restore workaround, the team cannot use MSBuild server with MT mode."

#### Current Status & Sufficiency

| Item | Status |
|---|---|
| Issue #13315 | Open, .NET 10 milestone, assigned @OvesN |
| PR #13660 | **Open, not merged** (`mergeable_state: blocked`), reviewer @AR-May |
| Unresolved review | use `FrozenDictionary` / `const string` |
| Upstream root-cause #12246 | Long-term effort; no active PR |

**Merging PR #13660 alone is sufficient** for the RestoreTask blocker:
- Fix is entirely MSBuild-side — no NuGet changes needed.
- Non-MT, non-server builds are unaffected (only fires when `MultiThreaded=true` OR `_MSBUILDORIGINALUSESERVER=1`).
- The allow-list is the right extension point: future blocker tasks (FSharp.Build, ILLink/Trim, etc.) can be added the same way.

**Open follow-on:** Audit other widely-used third-party tasks for the same problem before flipping default-on. Strong candidates per Thread B/D: any task that maintains a credential cache (`Microsoft.DotNet.SignTool`?), sign-tool tasks, anything that calls into static native loaders.

### Thread D — Static caches / process-lifetime state in tasks (audit)

| Task / file | Line | Static / state kind | Risk under server reuse | Severity | Suggested mitigation |
|---|---:|---|---|---|---|
| `src/Tasks/AssemblyDependency/AssemblyFoldersExResolver.cs` | 164-178, 263-331 | Build-scoped cache held in `IBuildEngine.RegisterTaskObject`; `_filesInDirectories` snapshot + env-gated cache behavior | Can retain stale filesystem view across builds in a reused MSBuild node; behavior depends on first build's registry/filesystem snapshot | high | Make cache strictly per-build and always disposed/reset; consider short-lived TaskHost for RAR-heavy scenarios |
| `src/Tasks/GetFrameworkPath.cs` | 36-51, 57-120 | Process-lifetime `static Lazy<string>` framework path caches | First build's framework resolution is frozen for the whole server process; bad if environment/SDK layout changes mid-session | med | Move to per-build instance state or add explicit reset hook; avoid static lazy for env-dependent paths |
| `src/Tasks/AssemblyDependency/GlobalAssemblyCache.cs` | 27-42 | Process-lifetime `static readonly` delegates + `static Lazy<string> _gacPath` | GAC root path is cached forever; stale on config/runtime changes and shared across all builds/nodes | med | Resolve on demand or cache behind resettable per-build service |
| `src/Shared/ErrorUtilities.cs` | 25-29 | Static snapshot of `MSBUILDENABLEDEBUGTRACING` env var | Env var changes after process start won't be observed; debug tracing state persists across builds | low | Read env per call or gate behind a resettable config source |
| `src/Shared/Tracing.cs` | 28-48, 56-124, 144-154 | Mutable static dictionary/counters + cached slot/last timestamp | Debug-only state leaks across requests/builds in reused process; counters accumulate forever | low | Clear state on build boundary or keep debug tracing out of server-reused code paths |
| `src/Tasks/CultureInfoCache.cs` | 27-28, 37-58, 81-82 | `Lazy<HashSet<string>>` culture-name cache | Process-lifetime cache, but data is effectively immutable runtime metadata | low | Acceptable; no action unless runtime-specific culture enumeration must vary per request |

**Top 5 risks summary:**
1. `AssemblyFoldersExResolver` (HIGH) — biggest reuse hazard because it snapshots directory contents and can be registered into build lifetime state that survives longer than a single task invocation.
2. `GetFrameworkPath` (MED) — pins SDK/framework path resolution for the life of the server process; later builds can see stale paths if SDK layout changes mid-session.
3. `GlobalAssemblyCache` (MED) — caches GAC root path forever; stale on config/runtime changes.
4. `Tracing` (LOW) — clearest mutable static leak, but debug-only.
5. `ErrorUtilities` (LOW) — snapshots an env switch once at type load; harmless most of the time but still process-sticky.

**Recommended mitigation pattern:** prefer per-build instance state, add explicit reset hooks for server reuse, or force truly stateful tasks into short-lived TaskHost execution (see Thread C for the RestoreTask example pattern).

> **Caveat:** This audit covered `src/Tasks/**` and `src/Shared/**` only. The biggest server-reuse hazards in practice come from **out-of-tree** task assemblies (NuGet's `RestoreTask` — see Thread C; Roslyn's `Csc`/`Vbc`; FSharp.Build; SDK targets that load Microsoft.NET.Build.Tasks; AzCopy/MSDeploy tasks). A follow-up audit of `dotnet/sdk` and `NuGet/NuGet.Client` task assemblies is warranted.

### Thread E — VMR fsharp timeout root-cause analysis

**Summary:** The `System.TimeoutException` crash in the VMR fsharp build has two compounding causes:
1. **A code bug** where `TryConnectToPipeStream` propagates `TimeoutException` uncaught in the server-client path (unlike the worker-node path which wraps it).
2. **A structural problem** where all VMR verticals using the same SDK installation share the same `ServerNodeHandshake` hash (TMPDIR not in salt), causing cross-vertical server sharing.

A prior vertical's server in the pipe-recycling gap (between `InternalDisconnect()` and new `NamedPipeServerStream` creation) appears "running + not busy" but the pipe is temporarily unavailable; the 1s hot-server timeout fires, the uncaught exception crashes the process.

#### Per-Angle Assessment

| Angle | Status | Evidence |
|---|---|---|
| 1. Handshake mismatch / TMPDIR in salt | **LIKELY — structural root cause** | `Handshake.cs:83`: `salt = GetHashCode($"{handshakeSalt}{toolsDirectory}")` — no `Path.GetTempPath()` in current code. `ServerNodeHandshake.cs:20-23`: `includeSessionId: false, toolsDirectory: null`. Under VMR/SB each vertical sets distinct `TMPDIR`/`TMP` but shares the same `MSBuildToolsDirectoryRoot` → identical hash → same pipe name → shared server. Server carries vertical A's TMPDIR; when A's temp is cleaned, server crashes (issue #13594). Vertical B then sees "not running" → cold 20s start → if agent is loaded, another timeout. **PR #13651 (open draft) fixes this** by mixing `Path.GetTempPath()` into the salt for non-TaskHost handshakes. |
| 2. Pipe name collision + recycling race | **HIGHLY LIKELY — immediate trigger** | `OutOfProcServerNode.cs:167`: pipe = `MSBuildServer-{handshake.ComputeHash()}`. Between a `BuildCompleteReuse` cycle, `RunInternal()` (line 129) creates a new `ServerNodeEndpointOutOfProc` which calls `InternalConstruct()` (`NodeEndpointOutOfProcBase.cs:249`) to create a `NamedPipeServerStream`. During the gap between the old stream being disposed (`InternalDisconnect()`, line 319) and the new one being created, the pipe kernel object doesn't exist. The running mutex is still HELD (same process); busy mutex is RELEASED (build done). The next client sees "hot server, not busy" → tries `TryConnectToServer(1_000)` → pipe unavailable → 1s timeout → uncaught exception. |
| 3. Server crash before connect | **LIKELY — secondary** | Server from vertical A crashes when its TMPDIR is deleted (issue #13594 scenario). Running mutex released. Vertical B sees "not running" → launches new server (cold start) → `TryConnectToServer(20_000)` — on a CI agent under heavy load (6-10 verticals in parallel), 20s is insufficient → 20s `TimeoutException` uncaught. |
| 4. Client connect timeout too low | **CONFIRMED — hardcoded values** | `MSBuildClient.cs:186`: `serverIsAlreadyRunning ? 1_000 : 20_000`. Both values are hardcoded with no env-var override for the CLIENT side. `MSBUILDNODECONNECTIONTIMEOUT` only controls the server-side pipe wait (`CommunicationsUtilities.cs:73`), not the client-side `Connect(timeout)` call. |
| 5. Stdin/stdout redirection | **UNLIKELY** | Console properties are queried and passed over IPC via `ServerNodeBuildCommand.ConsoleConfiguration` (`MSBuildClient.cs:359-366`, `OutOfProcServerNode.cs:394`). No blocking on console I/O in the connect path. |
| 6. Working directory in handshake | **N/A** | CWD is not in the handshake. It's passed per-build in `ServerNodeBuildCommand.StartupDirectory` and applied in the server (`OutOfProcServerNode.cs:381`). |
| 7. FSharp-specific factor | **UNLIKELY (for timeout)** | The timeout fires at pipe-connect time, before any FSharp code runs. FSharp's build style (many short `dotnet build` invocations) makes it statistically more likely to hit the recycling race, but the root cause is in the generic server machinery. `FSharp.Build` tasks (if any carry statics) are relevant to server-reuse correctness but not to the connection timeout. |

#### Critical Code Bug: Uncaught `TimeoutException`

`TryConnectToPipeStream` (`NodeProviderOutOfProcBase.cs:802-848`) is called from two paths:

- **Worker-node path** — `TryConnectToProcess` (line 765): wrapped in `try { } catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))` at line 778, comment reads: *"TimeoutException -- Couldn't connect, might not be a node."* Exception handled gracefully, returns `null`.
- **Server-client path** — `MSBuildClient.TryConnectToServer` (line 609): calls `TryConnectToPipeStream` directly with NO exception handler. `MSBuildClient.Execute()` only catches `IOException` (line 191). `TimeoutException` (thrown by `nodeStream.Connect(timeout)` at line 804) is **not `IOException`** → propagates uncaught through `TryConnectToServer` → `Execute()` → `MSBuildClientApp.Execute()` → `MSBuildApp.Main()` → **unhandled exception crash**.

The retry logic in `TryConnectToServer` (lines 616-623) that recreates the pipe stream is **dead code** for the timeout case: it only runs when `TryConnectToPipeStream` returns `false`; it never runs on a thrown exception.

#### Ranked Hypotheses

1. **[#1 — CODE BUG, confirmed]** `TryConnectToPipeStream` does not catch `TimeoutException` from `nodeStream.Connect(timeout)` in the server-client path. All other issues depend on this bug to manifest as the observed unhandled exception crash. `NodeProviderOutOfProcBase.cs:804`.
2. **[#2 — STRUCTURAL, confirmed]** VMR verticals sharing the same SDK installation compute the same `ServerNodeHandshake` hash (no TMPDIR in salt → `Handshake.cs:83`). Server from prior vertical is mid-recycle (pipe gone, but mutexes show "running + idle"). Hot-server 1s timeout fires → bug #1. `MSBuildClient.cs:186`.
3. **[#3 — DETERMINISTIC, issue #13594]** TMPDIR mismatch causes cross-vertical server sharing and eventual server crash (when vertical A cleans its temp). Vertical B then does a 20s cold-start. Under CI load, this exceeds 20s → another timeout → bug #1. This explains why the failure is **deterministic across multiple verticals**. PR #13651 (open draft) fixes this.
4. **[#4 — CONTRIBUTING]** Hardcoded 1s hot-server timeout is too short for pipe recycling under I/O load (`ClearCacheDirectory()` + kernel pipe teardown/creation). Should be 5-10s minimum.

#### Mitigations

| Mitigation | Priority | Change | File |
|---|---|---|---|
| **M1: Catch `TimeoutException` in `TryConnectToPipeStream`** | **P0 — immediate** | Add try-catch around `nodeStream.Connect(timeout)` at line 804; convert to `HandshakeResult.Failure(HandshakeStatus.Timeout, ...)` and return `false`. This converts the crash into a graceful "unable to connect" result. | `NodeProviderOutOfProcBase.cs:804` |
| **M2: Fall back to in-proc on `TimeoutException`** | **P0 — immediate** | In the top-level `MSBuildClientApp.Execute()` or `MSBuildApp.Main()`, catch `TimeoutException` and fall back to in-proc `MSBuildApp.Execute()`. A server connection failure must never crash the build. | `MSBuildClientApp.cs` |
| **M3: Increase hot-server connect timeout** | **P1 — high** | Change `1_000 → 5_000` ms for hot server (or add `MSBUILDSERVERHOTTIMEOUT` env var). 1s is insufficient for pipe recycling under CI load. | `MSBuildClient.cs:186` |
| **M4: Merge PR #13651 (TMPDIR in salt)** | **P1 — high** | Include `Path.GetTempPath()` in salt for non-TaskHost handshakes. Isolates servers per-TMPDIR under VMR, prevents cross-vertical binding, eliminates issue #13594 as a trigger for cold-start timeouts. | `Handshake.cs:83`, `ServerNodeHandshake.cs` |
| **M5: Retry on `TimeoutException` with backoff** | **P2 — medium** | Retry up to 3x with 200-500ms backoff when pipe connection times out. Hot-server recycling is transient; a retry after 500ms would succeed once the new pipe is created. | `MSBuildClient.cs:599-634` |
| **M6: Add client-side `MSBUILDNODECONNECTIONTIMEOUT` override** | **P2 — medium** | Add env-var override for the client-side connect timeout (1s/20s values). CI pipelines could set it higher without code changes. | `MSBuildClient.cs:186`, `CommunicationsUtilities.cs` |

#### Concrete Repro Plan

```bash
# Minimal repro: same toolsdir, recycling race
export MSBUILDUSESERVER=1

# Build 1 to warm up server
dotnet build project.csproj

# Immediately after Build 1 finishes (server is in recycle gap), Build 2:
# The server is mid-cycle: running mutex held, busy mutex released, pipe gone
dotnet build project.csproj
# → TimeoutException after 1s in TryConnectToServer(1_000)

# Repro for TMPDIR scenario (#13594 trigger):
TMPDIR=/tmp/build-a dotnet build project.csproj &; wait
rm -rf /tmp/build-a
TMPDIR=/tmp/build-b dotnet build project.csproj
# → server from build-a crashes → build-b cold-start → 20s timeout
```

For reliable repro of the recycling race, add a `Thread.Sleep(2000)` in `OutOfProcServerNode.Run()` at line 122 (after `FileUtilities.ClearCacheDirectory()`) to hold the recycling gap open.

### Thread F — `-mt` interaction with server (state isolation)

**Current state (orthogonal):** `-mt` is parsed independently. In `XMake.cs`, `ProcessCommandLineSwitches` sets `cpuCount = ProcessMaxCPUCountSwitch(...)` and `multiThreaded = IsMultiThreadedEnabled(...)` separately, then passes both into `BuildManager.BeginBuild` (`src/MSBuild/XMake.cs:2251-2256, 2290-2320, 1593`). The CLI only routes builds to MSBuild server when `MSBUILDUSESERVER=1` env var is set AND `CanRunServerBasedOnCommandLineSwitches(args)` agrees; the server path is opt-in and orthogonal to `-mt` (`src/MSBuild/XMake.cs:313-327`). Neither implies the other.

**Isolation impact of `-mt`:** It switches from multi-process worker nodes to in-proc thread workers, so state that *was* process-isolated becomes shared. The framework explicitly warns `IMultiThreadableTask` authors to avoid global process state like `Environment.CurrentDirectory` and to use `TaskEnvironment` instead (`src/Framework/IMultiThreadableTask.cs:7-35`). Engine code already migrated to thread-local equivalents in some hot spots: `[ThreadStatic]` caches in `LogMessagePacket.cs:27-31`, thread-local working-directory handling in `Expander.cs`, `TaskEnvironment` save/restore in `RequestBuilder.cs`. This is the same class of state Thread D / G called out: statics, current directory, console/encoding, and other process globals stop being "one build per process" and become "all concurrent builds in one process".

**Combined risks with server reuse:** Server already keeps state across requests; `-mt` adds *concurrent sharing within a single server process*. Server persistence means caches, loaded assemblies, and any static/task state can survive between invocations; `-mt` means multiple requests can also hit those same objects at the same time. Combined, a bad static cache in a task is worse twice: it leaks across builds via server lifetime AND becomes a race when multiple build threads exercise it concurrently. Console state (`Console.Out/Error`, encoding), `Environment.CurrentDirectory`, mutable statics in tasks/loggers, and AssemblyLoadContext caches are the highest-risk examples.

**Decision matrix:**

| Option | Pros | Cons |
|---|---|---|
| A. Independent flags (status quo) | Simple; users opt into each risk separately. | No coordinated story for end users; perf wins of combination not realized by default. |
| B. `-mt` implies server | If the user already accepts process-shared state via `-mt`, server reuse barely adds risk. Reuses the JIT/ALC warm state that `-mt` benefits from across runs. | Surprises users who use `-mt` for one build and don't want a daemon. |
| C. Server-on by default, `-mt` opt-in | Largest perf win for all `dotnet build` invocations (warm JIT + cached SDK resolution). | Server-mode bugs hit everyone; default-on is irreversible signaling. |
| D. Both default-on together | Maximum perf. | Maximum risk surface; both classes of bugs at once for every user. |

**Recommendation:** Keep **Option A — independent flags** as the model, but ship **Option C** (server-on default at SDK level) as planned for .NET 10.0.200 (per #9379) once Thread C / D blockers are mitigated. **Do not couple `-mt` to server**: they solve different problems (parallelism vs. process reuse), have non-overlapping risk profiles, and a `-mt`-only user (e.g., on CI per-invocation) does not want a daemon process, while a server-only user (e.g., interactive `dotnet build`) does not want to widen the in-proc concurrency surface. Coupling them prevents users from exercising one risk without the other and raises the cost of rolling either back. The right user-facing argument for default-on server is **wall-clock time of repeated CLI invocations** (warm JIT, cached SDK resolution, cached project parses); `-mt` is independently justifiable on per-invocation parallelism even without server reuse.

### Thread G — Server lifecycle / shutdown / env-mutation / handshake / pipe-name

| Item | Status | Evidence | Risk |
|---|---|---|---|
| 1. Env vars | **Partially reset** | `HandleServerNodeBuildCommand` calls `CommunicationsUtilities.SetEnvironment(command.BuildProcessEnvironment)` then `Traits.UpdateFromEnvironment()`; no restore of previous process env before reuse. `HandleShutdown` only resets CWD. `src/Build/BackEnd/Node/OutOfProcServerNode.cs:380-385, 442-445` | high |
| 2. Working dir | **Reset** | `Directory.SetCurrentDirectory(command.StartupDirectory)` per request, then `NativeMethodsShared.SetCurrentDirectory(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory)` after build and again on shutdown. `src/Build/BackEnd/Node/OutOfProcServerNode.cs:381, 442-445, 253-255` | med |
| 3. Culture/UI culture | **Not reset** | Per request sets `Thread.CurrentThread.CurrentCulture/UICulture` from client; no restore to prior values before reuse. `src/Build/BackEnd/Node/OutOfProcServerNode.cs:386-387` | high |
| 4. BuildManager lifecycle | **Not reset** | Server uses `BuildManager.DefaultBuildManager.CancelAllSubmissions()`; singleton is lazily created once and reused. `src/Build/BackEnd/Node/OutOfProcServerNode.cs:345`; `src/Build/BackEnd/BuildManager/BuildManager.cs:353-368` | high |
| 5. Logger state | **Partially reset** | I found no logger unregister in server node; `BuildManager.EndBuild` clears build state and caches, but logger registration lives on the singleton/build manager unless the build callback tears it down. `src/Build/BackEnd/Node/OutOfProcServerNode.cs` (no unregister), `src/Build/BackEnd/BuildManager/BuildManager.cs:1016+` | high |
| 6. Process-wide caches | **Not reset** | `ProjectCollection.GlobalProjectCollection` is a process singleton (`src/Build/Definition/ProjectCollection.cs:98-102, 1462-1483`). `BuildManager` clears `FileMatcher.ClearCaches()` / file-existence caches on build-end, but `MSBuildEventSource`, loaded ALCs, and other static state remain process-lifetime. `src/Build/BackEnd/BuildManager/BuildManager.cs` and `src/Shared/TaskEngineAssemblyResolver.cs` | high |
| 7. AppContext / static config | **Not reset** | No per-request `AppContext.SetSwitch` reset in server node; static/trait config is updated from env and retained until process exit. `src/Build/BackEnd/Node/OutOfProcServerNode.cs:383-387` | med-high |
| 8. Handshake | **Reset (for reuse key), but not cwd/temp-path** | Handshake key uses version in upper bits plus salt from `MSBUILDNODEHANDSHAKESALT` + tools directory; session id can also vary. Current code does **not** include `Path.GetTempPath()` for the *server* handshake variant (worker-node handshake does include temp path per memory and #13594 fix — verify difference). `src/Framework/BackEnd/Handshake.cs:75-103`; server pipe/mutex names derive from `ComputeHash()` in `src/Build/BackEnd/Node/OutOfProcServerNode.cs:166-173` | med |
| 9. Idle shutdown / recycle / OOM | **Partially reset** | `Run` loops on `BuildCompleteReuse`, clears cache dir between requests; server self-terminates on cancel, busy collision, connection failure, or over-provisioning via `CountActiveNodesWithMode(...)`. No OOM self-protection found here. `src/Build/BackEnd/Node/OutOfProcServerNode.cs:111-124, 320-338, 450-452` | med |

**Prioritized fixes:**
1. Snapshot/restore env + culture around each request.
2. Ensure logger registration is request-scoped and torn down before reuse.
3. Audit/clear other process-wide statics (`AppContext`, `MSBuildEventSource`, ALC resolver hooks).
4. Add explicit per-request reset helper invoked before `BuildCompleteReuse`.
5. Verify (and align) handshake-salt inputs between client and server-node variants — discrepancy (server salt missing temp path) is itself a candidate root cause for the VMR fsharp timeout (Thread E should follow up).

## Findings

(filled in by sub-agents)

## Recommended Mitigations

(filled in after findings)

## Prototype branches

(if applicable)




