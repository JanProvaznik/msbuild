---
name: msbuild-change-workflow
description: 'The end-to-end loop for making any code change in dotnet/msbuild: classify, locate, plan, implement, verify, assess compatibility, review, and write the PR. Use at the start of every bug fix, feature, refactor, or perf change in this repo, before editing any file. Designed to be followed literally, step by step, with a hard gate before claiming the change is done.'
argument-hint: 'Describe the issue or change you are about to make.'
---

# MSBuild Change Workflow

Follow the phases in order. Do not skip a gate. Each gate names the exact output you must have before moving on. If a gate cannot be satisfied, stop and tell the user what is missing instead of continuing.

Scripts referenced here live in `.github/skills/*/` and print `RESULT: PASS` or `RESULT: FAIL` as their last line. Paste that last line, plus any `FAIL` detail, into your final message.

## Phase 0: Classify (2 minutes, no edits)

Write one line for each:

1. **Kind**: bug fix | new feature | refactor | perf | infra/CI | docs.
2. **Area**: pick the first matching row and read its instruction file before editing anything in that path.

| Touched path | Read first |
|--------------|------------|
| `src/Build/BackEnd/**` | `.github/instructions/backend-execution.instructions.md` |
| `src/Build/Evaluation/**` | `.github/instructions/evaluation.instructions.md` |
| `src/Build/Globbing/**` | `.github/instructions/globbing.instructions.md` |
| `src/Build/Logging/**` | `.github/instructions/logging.instructions.md` |
| `src/Build/BuildCheck/**`, `src/Framework/BuildCheck/**` | `.github/instructions/buildcheck.instructions.md` |
| `src/Framework/**` | `.github/instructions/framework.instructions.md` |
| `src/MSBuild/**` | `.github/instructions/cli.instructions.md` |
| `src/Shared/**` | `.github/instructions/shared-code.instructions.md` |
| `src/Tasks/**/*.cs` | `.github/instructions/tasks.instructions.md` |
| `src/Tasks/**/*.targets`, `*.props` | `.github/instructions/targets.instructions.md` |
| `src/*.UnitTests/**` | `.github/instructions/tests.instructions.md` |

3. **Skills to load now** (load each that applies, even if you think you know the content):
   - Any new or changed warning, error, or resource string: `authoring-errors-and-warnings`
   - Any user-visible behavior change: `assessing-breaking-changes`, then `changewaves` if the scan says a wave is needed
   - Anything in `Evaluator.cs`, `Expander.cs`, `LazyItemEvaluator.cs`, `TaskExecutionHost.cs`, `FileMatcher.cs`: `optimizing-msbuild-performance`
   - Anything touching `BinaryLogger`, `BuildEventArgs`, `BuildEventArgsWriter/Reader`: `maintaining-binary-log-compatibility`
   - Repro project to verify against the built MSBuild: `use-bootstrap-msbuild`

**Gate 0**: the three lines above are written out in your reply.

## Phase 1: Locate (read only)

1. Find the code. Use the `Explore` subagent with "very thorough" when the issue names a symptom rather than a file. Ask it for file paths and line ranges, not summaries.
2. Find the existing tests for that code:
   ```bash
   grep -rl "ClassOrMethodName" src/*.UnitTests --include='*.cs'
   ```
   If there are none, note that; you will add a test file next to the closest related one.
3. For a bug fix, find the original issue and the PR that introduced the code (`git log -S "snippet" --oneline -- path`). Read the PR discussion if it is linked. The fix must match the original design intent, not just make the symptom disappear.
4. Check for `#if` guards in the file (`FEATURE_*`, `RUNTIME_TYPE_NETCORE`, `NET472`). Every branch you change must still compile on both net472 and net11.0.

**Gate 1**: you can name the file and line range to change, the test project, and at least one existing test you will model yours on.

## Phase 2: Plan (5 lines, written before any edit)

```
Change:   <one sentence>
Files:    <list>
Tests:    <test project + test method names you will add or change>
Compat:   <"none: internal only" | "behavior change: see Phase 4" | "new warning: needs wave">
Not doing: <adjacent things you noticed but will leave alone>
```

For a feature or anything touching more than four files, show this plan to the user and wait. For a bug fix, write it and continue.

**Gate 2**: the plan block is in your reply.

## Phase 3: Implement

Rules that Sonnet-class agents most often get wrong here; check each one against your diff:

- Smallest diff that fixes the problem. Do not reformat, rename, or reorder code you are not changing.
- Match the file's nullable setting. A file with `#nullable disable` gets no `?` annotations. A new file has nullable enabled and no `#nullable disable`.
- `is null` / `is not null`, never `== null`.
- `MSBuildNameIgnoreCaseComparer` for property, item, target, and metadata names. `StringComparison.OrdinalIgnoreCase` for other identifiers. Never `ToLower()`/`ToUpper()` for comparison. Never `CurrentCulture`.
- No LINQ in `Evaluator.cs`, `Expander.cs`, `LazyItemEvaluator.cs`, `TaskExecutionHost.cs`, `FileMatcher.cs`, or anything called per item or per property. Cold-path LINQ is fine.
- Collection expressions (`[]`) for new collections. `ImmutableArray<T>` for build-once-read-many, never `ImmutableList<T>`.
- Every user-facing string goes in the area's `Strings.resx` with an `MSBxxxx` code. Never a hardcoded message.
- Shared mutable state needs a lock or a concurrent collection. Tasks run on many threads and in many processes at once.
- Write the test first when fixing a bug: it must fail before the fix and pass after. Record both runs.

Test authoring: follow `.github/instructions/tests.instructions.md` exactly. `TestEnvironment.Create(_output)` with a `using` declaration, Shouldly assertions, `MockLogger(_output)` on every build, raw string literals for project XML, no English-string assertions.

## Phase 4: Verify (the hard gate)

Run the scoped verification script. It builds every project you touched with warnings as errors and runs the mapped test projects.

```bash
.github/skills/verifying-changes/verify-change.sh --filter "*YourTestNamePattern*"
```

Drop `--filter` to run the whole mapped test projects (slower, required before you say done on an engine change). Never redirect build or test output to a file. The script writes its own log and tells you where it is.

Then run the compatibility scan:

```bash
.github/skills/assessing-breaking-changes/compat-scan.sh
```

If it prints any `SIGNAL [...]` line, open the `assessing-breaking-changes` skill and decide, in writing, whether a ChangeWave is needed. If it prints `WAVE: REQUIRED` and there is no `ChangeWaves.AreFeaturesEnabled` check in your diff, you are not done.

**Gate 4**: `verify-change.sh` ends with `RESULT: PASS`; `compat-scan.sh` ends with `RESULT: PASS`, or with `RESULT: REVIEW` and a one-line answer from you for every `SIGNAL`. Your reply quotes both result lines. A build warning is a failure. A skipped test count that went up unexpectedly is a failure. `RESULT: FAIL` from either script means you are not done.

## Phase 5: Review

Run the review skill on your own diff before handing it to the user:

```
/reviewing-msbuild-code
```

It launches narrow reviewer agents in parallel and verifies each finding against the code. Fix every CONFIRMED finding with severity BLOCKING or MAJOR, re-run Phase 4, and list the rest in the PR description as open questions.

## Phase 6: Done criteria and PR text

You may say the change is done only when all of these are true:

- [ ] Gate 4 result lines are quoted in your message.
- [ ] A test exists that fails without the change (bug fix) or covers the new behavior (feature). Name it.
- [ ] `compat-scan.sh` signals are each addressed: wave added, or a one-line reason why not.
- [ ] `documentation/` was checked for pages that describe the changed behavior, and updated if any.
- [ ] No files under `artifacts/`, `.dotnet/`, `global.json`, or `NuGet.config` changed.

PR description template (fill every line; delete none):

```
### Context
<issue link and the one-sentence problem>

### Changes Made
<what changed, by file group>

### Testing
<test names added; verify-change.sh result line; manual repro if any>

### Compatibility
<compat-scan.sh output summary; ChangeWave version or "no behavior change">

### Notes
<open questions, things deliberately left out>
```

## When you are stuck

- Build fails with "Could not resolve SDK": `source artifacts/sdk-build-env.sh` (after one full `./build.sh`).
- Build fails with locked files: `./artifacts/bin/bootstrap/core/dotnet build-server shutdown`.
- A test passes locally but you did not see it fail first: rerun it with the fix stashed. Do not trust a test you have not seen fail.
- You changed `src/Shared/**`: the verify script maps that to Build, Tasks, and Utilities tests. Run all three.
- You cannot satisfy a gate after two attempts: stop, report exactly which gate and what you saw, and ask.
