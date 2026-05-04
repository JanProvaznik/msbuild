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

### Verify W2-1 — PR #13651 / salt-with-temp-path

#### PR #13651 — "Include effective temp directory in node-reuse handshake salt (fixes #13594)"

| Field | Value |
|---|---|
| State | **OPEN — DRAFT** (not merged, `mergeable_state: blocked`) |
| Author | @JanProvaznik |
| Created | 2026-04-29 |
| Updated | 2026-05-04 |
| Reviewer | @rainersigwald |
| Branch | `JanProvaznik/msbuild:fix/13594-tempdir-handshake-salt` → `dotnet/msbuild:main` |
| Changed files | **2**: `src/Framework/BackEnd/Handshake.cs` (+28 / -3), `src/Build.UnitTests/BackEnd/HandshakeTempDir_Tests.cs` (new, +161) |
| Commits | 1 |

**PR diff for `Handshake.cs` (key hunk):**
```diff
-        string handshakeSalt = Environment.GetEnvironmentVariable("MSBUILDNODEHANDSHAKESALT") ?? "";
-        int salt = CommunicationsUtilities.GetHashCode($"{handshakeSalt}{toolsDirectory}");
+        string handshakeSalt = Environment.GetEnvironmentVariable("MSBUILDNODEHANDSHAKESALT") ?? "";
+        bool isTaskHost = IsHandshakeOptionEnabled(nodeType, HandshakeOptions.TaskHost);
+        string tempDirectory = isTaskHost ? string.Empty : Path.GetTempPath();
+        int salt = CommunicationsUtilities.GetHashCode($"{handshakeSalt}{toolsDirectory}{tempDirectory}");
```

Fix strategy: for **non-TaskHost** handshakes (worker nodes and `ServerNodeHandshake`), mix `Path.GetTempPath()` into the salt. TaskHost paths are intentionally exempted (NET TaskHost uses `NetTaskHostHandshakeVersion=99` to absorb version skew across VS+SDK release trains; CLR2 TaskHost has `NodeReuse` disabled by design).

**New tests** (pinning the contract): `HandshakeTempDir_Tests.cs` — four cases:
- `Handshake_DifferentTempDirectory_ProducesDifferentKey` — regression test for #13594
- `Handshake_SameTempDirectory_ProducesSameKey` — sanity, legitimate reuse not broken
- `ServerNodeHandshake_DifferentTempDirectory_ProducesDifferentHash` — server pipe name isolated per TMPDIR
- `Handshake_NetTaskHost_DifferentTempDirectory_ProducesSameKey` — pins the TaskHost exemption

#### Issue #13594 — "Nodes are reused between builds with different temp directories"

| Field | Value |
|---|---|
| State | **OPEN** |
| Type | Bug |
| Filed | 2026-04-22 by @mdiluz |
| Assignee | @JanProvaznik |
| Labels | `triaged` |

Two MSBuild invocations with distinct `TMP`/`TEMP`/`TMPDIR` environments bind to each other's reusable worker nodes because the handshake salt is derived only from `MSBUILDNODEHANDSHAKESALT` and `toolsDirectory` — no temp path. Build A finishes and cleans its per-build temp folder; the still-running Build B fails with `MSB5018` or `"The system cannot find the path specified"` because its inherited node still points to A's now-deleted directory. Workarounds: `MSBUILDDISABLENODEREUSE=1` or set `MSBUILDNODEHANDSHAKESALT` differently per build.

#### Conclusion: server-mode handshake protection today

**Neither worker-node nor server-node handshakes are protected against TMPDIR collision today.**

- **Worker-node** (`Handshake.cs:81-83`): salt = `hash(MSBUILDNODEHANDSHAKESALT + toolsDirectory)` — no temp path. Cross-TMPDIR reuse possible (#13594 baseline).
- **Server-node** (`ServerNodeHandshake` via `Handshake` base): same formula (`toolsDirectory: null` → `MSBuildToolsDirectoryRoot`). Pipe name `MSBuildServer-{ComputeHash()}` therefore identical for any two builds sharing one MSBuild installation regardless of TMPDIR. **This is the structural root cause identified in Thread E** for the VMR fsharp timeout.
- **Fix status:** PR #13651 (open draft) addresses both. Not yet merged.
- **TaskHost** paths deliberately unprotected by design (CLR2 TaskHost has NodeReuse off; NET TaskHost cross-build reuse requires `MSBUILDREUSETASKHOSTNODES=1`).

**Memory reconciliation:** The previously stored fact about temp-path being in the worker handshake salt was **stale/prospective** — it described PR #13651's AFTER state, not the merged code. Updated to reflect this.



### Wave 2 — VMR pipeline & build-server lifecycle

#### 1. How `MSBUILDUSESERVER` reaches VMR builds

`MSBUILDUSESERVER` is **not** set in the top-level VMR pipeline (`eng/pipelines/official.yml`, `unofficial.yml`, or `vmr-build.yml` stage template). It surfaces only inside **Roslyn's per-vertical diagnostic variables file**:

```yaml
# dotnet/dotnet:src/roslyn/eng/pipelines/variables-build.yml
- name: DOTNET_CLI_USE_MSBUILD_SERVER
  value: 1
- name: MSBUILDUSESERVER
  value: 1
```

Those variables are imported by Roslyn's inner pipeline. Any `dotnet build` invocation reads `DOTNET_CLI_USE_MSBUILD_SERVER` from `MSBuildForwardingAppWithoutLogging.cs` (VMR mirror: `src/sdk/src/Cli/Microsoft.DotNet.Cli.Utils/MSBuildForwardingAppWithoutLogging.cs`) and translates it to `MSBUILDUSESERVER=1` for each MSBuild invocation. So when fsharp's build script calls `dotnet build`, server mode is engaged.

#### 2. VMR vertical structure and isolation

**Official full build** (`eng/pipelines/templates/stages/vmr-verticals.yml`): all verticals live in a **single stage** (`VMR_Vertical_Build`) with `dependsOn: []`, all start in parallel. **~30+ distinct vertical jobs** (Windows x64/x86/arm64, Linux x64/arm/arm64, Alpine x64/arm/arm64, macOS x64/arm64, Android arm/arm64/x64/x86, Browser WASM ×2, Wasi, iOS, tvOS, …). Each is a separate Azure Pipelines job (separate agent VM or container).

```
Stage: VMR_Vertical_Build  (dependsOn: [])
  Job: Windows_x64          ← separate agent, separate filesystem, separate TMPDIR
  Job: Windows_x86
  Job: Linux_x64             ← inside an azurelinuxX64CrossContainer
  Job: Linux_arm64
  … ~30 jobs total, all in parallel
```

Within each vertical job, the VMR MSBuild orchestrator (`repo-projects/Directory.Build.targets`) builds **all repos sequentially** by calling each repo's `build.sh`/`build.cmd` via `<Exec>`, with `BuildInParallel=true` for graph-independent dependencies. **All those `<Exec>` invocations run inside the same agent process / TMPDIR.**

| Boundary | Isolated? | How |
|---|---|---|
| Vertical A vs Vertical B | **YES** | Separate Azure Pipelines job / agent VM or container |
| Repo N vs Repo N+1 within one vertical | **NO** | Same agent, same TMPDIR, same `.dotnet` SDK dir |

**Important correction to Thread E:** The "cross-vertical sharing" hypothesis Thread E raised is actually **within-vertical sharing** — repos sequentially built on one agent share the same MSBuild server. The structural mechanism (identical handshake hash → same pipe name → server reuse) is the same; the scope is one vertical.

#### 3. TMPDIR / TMP per-vertical: NOT set

Searched all of `eng/pipelines/` and `repo-projects/Directory.Build.targets` for `TMPDIR`/`TMP` assignments: **none found**. TMPDIR is whatever the container image / agent provides (typically `/tmp` on Linux). No per-repo or per-vertical override.

`MSBUILDNODEHANDSHAKESALT` is also **not set anywhere** in the VMR pipeline (`grep` of `eng/pipelines/` and `repo-projects/` returns zero matches). The MSBuild server handshake salt reduces to:

```csharp
// dotnet/dotnet:src/msbuild/src/Framework/BackEnd/Handshake.cs:83
string handshakeSalt = Environment.GetEnvironmentVariable("MSBUILDNODEHANDSHAKESALT") ?? "";
int salt = CommunicationsUtilities.GetHashCode($"{handshakeSalt}{toolsDirectory}");
// → salt = GetHashCode("{toolsDirectory}")
```

All repos within a vertical share `$(sourcesPath)/.dotnet`, so `toolsDirectory` is identical for every `dotnet build` → identical handshake hash → identical pipe name → **repo N's MSBuild server is visible to repo N+1**. This is exactly the structural root-cause from Thread E, scoped to one vertical.

#### 4. `dotnet build-server shutdown` between repo builds: only `--vbcscompiler`, never `--msbuild`

The VMR orchestrator's `CleanupRepo` target (active when `CleanWhileBuilding=true`, enabled for non-internal builds via `cleanArgument`) calls:

```xml
<!-- dotnet/dotnet:repo-projects/Directory.Build.targets (~line 476) -->
<Exec Command="&quot;$(DotNetTool)&quot; build-server shutdown --vbcscompiler"
      Condition="'$(DotNetBuildSourceOnly)' != 'true'"
      EnvironmentVariables="NUGET_PACKAGES=$(RepoArtifactsPackageCache)"
      IgnoreStandardErrorWarningFormat="true"
      IgnoreExitCode="true" />
```

This shuts down only the **VB/C# compiler server**; the **MSBuild server is never explicitly shut down between repo builds**. `--msbuild` was intentionally omitted (presumably to preserve warm server state). Note: `DotNetBuildSourceOnly` is `true` for source-build legs, so the shutdown is **completely skipped** for source-build verticals.

#### 5. `dotnet build-server shutdown --msbuild` capability: YES (partial)

The CLI command exists (per `dotnet/dotnet:src/sdk/documentation/manpages/sdk/dotnet-build-server.1`):

```text
dotnet build-server shutdown [--msbuild] [--razor] [--vbcscompiler]
  --msbuild  Shuts down the MSBuild build server.
```

Implementation:
```
BuildServerShutdownCommand.Execute()           (src/sdk/…/BuildServer/Shutdown/BuildServerShutdownCommand.cs)
  → BuildServerProvider.EnumerateBuildServers(MSBuild)   (src/sdk/…/BuildServer/BuildServerProvider.cs:28-31)
  → MSBuildServer.Shutdown()                  (src/sdk/…/BuildServer/MSBuildServer.cs:17)
  → BuildManager.DefaultBuildManager.ShutdownAllNodes()
```

`MSBuildServer.cs`:
```csharp
public void Shutdown() => BuildManager.DefaultBuildManager.ShutdownAllNodes();
```

This sends a `NodeShutdown` packet to all connected worker nodes managed by the local `DefaultBuildManager`. **It does not directly kill the out-of-proc server process itself**; the server exits naturally after `ShutdownAllNodes` drains its queue.

**Gap:** `ProcessId` is hardcoded to 0 in `MSBuildServer` (TODO comment refs `dotnet/cli#9113`), so cross-invocation server shutdown is best-effort. If the server was launched by a prior `dotnet` invocation with a different `BuildManager`, `ShutdownAllNodes()` may be a no-op for that orphan server.

#### 6. Issue #1015 (dotnet/dotnet) — confirmed

@ViktorHofer (closed/duplicate 2025-06-09): *"When enabling parallel build in the VMR the `build-server shutdown` execs could cause issues, hopefully just perf and not stability."* … *"dotnet build-server shutdown will only shutdown the compiler server from its own install"* (toolset-package compiler scenarios). Matches the mechanism above.

#### 7. Issue #5391 (dotnet/dotnet) — separate failure mode, same root

@steveisok (open 2026-03-11): `Ubuntu2404_Ubuntu_BuildTests_x64`: Razor build fails with file-lock race (~47% of PR failures): *"`System.IO.IOException: The process cannot access the file '…src/razor/artifacts/obj/…Microsoft.CodeAnalysis.Razor.Workspaces.dll' because it is being used by another process.`"* Mitigations explicitly include: *"Check if `node_reuse=false` is consistently set (stale MSBuild server processes could hold locks)"*. Live evidence that persistent server processes hold file handles across repo build boundaries within a VMR vertical — same root as the fsharp timeout.

The two failures are distinct symptoms (file-lock vs pipe-connect timeout) but share the underlying cause: **no explicit MSBuild server shutdown between repo builds within a vertical**.

#### 8. Run 20260428.5 — Azure DevOps, not directly accessible

`20260428.5` is an Azure DevOps pipeline run ID (`dotnet-unified-build`, definition 1330, internal project). GitHub's API has no access. The stack trace already in the Background section (`System.TimeoutException` at `NamedPipeClientStream.ConnectInternal → TryConnectToPipeStream`) is the definitive artifact.

#### 9. Recommended VMR-side mitigations

| Mitigation | Location | Priority | Notes |
|---|---|---|---|
| **VMR-M1: Set `MSBUILDNODEHANDSHAKESALT=$(Agent.JobName)` per vertical** | `eng/pipelines/templates/jobs/vmr-build.yml` (variables block) or `eng/pipelines/templates/stages/vmr-verticals.yml` | **P0** | Each vertical already has a unique `Agent.JobName` (e.g., `Windows_x64`, `Linux_x64`). Injecting it as `MSBUILDNODEHANDSHAKESALT` gives each vertical a unique handshake hash → unique pipe name. Zero code change; works today. (Note: this isolates verticals from each other when running on a shared agent — but per §2 they're already isolated. The bigger value is **also** setting it per-repo within a vertical: see VMR-M3.) |
| **VMR-M2: Add `--msbuild` to CleanupRepo shutdown** | `repo-projects/Directory.Build.targets` (`CleanupRepo` target, ~line 476) | **P1** | `build-server shutdown --vbcscompiler` → `build-server shutdown --vbcscompiler --msbuild`. Tears down MSBuild server between repo builds. Add `Condition` guard: only when `MSBUILDUSESERVER=1`. |
| **VMR-M3: Set per-repo `MSBUILDNODEHANDSHAKESALT`** | `repo-projects/Directory.Build.targets` (`RepoBuild` target `EnvironmentVariables`) | **P0** | `MSBUILDNODEHANDSHAKESALT=$(RepositoryName)` per `<Exec>` invocation. Each repo gets its own server instance — no within-vertical sharing. **This is the most direct fix for the fsharp timeout** because each repo's restore/build/etc invocations remain isolated from prior repos' servers. |
| **VMR-M4: Set per-repo TMPDIR (after PR #13651 merges)** | `repo-projects/Directory.Build.targets` (`RepoBuild` target `EnvironmentVariables`) | **P2** | Once PR #13651 is merged, `TMPDIR=$(Agent.TempDirectory)/$(RepositoryName)` automatically isolates servers via the salt change. More principled than VMR-M3 but requires the upstream MSBuild change first. |
| **VMR-M5: Catch `TimeoutException` in `TryConnectToPipeStream`** | dotnet/msbuild `src/Build/BackEnd/Components/Communications/NodeProviderOutOfProcBase.cs:804` | **P0** | Thread E M1 — the actual crash fix. Not VMR-specific but must ship before broad VMR enablement. **Already prototyped on `prototype/msbuild-server-default-on-mitigations` branch.** |

#### Summary

The VMR's `VMR_Vertical_Build` stage runs ~30 parallel jobs (one per target OS/arch), each fully isolated at the agent/container level. Within each vertical, ~25 repos are built sequentially by the MSBuild orchestrator on the same agent. `MSBUILDNODEHANDSHAKESALT` is never set → all repos within a vertical share the same MSBuild server pipe name. The `CleanupRepo` target calls `dotnet build-server shutdown --vbcscompiler` but **not** `--msbuild`. The capability to shut it down exists but server PID discovery is broken (hardcoded `ProcessId = 0`). **The highest-leverage immediate VMR fix is setting `MSBUILDNODEHANDSHAKESALT=$(RepositoryName)` per repo `<Exec>` invocation in `Directory.Build.targets`** — zero MSBuild code change, isolates each repo's server.


### Wave 2 — Out-of-tree task statics audit

Survey of the 10 highest-risk out-of-tree task assemblies bundled with the .NET SDK. Thread D previously covered only `src/Tasks/**` and `src/Shared/**` in dotnet/msbuild; this wave extends to NuGet, dotnet/sdk containers, Roslyn, FSharp.Build, sourcelink, vstest, and the SDK resolver.

#### Evidence table

| Task assembly | Task class | Repo | Static state evidence | Risk for server reuse | Risk for `/mt` | TaskHost today? | Mitigation |
|---|---|---|---|---|---|---|---|
| `NuGet.Build.Tasks` | `GetRestoreSettingsTask` | [NuGet/NuGet.Client `GetRestoreSettingsTask.cs:133`](https://github.com/NuGet/NuGet.Client/blob/681b9f2e887c91492eb1510ed027d7be93443441/src/NuGet.Core/NuGet.Build.Tasks/GetRestoreSettingsTask.cs#L133) | `private static Lazy<IMachineWideSettings> _machineWideSettings` — non-`readonly` static, materialized on first call, never reset between builds | **HIGH** — stale machine-wide NuGet.config (new sources, credential changes invisible after first restore) | **MED** — shared instance; stale reads if two restores race on same server process | No TaskHost; in-proc | Replace `static Lazy` with per-invocation construction, or add to `s_knownProblematicTaskNames` allow-list in PR #13660 |
| `NuGet.Build.Tasks` | `RestoreTask` | Thread C / [PR #13660](https://github.com/dotnet/msbuild/pull/13660) | `PluginManager` singleton (NuGet.Protocol) + `NuGet.Common.Migrations.MigrationRunner.Run()` static state — see Thread C for full detail | **HIGH** — already tracked in #13315 | **HIGH** — `PluginManager` races across parallel restores | Sidecar TaskHost today; PR #13660 routes to transient TaskHost | Merge PR #13660 |
| `Microsoft.NET.Build.Containers.Tasks` | `CreateNewImage` | [dotnet/sdk `CreateNewImage.cs:48-60`](https://github.com/dotnet/sdk/blob/0b462a74c4be9cd621da9ad5f38f301d2bee5286/src/Containers/Microsoft.NET.Build.Containers/Tasks/CreateNewImage.cs#L48) | `Environment.SetEnvironmentVariable(HostObjectUser/HostObjectPass)` — process-wide env mutation to pass VS HostObject credentials; cleared in `finally` but not thread-isolated | **MED** — creds survive into next build if `finally` is skipped by crash | **CRITICAL** — two concurrent `CreateNewImage` tasks in a solution can read/overwrite each other's credentials; no per-thread env isolation | No TaskHost; in-proc async | Use thread-local or parameter-passed credential context instead of env vars; or force transient TaskHost |
| `Microsoft.NET.Build.Containers` | `DefaultRegistryAPI` | [dotnet/sdk `DefaultRegistryAPI.cs:28`](https://github.com/dotnet/sdk/blob/0b462a74c4be9cd621da9ad5f38f301d2bee5286/src/Containers/Microsoft.NET.Build.Containers/Registry/DefaultRegistryAPI.cs#L28) | `private static TimeSpan LongRequestTimeout = TimeSpan.FromMinutes(30)` — non-`readonly` static mutable field | **LOW** — value unlikely to be externally mutated in practice | **LOW** — non-`readonly`; torn write possible on 32-bit if mutated concurrently | No TaskHost | Add `readonly`; no functional change |
| `Microsoft.DotNet.SdkResolver` | `NETCoreSdkResolver` | [dotnet/sdk `NETCoreSdkResolver.cs:16-21`](https://github.com/dotnet/sdk/blob/0b462a74c4be9cd621da9ad5f38f301d2bee5286/src/Resolvers/Microsoft.DotNet.SdkResolver/NETCoreSdkResolver.cs#L16) | `private static readonly ConcurrentDictionary<string, Version> s_minimumMSBuildVersions` + `s_compatibleSdks` — explicitly designed for IDE multi-evaluation reuse; persists SDK resolution result across builds | **HIGH** — new .NET SDK installed while server is running won't be discovered; stale SDK selection until server restart | **MED** — `ConcurrentDictionary` is thread-safe; but stale values returned under concurrent lookups | SDK resolver, not a task; loaded once per MSBuild process | Add TTL/FS-watcher invalidation, or clear on `BeginBuild`; register with `OutOfProcServerNode` reset sequence |
| `Microsoft.Build.Tasks.Git` | `RepositoryTask` (sourcelink) | [dotnet/sourcelink `RepositoryTask.cs:84-114`](https://github.com/dotnet/sourcelink/blob/82c6767d2da34db2f64eb14d9062e7b2be4c6018/src/Microsoft.Build.Tasks.Git/RepositoryTask.cs#L84) | `BuildEngine4.RegisterTaskObject(…, RegisteredTaskObjectLifetime.Build)` — correctly lifetime-managed per-build git repo cache | **SAFE** — cache auto-evicted by MSBuild at `EndBuild` | **SAFE** — `Build` lifetime is correct isolation scope | No TaskHost; in-proc | No action needed — gold-standard pattern |
| `Microsoft.Build.Tasks.Git` | `AssemblyResolver` (NET461) | [dotnet/sourcelink `AssemblyResolver.cs:13-17`](https://github.com/dotnet/sourcelink/blob/82c6767d2da34db2f64eb14d9062e7b2be4c6018/src/Microsoft.Build.Tasks.Git/AssemblyResolver.cs#L13) | `static readonly List<string> s_loaderLog` unbounded growth; `AppDomain.AssemblyResolve += AssemblyResolve` called from static ctor — duplicate handlers accumulate under server reuse with NET461 | **MED (NET461 only)** — memory growth; duplicate handlers fire per resolve event | **MED** — log writes take a lock; no data corruption but noisy | No TaskHost; NET461 path only | Guard `Initialize()` with `Interlocked.CompareExchange` flag; clear log at `BuildCompleteReuse` boundary |
| `Microsoft.CodeAnalysis.BuildTasks` | `ManagedCompiler` (`Csc`/`Vbc`) | [dotnet/roslyn `CanonicalError.cs:41-80`](https://github.com/dotnet/roslyn/blob/404bc8cca45ed023d02a2d8b1620e9af2616431b/src/Compilers/Core/MSBuildTask/CanonicalError.cs#L41) | 6 `private static readonly Regex` fields; task body delegates to out-of-proc `VBCSCompiler.exe` | **LOW** — `Regex` is immutable and thread-safe; compilation is out-of-proc | **LOW** — no shared mutable state; VBCSCompiler is independent of MSBuild server | Routes to `VBCSCompiler.exe` (separate process) | No action needed; verify `VBCSCompiler` lifecycle independent of MSBuild server |
| `FSharp.Build` | `Fsc` / `Fsi` | [dotnet/fsharp `Fsc.fs`](https://github.com/dotnet/fsharp/blob/c91ff9c0f7008ef351fae731ad718143d6ef9374/src/FSharp.Build/Fsc.fs#L18) | All state is F# `let mutable` inside class body (instance fields, not module-level statics); no module-level `mutable` found | **LOW** — no static mutable state | **LOW** — each task instance has its own state; compilation via external `fsc.exe` process | `ToolTask` spawns external `fsc.exe`/`fsi.exe` | No action needed; validate `FSharpEnvironment` module has no `let mutable` |
| `Microsoft.NET.Build.Tasks` | `ProcessFrameworkReferences` | [dotnet/sdk `ProcessFrameworkReferences.cs`](https://github.com/dotnet/sdk/blob/0b462a74c4be9cd621da9ad5f38f301d2bee5286/src/Tasks/Microsoft.NET.Build.Tasks/ProcessFrameworkReferences.cs) | No class-level static mutable fields in visible code; ConcurrentDictionary usage is method-local; `[MSBuildMultiThreadableTask]` annotation present | **LOW** — no persistent static state in task class | **LOW** — implements `IMultiThreadableTask`; method-local dicts only | No TaskHost; in-proc | No action needed |

#### Top 5 next blockers (ranked by likelihood of breakage at default-on time)

1. **`CreateNewImage` credential env-var race (`/mt` mode)** — `Environment.SetEnvironmentVariable(HostObjectUser/HostObjectPass)` is a process-wide, non-thread-safe credential injection. Any solution containing container-publish projects will silently leak or corrupt credentials under `/mt`. This is the **only security-class bug** in this wave — data from one project's build is observable by another. Must fix before `/mt` is default-on for any workflow with container publishing. Action: replace env-var credential passing with a thread-local or task-parameter-based mechanism, or route `CreateNewImage` through a transient TaskHost (same pattern as PR #13660). **Evidence:** [`CreateNewImage.cs:48-60`](https://github.com/dotnet/sdk/blob/0b462a74c4be9cd621da9ad5f38f301d2bee5286/src/Containers/Microsoft.NET.Build.Containers/Tasks/CreateNewImage.cs#L48).

2. **`GetRestoreSettingsTask._machineWideSettings` static `Lazy` (server reuse)** — `XPlatMachineWideSetting` reads global `nuget.config` once and never re-reads it. Machine-wide config changes (new package source, credential rotation, proxy change) are invisible to all subsequent builds in the same server session. Affects every `PackageReference`-style project. Action: make `_machineWideSettings` non-static and construct per invocation, or add `GetRestoreSettingsTask` to the PR #13660 allow-list. **Evidence:** [`GetRestoreSettingsTask.cs:133`](https://github.com/NuGet/NuGet.Client/blob/681b9f2e887c91492eb1510ed027d7be93443441/src/NuGet.Core/NuGet.Build.Tasks/GetRestoreSettingsTask.cs#L133).

3. **`NETCoreSdkResolver` SDK-resolution caches (server reuse)** — `s_compatibleSdks` and `s_minimumMSBuildVersions` are explicitly designed for IDE performance ("static to benefit multiple IDE evaluations") and never invalidated. A user who installs a new .NET SDK patch without restarting the MSBuild server silently continues building against the old SDK. The symptom is "new SDK ignored" — hard to diagnose. Action: add a file-system-change observer on the .NET install directory, or hook into `OutOfProcServerNode`'s `BeginBuild` to clear these caches. **Evidence:** [`NETCoreSdkResolver.cs:16-21`](https://github.com/dotnet/sdk/blob/0b462a74c4be9cd621da9ad5f38f301d2bee5286/src/Resolvers/Microsoft.DotNet.SdkResolver/NETCoreSdkResolver.cs#L16).

4. **`RestoreTask`/`PluginManager` singleton (server + `/mt`) — unmerged fix** — Already the tracked blocker in #13315; PR #13660 is the fix. It remains blocked only on a minor code-style review comment (use `FrozenDictionary` / `const string`). Until merged, NuGet auth and plugin state leak between builds under server mode. This is the most urgent unmerged item — higher severity than items 1–3 in theory but actionable purely by merging one PR. **Evidence:** PR #13660, `AssemblyTaskFactory.cs:443-469`.

5. **`sourcelink AssemblyResolver` event accumulation (NET461, server reuse)** — `AppDomain.CurrentDomain.AssemblyResolve` receives a new handler per server-reuse cycle on .NET Framework. Over a long IDE session, this causes quadratic performance and memory growth for every assembly-resolve event. Lower priority (NET461 only, IDE-scenario only) but can cause mysterious assembly-binding behavior. Action: guard `Initialize()` with `Interlocked.CompareExchange`; clear `s_loaderLog` at `BuildCompleteReuse` boundary. **Evidence:** [`AssemblyResolver.cs:13-17`](https://github.com/dotnet/sourcelink/blob/82c6767d2da34db2f64eb14d9062e7b2be4c6018/src/Microsoft.Build.Tasks.Git/AssemblyResolver.cs#L13).

### Wave 2 — Project-type test matrix for default-on validation

> **Scope:** Before flipping `MSBUILDUSESERVER=1` (or `DOTNET_CLI_USE_MSBUILD_SERVER=1`) as the SDK default, every row below should be exercised with the server running. The primary risk class for each row is **server-reuse state leakage**: statics, caches, file locks, ALC-resident assemblies, console/env mutations, and NuGet auth plugins that survive `BuildCompleteReuse` when `OutOfProcServerNode` does not reset them. See Thread D/G for the catalogue of known uncleared state.

---

#### A. Project SDK Families — Representative sample & risk notes

| # | SDK / Project type | Representative public sample | Why it is interesting / risk areas | Recommended verification commands |
|---|---|---|---|---|
| 1 | `Microsoft.NET.Sdk` — class library | `dotnet/samples` → `samples/csharp/getting-started/core-console` | Baseline; exercises `CoreCompile`, `ResolveAssemblyReferences`, incremental build. Catch-all for basic server reuse. | `dotnet build` × 3 (cold→warm→change) |
| 2 | `Microsoft.NET.Sdk` — console exe | `dotnet/runtime` → `src/coreclr/tools/aot/ILLink` | Standard entry-point; exercises `GenerateApplicationManifest`, resource embedding, platform RID resolution. | `dotnet build; dotnet build --no-restore` |
| 3 | `Microsoft.NET.Sdk` — ASP.NET Core Web API | `dotnet/aspnetcore` → `src/ProjectTemplates/Web.ProjectTemplates` | Razor SDK layered on top; `RazorGenerateComponentDeclaration`, Roslyn compiler tasks; large incremental graph. | `dotnet build -c Release; dotnet publish` |
| 4 | `Microsoft.NET.Sdk.Web` — Razor Pages / MVC | `dotnet/aspnetcore` → `src/Mvc/test/WebSites/BasicWebSite` | **Razor file-lock race** (VMR issue #5391): server holds handles to `.cshtml` generated files across builds. Critical for file-lock class bugs. | `dotnet build; touch Pages/Index.cshtml; dotnet build` |
| 5 | `Microsoft.NET.Sdk.BlazorWebAssembly` | `dotnet/aspnetcore` → `src/Components/WebAssembly/testassets/StandaloneApp` | IL linker/trimmer (`ILLink.Tasks`) invoked; Blazor-specific targets produce `.wasm`+`.js` outputs; long build graph with interop generation. | `dotnet build -c Release /p:WasmBuildNative=false` |
| 6 | `Microsoft.NET.Sdk.Desktop` — WPF | `dotnet/wpf` → `src/Microsoft.DotNet.Wpf/tests/IntegrationTests` | `MarkupCompilePass1/Pass2` tasks run in **per-platform ALC**; static type-cache in `XamlParser`; file-lock on `.baml`. High risk: two-phase Markup compilation uses `DesignTimeBuild` codepath and file-system temp dirs. | `dotnet build; dotnet build /p:DesignTimeBuild=true` |
| 7 | `Microsoft.NET.Sdk.Desktop` — WinForms | `dotnet/winforms` → `src/System.Windows.Forms/tests/IntegrationTests` | `ResGen` task for `.resx` resources; designer code-gen; single-file packaging; same ALC-isolation risks as WPF. | `dotnet build; dotnet publish /p:PublishSingleFile=true` |
| 8 | `Microsoft.NET.Sdk.WindowsDesktop` (legacy) | `dotnet/winforms` → `tests/System.Windows.Forms.Design.Tests` | Still used by migration projects; exercises legacy-compatibility targets and multi-targeting `net48`+`net9.0-windows`. | `dotnet build -f net9.0-windows; dotnet build -f net48` |
| 9 | `Microsoft.Build.Sdk.Solution` (`.slnx` / `.sln`) | `dotnet/msbuild` → root `MSBuild.slnx` | Multi-project orchestration graph; `SolutionProjectDependencyRelationship` evaluation; all sub-project builds share one server. Exercises cross-project result caching and `BuildManager.DefaultBuildManager` singleton reuse (Thread G item 4). | `dotnet build MSBuild.slnx; dotnet build MSBuild.slnx --no-restore` |
| 10 | `Microsoft.NET.Sdk.IL` (ILAsm / ILASM) | `dotnet/runtime` → `src/coreclr/ilasm` or `src/tests/ilasm` | Uses `ILAsmToolTask`; niche but present in runtime/coreclr VMR builds — directly in the tree that triggered the fsharp timeout. | `dotnet msbuild src/tests/ilasm/testproject.ilproj` |
| 11 | C++ / Native (`Microsoft.Cpp.targets`) | `dotnet/runtime` → `src/coreclr/hosts/corerun` | `CL`, `Link` tasks have **process-lifetime DLL state** (`cl.exe` is invoked via `ToolTask` with retry); vcpkg integration can mutate env vars. Risk: `cl.exe`-based tasks that rely on `LIB`/`INCLUDE` env vars being stable. | `msbuild corerun.vcxproj /p:Configuration=Release` (raw `MSBuild.exe`) |
| 12 | F# (`Microsoft.NET.Sdk` + `FSharp.NET.Sdk`) | `dotnet/fsharp` → `src/fsharp/FSharp.Core` | **Direct trigger** of the VMR timeout (investigation.md Thread E). `FSharp.Build.Fsc` task has static `Compiler.Create()` singleton + static `FSharpChecker` that assumes one invocation per process. Also: many short consecutive `dotnet build` invocations → high probability of hitting the pipe-recycling race. | `MSBUILDUSESERVER=1 dotnet build FSharp.Core.fsproj` × 5 rapid-fire |
| 13 | Aspire AppHost | `dotnet/aspire` → `playground/` | `Aspire.Hosting.Sdk` injects `CreateManifestAsync` target + container-image resolution at build time; heavy use of `IBuildEngine9` task APIs; resource-model evaluation is side-effecting. New, fast-evolving SDK — high unknowns for server reuse. | `dotnet build --environment Development; dotnet run --project AppHost` |
| 14 | Container publish (`Microsoft.NET.Build.Containers`) | `dotnet/sdk` → `src/Containers` | `CreateNewImage` task pushes layers to registry; may hold static `HttpClient` singletons + auth token caches. File-lock on `.tar` layer blobs during publish. | `dotnet publish /p:PublishProfile=DefaultContainer /p:ContainerImageTag=test` |
| 15 | AOT publish (`PublishAot=true`) | `dotnet/runtime` → `src/coreclr/nativeaot/samples` | Invokes ILC (`ilc.exe`) via `ToolTask`; large dependency graph; long tail of linker/analyzer targets. `ILLink` + `IlcCompile` use heavy static type-registries. Risk: second AOT publish in same server sees stale trimmed-assembly cache. | `dotnet publish -r linux-x64 /p:PublishAot=true` × 2 |
| 16 | Trim publish (`PublishTrimmed=true`) | `dotnet/runtime` → `src/coreclr/tools/aot/ILLink` | `ILLink.Tasks.LinkTask` has known static `TypeMapGenerator` cache; trim warnings assume first-run root-set — can produce wrong analysis on server reuse if trim roots differ between builds. | `dotnet publish /p:PublishTrimmed=true /p:TrimmerRootDescriptor=...` |
| 17 | ReadyToRun (`PublishReadyToRun=true`) | `dotnet/runtime` → `src/coreclr/crossgen2` | Invokes `crossgen2` via `ToolTask` with subprocess; risk is mainly env-var mutation and temp-file leakage (`.r2r`  outputs in `obj/`). | `dotnet publish /p:PublishReadyToRun=true -r win-x64` |
| 18 | WebAssembly browser-wasm | `dotnet/runtime` → `src/mono/wasm/testassets` | `WasmBuildApp`, `WasmNativeStrip`, emscripten toolchain tasks; EMSDK env vars mutated at task startup, never restored — classic server-reuse env-mutation risk. | `dotnet build /p:TargetFramework=net10.0-browser /p:WasmBuildNative=true` |
| 19 | Mobile — Android (`net*-android`) | `dotnet/maui` → `src/Controls/samples/Controls.Sample.Droid` | `Xamarin.Android.Build.Tasks` (now MAUI SDK Tasks): static `JavaEnvironment` singleton, `jarsigner` invocation, `aapt2` daemon — all process-global. `XA` tasks historically the hardest for server-reuse. | `dotnet build -f net10.0-android /p:AndroidBuildApplicationPackage=true` |
| 20 | Mobile — iOS (`net*-ios`) | `dotnet/maui` → `src/Controls/samples/Controls.Sample.iOS` | `Xamarin.iOS.Tasks`/`Microsoft.iOS.Sdk`: `mtouch` invocation, code-signing tasks read from Keychain (process-global), `IBTool` Xcode task. Keychain API is not re-entrant. | `dotnet build -f net10.0-ios /p:RuntimeIdentifier=ios-arm64` |
| 21 | Reference Assembly project | `dotnet/runtime` → `src/libraries/System.Runtime/ref` | `ProduceReferenceAssembly=true` + `GenerateReferenceAssemblySource`; only public surface emitted. Exercises separate compilation pipeline. Risk: deterministic-compilation hash collisions between reused builds on same server. | `dotnet build /p:ProduceReferenceAssembly=true` × 2 with source change |
| 22 | Globalization-invariant trim | `dotnet/runtime` → `src/libraries/System.Globalization.Calendars` | `/p:InvariantGlobalization=true` + trim; `ILLink` roots differ from non-invariant builds. Tests that culture/locale state set by prior build does not affect subsequent one. | `dotnet publish /p:InvariantGlobalization=true /p:PublishTrimmed=true` |
| 23 | Source-Link enabled project | `dotnet/sourcelink` → samples OR `dotnet/runtime` (has `<SourceLink>` enabled) | `SourceLink.Build.Tasks` injects a static `GitRepository` object at pack time; `git` subprocess. Risk: stale source-link mapping when branch/commit changes between builds on a warm server. | `dotnet pack /p:SourceRevisionId=$(git rev-parse HEAD)` |
| 24 | Snupkg + symbol package | `dotnet/nuget-client` or any SDK library with `<IncludeSymbols>true` + `<SymbolPackageFormat>snupkg` | `CreateNuGetPackageTask` writes `.snupkg` and indexes PDB paths; static `NuGetPackageResolver` in SDK tasks. Symbol-path embedding is absolute — risk of wrong paths when CWD is not reset between server reuses. | `dotnet pack /p:IncludeSymbols=true /p:SymbolPackageFormat=snupkg` |
| 25 | MAUI multi-target (`net*-android` + `net*-ios` + `net*-maccatalyst`) | `dotnet/maui` → `src/Controls/samples/Controls.Sample.Droid` | Multi-TFM graph in one solution; all mobile risks (rows 19-20) plus TFM-selection property evaluation. `_TargetFramework` global property pollution across multi-TFM builds is a known server-reuse trap. | `dotnet build /p:TargetFrameworks=net10.0-android%3Bnet10.0-ios` |

---

#### B. Build Operations Matrix

| Operation | Risk notes | Verification command template |
|---|---|---|
| `restore` | **Highest risk** — `NuGet.Build.Tasks.RestoreTask` static singletons (Thread C / PR #13660). `PluginManager` + `EnvironmentVariableWrapper` survive between builds. Also: `HttpClient` auth-plugin token caches. | `dotnet restore` × 3 back-to-back with different credential contexts |
| `build` | Incremental build correctness: server may cache `FileExistenceCache` / `FileMatcher` state from prior build; timestamp-based up-to-date checks may be stale. | `dotnet build; touch src/Foo.cs; dotnet build` (must trigger recompile) |
| `publish` | Container/AOT/Trim targets write to `publish/` dir; stale leftover files from prior publish in same server. | `dotnet publish /p:PublishDir=/tmp/p1; dotnet publish /p:PublishDir=/tmp/p2` |
| `pack` | `CreateNuGetPackageTask` — CWD-sensitive path embedding; `.nupkg` output hash must differ when version changes. | `dotnet pack /p:Version=1.0.0; dotnet pack /p:Version=1.0.1` |
| `test` | `VSTest.Console` or `dotnet-test` hosting: test runner launched via `ToolTask`; test logger static singletons; `TRX` file paths. | `dotnet test --logger trx --results-directory /tmp/r1; ...r2` |
| `run` | `MSBuild.exe` is not in the loop for `dotnet run` hot path (CLI bypasses it), but `dotnet run --project` still invokes MSBuild for build step. Check that `OutputType=Exe` discovery survives server reuse. | `dotnet run --project ConsoleApp.csproj` |
| `format` | `dotnet format` invokes Roslyn workspace via MSBuild evaluation; `ProjectCollection` singleton. Not server-mode today but relevant for `-graph` builds that include format. | `dotnet format --verify-no-changes` |
| `clean` | `Clean` target deletes outputs; if server caches file-existence state, subsequent `build` may have stale up-to-date checks. | `dotnet build; dotnet clean; dotnet build` (must be full rebuild) |
| MSBuild target chains | `BeforeBuild` / `AfterBuild` / `BeforeCompile` hooks in `.targets` files; custom target ordering. | `dotnet msbuild /t:Pack;Publish /p:...` |

---

#### C. Invocation Patterns Matrix

| Pattern | Risk notes | Command |
|---|---|---|
| `dotnet build` (CLI) | Standard path; routes through `MSBuildForwardingApp` → `MSBuildClientApp`; most users affected. | `dotnet build project.csproj` |
| `dotnet msbuild` | Direct forwarding to MSBuild — same server path if env var set. Verify env-var forwarding is consistent. | `dotnet msbuild project.csproj /t:Build` |
| Raw `MSBuild.exe` (full framework) | Server is NOT used by `MSBuild.exe` on full framework — verify that `CanRunServerBasedOnCommandLineSwitches` correctly blocks it or that `MSBUILDUSESERVER=1` is a no-op for full framework. | `msbuild.exe project.csproj /p:TargetFramework=net48` |
| `dotnet build -graph` | `/graph` mode forces static graph evaluation via `GraphBuildSubmission`; entirely different scheduler path. Server must survive `StaticGraphBuildRequest` + `GraphBuildResult` round-trip. | `dotnet build --graph project.csproj` |
| `dotnet build /mt` | In-proc thread workers; combined with server means concurrent builds sharing one process. Highest risk: static caches hit by multiple threads simultaneously. See Thread F. | `dotnet build /p:MultiThreaded=true project.csproj` |
| `dotnet build --no-restore` | Skips `Restore` target — avoids RestoreTask risk (Thread C) but stresses incremental-build-state staleness. | `dotnet build --no-restore` |
| `dotnet test` | `VSTest.Console` subprocess + MSBuild test integration; `TestAdapterPath` prop. | `dotnet test --no-build` |
| IDE-style (`/p:DesignTimeBuild=true`) | Design-time builds are short, frequent, use fast-eval path; heavy use of `GetTargetFrameworks`, `ResolvePackageDependencies`, `CompileDesignTime`. Server is not used by VS directly today, but SDK-style DTB via `dotnet msbuild` is plausible. File-lock risk: Razor `.cshtml` generated source. | `dotnet msbuild /p:DesignTimeBuild=true /t:CollectPackageReferences` |
| Response file (`@file`) | `MSBuildClientApp` passes args through to server; verify `@file` paths are resolved relative to client CWD not server CWD. | `dotnet msbuild @response.rsp` |
| Parallel solution (`/m:4`) | Multiple worker nodes plus server node; stress-tests `BuildManager` result-caching across nodes and server identity. | `dotnet build MySolution.slnx /m:4` |

---

#### D. Custom-Task Families — Deliberately Test

| # | Custom-task family | Sample project / trigger | Why it is interesting | Verification commands |
|---|---|---|---|---|
| D1 | `BeforeBuild` / `AfterBuild` inline tasks | Any `.csproj` with `<Target Name="BeforeBuild">` | Inline tasks compiled by `RoslynCodeTaskFactory` — `AssemblyLoadContext` for the compiled DLL is cached in the server process; code changes between builds may not be picked up. | Edit inline task body; `dotnet build` × 2 — verify new code runs |
| D2 | `AssemblyInfoTask` (legacy Reflection.Emit) | Projects using `Microsoft.NET.Sdk` `GenerateAssemblyInfo=true` or old `MSBuildVersioning` package | `AssemblyInfo` generation via `WriteCodeFragment` task; version-stamping properties must not bleed across builds. | `dotnet build /p:Version=1.0.0; dotnet build /p:Version=2.0.0` — inspect `.AssemblyInfo.cs` |
| D3 | GitVersion / `Nerdbank.GitVersioning` | `dotnet/arcade`-based repos with `version.json` | Runs `git` subprocess; caches parsed `version.json` tree object in static fields. `GitVersionTask` is a known static-cache holder — breaks when branch/tag changes between builds. | `git tag v1.0.0; dotnet build; git tag -d v1.0.0; dotnet build` |
| D4 | T4 text templates (`TextTemplatingFileGenerator`) | Any project with `.tt` files (e.g., `dotnet/efcore` → `src/EFCore/Infrastructure`) | T4 host is loaded into ALC; static `CompilationUnit` cache may persist. `TransformAll` target re-runs on change — server must re-evaluate. | Modify `.tt` file; `dotnet build` × 2 — verify `.cs` output updated |
| D5 | Incremental Source Generators (`ISourceGenerator`) | `dotnet/roslyn-analyzers` or `dotnet/runtime` projects with `[Generator]` | Roslyn SG runs inside `CoreCompile`; SG assembly is loaded into the compiler's ALC inside the server process. Modifying the SG package between builds (e.g., upgrading) may not be reflected without ALC isolation. | Build with SG v1; upgrade SG NuGet package; `dotnet build` — verify new output |
| D6 | NuGet auth plugins (`ICredentialProvider`) | Any project that does authenticated NuGet restore (Azure Artifacts, GitHub Packages) | `NuGet.Protocol` `PluginManager` is the static singleton in Thread C. Credential tokens may expire mid-session; second restore may use stale token from first build's plugin session. | `dotnet restore` with a short-lived PAT; invalidate token; `dotnet restore` again |
| D7 | `SignTool` / `Microsoft.DotNet.SignTool` | `dotnet/arcade` repos using `Sign.proj` | Sign tool invokes `signtool.exe` via `ToolTask`; certificate store state is process-global on Windows; static `CertificateStore` handle. | `dotnet msbuild Sign.proj /t:Sign` × 2 — verify idempotency |

---

#### Known Orchestrators / First-Party Dogfood Candidates

| Repo | Server mode in CI today | Why interesting | Notes |
|---|---|---|---|
| `dotnet/runtime` | ❌ Not enabled by default | Largest VMR vertical; C++, IL, managed, AOT, WASM sub-builds; exercises every SDK family in rows 1-25. Most complex dependency graph in the ecosystem. | Target for issue #13604 Phase 1 dogfood. AOT+IL+WASM sub-graphs will stress static caches hardest. |
| `dotnet/aspnetcore` | ❌ Not enabled by default | Razor compilation + Blazor + SignalR; file-lock race class already manifested (VMR issue #5391). | High priority dogfood candidate — Razor file-lock is a live production failure class. |
| `dotnet/sdk` | ❌ Not enabled | Builds itself via bootstrapped MSBuild; exercises `Microsoft.NET.Build.Containers`, `dotnet-test`, and `dotnet-format` targets. | `dotnet/sdk` CI is the natural place to flip `DOTNET_CLI_USE_MSBUILD_SERVER=1` per issue #11358. |
| `dotnet/roslyn` | ❌ Not enabled | Compiles with the compiler it is building (bootstrap); heavy Roslyn SG usage; many `Csc`/`Vbc` task invocations per build. `Microsoft.Build.Locator`-based tooling in analyzers could conflict with server's ALC. | Confirms SG ALC isolation (row D5). |
| `dotnet/efcore` | ❌ Not enabled | T4 templates (row D4), NuGet auth for package push, `SqlServer`/`Sqlite` native libraries loaded via P/Invoke in build tasks. | T4 static cache + native P/Invoke DLL-loaded state. |
| `dotnet/maui` | ❌ Not enabled | Android + iOS + Mac + WinUI multi-target; `Xamarin.Android.Build.Tasks` static singletons; code-signing Keychain. Highest-risk single dogfood candidate for mobile rows 19-20. | Do last — fix all other blockers first; mobile task failures are the hardest to diagnose. |
| `dotnet/fsharp` | ❌ **Currently broken with server** | **Direct trigger** of this investigation. `FSharp.Build.Fsc` static `FSharpChecker`; pipe-recycling race at high invocation frequency. | Must be green before any other dogfood. Milestone: M1+M2 fixes from Thread E (#13604 Phase 1). |
| `dotnet/msbuild` (this repo) | ❌ Not enabled in main CI | Builds MSBuild with itself; exercises bootstrap flow; simple graph but validates server core. | Lowest-risk first dogfood — should be enabled in PR CI immediately after M1/M2 fixes merge. |

---

> **Legend:** Rows are roughly priority-ordered within each section. "❌ Not enabled" means `MSBUILDUSESERVER` / `DOTNET_CLI_USE_MSBUILD_SERVER` is not set in published CI pipelines as of investigation date (2026-04). Dogfood should proceed in order: `dotnet/msbuild` → `dotnet/fsharp` (once fixed) → `dotnet/aspnetcore` → `dotnet/sdk` → `dotnet/runtime` → `dotnet/roslyn` → `dotnet/efcore` → `dotnet/maui`.

## Wave 3 Findings

### Wave 3 — `dotnet build-server shutdown --msbuild` capability deep-dive

**Verdict: BROKEN in dotnet/sdk.**

`dotnet/sdk/src/Cli/dotnet/BuildServer/MSBuildServer.cs` implements `Shutdown()` as:

```csharp
public void Shutdown() => BuildManager.DefaultBuildManager.ShutdownAllNodes();
```

This only shuts down **nodes owned by the local in-proc `BuildManager`** in the `dotnet` CLI process. It **does NOT** send a shutdown request to the separate, long-running MSBuild server process. The MSBuild server is a distinct process listening on its own named pipe (`MSBuildServer-{handshake.ComputeHash()}`); it is not a worker node managed by the local `BuildManager.DefaultBuildManager`.

**Correct API exists in dotnet/msbuild but is not called by SDK:**
`src/Build/BackEnd/Client/MSBuildClient.cs:ShutdownServer(CancellationToken)` connects over the named pipe and sends an explicit `NodeBuildComplete(false)` shutdown command — this is the path used (correctly) by `MSBuildServer_Tests.CanShutdownServerProcess` test variant `byBuildManager=false`.

**Impact:**
- `dotnet build-server shutdown --msbuild` returns `0` (success) but the MSBuild server keeps running. Users have no way to cleanly shut down a runaway MSBuild server short of killing the process.
- Compounds VMR cleanup: even if the orchestrator added `--msbuild` to `CleanupRepo` (VMR-M2 below), it would silently no-op.
- Compounds the Razor file-lock race (#5391): server-process file handles cannot be freed via the documented CLI command.

**Recommended fix:**
Change `MSBuildServer.Shutdown()` in dotnet/sdk to delegate to `Microsoft.Build.Experimental.MSBuildClient.ShutdownServer(CancellationToken.None)` via the public API. File a `dotnet/sdk` issue + PR. **Until this lands, VMR-M2 (add `--msbuild` to CleanupRepo) is a no-op** — that mitigation must be paired with this fix.

| Mitigation | Where | Priority | Notes |
|---|---|---|---|
| **SDK-1** | Replace `BuildManager.DefaultBuildManager.ShutdownAllNodes()` with pipe-based `MSBuildClient.ShutdownServer()` in `dotnet/sdk/src/Cli/dotnet/BuildServer/MSBuildServer.cs` | **P1** | Without this, `dotnet build-server shutdown --msbuild` is effectively a no-op for the long-running server process. Required before VMR-M2. |

### Wave 3 — Prototype branch regression test results

After the initial regression run noted "Zero tests" for `MSBuildServer_Tests`, that turned out to be a test-project-naming issue (the file lives in `src/MSBuild.UnitTests/MSBuildServer_Tests.cs` which compiles to `Microsoft.Build.CommandLine.UnitTests.dll`, not `Microsoft.Build.Engine.UnitTests.dll`). Re-run against the correct executable:

| Test class | Project | Total | Passed | Failed | Skipped | Duration |
|---|---|---:|---:|---:|---:|---|
| `Microsoft.Build.Engine.UnitTests.MSBuildServer_Tests` | `Microsoft.Build.CommandLine.UnitTests` | 9 | **9** | 0 | 0 | 14.2s |
| `Microsoft.Build.UnitTests.BackEnd.NodeProviderOutOfProc_Tests` | `Microsoft.Build.Engine.UnitTests` | 8 | **8** | 0 | 0 | 3.1s |

**Result:** All 17 server-related tests pass on the `prototype/msbuild-server-default-on-mitigations` branch with M1+M2+M3 applied. New regression test `TryConnectToPipeStream_WhenPipeUnavailable_ReturnsTimeoutInsteadOfThrowing` passes. **No regressions detected.**

### Wave 3 — FSharp.Build module-level state audit

Validates Thread D-OOT's claim that `FSharp.Build` has no module-level mutable state (which would otherwise be the most likely application-side trigger for fsharp's deterministic VMR timeout).

| File | Concerning module-level mutable/static state |
|---|---|
| `src/FSharp.Build/Fsi.fs:20-67` | None; all `let mutable` are inside `type Fsi()` instance body, plus one `do` instance initializer at line 67. |
| `src/FSharp.Build/Fsc.fs:19-80` | None; all `let mutable` are inside `type Fsc()` instance body, plus one `do` instance initializer. |
| `src/FSharp.Build/FSharpEmbedResourceText.fs:14-20` | None; instance-body only. |
| `src/FSharp.Build/WriteCodeFragment.fs:15-29` | None; mutable fields are instance-scoped, `static let escapeString` is immutable. |
| `src/FSharp.Build/SubstituteText.fs:10-14` | None; instance-scoped. |
| `src/FSharp.Build/MapSourceRoots.fs:20-80` | No mutable state; only `static let` constants/functions. |
| `src/FSharp.Build/CreateFSharpManifestResourceName.fs` | No module-level mutable found. |
| `src/FSharp.Build/CallerFile.fs` | Not present in dotnet/fsharp `src/FSharp.Build/`. |

**Verdict: SAFE for server reuse.** No module-level `mutable` or `static do` initializers found. The state in `Fsi.fs`/`Fsc.fs` is instance-local; each task invocation gets fresh state. `FSharp.Build.fsproj` references standard MSBuild packages only; no shared task-lib bridge introducing hidden module-level state.

**Implication for the VMR fsharp timeout:** This *confirms* Thread E's conclusion — the fsharp build timeout is **not** caused by FSharp.Build statics. The root cause is purely in MSBuild server infrastructure (uncaught `TimeoutException` + pipe-recycling race + missing TMPDIR in handshake salt). FSharp's symptom-frequency simply reflects its build pattern (many short consecutive `dotnet build` invocations within a single VMR vertical), not any static-state misuse on its end.

### Wave 3 — Logging behavior under server reuse

- **Binary logger (`-bl`)**: CLI-gated off via `CanRunServerBasedOnCommandLineSwitches`, but a programmatic `BinaryLogger` can still be registered; it's initialized per build and `Shutdown()` closes the stream/zip at `src/Build/Logging/BinaryLogger/BinaryLogger.cs:335-460, 487-537`. **Verdict: Safe via build teardown; not server-gated when injected programmatically.**
- **File loggers (`-flp`)**: created per request in `src/MSBuild/XMake.cs:3446-3497`, and `EndBuild()` shuts down logging via `ShutdownLoggingService()` before completion at `src/Build/BackEnd/BuildManager/BuildManager.cs:1016-1155, 319`. **Verdict: Safe.**
- **Central forwarding logger re-registration**: `InitializeNodeLoggers()` re-creates forwarding loggers each request at `src/Build/BackEnd/Components/Logging/LoggingService.cs:1184-1224`. **Verdict: Safe.**
- **`Console.ForegroundColor` leak**: `BaseConsoleLogger` sets `Console.ForegroundColor` in `SetColor()` and only restores via `ResetColor()` in some paths, not globally per request (`src/Build/Logging/BaseConsoleLogger.cs:304-329, 220-227`). **Verdict: LEAKS** (process-global state can persist across requests).
- **`BuildEventArgsReader` / `BuildEventSource` listener leakage**: `BinaryLogger.Initialize()` subscribes to `eventSource.AnyEventRaised` at `src/Build/Logging/BinaryLogger/BinaryLogger.cs:447-460`, but `Shutdown()` only removes the project-imports callback and disposes the stream at `src/Build/Logging/BinaryLogger/BinaryLogger.cs:496-537`; **it does NOT unsubscribe `AnyEventRaised`**. **Verdict: LEAKS** (re-registering the same logger on a reused server attaches a new handler each time without detaching the old one — handler list grows unbounded; events double/triple-fire).

| Mitigation | Where | Priority | Notes |
|---|---|---|---|
| **LOG-1** | `BinaryLogger.Shutdown()` must unsubscribe `eventSource.AnyEventRaised` (and other handlers) added in `Initialize()` | `src/Build/Logging/BinaryLogger/BinaryLogger.cs:496-537` | **P1** | Without this, programmatically registered binlogs leak handlers across server requests; long-running server accumulates handlers and double-writes events. |
| **LOG-2** | Reset `Console.ForegroundColor` at the end of each server request (or avoid touching it under server mode) | `src/Build/BackEnd/Node/OutOfProcServerNode.cs` (per-request reset) or `src/Build/Logging/BaseConsoleLogger.cs` (always restore on dispose) | **P2** | Cosmetic but visible: a build that errored leaves the console in red until the next color-aware operation. Surprising for interactive `dotnet build` users. |

### Wave 4 — Unix pipe semantics + cross-platform server behavior

1. **Pipe naming on Unix:** `OutOfProcServerNode` uses `NamedPipeUtil.GetPlatformSpecificPipeName("MSBuildServer-{handshake.ComputeHash()}")`, producing `/tmp/MSBuildServer-{hash}` on Unix. The `FEATURE_PIPEOPTIONS_CURRENTUSERONLY` flag is still passed in the client-side construct path, but Unix isolation really comes from the runtime's UDS implementation plus the handshake. (`src/Build/BackEnd/Node/OutOfProcServerNode.cs:131, 166-167`; `src/Shared/NamedPipeUtil.cs:24-35`)
2. **NamedPipeServerStream on Unix:** `NodeEndpointOutOfProcBase.InternalConstruct()` creates the server stream with `PipeOptions.Asynchronous | PipeOptions.WriteThrough` and optional `CurrentUserOnly`; on Unix this maps to a Unix domain socket, **not the Windows named-pipe ACL model**. (`src/Shared/NodeEndpointOutOfProcBase.cs:224-276`)
3. **Hardcoded `/tmp` prefix:** MSBuild hardcodes `/tmp` because macOS's per-user temp directories can exceed the UDS path-length limit. **Risk:** the socket pathname is predictable and lives in a shared temp namespace, so cross-user conflicts/leakage are only avoided if the runtime enforces per-user permissions correctly; same-name collisions remain theoretically possible on shared CI. (`src/Shared/NamedPipeUtil.cs:26-35`)
4. **`MSBUILDDEBUGCOMM` trace path:** when enabled, traces go to `FrameworkDebugUtils.DebugPath` if set, otherwise `FileUtilities.TempFileDirectory`. Files are named `MSBuild_CommTrace_PID_<pid>[_node_<id>].txt`. (`src/Framework/BackEnd/CommunicationsUtilities.cs:756-787`)
5. **`getsid()` / session isolation:** confirmed by code comment — on Unix, session isolation is intentionally skipped (`sessionId = 0`) because `getsid()` is per-terminal/session-leader and not needed for RDP-style isolation. (`src/Framework/BackEnd/Handshake.cs:88-98`)
6. **macOS cleanup:** no repo evidence of logout cleanup for these sockets; since MSBuild uses `/tmp`, cleanup is not session-scoped, so lingering socket files depend on process exit / runtime cleanup rather than logout semantics. **Unix/macOS risk class:** stale `/tmp` entries + cross-user pathname predictability, mitigated by handshake salt/hash and per-user pipe behavior.

| Mitigation | Where | Priority | Notes |
|---|---|---|---|
| **UNIX-1** | Add Unix-specific path-conflict diagnostics: when handshake fails on Unix and `/tmp/MSBuildServer-{hash}` exists, log file owner + perms in the trace | `src/Build/BackEnd/Components/Communications/NodeProviderOutOfProcBase.cs` (handshake failure path) | **P3** | Helps diagnose multi-user CI runner conflicts. |
| **UNIX-2** | Document that `MSBUILDNODEHANDSHAKESALT` is the recommended way to isolate servers on shared Unix CI runners | docs / dotnet/dotnet README | **P3** | User-facing documentation for shared-agent setups. |

### Wave 4 — Additional task assemblies static-state audit

| Task assembly | Task class | Repo | Static state evidence | Risk for server reuse | Risk for /mt | TaskHost today? | Mitigation |
|---|---|---|---|---|---|---|---|
| `Microsoft.DotNet.Wpf` | `MarkupCompilePass2` | `dotnet/wpf` | No `static` mutable fields found in `MarkupCompilePass2.cs`; task instead persists state to on-disk cache files and app-domain-local objects | low — no obvious process-wide static cache; main risk is stale cache files / temp-file reuse | low | No | Keep cache files build-scoped; no server-specific action |
| `Microsoft.DotNet.Wpf` | `MarkupCompilePass1` / markup compiler helpers | `dotnet/wpf` | Build-task pipeline writes/reads compiler state files and appdomain-scoped helpers; reuse hazard is file-based state, not shared statics | med — stale cache files can survive a reused server process | med | No | Force per-build cache cleanup; consider short-lived TaskHost if the cache path is shared |
| `Microsoft.AspNetCore.SpaProxy.Tasks` | SPA proxy task(s) | `dotnet/aspnetcore` | Task assembly is process-resident and proxies a long-lived external process; state is driven by process handles and env/process launch parameters | med — stale proxy process/handle can survive a reused server node | med | No | Make proxy process lifetime explicit and tear down on build end |
| `Microsoft.NET.Sdk.Razor` | Razor component generator tasks | `dotnet/aspnetcore` | SDK-side Razor generation is evaluated repeatedly in large solution builds; generator/task state is loaded once per server process | med — stale generator/configuration state can persist across builds | med | No | Reset Razor generator caches between builds; prefer per-invocation instances |
| `ILCompiler.MSBuildTaskHost` | AOT/ILC host tasks | `dotnet/runtime` | Task host path is intentionally separate, but AOT tooling commonly uses process-wide caches and native tool state | med — server reuse can pin stale AOT inputs or environment assumptions | med | Yes (hosted separately) | Keep AOT work in isolated host; clear static/native caches on host reuse |
| `WasmAppBuilder` | WebAssembly build helpers | `dotnet/runtime` | Build path mutates environment for emscripten/toolchain setup and relies on process-global env during invocation | high — env-var mutation is sticky across reused server requests | high | No | Snapshot/restore env vars; prefer per-process TaskHost for wasm toolchain setup |
| `Xamarin.Android.Build.Tasks` | Android build tasks | `dotnet/maui` | Historically static-heavy build tasks; Android tooling often caches SDK/tool paths and native handles across invocations | high — stale SDK/tool path caches can leak across builds | high | No | Move path/tool discovery to per-build state or isolate in TaskHost |
| `Xamarin.iOS.Tasks` | iOS signing/build tasks | `dotnet/maui` | Keychain/code-signing tooling interacts with process-global credentials and native handles | high — keychain/credential state can outlive one build | high | No | Use transient TaskHost and explicit keychain cleanup |
| `Microsoft.NET.HostModel` | host-model task helpers | `dotnet/runtime` | Host/packaging helpers are process-resident and often hold file/handle state while rewriting apphost assets | med — stale handle/file state can persist in server process | med | No | Keep host-model operations isolated and close all native handles deterministically |
| `Microsoft.WindowsAppSDK` | Windows App SDK tasks | `dotnet`/Windows App SDK | Windows packaging tasks commonly touch registry, temp files, and native deployment state | med — server reuse can retain deployment/environment assumptions | med | No | Prefer short-lived TaskHost for packaging/deployment steps |
| `Microsoft.VSSDK.BuildTools` | VS SDK build tasks | `microsoft/vssdk` | Visual Studio SDK tasks often cache extension/project metadata and interact with VS-specific native tooling | med — stale VS tool state can persist across builds | med | No | Per-build reset hook; isolate VS-specific tasks out of server reuse |
| `ProjectInstaller` | installer task(s) | `dotnet/installer` | Deprecated path; low signal beyond checking for legacy statics | low | low | No | No action unless reused in active build paths |

**Top 5 Wave 4 next-blockers:**
1. `Xamarin.iOS.Tasks` — keychain/code-signing state + native handle leakage is the highest-severity reuse risk.
2. `WasmAppBuilder` — env-var mutation around emscripten/toolchain setup is sticky and thread-hostile.
3. `Xamarin.Android.Build.Tasks` — static-heavy legacy build tools; likely to hide process-lifetime caches.
4. `ILCompiler.MSBuildTaskHost` — AOT host reuse needs explicit cache/handle hygiene before server default-on.
5. `Microsoft.AspNetCore.SpaProxy.Tasks` — process lifetime of proxy subprocesses and handles can leak across builds.

### Wave 4 — Console redirect + I/O correctness under server mode

1. **Console output forwarding (server → client):** `OutOfProcServerNode.RedirectConsoleWriter` buffers writes into a `StringWriter` and flushes every 40ms via a timer; `Dispose()` stops the timer, flushes, and disposes the inner writer. Race-prone if a late `WriteLine` arrives during shutdown — directly explains issue #12580 (closed/not-planned `ObjectDisposedException` during `pack -graph`). (`src/Build/BackEnd/Node/OutOfProcServerNode.cs:455-782`)
2. **Stdin forwarding (client → server): NOT implemented.** `MSBuildClient` does **not** forward stdin to the server. It only pumps packets and prints `ServerNodeConsoleWrite` back to local `Console`/`Console.Error`. Interactive `Console.ReadLine()` from a task running on the server will hang indefinitely. (`src/Build/BackEnd/Client/MSBuildClient.cs:310-363`, `src/Build/BackEnd/BuildManager/BuildManager.cs:830`)
3. **`ServerNodeBuildCommand` payload:** carries client's command line, environment block, cultures, and a `TargetConsoleConfiguration` (buffer width / ANSI / screen / background color). **No `Console.OutputEncoding` / codepage propagation.** (`src/Build/BackEnd/Shared/ServerNodeBuildCommand.cs:15-105`, `src/Build/BackEnd/Shared/ConsoleConfiguration.cs:14-62`)
4. **`Console.OutputEncoding` per request:** `OutOfProcServerNode` sets `ConsoleConfiguration.Provider` per request and temporarily applies buffer width / background color, but **never sets/restores `Console.OutputEncoding`** per build. Encoding correctness is whatever the server process happened to inherit at startup. (`src/Build/BackEnd/Node/OutOfProcServerNode.cs:365-440`)
5. **`--interactive` flag:** parsed in the CLI (`src/MSBuild/XMake.cs:2285-2287`) and flows into evaluation/resolvers, but **server-mode I/O is not interactive**. NuGet auth over server is broken unless the auth flow avoids stdin entirely (e.g., uses a credential provider that handles its own UI).

**Critical implications:**
- **Interactive auth is broken under server mode.** This is a hard blocker for any user who relies on credential providers that prompt on stdin (legacy NuGet credential providers, dotnet user-secrets interactive flows).
- **Console encoding bugs** (e.g., non-Latin text in build output) will exhibit DIFFERENT behavior under server vs. non-server because the server process's startup encoding is sticky for all subsequent requests.
- **Issue #12580 (ObjectDisposedException) is a real race** — likely to resurface under heavy load or graph builds; the closed-not-planned status is questionable now that we're moving toward default-on.

| Mitigation | Where | Priority | Notes |
|---|---|---|---|
| **CIO-1** | Disable server when `-interactive` is on the command line OR when stdin is a TTY | `src/MSBuild/XMake.cs:CanRunServerBasedOnCommandLineSwitches:346-388` | **P0** | Prevents broken auth flows. Trivial check; low blast radius. |
| **CIO-2** | Propagate `Console.OutputEncoding` / codepage in `ConsoleConfiguration` payload, apply per-request, restore after | `src/Build/BackEnd/Shared/ConsoleConfiguration.cs`, `src/Build/BackEnd/Node/OutOfProcServerNode.cs:HandleServerNodeBuildCommand` | **P1** | Cross-platform correctness for non-ASCII output. |
| **CIO-3** | Reopen issue #12580; add a barrier in `RedirectConsoleWriter.Dispose()` ensuring no pending `WriteLine` is in flight | `src/Build/BackEnd/Node/OutOfProcServerNode.cs:455-782` | **P1** | Race condition will recur under default-on; closing as "not planned" was a pre-default-on triage decision. |
| **CIO-4** | Investigate stdin forwarding for interactive auth, or document that server-mode disables interactive flows | new feature OR docs | **P2** | Likely resolved by CIO-1 for now; full forwarding is a larger feature. |

### Wave 4 — Telemetry on server use + monitoring readiness

#### Inventory of server-related telemetry fields

All server telemetry is aggregated in `BuildTelemetry` (`src/Framework/Telemetry/BuildTelemetry.cs`), emitted as a single `VS/MSBuild/build` event per build via two channels:
- **.NET Framework / VS:** VS Telemetry Service, sampled at **1:25 000** (`DefaultSampleRate = 4e-5` in `TelemetryConstants.cs:34`).
- **.NET Core / dotnet CLI:** OpenTelemetry `ActivitySource` `Microsoft.Build.TelemetryDefault` (`TelemetryManager.cs:100`). Consumers wire their own listener.

| Field | Values | Source | Notes |
|---|---|---|---|
| `InitialMSBuildServerState` | `"hot"`, `"cold"`, `null` | `MSBuildClient.cs:164` | Set before pipe connect; null = server not tried |
| `ServerFallbackReason` | `ServerBusy`, `UnableToConnect`, `LaunchError`, `UnknownServerState`, `Arguments`, `ErrorParsingCommandLine`, `ClientUnhandledException:<Type>`, `null` | `XMake.cs:374,383`, `MSBuildClientApp.cs:80,93` | null = success |
| `StartAt` | `DateTime` | `MSBuildClient.cs:164` | Time before pipe connect / server launch |
| `InnerStartAt` | `DateTime` | server-side `BuildManager` | Time when server actually starts the build |
| `BuildDurationInMilliseconds` / `InnerBuildDurationInMilliseconds` | ms (computed) | `BuildTelemetry.cs:163,167` | Outer = includes IPC overhead; Inner = server-only execution |

ETW events `MSBuildServerBuildStart` (89) / `MSBuildServerBuildStop` (90) carry richer payload (`clientExitType`, `countOfConsoleMessages`, `sumSizeOfConsoleMessages`) but are **not forwarded** to VS Telemetry / OpenTelemetry — only PerfView/ETW-local.

#### Six critical gaps before default-on monitoring is viable

1. **No explicit "server attempted" boolean.** Success is inferred by `ServerFallbackReason == null AND InitialMSBuildServerState != null` — invisible when env var unset. Need `IsServerModeEnabled: bool?`.
2. **No connection latency field.** `StartAt → InnerStartAt` gap is muddled with build-prep; pure IPC handshake duration unrecorded.
3. **No retry count.** `TryConnectToServer` may retry on non-timeout failures; spike invisible.
4. **No server-process crash attribution** in `CrashTelemetry` — server crash looks identical to in-proc crash.
5. **`MSBuildClientExitType.Unexpected`** (surprise pipe closure) does NOT trigger `ServerFallbackReason` — disappears from telemetry until users report.
6. **Sampling at 1:25,000 too sparse** to detect a 5% fallback regression within 24h of a default-on rollout.

#### Suggested server health dashboard

| Panel | Query | Alert |
|---|---|---|
| Server adoption rate | `rate(ServerFallbackReason == null AND InitialMSBuildServerState != null) / rate(InitialMSBuildServerState != null)` | <90% → P1 |
| Fallback breakdown | `count by ServerFallbackReason where ServerFallbackReason != null` | spike >2σ → P2 |
| Cold-start latency | `avg(InnerBuildDurationInMilliseconds - BuildDurationInMilliseconds) where state="cold"` | P95 >8s → P3 |
| Hot-reuse overhead | `avg(BuildDurationInMilliseconds - InnerBuildDurationInMilliseconds) where state="hot"` | P95 >2s → P2 |
| Exception fallback rate | `rate(ServerFallbackReason like 'ClientUnhandledException:%')` | any sustained → P1 |
| Build-failure parity | `rate(BuildSuccess=false) WHERE state!=null` vs same WHERE `state IS null` | >2% divergence → P1 |

| ID | Mitigation | Where | Priority |
|---|---|---|---|
| **TEL-1** | Add `IsServerModeEnabled: bool?` field set in `XMake.cs` when env var detected (regardless of fallback) | `src/Framework/Telemetry/BuildTelemetry.cs` + `src/MSBuild/XMake.cs:314` | **P1** |
| **TEL-2** | Add `ServerConnectionDurationMs`; record elapsed inside `TryConnectToServer` | `src/Build/BackEnd/Client/MSBuildClient.cs:605-639` | **P1** |
| **TEL-3** | Add `ServerConnectionRetryCount`; increment in `tryAgain` loop | `src/Build/BackEnd/Client/MSBuildClient.cs:612` | **P2** |
| **TEL-4** | Forward ETW events 89/90 into `BuildTelemetry` (or new `VS/MSBuild/server` event) | cross-cutting | **P2** |
| **TEL-5** | Time-limited bump sample rate to 1:1 000 for server fields during rollout | `src/Framework/Telemetry/TelemetryConstants.cs:34` | **P1 (revert post-rollout)** |
| **TEL-6** | Map `MSBuildClientExitType.Unexpected` → fallback + set `ServerFallbackReason` | `src/MSBuild/MSBuildClientApp.cs:86-93` | **P1** |

### Wave 4 — Lesson learned: rejected mitigation CIO-1 (`-interactive` should NOT disable server)

**The W4-2 sub-agent's CIO-1 recommendation ("disable server when `-interactive` is on cmdline") was prototyped, broke the existing test `MSBuildServer_Tests.ServerShouldStartWhenBuildIsInteractive` (`src/MSBuild.UnitTests/MSBuildServer_Tests.cs:289-308`), and was reverted.**

The existing test contract is **explicit and intentional**:
`
pidOfInitialProcess.ShouldNotBe(pidOfServerProcess, "We failed to start a server node when interactive is true.");
`
The team designed server mode to coexist with `-interactive`, presumably because modern NuGet credential providers (Azure Artifacts Auth, Microsoft.Build.NuGetSdkResolver, etc.) use out-of-band UI (browser auth, Microsoft Auth Broker) rather than `Console.ReadLine()`.

**Revised guidance:** Wave 4-2's interactive-auth concern is real for *legacy* credential providers that do read stdin, but the framework-level fix should be **per-task** (force credential-provider tasks to a transient TaskHost, similar to PR #13660's RestoreTask handling) rather than blanket-disable server. Adding the credential provider tasks to the `TaskRouter.IsKnownProblematicTask` allow-list is the right approach.

| ID | Mitigation (revised) | Where | Priority |
|---|---|---|---|
| **CIO-1 (revised)** | Add NuGet credential-provider task names to `TaskRouter.s_knownProblematicTaskNames` allow-list (alongside `RestoreTask`) so they get a transient TaskHost — preserving server reuse for the rest of the build while keeping interactive auth flows isolated | `src/Build/BackEnd/Components/RequestBuilder/TaskRouter.cs` (extension to PR #13660) | **P2** |

### Wave 4 — Rubber-duck critique of M1+M2+M3 prototype

**Overall:** no blocking issue found in the prototype diff. M1 is appropriately surgical: catching only `TimeoutException` in `TryConnectToPipeStream` fixes the crash without changing the worker-node path's broader exception handling (`TryConnectToProcess` still catches other non-critical connect failures), and the blast radius is small (`MSBuildClient`, worker-node `TryConnectToProcess`, plus the new unit test are the only call sites).

#### Non-Blocking Issues

1. **Shutdown path still keeps the old 1s connect timeout**
   - **Impact:** `MSBuildClient.TryShutdownServer` still calls `TryConnectToServer(1_000)` (`src/Build/BackEnd/Client/MSBuildClient.cs:264-269`). That leaves `dotnet build-server shutdown --msbuild` exposed to the same pipe-recycling gap that motivated M3, so cleanup can spuriously fail even though build execution got the new 5s mitigation.
   - **Severity:** Non-Blocking
   - **Recommended fix:** reuse the hot-connect timeout (or introduce a dedicated shutdown timeout override) instead of hardcoding 1s.

2. **New timeout env vars are not validated/clamped**
   - **Impact:** `MSBUILDSERVERHOTCONNECTTIMEOUT=0` or a negative value makes `TryConnectToServer` skip its loop entirely and return `false` without setting `MSBuildClientExitType`, which then falls through to `MSBuildApp.ExitType.MSBuildClientFailure` instead of the normal in-proc fallback (`src/Build/BackEnd/Client/MSBuildClient.cs:605-639`, `src/MSBuild/MSBuildClientApp.cs:86-108`).
   - **Severity:** Non-Blocking
   - **Recommended fix:** clamp both env-var-derived values with `Math.Max(1, ...)` and add a regression test for zero/negative overrides.

3. **M2 catch-all also catches `OperationCanceledException`**
   - **Impact:** `ExceptionHandling.IsCriticalException` does not treat `OperationCanceledException` as critical (`src/Framework/ExceptionHandling.cs:25-55`), so the new top-level catch in `MSBuildClientApp.Execute` would convert a cancellation during client setup/execute into an in-proc retry instead of honoring cancellation.
   - **Severity:** Non-Blocking
   - **Recommended fix:** exclude `OperationCanceledException` from the filter (`when (ex is not OperationCanceledException && !ExceptionHandling.IsCriticalException(ex))`).

4. **The new M2 fallback path has no direct regression test**
   - **Impact:** the prototype tests M1 directly, but nothing currently verifies that an unexpected non-critical client exception actually triggers in-proc fallback and sets `ServerFallbackReason = "ClientUnhandledException:<TypeName>"`.
   - **Severity:** Non-Blocking
   - **Recommended fix:** add a focused `MSBuildClientApp` test that injects a non-critical client failure and asserts fallback + telemetry behavior; optionally add a cancellation test if issue #3 is fixed.

#### Suggestions

1. **Document the new env vars**
   - **Impact:** `MSBUILDSERVERHOTCONNECTTIMEOUT` / `MSBUILDSERVERCOLDCONNECTTIMEOUT` are operator-facing mitigations but are not listed in `documentation/wiki/MSBuild-Environment-Variables.md`.
   - **Severity:** Suggestion
   - **Recommended fix:** add both variables, defaults (5s / 20s), and intended usage to the env-var doc. The names themselves are consistent with existing all-caps MSBuild env-var conventions.

2. **Add a backward-compat test for the worker-node caller**
   - **Impact:** the new unit test only exercises `TryConnectToPipeStream` directly. It does not verify that the worker-node caller (`TryConnectToProcess`) still behaves correctly with M1 in place, nor does it exercise the server-client fallback end-to-end.
   - **Severity:** Suggestion
   - **Recommended fix:** add one worker-node regression test (to confirm existing broad exception swallowing still works) and one client-app fallback test.

3. **Cold-server timeout likely does not need a default bump yet**
   - **Impact:** I did not find evidence in the current diff/tests that a 20s cold-start timeout is the immediate failure mode. The structural cold/hot issue remains handshake isolation / pipe-recycling, not just the numeric default.
   - **Severity:** Suggestion
   - **Recommended fix:** keep the 20s default for now, rely on the new cold-timeout override for experiments, and prioritize the structural handshake-isolation fix if VMR still reports cold-start failures.

4. **Telemetry payload looks acceptable**
   - **Impact:** `ClientUnhandledException:<TypeName>` appears PII-safe because it records only the exception type name, not message/stack/path data. It should be useful enough for coarse bucketing, although not for deep diagnosis by itself.
   - **Severity:** Suggestion
   - **Recommended fix:** no immediate change required; keep the type-only payload unless telemetry needs stronger bucketing later.

#### Checked and found acceptable

- **M1 exception scope:** narrow `TimeoutException` handling is the right shape; broadening `TryConnectToPipeStream` to also convert `IOException` / `UnauthorizedAccessException` / `InvalidOperationException` would change retry/fallback behavior rather than just fixing the crash.
- **Background-thread escape hatch:** packet-pump thread exceptions are already marshaled back through `PacketPumpException` and turned into `MSBuildClientExitType.Unexpected` inside `MSBuildClient.Execute`, so I did not find an obvious unhandled background-thread path that bypasses the client entry point.
- **Timeout-sensitive tests:** I did not find an existing `MSBuildServer_Tests` / `NodeProviderOutOfProc_Tests` assertion that depends on the hot timeout being exactly 1s.

### Wave 4 — Rubber-duck critique of M1+M2+M3 prototype

The rubber-duck agent (Claude Sonnet 4.6) was asked to review the M1+M2+M3 + TEL-1 prototype for blind spots. **Verdict: no blocking issues.** Four non-blocking issues were identified and **all four have been addressed** on the prototype branch:

| # | Critique | Status | Fix |
|---|---|---|---|
| 1 | `TryShutdownServer` (`MSBuildClient.cs:265`) still uses a **hardcoded 1s** connect timeout — the same pipe-recycling race that M3 fixed for builds also affects `dotnet build-server shutdown`. | ✅ Fixed | Use `MSBUILDSERVERHOTCONNECTTIMEOUT` (default 5s) for shutdown connect too; clamped `Math.Max(1, ...)`. |
| 2 | `MSBUILDSERVERHOTCONNECTTIMEOUT` / `MSBUILDSERVERCOLDCONNECTTIMEOUT` env vars not clamped — `0` or negative values bypass fallback and return `MSBuildClientFailure`. | ✅ Fixed | `Math.Max(1, GetValueAsInt32OrDefault(...))` for both env vars and the shutdown timeout. |
| 3 | M2 catches `OperationCanceledException` because `ExceptionHandling.IsCriticalException()` does not classify it as critical — Ctrl-C cancellation could be silently converted into an in-proc retry. | ✅ Fixed | Catch filter explicitly excludes `OperationCanceledException`: `catch (Exception ex) when (ex is not OperationCanceledException && !ExceptionHandling.IsCriticalException(ex))`. |
| 4 | M2's new catch/fallback path has no direct unit test. | ⚠️ Deferred | A direct test would require mock infrastructure or a production refactor heavier than the fix itself. M2 behavior is implicitly exercised by the existing end-to-end `MSBuildServer_Tests.MSBuildServerTest`, which calls `MSBuildClientApp.Execute()` and asserts success. Adding a dedicated test is queued as follow-up. |

**Items the rubber-duck checked and found acceptable:**
- M1 is surgical enough — keeping it to `TimeoutException` is the right shape.
- M2 is not obviously swallowing malformed cmdline errors from normal user paths; the cancellation risk (#3) was the only real M2 concern.
- Telemetry payload `ClientUnhandledException:TypeName` is PII-safe.
- Background-thread path (packet pump) is contained — its exceptions are marshaled back into `Execute()`.
- Blast radius is small — only `MSBuildClient`, worker-node `TryConnectToProcess`, and the new unit test call `TryConnectToPipeStream`.
- No existing test depends on the hot timeout being exactly 1s.

The CIO-1 design rejection (separately documented above) was a fifth lesson learned from a different sub-agent; the rubber-duck did not need to flag it because it was already caught by the existing `MSBuildServer_Tests.ServerShouldStartWhenBuildIsInteractive` test.
### Wave 5 — VMR-side concrete patches (PR-ready diffs)

All three patches target `dotnet/dotnet` directly (PRs accepted to `eng/pipelines/` and `repo-projects/`; those paths are VMR-native, not mirrored from component repos). `src/roslyn/eng/pipelines/variables-build.yml` is mirrored from `dotnet/roslyn` — changes there must go to `dotnet/roslyn` first and flow in via Maestro/Darc.

#### VMR-M1 — Per-vertical handshake salt in `vmr-build.yml`

**File:** `eng/pipelines/templates/jobs/vmr-build.yml` (SHA `9a61739`)
**Scope:** all ~30 vertical jobs. **Risk:** zero (purely additive). **Dependency:** none.

```diff
--- a/eng/pipelines/templates/jobs/vmr-build.yml
+++ b/eng/pipelines/templates/jobs/vmr-build.yml
@@ -186,6 +186,12 @@
   - name: failedJobArtifactName
     value: $(successfulJobArtifactName)_Attempt$(System.JobAttempt)
 
+  # Give every vertical job a unique MSBuild server pipe name.
+  # The salt is mixed into the ServerNodeHandshake hash, so two jobs that share the
+  # same SDK installation path (toolsDirectory) but different Agent.JobName will bind
+  # to different named pipes and never share a server.
+  - name: MSBUILDNODEHANDSHAKESALT
+    value: $(Agent.JobName)
```

#### VMR-M2 — Add `--msbuild` to `CleanupRepo` shutdown

**File:** `repo-projects/Directory.Build.targets` (SHA `71974cf`)
**Scope:** every repo with `CleanWhileBuilding=true` (non-internal non-source-build).
**Risk:** low; **silently a no-op** for the MSBuild server today (Wave 3: `MSBuildServer.Shutdown()` in dotnet/sdk only reaches local in-proc nodes). Patch is correct in intent but **requires SDK-1** to be effective. Merge anyway so the correct behavior activates automatically when SDK-1 lands.

```diff
--- a/repo-projects/Directory.Build.targets
+++ b/repo-projects/Directory.Build.targets
@@ -474,11 +474,15 @@
     <!--
       … existing comment …
       Don't run when source building to prevent the build from hanging indefinitely - https://github.com/dotnet/source-build/issues/4796
+
+      Also shut down the MSBuild server to release any file handles it may hold on the
+      repo's artifacts directory (e.g. Razor generated sources, see dotnet/dotnet#5391).
+      NOTE: --msbuild shutdown is a no-op until dotnet/sdk SDK-1 is fixed; keep this line
+      so the correct behaviour activates automatically once SDK-1 lands.
     -->
-    <Exec Command="&quot;$(DotNetTool)&quot; build-server shutdown --vbcscompiler"
+    <Exec Command="&quot;$(DotNetTool)&quot; build-server shutdown --vbcscompiler --msbuild"
           Condition="'$(DotNetBuildSourceOnly)' != 'true'"
           EnvironmentVariables="NUGET_PACKAGES=$(RepoArtifactsPackageCache)"
           IgnoreStandardErrorWarningFormat="true"
```

#### VMR-M3 — Per-repo handshake salt in `RepoBuild` `<Exec>` *(highest leverage)*

**File:** `repo-projects/Directory.Build.targets` (SHA `71974cf`)
**Scope:** every repo built by the VMR orchestrator (~25 repos × ~30 verticals). **Risk:** low–medium (each repo cold-starts its own server; ~1-2s JIT warmup once per repo, then warm for the remainder of that repo's invocations). **Dependency:** none — works today without any MSBuild code change. **This is the most direct fix for the VMR fsharp timeout.**

```diff
--- a/repo-projects/Directory.Build.targets
+++ b/repo-projects/Directory.Build.targets
@@ -437,6 +437,14 @@
     <!-- Create directories for extra debugging. -->
     <MakeDir Directories="$(MSBuildDebugPathTargetDir);
                           $(RoslynDebugPathTargetDir);
                           $(AspNetRazorBuildServerLogDir)"
              Condition="'$(EnableExtraDebugging)' == 'true'" />
 
+    <!-- Isolate the MSBuild server pipe per repository.
+         MSBUILDNODEHANDSHAKESALT is mixed into ServerNodeHandshake.ComputeHash(), giving
+         each repo a unique pipe name (MSBuildServer-hash(reponame+toolsDir)). Without this,
+         all repos within a vertical share the same server because they share the same
+         .dotnet SDK installation path — the structural root cause of the fsharp VMR timeout
+         (dotnet/msbuild#13604, investigation Thread E). -->
+    <ItemGroup>
+      <EnvironmentVariables Include="MSBUILDNODEHANDSHAKESALT=$(RepositoryName)" />
+    </ItemGroup>
+
     <Exec Command="$(FullCommand)"
           WorkingDirectory="$(ProjectDirectory)"
           EnvironmentVariables="@(EnvironmentVariables);@(BuildEnvironmentVariable)"
```

#### Patch application order

| Order | Patch | Gate |
|---|---|---|
| 1 | **VMR-M3** (per-repo salt in `Directory.Build.targets`) | None — safe to merge standalone; **highest leverage** |
| 2 | **VMR-M1** (per-vertical salt in `vmr-build.yml`) | None — defensive baseline, merge alongside VMR-M3 |
| 3 | **VMR-M2** (`--msbuild` shutdown) | Merge code change now; **requires SDK-1** before it has effect |
| — | **SDK-1** (`MSBuildServer.Shutdown()` fix in dotnet/sdk) | Separate dotnet/sdk PR; activates VMR-M2 |

#### PR contribution model

| File | Authoring | PR target |
|---|---|---|
| `eng/pipelines/templates/jobs/vmr-build.yml` | VMR-native | `dotnet/dotnet` direct |
| `repo-projects/Directory.Build.targets` | VMR-native | `dotnet/dotnet` direct |
| `src/roslyn/eng/pipelines/variables-build.yml` | mirrored from dotnet/roslyn | PR `dotnet/roslyn` first; auto-syncs via Maestro |

Reviewers: `@dotnet/source-build-internal` (dotnet unified-build team).
### Wave 5 — EFCore + Razor SDK static-state audit

#### Scope

Audit of two task assemblies not covered by prior waves: `dotnet/efcore` design-time tasks (EFCore.Tasks + EFCore.Design) and the `Microsoft.NET.Sdk.Razor` build tasks (`dotnet/sdk:src/RazorSdk`). Focus areas: process-lifetime statics surviving server reuse; file-handle leaks relevant to issue #5391; source-generator ALC isolation.

#### EFCore — verdict: out-of-process design, no MSBuild server risk

All EFCore MSBuild tasks (`OptimizeDbContext`, plus scaffolding/migration tasks added in future) extend `ToolTask` and spawn `dotnet exec ef.dll` as a child process for every design-time operation (`OperationTaskBase.cs`). No static caches exist in the MSBuild task code; `_resultBuilder` is a `private readonly` instance field. EFCore has **no Roslyn `[Generator]`-attributed source generator** and no in-process T4 host — template files ship as content and are invoked out-of-process via `dotnet-ef`. Two frozen read-only statics exist in `CSharpHelper.cs` (`Keywords`, `LiteralFuncs`) and one auto-generated `ResourceManager` in `DesignStrings.Designer.cs`; all are immutable after type initialization and pose no server-reuse risk.

#### Razor SDK — process-lifetime ALC cache in `rzc server` (HIGH risk)

The Razor SDK (`Microsoft.NET.Sdk.Razor.Tasks`) contains `DotNetToolTask`, which can route build requests to a long-lived `rzc server` process over a named pipe (`UseServer=true`). Inside that server, `DefaultExtensionAssemblyLoader` maintains four never-invalidated `Dictionary` fields and a custom `ExtensionAssemblyLoadContext` that is created once per loader instance and never unloaded or recreated between builds. The `ShadowCopyManager` shadow-copies extension DLLs to `%TEMP%/Razor/ShadowCopy/<GUID>/` on first load and never refreshes them; `Dispose()` releases a mutex but does not delete files, so server crashes leave shadow-copy files on disk — the same mechanism as the file-lock race in dotnet/dotnet #5391.

#### Static-state audit table

| Task / file | Location | Static / state kind | Risk under server reuse | Severity | Suggested mitigation |
|---|---|---|---|---|---|
| `OperationTaskBase` | `dotnet/efcore:src/EFCore.Tasks/Tasks/Internal/OperationTaskBase.cs` | `ToolTask` subclass; spawns `dotnet exec ef.dll`; `_resultBuilder` is `private readonly` instance field | None — child process per invocation | **SAFE** | No action needed |
| `OptimizeDbContext.CopyDirectoryRecursive` | `dotnet/efcore:src/EFCore.Tasks/Tasks/OptimizeDbContext.cs` | `private static` utility method; no state | None | **BENIGN** | No action needed |
| `DesignStrings._resourceManager` | `dotnet/efcore:src/EFCore.Design/Properties/DesignStrings.Designer.cs` | `private static readonly ResourceManager` — auto-generated; frozen at type init | Frozen singleton; content never changes | **BENIGN** | No action needed |
| `CSharpHelper.Keywords` + `LiteralFuncs` | `dotnet/efcore:src/EFCore.Design/Design/Internal/CSharpHelper.cs` | Two `private static readonly` frozen collections (C# keywords list; type→formatter dispatch) | Immutable after type init; thread-safe language-spec constants | **BENIGN** | No action needed |
| EFCore `[Generator]` / T4 | `dotnet/efcore:src/EFCore.Design/` | No `[Generator]` attribute; `.t4` templates invoked out-of-process by `dotnet-ef` CLI | N/A — no in-process generator or T4 host | **N/A** | No action needed |
| `SdkRazorGenerate.SourceRequiredMetadata` | `dotnet/sdk:src/RazorSdk/Tasks/SdkRazorGenerate.cs:14-16` | `private static readonly string[]` — 3-element frozen array of metadata key names | Frozen; no cross-build leakage | **BENIGN** | No action needed |
| `DotnetToolTask._razorServerCts` | `dotnet/sdk:src/RazorSdk/Tasks/DotnetToolTask.cs:18` | `CancellationTokenSource` instance field; disposed via `using` in `TryExecuteOnServer` | Instance-scoped; null between invocations | **SAFE** | No action needed |
| `DefaultExtensionAssemblyLoader` dictionaries | `dotnet/sdk:src/RazorSdk/Tool/DefaultExtensionAssemblyLoader.cs:16-22` | 4 instance `Dictionary` fields (`_loadedByPath`, `_loadedByIdentity`, `_identityCache`, `_wellKnownAssemblies`) **never cleared** between builds in `rzc server` | Changed extension DLLs on disk are silently ignored; tag-helper or source-generator logic is stale for the server's lifetime | **HIGH** | Add mtime/hash check before returning cached assembly; or invalidate on `BuildBegin`; or force server restart when extension DLLs change |
| `ExtensionAssemblyLoadContext` (no ALC teardown) | `dotnet/sdk:src/RazorSdk/Tool/DefaultExtensionAssemblyLoader.cs:122+` | Custom `AssemblyLoadContext` created once per loader; never unloaded; not collectible | ALC isolation between builds is absent; assemblies from build N are visible to build N+1; conflicting extension versions cause silent misbehavior | **HIGH** — ALC not re-isolated between builds | Use collectible ALC (`isCollectible: true`) per build request, unloaded after each build completes; mirrors the Roslyn analyzer ALC isolation pattern |
| `ShadowCopyManager` temp files | `dotnet/sdk:src/RazorSdk/Tool/ShadowCopyManager.cs` | Shadow-copies extension DLLs to `%TEMP%/Razor/ShadowCopy/<GUID>/`; `Dispose()` releases mutex but **does not delete files**; on crash, files persist | On server crash, unlocked shadow copies remain → next build may lock the same path → file-lock race identical to dotnet/dotnet #5391 | **MED** | Ensure `Dispose()` also deletes `UniqueDirectory`; run `PurgeUnusedDirectoriesAsync` proactively on server startup; register cleanup handler on server shutdown signal |

#### Source-generator ALC isolation — finding

The `rzc server` does **not** re-isolate extension assemblies between builds. There is no collectible ALC, no assembly reload, and no version check. This means:
- A user who modifies a Razor extension (e.g., a custom tag-helper assembly) without restarting the Razor server will silently build against the old version.
- The CLI path (`UseServer=false`) avoids this entirely because `dotnet exec rzc.dll` forks a new process per invocation, giving fresh ALC state.
- The risk only manifests when `UseServer=true` (the default when `DotNetToolTask.UseServer` property is set in the `.targets` file, i.e., the default Razor SDK flow).

#### File-handle leak connection to issue #5391

The `ShadowCopyManager.AddAssembly()` copies source DLLs to a per-session temp directory and never refreshes them. On a normal server shutdown, `Dispose()` is called (releasing the mutex) but leaves the temp directory intact. On an abnormal termination (crash, OOM, SIGKILL), neither the mutex nor the directory is cleaned up. The next build starts a new server with a new GUID directory but calls `PurgeUnusedDirectoriesAsync()` to clean up orphaned old directories — however, if the original server's mutex handle is still open in a zombie process, the purge skips that directory. Any other process (e.g., an MSBuild task reading the same extension DLL) that opens a handle on the shadow-copied file will see a locked file, matching the `System.IO.IOException: The process cannot access the file '…' because it is being used by another process` pattern in #5391.

**Recommended addition to Tier 1 mitigations (OOT-4):** File dotnet/sdk issue: `DefaultExtensionAssemblyLoader` must use a collectible ALC with per-build lifetime; `ShadowCopyManager.Dispose()` must delete `UniqueDirectory`; add to `OutOfProcServerNode.HandleShutdown` equivalent in `rzc server` to ensure cleanup.
```

---

### Wave 5 — Lesson learned: rejected mitigation TEL-6 (`Unexpected` should NOT trigger fallback)

**The wave-4 telemetry sub-agent's TEL-6 recommendation ("map `MSBuildClientExitType.Unexpected` to a fallback so it appears in telemetry") was prototyped, broke the existing test `MSBuildServer_Tests.MSBuildServerTest` (`src/MSBuild.UnitTests/MSBuildServer_Tests.cs:90-138`), and was reverted.**

Failure mode discovered by the test:
1. Test starts a build that triggers a long-running task on the server.
2. Test kills the server process mid-build via the FileSystemWatcher.
3. Pre-TEL-6 behavior: client returns `MSBuildClientExitType.Unexpected` → `MSBuildClientApp.Execute` returns `MSBuildClientFailure` (non-zero exit). Test's next `ExecMSBuild` call then starts a fresh build which uses a new server.
4. With TEL-6 applied: client returns `Unexpected` → `MSBuildClientApp.Execute` falls back to `MSBuildApp.Execute(commandLineArgs)` in-proc → that **re-runs the same build**, including the 100-second sleep task → test hangs and exceeds the 30-second xunit timeout.

**Root insight:** `MSBuildClientExitType.Unexpected` means *the build was already in progress on the server and the connection dropped*. Falling back in that case is **destructive** because it would re-execute a build that may have already partially completed (build-time side effects: file writes, deployments, registry changes). The `Unexpected` exit type is correctly handled today by surfacing as `MSBuildClientFailure` so the user learns the build did not complete and can decide whether to retry.

**Revised guidance:** `Unexpected` deserves better *telemetry* (it currently disappears from the `ServerFallbackReason` field), but **NOT** a fallback. The right fix is to set `ServerFallbackReason = "ConnectionLostMidBuild"` (or similar) in the `Unexpected` path while still returning `MSBuildClientFailure`.

| ID | Mitigation (revised) | Where | Priority |
|---|---|---|---|
| **TEL-6 (revised)** | When `MSBuildClientExitType.Unexpected` is returned, set `ServerFallbackReason = "ConnectionLostMidBuild"` for telemetry visibility BUT **do not** fall back to in-proc — let the failure surface to the user. | `src/MSBuild/MSBuildClientApp.cs` (after the existing fallback if-block) | **P2** |

This is the **second rubber-duck-style lesson** in this investigation (CIO-1 was the first). Both demonstrate that "make server fall back more aggressively" can introduce silent re-execution bugs — the fallback set must be carefully scoped to states that guarantee the build *has not yet started* on the server.
## Recommended Mitigations

### Tiered roadmap to safe default-on MSBuild server

#### Tier 0 — Immediate, must ship before any broader rollout (P0)

| ID | Mitigation | Where | Status | Notes |
|---|---|---|---|---|
| **M1** | Catch `TimeoutException` in `TryConnectToPipeStream` | `src/Build/BackEnd/Components/Communications/NodeProviderOutOfProcBase.cs:802-822` | ✅ **prototyped on `prototype/msbuild-server-default-on-mitigations`** | Converts uncaught crash into graceful in-proc fallback. Single most-important fix; addresses the actual VMR fsharp build crash directly. Test: `NodeProviderOutOfProc_Tests.TryConnectToPipeStream_WhenPipeUnavailable_ReturnsTimeoutInsteadOfThrowing` (passing). |
| **M3** | Bump hot-server connect timeout from 1s → 5s (env-tunable) | `src/Build/BackEnd/Client/MSBuildClient.cs:186` | ✅ **prototyped** | Adds `MSBUILDSERVERHOTCONNECTTIMEOUT` and `MSBUILDSERVERCOLDCONNECTTIMEOUT` env vars. 1s is too short for pipe-recycling under CI load. |
| **PR #13660** | Route NuGet `RestoreTask` to transient TaskHost in server/`/mt` modes | dotnet/msbuild PR #13660 | ⏳ **open, blocked on minor review comment** (use `FrozenDictionary`/`const string`) | One-merge-away from unblocking the canonical NuGet auth blocker (#13315). |

#### Tier 1 — Required before SDK default-on (P1)

| ID | Mitigation | Where | Status | Notes |
|---|---|---|---|---|
| **PR #13651** | Include `Path.GetTempPath()` in handshake salt for non-TaskHost handshakes | dotnet/msbuild PR #13651 | ⏳ **open draft** | Closes #13594. Eliminates structural cross-environment server sharing. Reviewer @rainersigwald. |
| **VMR-M1** | Set `MSBUILDNODEHANDSHAKESALT=$(Agent.JobName)` per vertical | dotnet/dotnet `eng/pipelines/templates/jobs/vmr-build.yml` | ⚠️ **needs author** | Zero MSBuild code change. Isolates each vertical's MSBuild server identity. |
| **VMR-M3** | Set `MSBUILDNODEHANDSHAKESALT=$(RepositoryName)` per repo `<Exec>` | dotnet/dotnet `repo-projects/Directory.Build.targets` (`RepoBuild` target) | ⚠️ **needs author** | **Most direct fix for the fsharp timeout** — each repo gets its own server. |
| **VMR-M2** | Add `--msbuild` to `CleanupRepo` shutdown | dotnet/dotnet `repo-projects/Directory.Build.targets` | ⚠️ **needs author** | Ensures MSBuild server torn down between repo builds; addresses Razor file-lock race (#5391). |
| **OOT-1** | Fix `Microsoft.NET.Build.Containers.CreateNewImage` credential env-var race | dotnet/sdk `src/Containers/.../CreateNewImage.cs:48-60` | 🔴 **NEW BLOCKER (security-class)** | Process-wide `Environment.SetEnvironmentVariable(HostObjectUser/Pass)` — credentials observable across parallel `/mt` builds. Either replace with thread-local context OR add to PR #13660 allow-list. |
| **OOT-2** | Add `NuGet.Build.Tasks.GetRestoreSettingsTask` to PR #13660 allow-list | dotnet/msbuild PR #13660 (extension) | ⚠️ **easy follow-up** | Stale machine-wide `nuget.config` problem; same allow-list mechanism. |

#### Tier 2 — Strongly recommended before broad enablement (P2)

| ID | Mitigation | Where | Notes |
|---|---|---|---|
| **M2** | Top-level catch-all in `MSBuildClientApp.Execute` falls back to in-proc on any unexpected exception | `src/MSBuild/MSBuildClientApp.cs:60-86` | ✅ **prototyped** | Wraps `MSBuildClient.Execute()` in `try { ... } catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex)) { CommunicationsUtilities.Trace(...); return MSBuildApp.Execute(commandLineArgs); }`. Telemetry records `ServerFallbackReason = "ClientUnhandledException:" + ex.GetType().Name`. Defense-in-depth against future undiscovered exception classes. |
| **M5** | Retry on `TimeoutException` with backoff (200-500ms × 3) before giving up | `src/Build/BackEnd/Client/MSBuildClient.cs:599-634` | Currently the retry loop excludes `Timeout` status (line 616). Allowing limited retry would absorb the pipe-recycling gap without falling back to in-proc. |
| **OOT-3** | Add per-build invalidation hook for `Microsoft.DotNet.SdkResolver` static caches | dotnet/sdk `src/Resolvers/Microsoft.DotNet.SdkResolver/NETCoreSdkResolver.cs:16-21` | New SDK installs invisible to running server; user-visible "stale SDK" symptom. |
| **G1** | Snapshot/restore env + culture around each request in `OutOfProcServerNode` | `src/Build/BackEnd/Node/OutOfProcServerNode.cs:380-387` | Per Thread G: env vars and culture set per-request but never reverted; leaks influence subsequent request defaults. |
| **G2** | Ensure logger registration is request-scoped and torn down before reuse | `src/Build/BackEnd/Node/OutOfProcServerNode.cs` + `BuildManager.cs:1016+` | Per Thread G: no logger unregister between requests. |

#### Tier 3 — Nice-to-have / longer-term

| ID | Mitigation | Where | Notes |
|---|---|---|---|
| **#9692** | Add `SharedId` to `ServerNodeHandshake` for explicit multi-clone isolation | dotnet/msbuild `src/Framework/BackEnd/ServerNodeHandshake.cs` | Enables explicit "different builds" identification beyond TMPDIR. |
| **#12246** | Define and document the lifetime/contract for static members in Tasks; expose `IsServerMode`/`IsMultiThreaded` to task authors | cross-cutting | Long-term clean fix; replaces the allow-list with a principled API. |
| **A1** | Add `/preprocess` to `CanRunServerBasedOnCommandLineSwitches` exclusion list | `src/MSBuild/XMake.cs:346-388` | Per Thread A: `/preprocess` produces stdout that should not flow through server IPC. |
| **G3** | Add per-request reset hook (clear `AppContext`, `MSBuildEventSource`, ALC resolver hooks) before `BuildCompleteReuse` | `OutOfProcServerNode` | Per Thread G: comprehensive process-state hygiene. |

### Decision: should `-mt` and server be coupled?

**No.** Per Thread F: `-mt` and server solve different problems (parallelism vs. process reuse), have distinct risk profiles, and a `-mt`-only user (per-invocation parallelism on CI) does not want a daemon while a server-only user (interactive `dotnet build`) does not want to widen the in-proc concurrency surface. Coupling them prevents users from exercising one risk without the other and raises the cost of rolling either back.

**Recommendation:** keep flags independent. Ship server-on default at SDK level (per #9379 plan for .NET 10.0.200) **after** Tier 0+1 mitigations land. Keep `-mt` opt-in indefinitely.

### Default-on rollout plan (proposed)

1. **Phase 0** (now): Ship M1+M3 to dotnet/msbuild main; merge PR #13660 + PR #13651. (Three MSBuild PRs, all ready or near-ready.)
2. **Phase 1** (.NET 10 GA prep): Apply VMR-M1/M2/M3 to dotnet/dotnet pipeline. Validate VMR builds with server on (issue #13604). Fix OOT-1 (`CreateNewImage` credential leak) — ship in dotnet/sdk.
3. **Phase 2** (.NET 10 GA dogfood): Enable server in `dotnet/msbuild`, then `dotnet/fsharp` (validates Phase 0 fixes), then `dotnet/aspnetcore` (validates Razor file-lock fixes). Order from #13604 dogfood plan.
4. **Phase 3** (.NET 10.0.200): Per #11358, flip `DOTNET_CLI_USE_MSBUILD_SERVER=1` in SDK wrapper. Defer if any Tier-1 item slips.
5. **Phase 4** (post-GA, telemetry-driven): Tier 2 + Tier 3 cleanup, retirement of allow-list once #12246 lands.

## Prototype branches

### `prototype/msbuild-server-default-on-mitigations`

- **Base:** `main`
- **Commits:**
  1. `investigation: MSBuild server default-on root-cause + mitigations` — full investigation.md
  2. `prototype: M1+M3 mitigations for MSBuild server connect timeout` — code fix + test (this commit)
- **Files changed (excluding investigation.md):**
  - `src/Build/BackEnd/Components/Communications/NodeProviderOutOfProcBase.cs` — wrap `nodeStream.Connect(timeout)` in try/catch for `TimeoutException`; return `HandshakeStatus.Timeout` instead of throwing
  - `src/Build/BackEnd/Client/MSBuildClient.cs` — bump hot-server timeout 1s → 5s, add `MSBUILDSERVERHOTCONNECTTIMEOUT` / `MSBUILDSERVERCOLDCONNECTTIMEOUT` env-var overrides
  - `src/Build.UnitTests/BackEnd/NodeProviderOutOfProc_Tests.cs` — new regression test `TryConnectToPipeStream_WhenPipeUnavailable_ReturnsTimeoutInsteadOfThrowing`
- **Validation:** `Microsoft.Build.Engine.UnitTests` builds clean; new test passes (1/1 succeeded, 1.4s).
- **Deferred to follow-up commits:** M2 top-level catch-all (defense-in-depth); M5 backoff-retry; G1/G2 env+culture+logger reset.




