# Native AOT MSBuild: measurements and open questions

Results from the prototype in [`src/MSBuild.Aot`](../../src/MSBuild.Aot/README.md), measured on an Apple M3 Pro
(12 cores, macOS, osx-arm64) against SDK `11.0.100-preview.7.26360.111` with this repository's engine. "JIT MSBuild"
is `dotnet MSBuild.dll` from the same SDK directory; "AOT MSBuild" is `MSBuildAot` deployed next to it.

## Output parity

Each `dotnet new` template was built from clean with both executables in the same directory and with the same
arguments, and every file under `bin/` and `obj/` was hashed. Files whose hash also differs between two JIT builds
(StaticWebAssets caches that embed timestamps) are excluded. These templates produced identical outputs: `console`,
`classlib`, F# and VB `console`, `web`, `webapi`, `mvc`, `webapp`, `blazor`, `worker`, `razorclasslib`, `xunit`,
`mstest`, `grpc`.

## Single-project templates

Median wall time of the whole command, compiler server warm. `clean` deletes `bin` and `obj`; `noop` rebuilds with
nothing changed; `touch` updates the timestamp of one source file.

| Project | Scenario | AOT, `-nr:false` | JIT, `-nr:false` | AOT vs JIT | AOT, `-nr:true` | JIT, `-nr:true` | AOT vs JIT |
|---|---|---:|---:|---:|---:|---:|---:|
| console | clean | 0.38s | 1.12s | 3.0x | 0.34s | 1.04s | 3.1x |
| console | noop | 0.35s | 0.97s | 2.8x | 0.30s | 0.91s | 3.0x |
| classlib | noop | 0.33s | 0.95s | 2.8x | 0.29s | 0.89s | 3.0x |
| webapi | clean | 0.61s | 1.61s | 2.6x | 0.61s | 1.55s | 2.5x |
| webapi | noop | 0.46s | 1.17s | 2.5x | 0.41s | 1.16s | 2.8x |
| mvc | clean | 0.70s | 1.57s | 2.2x | 0.65s | 1.52s | 2.3x |
| blazor | clean | 0.72s | 1.65s | 2.3x | 0.66s | 1.63s | 2.5x |
| blazor | noop | 0.49s | 1.26s | 2.6x | 0.45s | 1.22s | 2.7x |
| xunit | noop | 0.36s | 0.98s | 2.7x | 0.31s | 0.94s | 3.0x |

`dotnet build` was within about 10% of the JIT MSBuild in every row: it runs MSBuild inside the JIT `dotnet` process.

## Scaling: does the JIT catch up on larger builds?

Synthetic solutions of N class libraries, where project *i* references projects *i-1*, *i/2* and *i-7*, each with 12
small source files. The transitive reference closure grows roughly with N², so engine work (evaluation,
`ResolveProjectReferences`, up-to-date checks) dominates no-op builds. AOT and JIT runs alternate within every round so
that machine noise affects both equally. Best of 3-6 runs, in seconds; `clean` keeps the restore output.

| N | Mode | AOT noop | JIT noop | AOT vs JIT | AOT clean | JIT clean |
|---:|---|---:|---:|---:|---:|---:|
| 10 | `-mt`, server (warm) | 0.28 | 0.45 | 1.6x | 0.74 | 0.93 |
| 50 | `-mt`, server (warm) | 1.72 | 2.56 | 1.5x | 4.09 | 5.09 |
| 100 | `-mt`, server (warm) | 6.25 | 8.79 | 1.4x | 12.45 | 15.50 |
| 200 | `-mt`, server (warm) | 12.92 | 18.06 | 1.4x | 32.28 | 35.85 |
| 10 | `-mt`, `MSBUILDUSESERVER=0` | 0.46 | 1.28 | 2.8x | 0.85 | 1.86 |
| 50 | `-mt`, `MSBUILDUSESERVER=0` | 1.93 | 4.57 | 2.4x | 4.18 | 7.46 |
| 100 | `-mt`, `MSBUILDUSESERVER=0` | 4.34 | 9.82 | 2.3x | 10.03 | 15.36 |
| 200 | `-mt`, `MSBUILDUSESERVER=0` | 12.72 | 22.62 | 1.8x | 31.56 | 39.83 |
| 10 | `-m -nr:true` (warm nodes) | 0.50 | 1.18 | 2.4x | 0.74 | 1.52 |
| 50 | `-m -nr:true` (warm nodes) | 2.22 | 3.68 | 1.7x | 4.05 | 5.82 |
| 100 | `-m -nr:true` (warm nodes) | 9.02 | 10.93 | 1.2x | 19.38 | 16.70 |

Observations:

- **The JIT did not overtake the native image at any size.** In a warm server, JIT no-op times settle after about two
  builds (18.8, 18.5, 18.1, 18.5 s at N=200) and stay about 1.4x above AOT. The ratio falls from 1.6x towards 1.4x as
  startup is amortized, then levels off: what remains is the difference in steady-state code, and it favors AOT for
  this workload. Both executables use the default GC configuration (workstation, concurrent).
- **Clean builds converge** because compilation happens in the shared, JIT-compiled `VBCSCompiler` either way. Real
  projects with more source will be closer still.
- **The AOT server was slower than AOT without a server** at N=100 (6.25 s vs 4.34 s no-op); not investigated.
- **`-m` breaks down on the N=200 graph for both executables**: no-op builds take 70-80 s (and vary between 33 s and
  80 s run to run) against 13 s with `-mt`. The time is spent waiting in `ResolveProjectReferences` on results passed
  between node processes. These rows are left out of the comparison.
- **Not measured:** builds that run many tasks which are not compiled in (tasks from NuGet packages, inline tasks).
  The native image sends those to a JIT task host and pays a process round trip per task call; that is the most
  likely case for the JIT MSBuild to win.

## Open questions

Decisions that would have to be made before shipping a native MSBuild, roughly in order of how much they block:

1. **How `dotnet build` would use it.** The CLI runs MSBuild in its own JIT process, so it would have to start the
   native image as a child process, decide when to fall back to the in-process path, and still pay its own startup.
   (dotnet/sdk)
2. **What to do when a build needs reflection**: custom loggers, third-party task factories, project-cache plugins,
   custom BuildCheck checks. Options are failing (as now, MSB4283 for task factories), handing the build to the JIT
   MSBuild (task factories are found only during evaluation, so this may need a restart), or running the component out
   of process (plausible for loggers, hard for cache plugins).
3. **Where the image is built.** It is bound to one exact SDK layout, so it can only be produced in the unified build
   after the SDK is assembled, once per runtime identifier, adding about 63 MB per RID. Every repo that services a task
   assembly needs to trigger a rebuild.
4. **What a mismatched task assembly reports.** A serviced task DLL quietly runs out of process: correct but slower.
   A warning would break `/WarnAsError` builds; a message or telemetry would not.
5. **Whether the registration APIs are public.** `Task.RegisterPrecompiledTask<T>` and `SdkResolver.RegisterPrecompiled`
   are new public API.
6. **Whether `-mt` should imply the server in the native image**, given that it gains little and was slower at N=100.

Known friction that needs work rather than a decision:

- `WorkloadSdkResolverAdapter` repeats internal steps of the SDK's workload resolver and breaks if they change; a
  small change in dotnet/sdk to accept a supplied SDK root would remove it.
- Task assemblies from other repos rely on reflection (System.Text.Json, `DefaultValueAttribute`, resources) and are
  rooted whole; making them trim-safe would shrink the image, which is close to Apple `ld-prime`'s
  "too many large addends" limit. LLVM `lld` was not tested, nor were Linux and Windows images.
- The first task that is not compiled in starts a JIT task host (several hundred milliseconds).
- Native and JIT nodes do not reuse each other, so a machine running both keeps two node pools.
- `Assembly.Location` is empty under AOT; any task that finds files next to its own assembly would fail.
- The unit tests run only on the JIT; the native image needs its own test leg and a broader parity corpus than
  `dotnet new` templates.
- Debugging and tracing behavior of the native image (`MSBUILDDEBUGONSTART`, EventSource) has not been checked.
- Visual Studio's MSBuild and API hosts stay on the JIT, so there would be three ways to host the engine.
