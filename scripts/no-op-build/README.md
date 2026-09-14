# No-op build laboratory

**Preferred follow-up:** [native target incrementality](native/README.md) uses
ordinary `dotnet build`, native `Inputs`/`Outputs`, and dependency ordering.
The external freshness gate documented below is retained as an earlier
experiment, not the recommended direction.

This is an opt-in experiment, not a change to MSBuild's default behavior.
It uses the installed SDK's engine and binary-log reader, without rebuilding
dotnet, replacing the installed SDK, or adding NuGet dependencies.

## Reproduce

Use PowerShell 7 on Windows, a .NET 10 SDK, and a restored/built workload.
Do not run other builds against that workload while measuring.

```powershell
# OrchardCore commit used for the investigation:
# 6587325652261a4606123d19502707dbcfc09ecd
# Clone/build into a disposable directory, not your working checkout.
git clone https://github.com/OrchardCMS/OrchardCore.git C:\lab\OrchardCore
git -C C:\lab\OrchardCore checkout 6587325652261a4606123d19502707dbcfc09ecd
Push-Location C:\lab\OrchardCore
dotnet build src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj -m:8 -v:q
Pop-Location

# From this MSBuild checkout. OutputDirectory must be outside the workload.
.\scripts\no-op-build\Measure-NoOp.ps1 `
    -Root C:\lab\OrchardCore `
    -Project src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj `
    -Sdk 'C:\Program Files\dotnet\sdk\10.0.400' `
    -OutputDirectory C:\lab\measurements-1 `
    -Iterations 5 `
    -EnableExperimentalGate
```

Omit `-EnableExperimentalGate` to measure only the stock no-restore build and
evaluation-only control. The flag explicitly accepts the gate's restricted
file-build contract below. Use a new output directory for each measurement set.
Elapsed values in `measurements.csv` include process startup. Binlogs are
captured separately to avoid charging only one timing scenario for logging.

`BuildProbe.csproj` is intentionally built independently of this repository's
Arcade/preview-SDK infrastructure by `Invoke-Probe.ps1`; it references assemblies
from the SDK under test. The tested SDK and runtime are selected explicitly.
The application runs under that SDK's `MSBuild.deps.json` and
`MSBuild.runtimeconfig.json`, so it can read current binary-log event types.

## Individual probes

```powershell
.\scripts\no-op-build\Invoke-Probe.ps1 `
    -Sdk 'C:\Program Files\dotnet\sdk\10.0.400' `
    -ProbeArguments @('gate', 'C:\lab\measurements-1\gate-config.json')

.\scripts\no-op-build\Invoke-Probe.ps1 `
    -Sdk 'C:\Program Files\dotnet\sdk\10.0.400' `
    -ProbeArguments @('analyze', 'C:\lab\measurements-1\stock.binlog', 'C:\lab\stock-summary.json')
```

The JSON configuration supports `Properties` (MSBuild `Name=Value` strings),
`ExternalRoots`, `MissBinlog`, and `EvaluationBinlog`. Keep state, logs, and
results outside every observed root. `EvaluationBinlog` makes the gate's
zero-task graph construction observable in a real binlog; leave it unset for
unlogged performance comparisons.

For a **dispatch-only control**, import `EmptyGraph.targets` with both
`CustomAfterMicrosoftCommonTargets` and
`CustomAfterMicrosoftCommonCrossTargetingTargets`, then run
`-graphBuild -t:ProbeEmpty -m:8`. This is deliberately not a build or an
up-to-date check. It measures evaluation plus scheduling empty targets through
the real project-reference graph.

## Target hotpatch

The concrete Orchard translation-copy candidate is in `hotpatches`. It changes
task batching, not whether translations are checked/copied:

```powershell
$patch = (Resolve-Path .\scripts\no-op-build\hotpatches\inject.targets).Path
Push-Location C:\lab\OrchardCore
dotnet build src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj --no-restore -m:8 -v:q `
    "-p:CustomAfterMicrosoftCommonTargets=$patch" -p:TargetExperimentTranslations=true
Pop-Location
```

Warm the exact import configuration before timing: adding a new import can
legitimately invalidate compilation. The paired control uses the same import
with `TargetExperimentTranslations` unset. Do not combine these comparisons
with gate measurements without establishing a new gate baseline.
`translations-source.patch` is the corresponding minimal OrchardCore source
change. No package-cache or SDK edits are required.

This reduced the translation target from about 407 ms to 72 ms and removed
2,394 task invocations, but produced no distinguishable whole-build speedup
in three interleaved samples. The measured input mapping had no destination
collisions. Aliased directories, conflicting destinations, and failure-side
effect ordering need broader coverage before claiming universal equivalence.

## Experimental gate contract

On every invocation the gate:

1. Reevaluates the real, complete project graph with `-graphBuild:NoBuild`.
2. Compares an environment/configuration/tool fingerprint and filesystem
   snapshot against the last successful ordinary build.
3. Returns without running any targets or tasks only when both match.
4. Otherwise invokes the normal `Build` target without restore. A failed build
   removes the previous success state; a successful build establishes a new
   snapshot unless source/dependency inputs changed during that build.

The snapshot includes the entire workload tree (including `bin`, `obj`, and
`.git`), the tested SDK, explicit external roots, and all package directories
referenced by the workload's `project.assets.json` files. It records file
lengths and UTC last-write timestamps, directory existence, and additions and
deletions. Package-root discovery is cached, **not package file freshness**:
every hit enumerates the files in those directories again. The generated
configuration also includes the installed targeting packs, runtimes and host.
Changed asset files invalidate the snapshot and refresh the package closure.

This is intentionally conservative about files, but **cannot establish that
arbitrary MSBuild authoring is pure**. It is only valid under these assumptions:

- All relevant source/import/tool/output state is inside the observed roots.
  Add linked sources, external imports, native tools, custom intermediate/output
  roots, and any other external dependencies to `ExternalRoots`.
- Targets have no required effects on every invocation, and do not depend on
  network state, the clock, registry state, services, or untracked processes.
- Builds are quiescent, without concurrent writers. This is not a transactional
  filesystem snapshot or a build lock respected by other processes.
- File changes update timestamps or sizes. Same-size changes with deliberately
  preserved timestamps are not detected. This is not a content-addressed cache.
- A fresh `Build` is the requested operation. This does not implement restore,
  clean, rebuild, publish, pack, design-time builds, or return cached target
  items/properties to another MSBuild caller.

Reparse points are rejected rather than silently following untracked trees.
State/configuration parse failures and I/O errors fail visibly.
The state-file lock prevents two instances of this gate from writing the same
cache concurrently. Changing this probe's binary invalidates its cache.

An edited input causes a **whole ordinary incremental build**, not just an
affected subgraph. A miss is slower than stock: it pays graph evaluation and
snapshot costs before the ordinary build reevaluates its projects. This
prototype optimizes the no-change case only. Output equivalence and gate
invalidation checks on OrchardCore do not prove compatibility with arbitrary
custom targets.

## Reading the results

`analyze` records every `TaskStarted`/`TaskFinished` and target/evaluation event
using the exact SDK reader, without millisecond truncation. Task and evaluation
times are cumulative across nodes. `MSBuild` and `CallTarget` include nested
work/waits and **must not** be added to wall time or interpreted as scheduler CPU
time. `nonOrchestrationTaskSeconds` excludes those two but remains cumulative.

See [the measured investigation](../../documentation/investigations/no-op-orchardcore.md)
for the workload, comparisons, controls, and limits.
