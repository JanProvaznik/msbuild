---
name: "Flaky Test Triage"
description: "Use complete CI evidence to track flakes and propose one test-only quarantine/un-quarantine PR."
on:
  schedule: daily around 11:30 AM
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
  create-issue:
    title-prefix: "[Flaky Test] "
    labels: [flaky-test]
    max: 5
  add-comment:
    target: "*"
    max: 12
  noop:
    report-as-issue: false
  create-pull-request:
    title-prefix: "[Flaky Test] "
    labels: [flaky-test]
    draft: false
    base-branch: main
    auto-close-issue: false
    fallback-as-issue: false
    max: 1
    max-patch-files: 13
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

# Flaky-test triage

Track genuine flakes, not product regressions, and propose at most one combined PR containing only
selected tests' `[ActiveIssue]` changes. No test-body fixes belong in this workflow.
The procedure at
[`.github/workflows/shared/references/flaky-test-detector.md`](shared/references/flaky-test-detector.md)
preserves the data model, matching recipe, platform rules, and issue/PR templates; read its relevant
section at each phase.

## Scan and stop conditions

Run these Linux-runner commands synchronously, once each, and wait for completion before reading JSON:

```bash
pwsh -File .github/workflows/scripts/Get-FlakyTests.ps1 -TargetBranch main -DaysBack 14 -MinSources 3 -MaxBuilds 200 -MaxArtifactDownloads 400 -JsonOut flaky-report.json
pwsh -File .github/workflows/scripts/Get-FlakyTests.ps1 -DefinitionId 344 -TargetBranch main -DaysBack 30 -MinSources 1 -MaxBuilds 750 -MaxArtifactDownloads 1800 -IncludePassed -JsonOut quarantine-health.json
```

Caps bound work, not completeness. If a scan has `scanComplete: false` or reports missing/unreadable
evidence, do not act on that scan.
The two scans are independent. Do not retry merely because an output file does not exist mid-run.
If neither scan has actionable evidence, emit one `noop` only when no other output is being declared.

## Decide, edit, and publish

1. Read the procedure's evidence model and classification rules. Correlated failures on a broken
   main baseline do not become independent flake evidence just because several PRs reproduce them.
2. Use the complete primary-store/local matching recipe in Step 4 before creating issues. Failed
   pagination or malformed matching data blocks issue actions and new quarantines. Newly declared
   issues do not yet have real numbers; do not quarantine them until a later run.
3. Select at most eight new quarantines with pre-existing open tracking issues. Exclude existing
   quarantines and open-PR matches using whole-line `flaky-test-id` keys.
4. Select at most five un-quarantines independently: at least 50 main-branch green builds across
   14 days, the required platform scope, and **zero observed failures**. The backlog scan deliberately
   uses `MinSources = 1`; absence from a higher-threshold failure list would not prove zero failures.
5. Apply only the selected attribute changes. Validate affected projects as described in Step 7;
   preserve the documented handling of compiler errors versus environmental blockers.
6. Recheck open-PR deduplication and the exact diff against the recorded base SHA. Reject product,
   helper, manifest, or unrelated edits. Commit only the intended files to the current local branch.
7. Declare one real PR with that exact current branch name and a `temporary_id`. Use one planned
   comment per issue for new evidence plus its action/reference, within the shared twelve-comment
   budget. Never poll for deferred IDs or post via another mechanism.

Use `Tracked by #N` for quarantines. A closing keyword is allowed only for a complete un-quarantine
with no remaining reference to that issue anywhere in the tree. Do not ping users or claim that a
successful output declaration proves compilation, CI execution, or multi-platform stability.
