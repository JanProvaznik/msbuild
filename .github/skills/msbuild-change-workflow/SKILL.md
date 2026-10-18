---
name: msbuild-change-workflow
description: 'The end-to-end loop for making any code change in dotnet/msbuild: classify, locate, plan, implement, verify, assess compatibility, review, and write the PR. Use at the start of every bug fix, feature, refactor, or perf change in this repo, before editing any file. Designed to be followed literally, step by step, with a hard gate before claiming the change is done.'
argument-hint: 'Describe the issue or change you are about to make.'
---

# MSBuild Change Workflow

Follow the phases in order. Each gate names the output you must have before moving on. If a gate cannot be satisfied, stop and tell the user what is missing instead of continuing.

Scripts referenced here live in `.github/skills/*/` and print `RESULT: PASS`, `RESULT: REVIEW`, or `RESULT: FAIL` as their last line. Quote that line, plus any `FAIL` detail, in your final message.

## Phase 0: Classify (no edits)

Write one line for each:

1. **Kind**: bug fix | new feature | refactor | perf | infra/CI | docs.
2. **Area**: pick every matching row and read its instruction file before editing anything in that path. Copilot applies these files when you edit a matching path; reading the right one up front lets its rules shape the plan, not just the edits.

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

Other skills in `.github/skills/` load from their own descriptions (diagnostics, compatibility, binary logs, performance, bootstrap testing); use whichever match the change.

**Gate 0**: the two lines above are written out in your reply.

## Phase 1: Locate (read only)

1. Find the code. When the issue names a symptom rather than a file, delegate the search to a subagent (the `agent` tool) so the file dumps stay out of your context. Ask it for file paths and line ranges, not summaries.
2. Find the existing tests for that code:
   ```powershell
   git grep -l "ClassOrMethodName" -- "src/*.UnitTests/*.cs"
   ```
   If there are none, note that; you will add a test file next to the closest related one.
3. For a bug fix, find the original issue and the PR that introduced the code (`git log -S "snippet" --oneline -- path`). Read the PR discussion if it is linked. The fix must match the original design intent, not just make the symptom disappear.
4. Check for `#if` guards in the file (`FEATURE_*`, `RUNTIME_TYPE_NETCORE`, `NET472`). Every branch you change must still compile on both `net472` and the current .NET TFM (`net11.0`).

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

Coding rules live in `AGENTS.md` (General, Formatting, Nullable Reference Types, Performance Best Practices) and in the instruction file you read in Phase 0; apply them to every hunk. Two rules belong to this workflow itself:

- Smallest diff that fixes the problem. Do not reformat, rename, or reorder code you are not changing; list such temptations under "Not doing".
- For a bug fix, write the test first. It must fail before the fix and pass after; record both runs.

Test authoring follows `.github/instructions/tests.instructions.md`.

## Phase 4: Verify (the hard gate)

This phase defines "done" for a change in this repo. Other skills point here rather than restating it.

1. While iterating, run the scoped verification with a filter (`pwsh` on macOS/Linux):
   ```powershell
   .\.github\skills\verifying-changes\verify-change.ps1 -Filter "*YourTestNamePattern*"
   ```
2. Before saying done, run it with no filter. It builds every project your diff maps to with warnings as errors and runs those test projects in full (`src/Shared/**` maps to Build, Tasks, and Utilities). That is the required local test run; the whole-repo `.\build.cmd -test` is what CI runs and is not a local gate. The `verifying-changes` skill explains the script's output and options.
3. Run the compatibility scan:
   ```powershell
   .\.github\skills\assessing-breaking-changes\compat-scan.ps1
   ```
   If it prints any `SIGNAL` line, the `assessing-breaking-changes` skill decides, in writing, whether a ChangeWave is needed. `WAVE: REQUIRED` with no `ChangeWaves.AreFeaturesEnabled` check in your diff means you are not done.

Never redirect build or test output to a file yourself; the script writes its own logs and tells you where they are.

**Gate 4**: `verify-change.ps1` ends with `RESULT: PASS`; `compat-scan.ps1` ends with `RESULT: PASS`, or with `RESULT: REVIEW` and a one-line answer from you for every `SIGNAL`. Your reply quotes both result lines. A build warning is a failure. A skipped test count that went up unexpectedly is a failure. `RESULT: FAIL` from either script means you are not done.

## Phase 5: Review

Load the `reviewing-msbuild-code` skill and run it on your own diff before handing it to the user. It launches narrow reviewer agents in parallel and verifies each finding against the code. Fix every CONFIRMED finding with severity BLOCKING or MAJOR, re-run Phase 4, and list the rest in the PR description as open questions.

## Phase 6: Done criteria and PR text

You may say the change is done only when all of these are true:

- [ ] Gate 4 result lines are quoted in your message.
- [ ] A test exists that fails without the change (bug fix) or covers the new behavior (feature). Name it.
- [ ] `compat-scan.ps1` signals are each addressed: wave added, or a one-line reason why not.
- [ ] `documentation/` was checked for pages that describe the changed behavior, and updated if any.
- [ ] No files under `artifacts/`, `.dotnet/`, `global.json`, or `NuGet.config` changed.

PR description template (fill every line; delete none):

```
### Context
<issue link and the one-sentence problem>

### Changes Made
<what changed, by file group>

### Testing
<test names added; verify-change.ps1 result line; manual repro if any>

### Compatibility
<compat-scan.ps1 output summary; ChangeWave version or "no behavior change">

### Notes
<open questions, things deliberately left out>
```

## When you are stuck

- A test passes but you never saw it fail: rerun it with the fix temporarily reverted. Do not trust a test you have not seen fail.
- You cannot satisfy a gate after two attempts: stop, report exactly which gate and what you saw, and ask.
