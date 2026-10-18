# MSBuild as a Native AOT application

`MSBuild.Aot` publishes the MSBuild command line as a Native AOT executable (`MSBuildAot`) for one SDK layout. It is
meant to sit in that SDK's versioned directory, next to `MSBuild.dll`, and behaves like `dotnet MSBuild.dll` there:
same targets, same SDK resolution, same task semantics, same outputs.

> Like [`src/aot-validation`](../aot-validation/README.md), this project is **not part of the Arcade build** (it is not
> in `MSBuild.slnx`); its local `Directory.Build.*` files isolate it. Build it explicitly.

## Build and deploy

```bash
./build.sh -v quiet                                     # produces the bootstrap SDK layout the image is bound to
.dotnet/dotnet publish src/MSBuild.Aot -c Release       # or -p:AotSdkLayout=<dotnet root>/sdk/<version>
cp src/MSBuild.Aot/bin/Release/net11.0/<rid>/publish/MSBuildAot <dotnet root>/sdk/<version>/
<dotnet root>/sdk/<version>/MSBuildAot -restore path/to/project.csproj
```

The executable must be deployed into the layout it was built from (its task registrations are keyed to those exact
files; see below). Build again whenever the SDK layout changes.

## How tasks work

Task execution is the part of MSBuild that is normally incompatible with Native AOT: a `<UsingTask>` names an assembly
that MSBuild loads at run time. `MSBuildAot` splits tasks into two kinds without changing how a project selects them.

**Tasks that ship in the SDK layout are compiled into the image.** At build time,
[`TaskCatalogGenerator`](TaskCatalogGenerator/Program.cs) scans the layout for every assembly with public `ITask`
classes (Microsoft.NET.Build.Tasks, NuGet.Build.Tasks, Roslyn's `Csc`, Razor, StaticWebAssets, SourceLink, F#, ...)
and every resolver an `SdkResolvers` manifest names, and generates registrations for them
(`Task.RegisterPrecompiledTask<T>(mvid)`, `SdkResolver.RegisterPrecompiled(...)`), each keyed by the **module version id
of the exact file** it was compiled from. Their dependency closures (and the engine's own private dependencies, such as
`Microsoft.VisualStudio.SolutionPersistence`) are taken from the same layout, so they bind to the same files a JIT
MSBuild would load.

At run time nothing about task selection changes: the engine still evaluates `<UsingTask>` (including `TaskFactory`,
`Runtime`, `Architecture`, conditions and overrides), and only the final "load this assembly, find this type" step
consults the registry. A compiled-in class is used only when the file the `<UsingTask>` names has the registered module
version id - a project that points at a different build of an SDK task assembly gets that build, not the compiled-in
one. MSBuild's own `Microsoft.Build.Tasks.Core` is bound by name, as the JIT engine does.

**Every other task runs in a .NET task host.** A task from a NuGet package, a project-local task assembly, or anything
else not compiled in is described through a `MetadataLoadContext` (no code is loaded) and executed in a JIT `MSBuild`
task-host process from the same SDK - the same out-of-proc mechanism `TaskHostFactory` uses - so it is loaded exactly as
an in-proc JIT MSBuild would load it (into .NET, whatever framework the assembly targets, unless the `<UsingTask>` asks
for a specific `Runtime`). The host is a reused sidecar process, so its startup is paid once per build.

Inline tasks (`RoslynCodeTaskFactory`) work the same way: the factory compiles the task in the AOT process and the
engine runs it in the task host. A third-party `ITaskFactory` must run in-proc and cannot be loaded; it fails with
**MSB4283** rather than crashing.

## Other host responsibilities

- **Toolset discovery.** A native MSBuild process inside a toolset directory is recognized as the MSBuild executable
  (`BuildEnvironmentHelper.TryFromNativeAotMSBuildProcess`), so worker nodes relaunch the native image and node reuse
  finds them.
- **`DOTNET_HOST_PATH`** is set to the layout's `dotnet` when unset (as `dotnet build` does), because tools such as
  `Csc` and the .NET task host locate the host through it and the current process is not `dotnet`.
- **`Microsoft.DotNet.Sdk.Root`** is published as an AppContext value so SDK components that locate the SDK from their
  own assembly location (empty under AOT) find it. Older builds of the workload resolver ignore that value; for those
  [`WorkloadSdkResolverAdapter`](WorkloadSdkResolverAdapter.cs) performs the resolver's own steps with the SDK
  directory supplied, and only when every internal member it needs has the expected shape.
- **Loggers.** MSBuild's own forwarding loggers are constructed directly. A custom `-logger` would need reflection
  and fails with an error.
- **Library defaults.** Native AOT publishing turns off a few library behaviors that task code relies on; the project
  restores the JIT defaults (System.Text.Json reflection serialization, `DefaultValueAttribute`, custom resource types).

## Verifying it

Equivalence with the JIT MSBuild was checked by building `dotnet new` templates from clean with both (same directory,
same arguments) and comparing a hash of every file under `bin/` and `obj/`; files that also differ between two JIT
builds (StaticWebAssets caches that embed timestamps) are excluded. All of these produced identical outputs: `console`,
`classlib`, F# and VB `console`, `web`, `webapi`, `mvc`, `webapp`, `blazor`, `worker`, `razorclasslib`, `xunit`,
`mstest`, `grpc`. Custom compiled tasks (with item metadata round-tripping), `TaskHostFactory`, and inline tasks of
every code type were checked as well, as were a cold NuGet restore, `.slnx` solutions, `-m` worker nodes and node
reuse, and `-bl`.

## Known limits

- **Apple linker ceiling.** On macOS, Apple's linker (`ld-prime`) fails with *"too many large addends"* once an object
  file carries too many distinct relocation addends; this image is close to that limit. That is why only assemblies
  that reflect over their own types are rooted whole (task assemblies, NuGet, JSON libraries) while everything else -
  including MSBuild's own trim-annotated assemblies - is trimmed normally, and why size-optimized LINQ stays on. The
  classic linker (`-ld_classic`) does not work with the runtime's Swift dependencies. LLVM `lld`
  (`-p:LinkerFlavor=lld`) is expected to avoid the limit but was not available to test.
- The image is bound to one SDK layout; a different SDK needs its own build (tasks of a mismatched layout still work,
  out of process, just slower).
- Custom loggers (`-logger` / `-distributedlogger` naming a non-MSBuild assembly) fail with the usual "logger not found"
  error; MSBuild's own loggers work by name. Project-cache plugins and custom BuildCheck checks load assemblies by
  reflection and are not supported in the AOT image.
- MSBuild server works: `-mt` (which implies the server unless `MSBUILDUSESERVER=0`) launches the native image as the
  server node. It gains less than it does for the JIT MSBuild, since the native image starts fast; see
  [the findings](../../documentation/aot/native-aot-msbuild-findings.md).
