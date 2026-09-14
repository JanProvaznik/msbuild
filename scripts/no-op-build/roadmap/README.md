# Target-only roadmap and compatibility matrix

**Start here rather than enabling the coarse CoreBuild prototype.**
The [roadmap](../../../documentation/specs/proposed/target-incrementality-roadmap.md)
separates measured, hook-preserving SDK changes from laboratory shortcuts that
failed adversarial review.

## Contents

- `patches/sdk-safe-task-elision.patch`: source patch for the VMR commit matching SDK 10.0.400.
- `patches/sdk-safe-task-elision-main.patch`: source-reviewed port to the recorded current whole VMR.
- `source-provenance.json`: both VMR SHAs and current component revisions.
- `New-SafeTargetSdk.ps1`: copies an installed SDK and overlays the two patched VMR target files.
- `Validate-SafeLeaves.ps1`: public hook/order, cached-item, FileWrites, timestamp, required-error and opt-out checks.
- `scenarios/`: fresh nine-solution fixture generator, executable correctness harness, and paired project/solution benchmarks.
- `results/`: compact evidence. SDKs, binlogs and generated projects are kept outside the checkout.
- `rejected/reference-mapping.targets`: guarded target-only mapping experiment. Focused parity passed, but no workload performance win was established. It is not part of the safe switch.

Only XML task conditions change in the safe SDK. No public target names or
dependency lists are replaced. No engine or task C# implementation is changed.

## Source-backed SDK copy

Use PowerShell 7 and an installed SDK 10.0.400. The example paths are disposable
external artifact directories:

```powershell
git clone --filter=blob:none https://github.com/dotnet/dotnet.git C:\lab\dotnet-vmr
git -C C:\lab\dotnet-vmr checkout 14fbf8d5271c98133561eb55185fdb05b286f578

# Run from this msbuild fork checkout.
$patch = (Resolve-Path .\scripts\no-op-build\roadmap\patches\sdk-safe-task-elision.patch).Path
git -C C:\lab\dotnet-vmr apply --check $patch
git -C C:\lab\dotnet-vmr apply $patch

.\scripts\no-op-build\roadmap\New-SafeTargetSdk.ps1 `
    -VmrSource C:\lab\dotnet-vmr `
    -OutputDirectory C:\lab\safe-sdk
```

Do not apply both patch files. The main port targets
`b75d52e8cf8e105291314663374d7a30f694db4d`; its changed target data flow was
inspected, but an SDK 11 binary build was not performed. The setup helper
deliberately requires the tested SDK snapshot.

The entire SDK directory is copied because targets import sibling Roslyn,
NuGet pack targets and RID/version files. Installed runtimes and targeting packs
are reused through `NetCoreRoot`. There is no dotnet source build.

### Switches

Opt in with `EnableIncrementalTargetOptimizations=true`. Individual explicit
opt-outs override the master:

```text
EnableEmptyPackTaskElision=false
EnableStaticWebAssetsTaskElision=false
```

The master does **not** activate the old native CoreBuild shortcut.
With no master or feature switches, the patch takes the original task paths.

## Generate and validate the matrix

```powershell
.\scripts\no-op-build\roadmap\scenarios\Setup-ScenarioMatrix.ps1 `
    -OutputRoot C:\lab\scenario-matrix

.\scripts\no-op-build\roadmap\scenarios\Test-ScenarioMatrix.ps1 `
    -Registry C:\lab\scenario-matrix\registry.json

.\scripts\no-op-build\roadmap\scenarios\Test-ScenarioMatrix.ps1 `
    -Registry C:\lab\scenario-matrix\registry.json `
    -SdkConfiguration C:\lab\safe-sdk\configuration.json `
    -BuildProperties @('EnableIncrementalTargetOptimizations=true') `
    -Variant safe

.\scripts\no-op-build\roadmap\Validate-SafeLeaves.ps1 `
    -SafeSdk C:\lab\safe-sdk `
    -ArtifactsDirectory C:\lab\safe-leaf-contract
```

There are nine real `.slnx` solutions and 38 projects: default and explicitly
trusted console/library chains, a reachable seven-level 24-project DAG,
multitargeting, Web, Razor/static assets, WPF, WinForms and net472.
The eight non-trusted scenarios retain default SourceLink=true.

The correctness harness builds both solution and project entries, changes a
source, checks changed output/runtime behavior, deletes an output, verifies
repair, restores sources, and returns to a built baseline. Web/RCL outputs are
served and queried over loopback; desktop fixtures have a headless verification
argument. Missing platform prerequisites remain explicit registry rows.
The net472 row needs installed Framework reference assemblies.

Restore is separate from Build. The stock harness first attempts the expected
no-restore build and only restores after missing-assets evidence.
No additional test packages or feeds are introduced by the scenario generator.

## Measure without confusing counts and wall time

```powershell
.\scripts\no-op-build\roadmap\scenarios\Measure-ScenarioMatrix.ps1 `
    -Registry C:\lab\scenario-matrix\registry.json `
    -SdkConfiguration C:\lab\safe-sdk\configuration.json `
    -OutputDirectory C:\lab\safe-measurements `
    -Iterations 5 `
    -OrchardRoot C:\lab\OrchardCore
```

`OrchardRoot` is optional and must already contain the restored/built pinned
workload described in the original investigation. Its entry is the full CMS
project-reference closure, not the entire Orchard test solution.

Run one workload at a time on a quiescent machine. The script warms both
variants, alternates their order, times unlogged invocations, and collects
separate binlogs. MSBuild server is explicitly off and both variants use the
same private SDK. The standard project and solution routes are measured
separately. Use a fresh output directory for every run.

The committed five-pair data shows approximately 1.07 s saved on Orchard,
but little/no reliable small-project wall-time saving. The 24-project solution
showed a small elapsed regression despite fewer tasks. These observations are
retained, not filtered out.

## Adversarial interpretation

The original coarse native boundary reproduced seven failures, including
ordinary runtimeconfig/assembly/copy settings and an unchanged primary binary
whose transitive dependency changed. General RAR elimination is not established.

The retained native lab now requires explicit
`NativeCoreBuildContract=DeclaredInputsAndHooksV1` acknowledgment in addition to
`NativeCoreBuild=true`. It adds conservative fallbacks and known regression
fixes, but that is not a certificate for arbitrary SDK/custom target contracts.
Its old timing data is historical, not a safe-tier performance claim.

The safe leaf approach intentionally leaves authoritative RAR execution and
the SDK's semantic input caches in place. See the roadmap's staged acceptance
criteria before proposing broader optimizations upstream.

### Replay the counterexamples and mitigations

```powershell
$native = (Resolve-Path .\scripts\no-op-build\native).Path
.\scripts\no-op-build\roadmap\red-team\Replay-Review.ps1 `
    -NativeTargetsDirectory $native `
    -ArtifactsDirectory C:\lab\red-team-current `
    -NativeExtraArgs @('-p:NativeCoreBuildContract=DeclaredInputsAndHooksV1')
```

The runner refuses an inactive native boundary, so stock fallback cannot be
mistaken for a fix. The expected current outcome is that cases 3-7 match stock,
while cases 1-2 still reproduce the architectural hook/order incompatibilities.
A reproduced *known* counterexample is recorded as data, not treated as a runner
failure. Inspect `results.json`, not only the process exit code.

The original seven failures and current mitigation results, target hashes,
templates, and severity/disposition report are in
[`red-team`](red-team/README.md). To replay the original implementation, extract
`scripts/no-op-build/native` from commit `c6e6728fb` into a separate directory,
pass that directory, and omit the newer contract acknowledgment.
