# RAR input/result caching: implemented follow-up

This follow-up tests the hypothesis that RAR can check its previous inputs and
skip resolution. **It can, for an explicitly supported file-based contract,
without an engine change or skipping public targets.** The earlier statement
that RAR must remain active did not establish that its entire algorithm must
execute on every invocation.

This is separate from the [target-only roadmap](../specs/proposed/target-incrementality-roadmap.md).
It includes actual C# task changes, built from the matching dotnet/dotnet source,
and a hotpatched private SDK. It is not the incompatible CoreBuild gate.
Source, commands and the exact freshness/rollback contract are in
[`scripts/no-op-build/rar-cache`](../../scripts/no-op-build/rar-cache/).

## What was implemented

The task records the inputs and filesystem observations of successful RAR
execution, then revalidates them before restoring the complete task result.
The implementation preserves output arrays/order/metadata, copy-local results,
state-file bookkeeping, ordinary diagnostics and existing target hooks.
Unsupported resolution environments execute normally.

The opt-in is `EnableRARResultsCache=true`; false is the explicit rollback.
It does not enable any earlier target-level shortcuts. Both timing arms use
the same rebuilt task binary and private SDK.

Important distinctions:

1. This caches **resolved results**, not only PE metadata. RAR already has
   metadata caches, so its ordinary warm path is a much stronger competitor
   than a completely uncached resolver.
2. Negative observations matter. Checking just yesterday's `ReferencePath`
   cannot detect an earlier search candidate or a newly appearing related file.
3. Input fingerprints cannot repair stale underlying metadata. Real file
   generations and synthetic framework identities must not collide.
4. Replaying output state still costs time. Removing resolution does not remove
   task invocation, input expansion, output marshaling, SDK conflict processing
   or the rest of the target graph.

## Measured iterations

The initial complete-result prototype hit all 202 Orchard projects but was
slower than ordinary RAR. It was not treated as a successful optimization.
The implementation was then revised to batch filesystem metadata checks,
compress/buffer result storage, canonicalize input metadata order, avoid
legacy-cache rewrite invalidations, hydrate normal `TaskItem` objects,
retain copy-on-write output prototypes and encode metadata deltas.

The initial result payload was approximately 892 KB/project, about 180 MB for
the graph. Compression reduced an intermediate version to about 41 KB/project,
8.4 MB total. Batched validation cut representative reference-pack checks from
roughly 10 ms to 1.5-3 ms. These are component improvements, not whole-build
speedup claims.

The first benchmark with **hit counts in every timed call** still regressed:
18.90 s ordinary versus 19.82 s cached, with 202/202 hits in every cached sample.
A later metadata-delta run measured 19.43 versus 19.75 s, still not
a useful win. Five alternating pairs were used in both runs; different baseline
times mean the change between runs is not itself a controlled speedup claim.
This ruled out "the timed builds probably missed their caches" as the
explanation for those results.

There is a real favorable scenario: twenty explicit file-based resolutions of
the actual `OrchardCore.ContentManagement.dll` dependency closure took 2,715 ms
of RAR time without result caching versus 598 ms with it, including one fill
and nineteen hits. That is about **78% less RAR time**, not a claim of a 78%
faster Orchard build. It uses ordinary file-reference discovery and explicit
search directories; Orchard's normal SDK build delegates much of that work
to NuGet/framework resolution already.

## Final measured result

Windows x64, SDK 10.0.400, same task binary in both arms, `-m:8`, separate restore,
alternating off/on order. Whole-build timings use the lightweight counter logger
in both arms, **not** a binary logger. Five pairs cover Orchard and the
file-reference solution; the smaller matrix uses three pairs.

| Scenario | Ordinary build | Result cache | Enabled hits / bypasses |
| --- | ---: | ---: | ---: |
| Orchard CMS, 202 projects | **18.928 s** | **20.082 s** | 202 / 0 |
| Eleven-project ordinary file-reference solution | 1.736 s | 1.710 s | 11 / 0 |
| Default three-project console | 1.226 s | 1.299 s | 3 / 0 |
| Explicitly trusted three-project console | 1.153 s | 1.141 s | 3 / 0 |
| 24-project, seven-level solution | 2.396 s | 2.500 s | 24 / 0 |
| net8.0/net10.0 multitarget solution | 1.199 s | 1.161 s | 3 / 0 |
| Web | 1.335 s | 1.291 s | 1 / 0 |
| Web + Razor/static assets | 1.606 s | 1.635 s | 2 / 0 |
| WPF | 1.132 s | 1.119 s | 1 / 0 |
| WinForms | 1.093 s | 1.111 s | 1 / 0 |
| .NET Framework 4.7.2 | 0.975 s | 0.984 s | 0 / 1 |

These are medians, not guaranteed savings. Every enabled timed sample had the
listed hit/bypass counts and **zero misses**. All timed builds exited zero.
The ordinary Orchard build is **6.1% slower** with this cache enabled in the
final run. Most small-solution changes are noise-sized or regressions.
The original near-evaluation-time goal is **not achieved**.

The file-reference solution's cumulative RAR median improved from **808.2 to
308.2 ms**, about **62%**, but parallelism and other work reduced its whole-build
improvement to just **26 ms / 1.5%**. Its executed output matched before and after.
This is another reason not to equate summed task time with saved wall time.

The final direct-task closure run, using the same real Orchard DLL and installed
10.0.11 reference packs, measured **1,934.5 to 568.9 ms** over twenty invocations:
**70.6% less RAR time**, including one fill and nineteen hits. This corroborates
task-level feasibility, but it is a microbenchmark with repeated resolution in
one process, not a solution build or a twenty-sample median.

Evidence:

- [Final Orchard samples, including cumulative RAR time and hit counts](../../scripts/no-op-build/rar-cache/results/orchard-final-samples.csv)
- [File-reference solution samples](../../scripts/no-op-build/rar-cache/results/file-reference-final-samples.csv)
- [Nine-solution matrix and output parity](../../scripts/no-op-build/rar-cache/results/solution-matrix.csv)
- [Final direct-task closure totals](../../scripts/no-op-build/rar-cache/results/task-closure-final.csv)
- [Source/binary provenance](../../scripts/no-op-build/rar-cache/source-provenance.json)

### Where the cache time went

In the **timed** Orchard samples, cumulative RAR medians were **5.778 s ordinary
versus 5.974 s cached**. Cache validation/restoration did not beat the existing
warm resolver.

A separate diagnostic cache-hit build recorded **23,697 tasks**, **31,252
targets**, **404 evaluations**, **202 cache hits**, and **zero compiler
invocations**. Its cumulative cache phases were:

| Phase | Cumulative time across 202 RAR calls |
| --- | ---: |
| Input fingerprint | 0.575 s |
| Load/decompress cached contracts | 1.022 s |
| Filesystem observation validation | 2.386 s |
| Output restoration | 1.899 s |

The balance includes diagnostic replay and other cache-path work. These phases
overlap across build nodes and come from a logged build; they must not be added
to, or subtracted directly from, unlogged wall time. The recorded
[diagnostic summary](../../scripts/no-op-build/rar-cache/results/orchard-diagnostic-summary.json)
also retains the cache-fill penalty rather than hiding it.

## Compatibility work, not just hit-rate work

The executable integration harness covers full output/metadata/order parity,
an unchanged primary DLL with a changed transitive DLL, new/removed XML files,
new higher-priority candidates, a missing candidate appearing, satellites,
changed aliases, preserved-time App.config edits, corrupt framing and payloads,
ordinary-cache deletion, repeated warnings, warning-as-error behavior and a
failing AfterTargets hook **on a cache hit**.

Independent review also found and drove fixes for:

- stale immutable timestamps authorizing a newly computed result;
- synthetic framework identity leaking into disk metadata caches;
- task-local versus process environment differences;
- drive-relative path resolution;
- malformed string lengths and unbounded decompression;
- integrity checks that covered output bytes but not negative observations;
- missing legacy state-file recreation and permanently replayed recovery messages;
- legacy empty-App.config state hidden by its public getter;
- `OriginalItemSpec` corruption during transport-item hydration;
- public metadata removal rejecting stored `RecursiveDir`.

The same-process regression suite verifies output mutation isolation,
`RecursiveDir`, escaped metadata, default-off behavior and legacy empty config.
The separate four-phase metadata probe exercises physical v1, synthetic v2,
physical v1 again, then a cached synthetic v2 result.

Final evidence: **38 adversarial cases**, including two expected failed builds;
**37 source regression tests** in the selected .NET RAR namespace, including
three new cache-specific tests; both net10.0 and net472 task assemblies compile.
All **nine solutions / 38 projects** retained identical paths and hashes for
**541 `bin` output files**. This is not a claim of full .NET Framework test-suite
coverage or of publish/pack validation with the new task implementation.
The [case outcomes](../../scripts/no-op-build/rar-cache/results/adversarial-cases.json)
and [scope summary](../../scripts/no-op-build/rar-cache/results/validation-summary.json)
are committed alongside the measurements.

The freshness guarantee is deliberately narrower than content addressing:
ordinary PEs use timestamp/length/attributes, while App.config uses a content
hash. General GAC/registry/redist/profile resolution remains a fallback.
No claim is made that all possible RAR inputs are now cacheable.

## What the evidence changes

**The historical RAR warning was right about blindly skipping discovery, not
about the impossibility of caching observed resolution.** The transitive and
new-candidate counterexamples can be handled while keeping the original target
graph and hooks.

However, the normal Orchard build does not spend all its post-evaluation time
inside RAR. It still executes roughly 23,700 tasks and 31,000 targets. Caching
one task's result does not remove that work, and this prototype's validation
and result restoration can cost more than the existing warm resolver.
The remaining time is not automatically "scheduler overhead"; item/property
processing, target execution and task preparation also remain.

An engine change was **not required to implement or demonstrate** result
caching. Nor has an engine change been demonstrated to deliver the desired
near-evaluation-time whole build. That remains an unmet performance goal,
not a conclusion that the goal is impossible.

The next decision should follow measured costs: keep this task-level path for
discovery-heavy cases only if its benefit survives whole-build measurements;
for normal SDK graphs, target the repeated reference preparation/state transfer
and other always-running SDK targets. Do not infer a shipping recommendation
from the favorable microbenchmark, and do not return to suppressing arbitrary
hooks to manufacture a large number.
