# Target-only incremental build optimization roadmap

## Decision

Proceed with **small, compatibility-preserving target changes first**.
Do **not** propose the earlier coarse CoreBuild shortcut as a generally
compatible SDK optimization.

The adversarial pass reproduced seven incompatibilities in that shortcut,
including stale transitive binaries and ordinary SDK settings. Its earlier
OrchardCore 20.47-to-7.59-second result remains a useful performance experiment,
not proof that arbitrary SDK projects can safely bypass reference resolution.

A separate safe tier changes task conditions **inside the existing targets**.
It retains RAR, SourceLink defaults, target dependencies, BeforeTargets and
AfterTargets ordering, cached items, and FileWrites. In five-pair measurements
it reduced OrchardCore from **20.77 to 19.70 seconds** and removed **1,854 task
invocations**. Small-project elapsed differences were mostly noise; this
roadmap does not turn task-count reductions into promised wall-clock savings.

**Scope of this original roadmap:** targets/props authoring and existing tasks only. No engine changes,
new tracking service, watcher, replacement build host, or new task implementation.
The C# probe in this branch only reads binlog events.

**Implemented follow-up:** the later [RAR result-cache investigation](../../investigations/rar-result-cache.md)
goes beyond that target-only scope with an opt-in task implementation and a
private SDK hotpatch. It preserves target reachability and observes resolution
inputs rather than suppressing RAR blindly. Its measurements distinguish
discovery-heavy task savings from the normal Orchard whole-build result.

## Source basis: the whole stack, not just installed targets

Both source trees were fully checked out:

| Purpose | dotnet/dotnet VMR commit | Coverage |
| --- | --- | --- |
| Exact SDK 10.0.400 source | `14fbf8d5271c98133561eb55185fdb05b286f578` | 81,048 tracked files; SDK release-branch component set |
| Current whole VMR | `b75d52e8cf8e105291314663374d7a30f694db4d` | 183,649 tracked files; includes SDK, MSBuild, runtime, ASP.NET Core, WPF, WinForms and WindowsDesktop |

The exact SDK source manifest identifies SDK commit
`32593ca81f8aae7b0d41c1a7198529c3365106b8` and MSBuild commit
`b0e826c1f29e30dfe49535b4194f960cafbc3510`. The current VMR includes SDK
`6f18fbcb4c871fe1105f6ba9d4f8ed83499112ff` and MSBuild
`5ee62bf0fa0db8677723d09c3481bfde5fe0fbf7`.

The full manifests, component revisions and source-backed patches are in
[`scripts/no-op-build/roadmap`](../../../scripts/no-op-build/roadmap/).
The current-main patch was source-ported, not executed with an SDK 11 build.
All executable measurements below use SDK 10.0.400 on Windows x64.
Nothing in dotnet/dotnet was rebuilt.

## Was the historical RAR warning wrong?

**No, not for general assembly resolution.** It was not a claim that every
closed, fully described reference graph must always pay the same cost.

The original [MSBuild #2015](https://github.com/dotnet/msbuild/issues/2015)
explicitly explains unconditional RAR execution in terms of newly installed
targeting packs and GAC changes. The discussion then explored narrower options:

- [Initial exploration](https://github.com/dotnet/msbuild/issues/2015#issuecomment-303841027):
  already-resolved NuGet paths looked suitable for a shortcut, but arbitrary
  machine state makes a general total-input cache difficult.
- [Conflict-closure counterexample](https://github.com/dotnet/msbuild/issues/2015#issuecomment-303875723):
  package conflict resolution did not replace assembly dependency exploration
  and binding-redirect inference. Restricted opt-in caching was proposed.
- [Follow-up conclusion](https://github.com/dotnet/msbuild/issues/2015#issuecomment-306951485):
  simply skipping RAR was rejected because associated files and unknown
  consequences remained relevant.

The current source still exposes these contracts in
`src/msbuild/src/Tasks/Microsoft.Common.CurrentVersion.targets`:
`ResolveAssemblyReferences` produces primary/dependency/related/satellite/
serialization/scatter/copy-local items, redirects, conflicts and properties.
Its `AssemblySearchPaths` can include registry, GAC, candidate files, hint paths
and output directories. `SystemState.cs` caches assembly information; that is
not the complete resolved-target output contract.

The red-team reproduction demonstrates the concern with today's SDK:

```text
App --file Reference--> Lib.dll --assembly dependency--> Dep.dll
```

After Dep changes and Lib's project is rebuilt, Lib.dll can remain unchanged
while its adjacent Dep.dll changes. The original coarse native App build
succeeded and ran **old** code; stock RAR found/copied the new dependency and
the app ran **new** code. Checking the primary reference and previous copy
destination was insufficient.

**Invariant:** authoritative resolution, or a proven equivalent closed-world
contract, is required. A general RAR bypass is outside this proposal.

## Compatibility rules

1. Keep public target names, dependency chains, entry points and hook ordering.
   A target still running is insufficient if the items/properties its hooks
   consume disappear.
2. Do not replace a public target body with new `DependsOnTargets` helpers and
   assume BeforeTargets is preserved: those new dependencies run before the
   original BeforeTargets hooks.
3. A task may be elided only when its complete output/validation behavior is
   accounted for. File existence alone does not reconstruct task-computed items.
4. Preserve required-parameter errors, FileWrites and output inference.
   No new warnings; opt-in must not unexpectedly break WarnAsError.
5. Do not freeze a handwritten subset of SDK properties and call it the SDK's
   complete input contract. RuntimeHostConfigurationOption and assembly
   attributes are concrete examples where that failed.
6. Keep restore/evaluation separation, SourceLink, project-reference protocols,
   multitargeting, desktop and publish/pack semantics. Unsupported cases remain
   explicit rather than being removed from the matrix.

### Proposed switches

Default behavior remains unchanged:

```xml
<EnableIncrementalTargetOptimizations>true</EnableIncrementalTargetOptimizations>
```

The master switch opts into the two proven SDK leaf changes. Each feature
defaults to the master only when unset, so explicit opt-outs win:

```xml
<EnableEmptyPackTaskElision>false</EnableEmptyPackTaskElision>
<EnableStaticWebAssetsTaskElision>false</EnableStaticWebAssetsTaskElision>
```

The old laboratory CoreBuild experiment is **not** part of this switch.
It now requires both `NativeCoreBuild=true` and the explicit
`NativeCoreBuildContract=DeclaredInputsAndHooksV1` acknowledgment. That
acknowledgment is an audit assertion, not automatic verification of a project's
customizations. Without it the stock pipeline is used and a message explains
why. `NativeForceCoreBuild=true` selects the stock path.

## Work packages and expected savings

Paths below are relative to the VMR root.

| ID / disposition | Component and exact target(s) | Target-only change / contract | Savings and gate |
| --- | --- | --- | --- |
| T1 — ready candidate | SDK: `src/sdk/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Sdk.FrameworkReferenceResolution.targets`, `ResolveFrameworkReferences` | Guard ten GetPackageDirectory sites when their corresponding item list is empty. Keep the target and every nonempty call. | Orchard: 2,010 to 205 calls. Matrix: 5-9 calls avoided per eligible framework evaluation, depending on pack kinds. Output metadata parity required. |
| T2 — ready candidate | SDK/web: `src/sdk/src/StaticWebAssetsSdk/Targets/Microsoft.NET.Sdk.StaticWebAssets.targets`, `GenerateStaticWebAssetsManifest` | Put the development writer's existing freshness predicate on the task, in place. Do not split/reorder the public target. | Orchard: 49 of 89 writer calls eliminated; RCL/Web fixture: one eliminated. No-output/equality cases still run. |
| T3 — application-authoring candidate | Orchard: `OrchardCore.Application.Cms.Core.Targets.targets`, `CopyPackageTranslationFiles` | Replace scalar FullPath task batching with paired source/destination transforms, retaining Copy flags, or directory batching where required. | Earlier measured translation target: 407 to 72 ms, 2,394 fewer task calls. No reliable whole-build win demonstrated. Collision/failure-order coverage required before generalizing. |
| T4 — investigate, no claimed win | MSBuild: `src/msbuild/src/Tasks/Microsoft.Common.CurrentVersion.targets`, `FindReferenceAssembliesForReferences` | Reduce interpreted per-identity mapping only with duplicate/order/escaping/metadata parity. Keep ResolveReferences reachable and preserve Returns consumers. | About 3.2 cumulative seconds in the original Orchard trace. Compiled and guarded-scalar experiments did not demonstrate a win; do not promise this time as recoverable wall time. |
| T5 — contract work, not blanket skipping | SDK: `Microsoft.NET.GenerateAssemblyInfo.targets` (`GetAssemblyAttributes`, `CreateGeneratedAssemblyInfoInputsCacheFile`, `CoreGenerateAssemblyInfo`); `Microsoft.NET.Sdk.targets` (`_GenerateRuntimeConfigurationFilesInputCache`, `GenerateBuildRuntimeConfigurationFiles`); MSBuild `_GenerateCompileDependencyCache` | Preserve or move effective semantic-input calculation, then skip only file-producing work. Do not skip the calculation that detects changed command-line/item values. | Correctness prerequisite for broader boundaries. Existing hashes/writers are small relative to total build; no independent speedup promised. |
| T6 — constrained research | MSBuild RAR target plus SDK `ResolveLockFileReferences`, `_HandlePackageFileConflicts`, `ResolveTargetingPackAssets` | Explore a closed-path SDK reference contract or direct projection of already-resolved data, with fallback for file/search-path/COM/framework cases. Must reproduce every downstream output and diagnostic. | RAR was about 5.3 cumulative seconds over 202 projects. Not additive to wall time. No shipping bypass or target-only proof of general equivalence is supplied. |
| T7 — reject current general rollout | MSBuild `CoreBuild`, `ResolveReferences`, `Compile`, and SDK `_BlockWinMDsOnUnsupportedTFMs` | Redesign earlier boundaries only while maintaining hook/state contracts. Retargeting the WinMD validation alone does not restore arbitrary ResolveReferences/CoreCompile hooks. | Original lab 20.47 to 7.59 s is a ceiling experiment under a restricted workload, not a general compatibility result. |
| T8 — preserve existing specialized contracts | WPF `src/wpf/src/Microsoft.DotNet.Wpf/src/PresentationBuildTasks/Microsoft.WinFX.targets`: `MarkupCompilePass1/2`, `GenerateTemporaryTargetAssembly`; Razor SDK compilation/source-generator targets | Any future elision must retain generated Compile/BAML/FileWrites and pass-2 flags, local-type dependency discovery and design-time state. Keep existing task caches until that is proven. | No markup/razor task skip proposed. Matrix proves T1/T2 coexist with these builds, not that markup work is redundant. |

### Why T1 and T2 are credible

`GetPackageDirectory.ExecuteCore` returns `Output = Items` immediately for an
empty list. Its empty-list task outputs append no items. The patch merely avoids
constructing/invoking those tasks; it does not assume package immutability.

`GenerateStaticWebAssetsDevelopmentManifest.Execute` already skips when the
development output exists and is **strictly newer** than the build manifest,
before processing live assets. The patch lifts that exact predicate to the
task condition, retaining the original target body, merged assets, cached
items, FileWrites and surrounding hooks. Equality runs the task. Empty required
scalar parameters also run it so validation is not suppressed.

The installed MSBuild allowlist does not expose File.GetLastWriteTimeUtc as a
property function. The working patch uses GetLastWriteTime followed by
ToUniversalTime; it does not compare ambiguous local clock ticks. Paths are
normalized against MSBuildProjectDirectory inside the original target body,
after BeforeTargets hooks, rather than relying on a process working directory.
The final path-hardening fixture passed 24 stock/safe/opt-out cases, including
relative paths and apostrophes. The five-pair timing data predates this
semantically equivalent path qualification; no additional speedup is claimed.

Current VMR main additionally filters static-web-asset groups. The main port
retains that work and the filtered items unchanged. This is why replacing the
whole target with an older template would be an unsafe approach.

## Measured scenario/solution matrix

Windows x64, SDK 10.0.400, `-m:8`, MSBuild server disabled. Both variants use the
same source-backed private SDK; only the master flag differs. Five alternating
pairs per project entry and per genuine `.slnx` solution. Binlogs are separate.

Nine solutions contain 38 projects, including a reachable 24-project,
seven-level DAG. SourceLink remains at its SDK-default **true in 35 projects**;
only the explicitly named trusted counterpart disables it in three projects.
Build/runtime checks also verify source edits and deleted-output repair.

Representative **solution** rows below; Orchard is its real CMS project graph.
Both project and solution routes are preserved in the raw data.

| Scenario | Projects / framework scope | Stock median | Safe median | Tasks: stock to safe | Interpretation |
| --- | --- | ---: | ---: | ---: | --- |
| Default console/library chain | 3, net10.0, SourceLink on | 1.35 s | 1.35 s | 267 to 244 | No distinguishable elapsed win |
| Explicit trusted local counterpart | 3, net10.0, SourceLink off | 1.36 s | 1.37 s | 237 to 214 | No distinguishable elapsed win |
| Real solution DAG | 24, net10.0 | 2.83 s | 2.94 s | 2,142 to 1,930 | Task reduction; elapsed regression/noise must not be hidden |
| Multitarget library + app | 2 projects; net8.0 and net10.0 outputs | 1.33 s | 1.32 s | 263 to 240 | Small/noisy elapsed delta |
| Web SDK app | 1, net10.0 | 1.46 s | 1.48 s | 120 to 115 | No distinguishable elapsed win |
| Web + Razor/static assets | 2, net10.0 | 1.85 s | 1.86 s | 274 to 259 | No distinguishable elapsed win; HTTP assets correct |
| WPF | 1, net10.0-windows | 1.25 s | 1.25 s | 92 to 87 | No distinguishable elapsed win |
| WinForms | 1, net10.0-windows | 1.25 s | 1.23 s | 88 to 83 | Small/noisy elapsed delta |
| Framework library | 1, net472 | 1.11 s | 1.11 s | 69 to 69 | No applicable task reduction; RAR retained |
| OrchardCore CMS | 202-project closure, net10.0 | **20.77 s** | **19.70 s** | **23,697 to 21,843** | Measured 1.07 s / approximately 5.2% reduction |

Every no-op row retained **zero Csc calls**. RAR counts did not change: 202
remained in Orchard and one or more remained in each applicable fixture.
That is intentional compatibility, not a failed attempt to hide resolution.

### Additional protocol matrix

Four stock/safe protocol rows also passed without disabling SourceLink or
importing the coarse CoreBuild target:

| Protocol | Observed parity |
| --- | --- |
| Console `publish --no-restore -c Debug` | All 10 published paths and SHA-256 hashes match; published app runs correctly |
| Web/Razor `publish --no-restore -c Debug` | All 18 paths and hashes match; Production HTTP endpoint and published RCL static asset return expected bytes |
| Razor library `pack --no-restore -c Debug` | 12 entry names, assembly payload, static asset and full nuspec semantics match; no raw ZIP-byte claim |
| Real local Git/SourceLink commit change | Two real commits update SourceRevisionId, executed AssemblyInformationalVersion and embedded portable-PDB SourceLink URLs identically |

The Git fixture uses a synthetic remote only to select the normal SourceLink
provider; it never fetches/pushes or claims that generated URLs are reachable.
The protocol rows cover Debug framework-dependent output, not RID-specific,
self-contained, AOT, signing or every deployment mode. Evidence and a portable
runner are committed under the roadmap harness.

For small projects, budget **zero guaranteed wall-clock saving** from T1/T2.
For large SDK-heavy graphs, the measured task-count reduction scales with
framework evaluations, but wall time depends on graph shape, node reuse,
filesystem and task hosting. The Orchard 1.07-second result is not a promise
for every large solution.

## Red-team results and iteration

The original commit `c6e6728fb` reproduced **7/7** executable counterexamples:

| Finding | What broke | Current disposition |
| --- | --- | --- |
| PreBuildEvent hook ordering | A BeforeTargets hook generated a referenced project's source too late; native cold build failed CS2001, stock printed 42 | Architectural incompatibility retained as a lab opt-out requirement; no generic claim |
| Hook reachability/state | Warm native skipped validation and left AfterBuild with empty hook item/property and ReferencePath; stock ran the hook | Architectural incompatibility; not repaired by adding file names |
| Transitive binary reference | Primary Lib.dll unchanged, adjacent Dep.dll changed; native ran old code | Conservative opaque-reference fallback added; RAR is retained |
| Runtime option | ServerGarbageCollection=true did not update runtimeconfig | Reuse SDK effective runtime-configuration cache outside the skipped body |
| Company attribute | Ordinary Company property change left the compiled attribute stale | Pinned SDK generated-attribute inputs/flags added; arbitrary late attribute hooks remain outside the contract |
| Compile copy metadata | Never to PreserveNewest succeeded without requested Program.cs output; a later query returned the right item | Copy-affecting metadata added to declared inputs |
| Execution-time translations | BeforeBuild-created package mapping changed but the evaluation-time signature was empty | Execution-time leaf mapping hash added; helper invocation moved into public target body to preserve BeforeTargets ordering |

The hardened lab adds regression coverage, an explicit contract gate and
fallbacks. It is still **not SAFE**: matching these regressions is not proof
that its handwritten contract covers every SDK option or customization.
Historical timing tables refer to the original experiment and must not be
quoted as performance certification of the hardened implementation.

The safe-leaf tests independently cover stock/safe/per-feature opt-out,
BeforeTargets-mutated inputs, AfterTargets ordering, cached metadata,
FileWrites, equality, missing output/cache, required Source errors and paths
containing spaces/apostrophes. No target-output/state shortcut is involved.

## Shipping sequence and acceptance criteria

### Wave 1: small source-backed patches

- SDK PR for T1 with empty/nonempty/metadata-preservation tests and explicit
  switch precedence tests.
- SDK StaticWebAssets PR for T2 with before/after hooks, equal timestamps,
  changed manifests, absent outputs, empty input and required-parameter tests.
- Run the same build, project/solution, desktop, web, multitarget and protocol
  matrix on all supported platforms before changing defaults.
- Keep switches off by default initially. A later default-on decision requires
  upstream review and an explicit per-feature escape hatch.

### Wave 2: specialized authoring wins

- Submit translation batching to Orchard separately from SDK changes, with
  collision/ordering/read-only/failure behavior covered.
- Profile actual SDK cache writers, copying and reference mapping by scenario.
  Proceed only if a target-only rewrite demonstrates parity and measurable
  savings; reject a prettier rewrite that is slower.
- Keep existing runtime/assembly-info semantic invalidation intact while
  identifying narrower file-producing leaves.

### Wave 3: earlier SDK-owned boundaries, only after a contract exists

- Specify every required item/property/diagnostic, not just generated files.
- Keep public targets reachable or make the new behavior an explicitly
  separate, documented project contract with per-project fallback.
- Include negative resolution dependencies and search-state changes, or
  exclude those reference kinds and continue running RAR for them.
- Require adversarial custom hooks, late item production, generated sources,
  option/metadata changes, mixed project types and failed-build recovery.
- Do not introduce an engine change or tracking service to rescue this proposal.
  If the full contract cannot be expressed with targets and existing tasks,
  that candidate remains out of scope rather than becoming an implied promise.

## Reproducibility and review package

See the [roadmap harness](../../../scripts/no-op-build/roadmap/README.md) for
source checkout/patch commands, private SDK creation, scenario generation,
correctness tests, paired benchmarks and adversarial replay.

Committed evidence includes raw five-pair samples, both project/solution
routes, task counts, source/component provenance, safe-leaf contracts and
regression results. Raw binlogs and SDK copies remain session artifacts, not
repository payload. No upstream issue or PR is required to review this fork.
