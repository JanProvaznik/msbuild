# OrchardCore no-change build overhead

Measured on 2026-09-14. This investigation changes no production MSBuild defaults.

## Result

The ordinary build already does **zero compilations and zero observed file
length/mtime changes**, but still executes **23,697 tasks**. In the final
repeatable run, its median elapsed time was **19.97 s**.

An explicitly opt-in, file-state gate reduced that to **6.71 s end-to-end,
zero targets and zero tasks**, including fresh evaluation of the real graph on
every invocation. That is **2.98x faster / 66% less elapsed time**, not a
production-safe general-purpose replacement for `Build`.

The same graph's evaluation-only lower bound was **4.11 s**. The remaining
approximately 2.6 s in the gate is mostly checking the filesystem snapshot,
not scheduling build work.

## Workload and measurement method

| Item | Value |
| --- | --- |
| Repository | OrchardCMS/OrchardCore |
| Revision | `6587325652261a4606123d19502707dbcfc09ecd` |
| Entry point | `src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj` |
| Scope | Complete CMS project-reference closure, 202 project paths; not the entire test solution |
| Configuration / TFM | Debug / net10.0, workload defaults |
| SDK / MSBuild | .NET SDK 10.0.400 / MSBuild 18.9.6+14fbf8d52 |
| OS | Windows 11 Enterprise, x64 |
| CPU visible to OS | AMD EPYC 7763, 8 cores / 16 logical processors |
| RAM | Approximately 64 GiB |
| Storage | Workload and generated outputs on local C: |
| Parallelism | `-m:8` unless stated otherwise |
| Initial restore + build | 164.19 s, successful; excluded from no-op measurements |
| Compilation policy | Original analyzers, source generators, and build authoring retained |

The workload was cloned into an isolated session directory. The installed SDK
and package cache were not edited. There was no rebuild of dotnet or MSBuild.
The small probe was compiled against the tested SDK's MSBuild assemblies.

Timed samples have no binlog. Separately collected binlogs supply the task
counts and attribution. Timings include process startup; the gate also reports
its internal phase timings. Samples are sequential, not concurrent builds.

The machine is shared, not a controlled benchmark machine. Earlier exploratory
stock samples ranged from 21.23 to 25.34 s, versus 19.61 to 21.38 s in the final
run. Prefer the final same-harness comparison below rather than comparing a
best optimized sample against the slowest earlier baseline.

## Measurements

### Final reproducible harness run

| Scenario | Runs | Median elapsed | Range | Actual build/up-to-date check? |
| --- | ---: | ---: | ---: | --- |
| Stock, no restore, m8 | 3 | 19.967 s | 19.609-21.379 s | Normal incremental Build |
| Evaluation-only control | 3 | 4.109 s | 4.080-4.118 s | No; lower bound only |
| Experimental file-state gate | 3 | 6.712 s | 6.639-6.757 s | Yes, only within its explicit restricted contract |

The gate's internal median was 6.445 s, plus launch/wrapper overhead to get
6.712 s. Its internal phases were approximately:

| Phase | Time |
| --- | ---: |
| Fresh full graph evaluation | 3.90 s |
| Read prior metadata manifest | 0.59 s |
| Enumerate current files/directories | 1.84 s |
| Compare snapshots and other overhead | 0.11 s |

The snapshot contained **142,167 file/directory entries**, covering the workload
including outputs, SDK, runtimes, targeting packs, and referenced package
directories. It does not merely compare source timestamps with a primary DLL.

### Scheduling and process controls

| Experiment | Median elapsed | Interpretation |
| --- | ---: | --- |
| Ordinary no-restore build, m1 | 34.06 s | Serializing the existing work is worse |
| Ordinary graph build, m8 | 23.92 s | Graph scheduling alone did not improve the early baseline |
| Restore-inclusive build, m8 | 26.87 s | Restore is additional cost, not the whole explanation |
| Graph dispatch of an empty target | 5.78 s | 0 tasks, 407 target executions; diagnostic control only |
| MSBuild server off, interleaved | 20.44 s | Four samples |
| MSBuild server on, interleaved | 16.77 s | Four paired samples; about 18% faster, same task count |

The empty-target control still evaluates the actual graph and dispatches
targets to build nodes. Its log contained 459 evaluations versus 404 for
graph construction alone: executing the graph introduces some additional
evaluations. It is a lower-bound control, not a promise that all scheduling
costs in the normal build are exactly `5.78 - 4.11` seconds.

The server experiment used `MSBUILDUSESERVER=1` with `dotnet build`, not
`UseSharedCompilation`. The latter controls the compiler server and would not
explain an improvement in a build with no compiler invocations.
Server reuse helps process/JIT/evaluation/task initialization overhead, but
does **not** remove the 23,697 tasks. It is a useful existing-tool improvement
without the gate's custom-authoring assumptions.

## What the stock build is doing

A representative stock binlog contained:

| Metric | Value |
| --- | ---: |
| Unique executed project paths | 202 |
| Evaluations | 404 |
| Executed targets | 31,252 |
| Skipped-target events | 29,796 |
| Task executions | 23,697 |
| Csc / Vbc / Fsc | 0 |
| Exec | 0 |
| Source-tree file length/mtime changes after an ordinary no-op build | 0 |

The doubled evaluation count is not automatically an engine bug. For example,
the Facebook module was evaluated once without `TargetFramework` (outer
build), then with `TargetFramework=net10.0` (inner build).

Selected task costs from the exact SDK binary-log reader:

| Task | Invocations | Cumulative time |
| --- | ---: | ---: |
| ResolveAssemblyReference | 202 | 5.294 s |
| Copy | 3,393 | 1.188 s |
| ResolvePackageFileConflicts | 202 | 0.970 s |
| ResolvePackageAssets | 202 | 0.651 s |
| WriteLinesToFile | 689 | 0.638 s |
| ProcessFrameworkReferences | 201 | 0.512 s |
| DefineStaticWebAssets | 534 | 0.487 s |
| DefineStaticWebAssetEndpoints | 623 | 0.355 s |
| AssignTargetPath | 1,294 | 0.188 s |
| GetPackageDirectory | 2,010 | 0.184 s |

There are also **1,776 MSBuild tasks and 404 CallTarget tasks**. Their inclusive
durations total much more than wall time because they include nested builds
and waiting. Calling that total "scheduler overhead" would be incorrect.
All non-orchestration tasks together account for 14.64 cumulative seconds,
also not a wall-clock partition because nodes run concurrently.

Target-side item/property processing is real work too. The target analysis
found roughly 3.06 cumulative seconds in
`FindReferenceAssembliesForReferences`, in addition to RAR itself.
`_CleanGetCurrentAndPriorFileWrites`, project-reference protocol,
package resolution, and static-web-assets processing execute even though no
compiler needs to run.

Orchard's `OrchardCoreEmbedModuleAssets` used about 0.24 cumulative seconds.
`CopyPackageTranslationFiles` used about 0.45 cumulative seconds. These are
worth improving, but neither explains a 20-second no-op by itself.

The bundled binlog viewer reported an "unknown event type" compatibility
warning. It was **not a build warning**. The probe reads with the same SDK that
wrote the logs: the stock build had zero errors and zero actual warnings.
It also avoids truncating every short task's duration to integer milliseconds.

## Why Inputs/Outputs everywhere is not sufficient

MSBuild target incrementality is not whole-project memoization.
Many targets calculate **in-memory** items/properties needed by subsequent
targets: resolved references, transitive content, resource identities, assembly
attributes, and static-web-assets metadata. Those values are not generally
available just because yesterday's DLL exists.

In addition, a target's dependencies execute before its timestamp check.
Adding `Inputs` and `Outputs` to `Build` does not turn its dependency chain
into a project-level fast-up-to-date check. Blindly overriding or disabling
reference-resolution/asset targets may be faster but loses required state.

The observed case is therefore mainly **repeated SDK/common-target execution
and its orchestration**, rather than broken compiler incrementality or a
custom target recompiling Orchard on every call. Scheduling and process
overhead contribute, but an empty dispatched graph is far cheaper than the
existing no-op target graph.

## Actual target hotpatches

Two independently opt-in target experiments were run on the complete workload,
with an empty injection control and an untimed warm-up for every configuration.
The first import change legitimately triggered compilation (103 seconds),
which was excluded. All four measured configurations subsequently had zero
compiler invocations.

| Variant | Median wall time, 3 interleaved runs | Tasks | Translation target | Reference-mapping target |
| --- | ---: | ---: | ---: | ---: |
| Same-import stock control | 20.57 s | 23,697 | 407 ms | 3,207 ms |
| Translation directory batching | 20.82 s | 21,303 | 72 ms | 3,042 ms |
| Compiled reference mapper | 20.83 s | 23,899 | 580 ms | 3,193 ms |
| Both | 20.10 s | 21,505 | 66 ms | 2,820 ms |

Target times come from separate instrumented runs and are cumulative.
The small wall-time differences are within the observed noise; **none of these
target patches demonstrated a reliable whole-build speedup**.

The translation patch is nevertheless a concrete authoring improvement:
`SourceFiles="%(PackageTranslationFiles.FullPath)"` causes a separate Copy task
for each file. Changing it to an item transform retains directory batching
but passes the entire directory's sources to one task. On this workload,
2,415 translation Copy tasks became **21**, with all 2,415 freshness checks
retained. Total Copy tasks fell from 3,393 to 999. The target itself became
approximately **82% cheaper**. This is not enough to fix the overall problem.
The [hotpatch and minimal source diff](../../scripts/no-op-build/hotpatches/)
are included.

The mapper experiment replaced interpreted per-item mapping in
`FindReferenceAssembliesForReferences` with a compiled task, retaining a stock
fallback for duplicates, relative paths and expression-sensitive values.
Twelve focused equivalence cases passed, but task marshaling and the retained
defaulting ItemGroup largely erased the potential gain. This candidate is
**not recommended** based on the real-workload results. Its source and focused
fixtures remain in the session's `files\target-experiments` directory.

A simpler scalar-metadata rewrite was rejected before workload timing because
duplicate identities changed item ordering and `OriginalPath` semantics.
Blind RAR skipping was also rejected: its existing cache stores assembly
metadata, not the complete resolved target output state.

Translation fixtures covered unchanged inputs, edits, missing destination
repair, nested directories, duplicate inputs, content, and timestamps.
The real translation mapping had no destination collisions. These results do
not establish equivalence for conflicting destinations, aliases, concurrent
input writes, or every possible failed-copy side-effect ordering.

## The zero-task experiment

The gate runs fresh static graph evaluation, then checks the known filesystem,
environment, invocation configuration, and probe/tool fingerprints. On a
match it does not dispatch `Build` at all. A captured **actual gate-hit binlog**
contained **404 evaluations, 0 target executions, and 0 tasks**.

On a miss it invokes the real ordinary incremental build. It does not
manufacture successful task outputs or suppress compilation when a change is
detected. This conservative whole-workload fallback is deliberately simpler
than per-project result caching.

The prototype improved during the investigation:

| Implementation | Internal no-change time |
| --- | ---: |
| Two filesystem scans per hit | About 10.60 s |
| One scan per hit | About 8.79 s |
| Reuse package-root list, still recheck package files | About 8.23 s |
| Hash-table snapshot instead of sorted-tree snapshot | About 6.54 s |
| Final repeatable run | About 6.45 s; 6.71 s including launch |

This is an **opt-in restricted experiment**, not a safe drop-in SDK change.
It assumes relevant state is file-based and included in the observed roots,
no required always-run side effects, no concurrent writers, and timestamp/size
changes for file edits. Untracked files/tools, network/clock/registry inputs,
and same-size edits preserving timestamps are outside its contract.
See the [full contract and runnable commands](../../scripts/no-op-build/README.md).

### Invalidation evidence

All source mutations were made in the disposable Orchard checkout and undone.

| Change | Observed behavior |
| --- | --- |
| Edit a module's assembly manifest source | Gate miss; 4 Csc invocations; next no-change call hit |
| Add a new globbed C# file | Gate miss; 4 Csc invocations; next no-change call hit |
| Delete that C# file | Gate miss; 4 Csc invocations |
| Introduce a deliberate compiler error | Exit 1; cached success removed; recovery must build |
| Delete a module's output DLL | Gate miss; DLL restored byte-for-byte; 0 Csc invocations |
| Delete copied appsettings.json | Gate miss; content restored byte-for-byte; 0 Csc invocations |
| Change an environment variable | Gate miss; normal build executed |
| Change probe/configuration | Gate miss; normal build executed |

These are bounded correctness probes, not a claim of compatibility with
arbitrary build authoring. A changed build is slower through this prototype:
it pays the evaluation/check cost before falling back to a normal build, and
then refreshes its manifest. Observed misses were about 30-44 s including
instrumentation, versus approximately 6.7 s for a stable hit.

## What would be required to ship this idea

The next architectural step is an explicit project/SDK contract for persistent
build results: declared file and non-file inputs, tracked output existence,
serialization of required target results, and opt-out for impure custom
authoring. A cache decision needs to happen **before** dispatching the normal
target graph, not after thousands of preparation tasks have already run.

A long-lived host with reliable filesystem change tracking could remove much
of the remaining 2.5-second scan and approach the measured 4.1-second evaluation
floor. Plain asynchronous `FileSystemWatcher` notifications are not enough to
claim correctness: startup gaps, delayed events, buffer overflow and concurrent
writes need conservative handling or a reliable journal protocol. This
investigation does not pretend to have solved that.

## Reproducibility artifacts

- [Runnable harness, analyzer, gate, and empty-target control](../../scripts/no-op-build/)
- [Final per-run measurements](../../scripts/no-op-build/results/measurements.csv)
- [Final summary](../../scripts/no-op-build/results/summary.csv)
- [Exploratory scheduling/restore/server samples](../../scripts/no-op-build/results/exploratory.csv)
- [Exact event counts and task timings](../../scripts/no-op-build/results/counters.json)
- [Gate phase timings](../../scripts/no-op-build/results/gate-breakdown.csv)
- [Invalidation counts](../../scripts/no-op-build/results/invalidation.json)
- [Interleaved target-hotpatch measurements](../../scripts/no-op-build/results/hotpatch-measurements.csv)
- [Target-hotpatch counters](../../scripts/no-op-build/results/hotpatch-counters.json)

Raw binlogs, the disposable workload, and machine-specific gate configuration
remain in this session's `files` directory; they are not committed. Binary logs
can contain imported source and environment data.
