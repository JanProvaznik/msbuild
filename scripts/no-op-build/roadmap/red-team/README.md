# Native CoreBuild compatibility counterexamples

Source-only portable replay bundle for the already-established counterexamples
against the opt-in native CoreBuild experiment. No engine changes or third-party
workloads are involved. This is **not** a test or review of the separate SAFE SDK
leaf optimizations.

Requires PowerShell and installed .NET SDK 10.0.400. The recorded SDK's source
commit, from `.version`, is `14fbf8d5271c98133561eb55185fdb05b286f578`.
`-DotNet` can override the default `C:\Program Files\dotnet\dotnet.exe`.

## Focused mitigation replay

From PowerShell, supply the fork's native targets and a **new external** artifact
directory:

```powershell
.\Replay-Review.ps1 `
    -NativeTargetsDirectory 'Q:\path-to-fork\scripts\no-op-build\native' `
    -ArtifactsDirectory 'C:\lab\native-review-001' `
    -NativeExtraArgs @('-p:NativeCoreBuildContract=DeclaredInputsAndHooksV1') `
    -Cases @('3', '4', '5', '6', '7')
```

To demonstrate the retained architectural limitations, use `-Cases @('1', '2')`
with the same acknowledgment. To replay all seven previously reported cases,
omit `-Cases`.

The runner creates fresh fixtures in ArtifactsDirectory, leaving the templates
and target directory unchanged. It refuses to overwrite an existing artifact
directory. It pins SDK selection, clears package sources for these package-free
fixtures, disables the MSBuild server, and uses one build node. Binlogs are
outside all project directories so default input globs cannot mask failures.

Before building each native project, the runner queries `_NativeCompletion` and
`NativeCoreBuildContract`. It **fails if the native boundary did not activate**,
rather than mistaking an unacknowledged opt-in's stock fallback for a mitigation.
The acknowledgment is supplied only to native invocations.

## Cases and recorded results

The original implementation is commit `c6e6728fb`. Its baseline reproduced all
seven counterexamples. The acknowledged candidate reruns covered cases 1-5 and
then all standard-input cases 3-7. The target-file hashes were identical across
both candidate runs and matched the working repository at verification:

| ID | Scenario | Original | Acknowledged candidate |
|---|---|---|---|
| 1 | PreBuildEvent hook generates a referenced project's source | Native CS2001; stock succeeds | Still reproduces: architectural incompatibility |
| 2 | Warm Build skips CoreCompile validation hook and its state | Native succeeds with empty state; stock fails | Still reproduces: architectural incompatibility |
| 3 | External Lib.dll unchanged, adjacent transitive Dep.dll changes | Native runs old Dep; stock runs new | Fixed for repro: native runs RAR and new Dep |
| 4 | `ServerGarbageCollection=true` | Native omits requested runtimeconfig option | Fixed for repro: native writes `System.GC.Server=true` |
| 5 | Execution-time translation mapping switches package directories | Native retains package-one text | Fixed for repro: native copies package-two text |
| 6 | `Company` changes on command line | Native retains old compiled attribute | Fixed for repro: native compiles and prints NewCompany |
| 7 | Compile CopyToOutputDirectory changes Never to PreserveNewest | Native misses requested copy, although public query returns the item | Fixed for repro: native creates the requested Program.cs copy |

The candidate native boundary was active for every tested project. On the
runtime fixture's final warm build, the core payload still skipped. On the
translation mapping change, core skipped and the translation leaf executed one
Copy task. Thus these mitigations were not verified solely by switching off the
optimization.

`evidence/baseline-observations.json`,
`evidence/candidate-ack-observations.json`, and
`evidence/final-standard-observations.json` retain compact observations and exact
target-file hashes. Source line references in these records refer to the
original `c6e6728fb` implementation. See `FINAL-RESULTS.md` for final severity and
the limits of the conclusion.

## Output and interpretation

Each run writes `results.json`, containing:

- Target hashes, SDK version, requested cases, and native arguments.
- Explicit per-project activation observations.
- Commands, exit codes, full console output, and stock/native outcomes.
- Per-case `reproducedOriginalFailure` values.
- Errors during setup or replay as `runnerError`.

The runner does not treat a reproduced known failure as a script failure.
Inspect its outcome records. `reproducedOriginalFailure=false` alone is not a
general compatibility verdict; inspect both build exit codes and actual values.
Unexpected execution errors must not be interpreted as successful fixes.

All case definitions remain intentionally small. They demonstrate specific
contracts, not completeness for arbitrary custom targets, filesystem state,
reference resolution, publish/pack, or all SDK properties.
