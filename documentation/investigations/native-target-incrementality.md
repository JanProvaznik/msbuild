# Pushing native target incrementality on OrchardCore

Follow-up measured on 2026-09-14, using the same SDK 10.0.400, machine, and
OrchardCore revision `6587325652261a4606123d19502707dbcfc09ecd` as the
[initial investigation](no-op-orchardcore.md). The entry point is the complete
202-project CMS dependency closure, not the test solution.

## Result

**Target incrementality and dependency ordering are a substantial leverage
point.** A native-target prototype reduced the no-change build from **20.47 s
to 7.59 s**, with the MSBuild server disabled on both sides. With the existing
server enabled on both sides, the comparison was **17.24 s to 6.18 s**.

These are ordinary `dotnet build --no-restore` invocations. There is **no
external freshness gate, filesystem snapshot service, watcher, or replacement
build host** in this experiment. MSBuild's existing incremental target checks
decide whether the expensive target body runs.

| Configuration | Samples | Median elapsed | Range |
| --- | ---: | ---: | ---: |
| Stock, server off | 5 | 20.47 s | 19.27-21.22 s |
| Native targets, server off | 5 | 7.59 s | 7.30-8.73 s |
| Stock, server on | 5 | 17.24 s | 16.31-32.61 s |
| Native targets, server on | 5 | 6.18 s | 5.92-8.91 s |

All samples are retained, including the first server-on startup outlier.
Each iteration ran stock-off, native-off, stock-on, native-on sequentially.
Both sides used the same private SDK copy and dormant/active target imports.
Binlogs were captured separately, not charged to only one timing scenario.
This remains a shared machine, not a controlled benchmark host.

| Event count in the complete graph | Stock | Native |
| --- | ---: | ---: |
| Project evaluations | 404 | 404 |
| Executed targets | 31,252 | 13,507 |
| Task executions | 23,697 | **7,857** |
| Compiler invocations | 0 | **0** |
| ResolveAssemblyReference invocations | 202 | **0** |
| Copy invocations | 3,393 | **0** |
| GetPackageDirectory invocations | 2,010 | **205** |

That is 15,840 fewer tasks, approximately 67%. It does **not** reach zero total
tasks: framework/package preparation, project-reference negotiation, validation,
and input-list/configuration checks still run.

## The important change: move the boundary, not just add attributes

The common `CoreBuild` target dispatches a large `DependsOnTargets` chain.
Putting an incremental check around the target while leaving that chain
outside it is too late: dependencies have already executed.

The prototype keeps the essential project-reference preparation outside, but
puts the original expensive chain **inside the incremental target's body**:

```xml
<Target Name="_NativeBuildProducts"
        Inputs="@(_NativeInputs->Distinct());@(_NativeKnownProducts->Distinct());$(_NativeInputsCache)"
        Outputs="$(_NativeCompletion);@(_NativeMissingProducts)">
  <CallTarget Targets="$(CoreBuildDependsOn)" />
  <!-- Record actual products, then mark this payload complete. -->
</Target>
```

An up-to-date target does not execute that CallTarget task. On a miss it runs
the real existing dependency chain, rather than substituting fake successful
results or skipping the compiler unconditionally.

`BeforeBuild`/`AfterBuild` remain normal entry points. `GetTargetPath` is
available outside the skipped products body, so existing callers still receive
the project's output path and framework metadata. Query targets are not
globally disabled; they can still compute their results when requested.

This is a new **authoring boundary using existing engine machinery**, not a
production-ready universal `CoreBuild` replacement. The declared input and
output contract must be valid for the workload.

## Discoveries that made a material difference

### An empty PreBuildEvent pulled reference resolution ahead of the check

The SDK target `_BlockWinMDsOnUnsupportedTFMs` has:

```xml
AfterTargets="PreBuildEvent"
DependsOnTargets="ResolveReferences"
```

The first prototype kept PreBuildEvent before the incremental boundary.
Consequently, **RAR still ran in every project even when the products target
was skipped**. Keeping the ordinary PreBuildEvent inside the dirty payload
removed this hidden prerequisite path. Nonempty pre/post-build command
properties select the stock pipeline instead of reordering those commands.

This is a concrete dependency-ordering issue, not evidence that the engine is
ignoring valid Inputs/Outputs.

### FileWrites is not an exact required-output contract

Treating every entry in `*.FileListAbsolute.txt` as a required output caused
permanent misses. Examples included optional `rpswa.dswa.cache.json`,
`rjimswa.dswa.cache.json`, static-web-assets pack/development files and CSS
products that were registered but never created.

After a successful payload, the prototype records the existing products from
the standard FileWrites record. Later deletion of one of those recorded
products makes the native target dirty. A failure invalidates the completion
stamp rather than retaining a successful-looking result.

This requires a small per-project output-list cache. It is ordinary target
state under `obj`, not a scan of the repository or package cache.

### Project references need to propagate more than reference-assembly changes

Using only the reference assembly would miss content-only changes that must be
copied into consumers. The prototype adds `NativeCoreCompletion` metadata to
the existing project output items. A producer's successful changed payload
updates that completion file; consumers include it in their native input check.

A method-body edit still recompiles only the producer when its reference
assembly is unchanged. A content-only edit runs the necessary copy preparation
without invoking a compiler. References that do not participate force the
ordinary payload rather than silently losing transitive content.

### Accidental metadata batching can make the check expensive

An early prototype used an unqualified `%(Identity)` condition on an Include
that mixed item lists with scalar paths. On one real project, this produced
**2,781 items with only 557 distinct identities**. Scalar project/import paths
were repeated 553 times.

Removing that unnecessary condition and deduplicating the native check inputs
reduced cumulative `_NativeBuildProducts` check time from about **31.8 s to
2.5 s** across the graph. The corresponding instrumented full build fell from
about **16.2 s to 10.1 s**. This was a prototype authoring mistake, not a claimed
pre-existing OrchardCore bug.

### An existing SDK task was called thousands of times with empty input lists

`ResolveFrameworkReferences` invokes GetPackageDirectory for ten pack kinds:
targeting packs, apphosts, single-file hosts, crossgen2, IL compilers, tool
shims, COM/IJW hosts and runtime packs.

The private SDK hotpatch adds a condition at each task site:

```xml
Condition="'$(NativeSkipEmptyFrameworkPacks)' != 'true' or '@(TargetingPack)' != ''"
```

The condition uses the corresponding input item type at each site.
An empty input produces no output items in the original task, so this does not
require reconstructing computed output state. The workload still executes the
205 nonempty calls. All eight probed output item types and their metadata were
identical with the flag off and on.

This change is individually simple and does not depend on a tracking system.
It is task elision for empty work rather than timestamp-based incrementality.

## Translation copying: stronger than a naive PreserveNewest rewrite

Adding a direct per-file Inputs/Outputs mapping can make translation copying
execute zero tasks, but it is not equivalent to the old Copy task:
`SkipUnchangedFiles` checks sizes and exact timestamps, whereas a native target
accepts a destination newer than its source. A package downgrade could therefore
leave newer translations from the previous package in place.

The integrated leaf instead:

1. Adds the source-to-destination package mapping to the root's native input
   signature, so a changed mapping invalidates the cooperating core boundary.
2. Uses source files, destination files, missing destinations and the core
   completion file as its native freshness inputs.
3. Runs one ordinary batched Copy, retaining SkipUnchangedFiles and read-only
   behavior, only when dirty; then touches the leaf completion stamp.

This handles the tested backdated package mapping, newer corrupted destination
and deleted destination cases. The leaf's stamp is excluded from core products;
otherwise its AfterBuild update would invalidate the core, which would then
invalidate the leaf again.

Ordinary timestamp limitations remain: arbitrary in-place edits with preserved
or backdated timestamps are not generally detected. No content-addressed cache
claim is made. Removed translation sources leave prior destinations in place,
as the original target does.

The translation fixture also caught output/input glob contamination: its
generated `Localization` directory initially entered the default None items.
Excluding that generated directory avoids an unnecessary extra settling build.

## Correctness evidence

The executable two-project contract fixture checks actual runtime output as
well as task counters:

| Change | Compiler invocations | Observed behavior |
| --- | ---: | --- |
| No change | 0 | 0 RAR, 0 Copy; AfterBuild observer still runs |
| Producer method body | 1 | Reference assembly unchanged; consumer runs new implementation |
| Transitive content only | 0 | Consumer receives updated content |
| Source addition/removal | 2 | Both sides see the changed public API |
| Missing producer output DLL | 0 | Repaired from intermediates |
| Missing consumer copy | 0 | Correct dependency DLL restored |
| DefineConstants change | 2 | Runtime behavior changes to the requested configuration |
| Request runtimeconfig generation | 0 | Newly requested runtime output created |
| Deliberate compiler error | Failure | Completion stamps invalidated; recovery performs work |
| Clean, then Build | 2 | Native state cleaned and both projects compiled |
| NoBuild=true | Failure | Original NETSDK1085 preserved |
| CI / non-participating reference | Stock path | RAR executes instead of an unsafe skip |

On the actual OrchardCore graph, a module manifest edit executed **4 compiler
and 4 RAR tasks**, not 202 RAR tasks. The subsequent no-change build returned
to **0 compiler and 0 RAR tasks**. Deleting a module's output DLL caused its
affected dependency chain to run, restored identical bytes and used **0
compiler tasks**. Source mutations were undone.

The translation fixture additionally covers package downgrade, adding/removing
sources, newer destination corruption, missing output and return to a zero-Copy
no-op. All fixture artifacts and binlogs are retained outside the repository;
the compact result tables are committed.

## Controls, limits, and what remains

Static graph mode did not improve this prototype. In a separate three-pair
comparison, dynamic builds had an **8.06 s** median and static graph builds
**13.54 s**. The standard graph target propagation schedules additional query
targets that the dynamic no-op path avoids. This experiment does not modify
the graph target protocol to hide that cost.

The zero-task evaluation-only control from the original SDK was about 4.1 s.
It is a lower bound, not a correct build and not a same-private-SDK matched
measurement. The current native prototype is much closer to it, but still pays
project-reference negotiation and thousands of preparation/validation tasks.

No engine changes, new P/Invoke tracker, filesystem watcher, or custom build
host were introduced. An SDK directory was copied for the ten guarded task
sites; dotnet itself was not rebuilt and the installed SDK was not patched.

This is deliberately **opt-in** and bounded to the audited local-build
contract. SourceLink-enabled, CI, design-time/IDE, pre/post-build command, and
compiler-command-line modes use the stock pipeline. Other custom authoring,
external/non-file state, or unlisted SDK settings need an explicit declaration
or opt-out. The input inventory is not proof of purity for arbitrary tasks.
The full limitations and extension items are in the
[native experiment README](../../scripts/no-op-build/native/README.md).

The result corrects the initial assessment: the compiler's incrementality was
working, but **the incrementality boundary around the preparation was too late**.
Native MSBuild mechanisms can avoid a large part of that work once the
authoring expresses a valid earlier boundary.

## Reproduce and inspect

- [Native targets, private SDK setup, and measurement scripts](../../scripts/no-op-build/native/README.md)
- [Five-pair raw samples](../../scripts/no-op-build/native/results/measurements.csv)
- [Summary](../../scripts/no-op-build/native/results/summary.csv)
- [Exact task counters](../../scripts/no-op-build/native/results/counters.json)
- [Core contract results](../../scripts/no-op-build/native/results/core-contract.json)
- [Translation contract results](../../scripts/no-op-build/native/results/translation-contract.json)
- [Real Orchard invalidation](../../scripts/no-op-build/native/results/orchard-invalidation.json)
- [Prototype stages](../../scripts/no-op-build/native/results/prototype-stages.json)
- [Graph comparison](../../scripts/no-op-build/native/results/scheduling.csv)
