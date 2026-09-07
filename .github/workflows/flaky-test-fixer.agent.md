---
name: "Flaky Test Auto-Fixer"
description: "Propose isolated, evidence-backed test-only fixes for still-flaking quarantined tests; keep quarantine by default."
on:
  schedule:
    - cron: "47 12 * * *"
  workflow_dispatch:
  permissions: {}

if: ${{ github.repository == 'dotnet/msbuild' && github.ref == 'refs/heads/main' }}

permissions:
  contents: read
  issues: read
  pull-requests: read

checkout:
  repository: ${{ github.repository }}
  ref: main

safe-outputs:
  add-comment:
    target: "*"
    max: 3
  noop:
    report-as-issue: false
  create-pull-request:
    title-prefix: "[Flaky Test Fix] "
    labels: [flaky-test]
    draft: false
    base-branch: main
    max: 3
    max-patch-files: 1
    auto-close-issue: false
    fallback-as-issue: false
    allowed-files:
      - "src/*.UnitTests/*.cs"
      - "src/*.UnitTests/**/*.cs"
    excluded-files:
      - ".github/**"

timeout-minutes: 60

imports:
  - uses: shared/pat_pool.md
    with:
      environment: copilot-pat-pool
  - shared/flaky-test-shared.md

environment: copilot-pat-pool

engine:
  id: copilot
  env:
    COPILOT_GITHUB_TOKEN: "${{ case( needs.pat_pool.outputs.pat_number == '0', secrets.COPILOT_PAT_0, needs.pat_pool.outputs.pat_number == '1', secrets.COPILOT_PAT_1, needs.pat_pool.outputs.pat_number == '2', secrets.COPILOT_PAT_2, needs.pat_pool.outputs.pat_number == '3', secrets.COPILOT_PAT_3, needs.pat_pool.outputs.pat_number == '4', secrets.COPILOT_PAT_4, needs.pat_pool.outputs.pat_number == '5', secrets.COPILOT_PAT_5, needs.pat_pool.outputs.pat_number == '6', secrets.COPILOT_PAT_6, needs.pat_pool.outputs.pat_number == '7', secrets.COPILOT_PAT_7, needs.pat_pool.outputs.pat_number == '8', secrets.COPILOT_PAT_8, needs.pat_pool.outputs.pat_number == '9', secrets.COPILOT_PAT_9, 'NO COPILOT PAT AVAILABLE') }}"
---

# Flaky-test repair

Propose at most three independent, one-test-file fixes for tests already quarantined on `main`.
Never change product code, shared helpers, assertions' meaning, or root manifests. Do not file or
close issues. Read the relevant section of
[`.github/workflows/shared/references/flaky-test-fixer.md`](shared/references/flaky-test-fixer.md)
for the evidence contract, diagnosis examples, un-quarantine gates, and publication templates.

## Evidence and candidate gates

Run once, synchronously, and wait for completion:

```bash
pwsh -File .github/workflows/scripts/Get-FlakyTests.ps1 -DefinitionId 344 -TargetBranch main -DaysBack 21 -MinSources 2 -MaxBuilds 500 -MaxArtifactDownloads 1200 -IncludePassed -IncludeErrorDetails -JsonOut quarantine-health.json
```

An incomplete scan, missing/unreadable evidence, no scanned builds, or no qualifying candidates
requires a `noop`, not speculative
edits or an expanded scan loop. Map assembly and normalized method names to real source before acting.

Require an open tracking issue, recent failures across independent sources/days, and a dominant
diagnosable signature with a concrete test-only causal explanation. Follow Step 3's stronger
single-platform history rule. Skip product regressions, unexplained deterministic breaks, infrastructure
failures, and fixes that weaken coverage. Do not infer distinct days or a recent recovery from an
aggregate count alone.

Deduplicate against open PR keys, tracking issues, and changed files. Select **at most one test per
normalized source path**; two methods in one file cannot become separate whole-file fix PRs this run.

## One isolated branch per candidate

1. Start a fresh branch from the recorded base SHA. Apply only this candidate's fix.
2. Keep `[ActiveIssue]` by default. Remove only its isolated attribute if **every** Step 5b gate is
   established: complete mechanism, all failure signatures explained, preserved coverage, and
   confirmed CI platform/TFM coverage. Removal is not needed merely to get quarantine PR evidence.
3. Validate this branch's affected project using Step 7 and the current test/build guidance. Do not
   infer independent validity from a union build or run unconditional whole-repository builds.
4. Recheck open-PR deduplication, the exact one-file/one-candidate diff, and the tracking issue's state.
   Correct or drop a broken fix; disclose environmental blockers rather than claiming compilation.
5. Commit only the intended file and declare `create_pull_request` with the actual current branch
   name and a unique temporary ID. Declare one tracking-issue comment referencing that ID.
   Then start the next independent candidate from the original base, not the previous fix.

Preserve the visible `flaky-test-id` key and use `Tracked by #N`, never closing keywords.
The quarantine pipeline also runs on PRs; its per-test results, not its deliberately tolerant job
conclusion, provide pre-merge evidence. Multi-day main-branch signal remains necessary for durable
un-quarantine decisions. No safe-output declaration alone proves a PR exists or that CI ran.
