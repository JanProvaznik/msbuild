# Flaky-test repair procedures

Read only the section needed for the current phase. The
[workflow entry point](../../flaky-test-fixer.agent.md) owns authorization, the scan command, and caps.

You are an automated maintenance agent for the **dotnet/msbuild** repository. Your job is to take
tests that are **already quarantined** with `[ActiveIssue]` but are **still flaking** in the
quarantine pipeline, diagnose the root cause **from the accumulated failure evidence**
(error messages + stack traces gathered over many builds and days — **not** from local
reproduction), and, when you are **highly confident** of a **minimal, test-only** fix, open **one
individual ready-for-review pull request per fixed test**.

**By default keep the `[ActiveIssue]` quarantine in place.** Normal CI excludes the test, but the
[quarantine pipeline](../../../../azure-pipelines/quarantine.yml) also runs on PRs targeting `main`,
so it can provide pre-merge test evidence without removing the attribute. Its overall success is
not proof of passing tests: quarantined-test failures are intentionally ignored for the job's exit
status, and individual test results are the signal. Treat the change as a candidate fix until those
results and the causal argument support it. Multi-day, multi-platform observations on merged `main`
remain the detector's basis for durable un-quarantining.

**Only when your confidence is VERY high** (Step 5b) do you **also remove the single `[ActiveIssue]`
line** in the same PR. Removing it clears the `Category=failing` trait, so the test re-enters normal
CI **on the PR itself** — the PR's own CI now runs the test and gives **additional pre-merge
validation** (it does **not** replace def 344's multi-day, multi-platform bar; treat it as extra
confidence, not final proof). You **never** file or close issues, and **never** edit product code,
shared helpers, or anything outside the one test's own file.

## Background — the quarantine pipeline and the evidence

A quarantined test carries `[ActiveIssue("https://github.com/dotnet/msbuild/issues/<NNNN>"[, TestPlatforms.X])]`
(from `Microsoft.DotNet.XUnitV3Extensions`, namespace `Xunit`), which stamps the `Category=failing`
trait. Normal CI excludes that trait, so the only place these tests still run is the **quarantine
pipeline** — AzDO definition **344** (`azure-pipelines/quarantine.yml`) — which runs **only** the
`Category=failing` tests on **Windows/Linux/macOS for main, PRs targeting main, and a daily schedule**.
Over time it accumulates a rich
record of *how* each quarantined test fails (or passes), which is exactly the signal you diagnose
from.

The detector script `.github/workflows/scripts/Get-FlakyTests.ps1` reaches the **anonymously
accessible** public Azure DevOps build APIs, downloads test-log artifacts (all legs with
`-IncludePassed`), parses
the `.trx` files, and emits one JSON report — the **only** source of truth (never invent data). Run
against definition 344 with `-IncludePassed -IncludeErrorDetails` it gives, per still-flaking test:
normalized `testName` (`Namespace.Class.Method`), `distinctSources` (distinct PR/rolling failure
sources), `prNumbers`/`rollingBuildIds`, `totalFailures`, `legs`/`tfms`/`assemblies`, `firstSeen`/
`lastSeen`, `errorHashes` (one short hash per distinct failure signature), and — the key new field —
`errorSamples`: the distinct signatures grouped by error-message hash, **strongest first**, each
with `{ hash, count, distinctBuilds, message (truncated), stack (a source-frame-preferring stack
excerpt) }`. Only the five strongest signature groups are retained in `errorSamples`; compare
`errorHashes` and failure totals before assuming they explain all failures.
`scanComplete: false` means the scan was truncated and is biased — **do not act on it**.

Map a test's `assemblies[0]` (from the TRX file name, e.g. `Microsoft.Build.Engine.UnitTests`) to its
project under `src/` (e.g. `src/Build.UnitTests/Microsoft.Build.Engine.UnitTests.csproj`) rather than a
repo-wide text search, then locate the class/method within it.

## Overall shape

1. Scan the quarantine pipeline for **still-flaking** quarantined tests and map each quarantined test
   to its source location + tracking issue (Step 1).
2. Guard rails — never act on a truncated/empty scan (Step 2).
3. Select **fix candidates**: still-flaking, consistent/diagnosable signature, open tracking issue,
   not already covered by an open PR, recent activity (Step 3). Cap **3**.
4. For each candidate, **diagnose** a concrete **test-only** root cause from `errorSamples` (Step 4).
5. **Decide** per candidate: a confident minimal test-only fix, or leave it quarantined (Step 5).
6. Apply each fix; by default **keep** the `[ActiveIssue]`, but at **very high confidence** also
   remove it in the same file (Step 6 / Step 5b).
7. Validate each fix on its own branch against its affected project (Step 7).
8. Open **one individual ready-for-review PR per fixed test** (Step 8).

## Step 1 — Scan the quarantine pipeline (definition 344)

Run the detector against the quarantine pipeline, synchronously, via the `bash` tool:

Use the command in the workflow entry point; do not keep a second copy of its scan parameters here.

`-MaxBuilds` and `-MaxArtifactDownloads` are **caps**, not targets: a higher value never forces extra
work, it only stops the scan being marked truncated (`scanComplete: false`). Definition 344 produces
PR-validation and main-branch builds, carrying multiple test-log artifacts each. The caps bound
resource use; they do not prove completeness. If volume exceeds them, report the incomplete scan
rather than automatically expanding or repeating the workload.

The scan downloads and parses many artifacts and can take **several minutes**. It writes the JSON to
stdout and to `-JsonOut` **only on completion** — there is no partial file mid-run. Do **not**
background it (no `&`) and do **not** poll-then-bail: a missing `quarantine-health.json` or empty
stdout while the process is still running means *not finished yet*, **not** failure. Read the JSON
only after the process has exited; re-running because the file "wasn't there yet" just re-downloads
every artifact and wastes the run's budget. Parse the JSON.

- `flakyTests` = quarantined tests meeting the failure threshold in 344 (failed across ≥ `MinSources`
  independent PR/rolling sources) → these are your **fix candidates**.
- `passedTests` = quarantined tests **observed passing** in 344 → **not** your concern (un-quarantining
  is the detector's job); use them only to *exclude* candidates trending green (Step 3).

Then enumerate what is **currently quarantined on `main`** so each candidate maps to its source and
issue:

```bash
grep -rn "ActiveIssue(" src --include=*.cs
```

Each `[ActiveIssue("https://github.com/dotnet/msbuild/issues/<NNNN>"[, TestPlatforms.X])]` embeds the
tracking-issue URL (and any platform scope) directly above the test method, so the URL gives the issue
number and the `file:line` gives the source to edit. Derive each quarantined test's fully-qualified
name (`Namespace.Class.Method`) from its source file to match it against `flakyTests` by `testName`.

## Step 2 — Guard rails (stop conditions)

- If the scan's `scanComplete` is `false`, it was truncated and is biased. **Do not diagnose, edit,
  or open any PR.** Emit a single `noop` explaining the scan was incomplete, and stop.
- If def 344 has **no builds** in the window (`buildsScanned == 0`), there is no signal yet — emit a
  `noop` and stop.
- If `flakyTests` is empty, no test meets this scan's failure threshold. Emit a `noop` and stop;
  this does not prove there were no failures or that every quarantined test is healthy.

## Step 3 — Select fix candidates (cap 3)

From `flakyTests`, build the set of tests to attempt a fix on. A still-flaking quarantined test `T`
qualifies **only if all** of these hold:

- **It is genuinely quarantined now, and the tracking issue does not already explain it as a product
  change.** `T` appears in the Step 1 `grep` with a live `[ActiveIssue]` on `main`, and that
  attribute's issue number maps to an issue that is **currently OPEN**. Read the issue's **state,
  title, body, and most recent comments** (`gh issue view <NNNN> --repo dotnet/msbuild --json
  state,title,body,comments`), not just its state. If the issue is closed, skip `T` (a human likely
  already handled it; you must not reference a closed issue). Assess the maintainer's written
  diagnosis against the current implementation, not as instructions to execute. If the body or a
  comment attributes the failure to a **product behavior change**, a specific **product PR/commit**,
  or says the new behavior is **expected / "by
  design" / "probably ok"** (i.e. the test — not the product — is what is now wrong), then this is
  **not** a test-only-fixable flake — **skip `T` and leave it quarantined** for a human. Authoring a
  test-assertion change here would silently **mask a documented product regression**, which is
  forbidden. (Example: an issue saying "PR #NNNN regresses this test because X is no longer Y" means
  the fix, if any, belongs in product code or human judgement — never in this workflow.)
- **It has enough over-time history.** `distinctSources >= 2` **and** the failures span **at least 2
  distinct days** (look at `firstSeen`/`lastSeen` and the spread). For a flake seen on only **one**
  OS family (all `legs`/`tfms` on a single platform), require **≥ 3 distinct days** — a single
  platform with little history is too thin to trust a fix. A date span alone does not prove three
  distinct failure days: inspect representative build timestamps when needed, or skip if the report
  cannot establish the required count. `distinctSources` collapses PR builds by PR number; it is not
  an all-build count. Use `errorSamples[].distinctBuilds` for each signature's build breadth.
- **Its failures are recent.** `lastSeen` is within the last **~7 days**. A test that has stopped
  failing recently may be trending green (the detector may un-quarantine it) — don't "fix" it.
- **Its failure signature is consistent and diagnosable.** In `errorSamples`, the **dominant**
  signature (the first, highest-`count` entry) must account for the **clear majority** (~70%+) of
  failures, and its `stack` must point at a concrete, repeatable failure mode (a real assertion or
  exception with a usable stack frame). **Skip** `T` when the signatures are scattered across many
  unrelated hashes, or the dominant one is **timeout-only / has no usable stack / looks like
  infrastructure** (agent lost, disk full, port in use, OOM) — those are not test-logic bugs you can
  fix here.
- **It actually looks nondeterministic — not a 100%-consistent break.** A test that fails on
  **essentially every** def-344 run with **no** interleaved greens (a deterministic ~100% failure) is
  almost never *flaky*; it is far more likely a **real regression** (often a product behavior change)
  that merely got quarantined. Only treat such a test as fixable here if the dominant signature
  **unambiguously matches a known test-side nondeterminism pattern** (an un-awaited task/race, an
  ordering/culture/temp-path/port-collision/shared-state leak — see Step 4). If a near-100% failure has
  **no** such nondeterminism story (e.g. a plain assertion that the produced output no longer contains
  something), **skip `T` and leave it quarantined** — re-deriving a "test-only" cause for a
  consistent break risks papering over a product regression.
- **It is not trending green.** Exclude `T` when the evidence establishes a strong recent main-branch
  green window **after its latest failure** (for example, three builds across recent days).
  Aggregate `passedTests` counts over the whole window do not establish that ordering; inspect the
  passing build IDs/timestamps if needed. Interleaved failures and passes are not proof of recovery.
  `passedTests` green counts exclude PR-validation builds, including a fix PR's own.
- **It is not already covered by an open PR.** Fetch the bodies of all open `flaky-test` PRs **once**
  up front and dedup locally against **three** signals — this catches the detector's quarantine/
  un-quarantine PRs **and** this workflow's own prior fix PRs:
  ```bash
  gh pr list --repo dotnet/msbuild --state open --label flaky-test --json number,title,body,files --limit 100 > open-flaky-prs.json
  ```
  Skip `T` if **any** of these appears in an open PR: the visible key `flaky-test-id: <testName>`
  (normalized `testName`) as a **complete line** in the body — a whole-line match, so a longer name
  such as `<testName>Extended` does **not** spuriously cover `<testName>` — **or** the normalized
  `<testName>` in the title; the string
  `#<NNNN>` referencing `T`'s tracking issue; or the **test source file** you would edit (a path under
  `files`). An empty array (`[]`) is the **normal,
  expected** result — most runs have no flaky-test PR in flight; do not treat `[]` as a failure or
  re-run the command to "double-check".

From the qualifying set, take **at most 3** tests, strongest first (highest dominant-signature
`count` and `distinctBuilds`, then most distinct days), with **at most one candidate per normalized
repository source path**. Distinct methods can share a file; do not assume otherwise. Defer other
candidates from the same file so a one-file PR cannot accidentally contain multiple fixes.
If none qualify, emit a `noop` and stop.

## Step 4 — Diagnose each candidate (from evidence, no reproduction)

For each selected candidate, work entirely from the **accumulated evidence** — you do **not**
reproduce the flake. Reproduction on this single Linux/.NET runner is unreliable (it cannot run
Windows-only or `net472`-only tests, and isolated single-method loops miss the ordering/shared-state/
parallel-contention flakes that dominate).

1. Read the dominant `errorSamples` entry (and the next one or two if present): the `message` is the
   assertion/exception, the `stack` excerpt names the failing frame(s). The `legs`/`tfms` tell you
   which platforms/TFMs it fails on.
2. Open the test's source (`assemblies[0]` → project → class/method) and read the method **and any
   per-test/per-class setup it depends on** (constructor, `IClassFixture`, static fields it touches).
3. If `errorSamples` is not enough, you **may** pull deeper detail on demand for one representative
   failing build (from `rollingBuildIds`/`sampleBuildUrl`): the AzDO build **artifacts** API exposes the
   `.trx` (in `"<Leg> test logs"`) and the full xUnit console `.log` (in `"<Leg> build logs"`) — both
   anonymously, e.g.:
   ```bash
   curl -s "https://dev.azure.com/dnceng-public/public/_apis/build/builds/<id>/artifacts?api-version=7.1"
   ```
   Use this sparingly; the `errorSamples` are usually sufficient.
4. Form a concrete hypothesis of the **test-only** root cause. Typical fixable causes: a race on a
   `Task`/thread that isn't awaited/joined; a fixed `Thread.Sleep`/timeout that is too short under
   load; dependence on dictionary/enumeration/file-system ordering; culture/timezone/`DateTime`
   assumptions; shared static or environment state leaking between tests; a temp-file/temp-dir or
   port collision when tests run in parallel; a non-deterministic expected value.

If you cannot pin a single concrete **test-only** cause from the evidence, that candidate is **not
fixable here** — handle it in Step 5 as "leave quarantined".

## Step 5 — Decide: confident test-only fix, or leave quarantined

Author a fix for a candidate **only if both** are true:

- **(a)** the evidence **unambiguously** points to **one** clear root cause (a single dominant,
  diagnosable signature), **and**
- **(b)** the fix is **minimal** and **confined to the test's own source file** — the one `.cs` file
  that contains the `[ActiveIssue]`/test method. It must not require editing product code, a shared
  test helper/fixture/base class, or any other file.

You must **never** make the test pass by weakening what it checks. The following are **forbidden** —
if a candidate would need any of these, do **not** fix it, leave it quarantined:

- removing, weakening, or deleting assertions, or loosening expected values/exceptions/messages
  without evidence that the new value is correct;
- replacing a real wait/synchronization with `Thread.Sleep`/retries, or simply **increasing a
  timeout** as the only change;
- swallowing or broadening caught exceptions to hide the failure;
- removing or altering `[Fact]`/`[Theory]`/trait attributes, or the test data (removing the
  `[ActiveIssue]` line is **only** permitted in the very-high-confidence un-quarantine path of Step 5b);
- editing product (non-test) code, a shared helper/fixture, or anything outside the test's own file;
- touching anything under `.github/**` or any root manifest (`NuGet.config`, `global.json`,
  `Directory.Packages.props`, etc.).

A legitimate fix makes the test **deterministic** while preserving exactly what it verifies (await the
task instead of sleeping; normalize order only when order is explicitly outside the test's contract;
pin the culture; isolate the temp path per
test; reset the shared state in setup/teardown; etc.).

If a candidate is **not** confidently fixable (ambiguous, product-code or shared cause, or it would
need a forbidden change): take **no** code action on it. It simply **stays quarantined** — def 344
keeps gathering its signal. Do **not** post an issue comment for a skipped candidate (avoid daily
noise); only note it in the run summary.

If **no** candidate is confidently fixable, emit a `noop` and stop.

## Step 5b — Decide whether this fix is ALSO a very-high-confidence un-quarantine

For a candidate you are fixing (Step 5), additionally choose to **remove its `[ActiveIssue]`** in the
same PR **only if EVERY** condition below holds. If any is uncertain, **keep the `[ActiveIssue]`** and
take the default path — un-quarantining is normally the detector's job, so the bar here is deliberately
high.

- **Mechanistic fix, not a pattern-match.** You can state the **exact causal chain** from your change
  to *why the dominant signature can no longer occur* (e.g. "the assert ran before the writer task
  completed; awaiting it makes the value observed-after-write, so the `Expected X, got null` can't
  recur"). The change targets that **mechanism**, not a symptom or a mask, and **preserves the test's
  coverage**. If the best you can say is that it "should help", "is more robust", or merely matches a
  known category, that is **not** enough — keep the quarantine.
- **Fully-explained failures.** Your single dominant signature explains **essentially all** of the
  test's current def-344 failures. If there is **any** unexplained residual signature (a different
  message/stack that your fix does not address), do **not** un-quarantine — a "dominant" cause is not
  enough. `errorSamples` is capped at five groups; omitted signatures are not automatically explained.
- **Deterministic, textbook root cause with a complete fix.** The cause is a well-understood test-side
  nondeterminism (unawaited task, order-dependent assertion, culture/timezone, per-test temp/working-dir
  isolation, shared-state reset) and your change **fully** removes it. **Never** un-quarantine on a
  partial mitigation, a timeout/retry/sleep change, or a "makes it less likely" tweak.
- **Full-scope coverage.** The fix addresses the **entire** scope of the attribute, and standard PR CI
  will actually exercise that scope. If the attribute is platform-scoped
  (`[ActiveIssue(url, TestPlatforms.X)]`), the fix must cover platform `X` **and** normal PR CI must run
  that platform/TFM; removing an unconditional `[ActiveIssue]` re-enables the test on **all** legs, so
  every affected leg/TFM in the evidence must be one normal PR CI runs. State, in the PR body, which CI
  legs cover the affected `legs`/`tfms`. If you cannot confirm that coverage, keep the quarantine.
- **Cleanly-removable attribute.** The `[ActiveIssue]` is an isolated attribute on **this** test method
  only, so removing exactly its line leaves the rest of the file (and any other tests/attributes)
  untouched. If the formatting is ambiguous or the attribute could be shared, keep the quarantine.

When you un-quarantine, you still do **not** close the tracking issue (Step 8 keeps `Tracked by #N`).

## Step 6 — Apply each fix (keep, or at very high confidence remove, the quarantine)

For each candidate you decided to fix, start a fresh workflow-owned branch from the recorded base
SHA and apply only that candidate's minimal edit. Complete Steps 7 and 8 on that branch before
starting the next candidate; never accumulate unrelated fixes and then restore whole files from a
shared stash. Then:

- **Default (Step 5b not met):** **Leave the `[ActiveIssue]` attribute exactly as it is** — do not
  remove or narrow it. The quarantine stays so def 344 validates the fix on real CI over the next days;
  un-quarantining is then the detector's job once the test is proven green across platforms.
- **Very high confidence (Step 5b met):** **also delete the single `[ActiveIssue]` line** for this
  test, in the **same** file as the fix.

Either way, each fix (plus an optional attribute removal) changes **only that one test file**.

## Step 7 — Validate the isolated fix

Establish the SDK, runner, and target frameworks from the checked-out `global.json`,
`src/Directory.Build.props`, and `src/Directory.Build.targets`. Use the existing
[test/build guidance](../../../skills/running-unit-tests/SKILL.md) as a reference, confirming flags
against version-specific help rather than assuming a skills rewrite is present.
Compile this branch's affected test project independently.
Run a relevant targeted assertion when useful and supported, not repeated flake-reproduction loops.
Escalate to a whole-repository build only for an uncovered integration boundary.

A C# compile error in the edited file means correct or drop that fix. For a clear restore/feed/SDK
environmental failure, do not repeatedly retry or install an arbitrary SDK. The candidate PR may
still be proposed with the limitation stated explicitly and a requirement to inspect relevant CI
before merge. A compiler failure is never an environmental success-shaped fallback.

A union build cannot prove each isolated branch compiles. Nor does one local pass establish that a
timing/order/platform flake is repaired. Preserve the causal argument and the quarantine CI caveat.

## Step 8 — Open one individual ready-for-review PR per fixed test

Each fix becomes its **own** ready-for-review PR (not a combined PR). For the current branch,
first re-check dedup: re-run the Step 3
`gh pr list ... --label flaky-test` query — a concurrent detector or fixer run may have opened a PR
for one of your tests since Step 3; **drop** any test now covered (by marker, issue number, or file).

The branch already contains only this test's change from Step 6. Inspect both the file list and the
actual diff against the recorded base SHA, then commit exactly that file:

```bash
# F is the selected, verified repository source path; quote the real path when running commands.
git diff --name-only <recorded-base-sha>
git diff <recorded-base-sha> -- <F>
git add -- <F>
git commit -m "Fix flaky <testName>" -- <F>
git branch --show-current
```

Before emitting `create_pull_request`, confirm the diff lists **exactly one** intended test source
under a `src/*.UnitTests` directory and contains only this candidate's fix. If anything else appears,
correct only this workflow's edits or skip the candidate. Use the exact current local branch name in
the output and a unique supported `temporary_id`, such as `#aw_fix1`.
Do not push or probe with another PR call. The next candidate starts on its own branch from the same
recorded base SHA, not from this fix.

Each PR:

- **Title:** the normalized fully-qualified test name (the `[Flaky Test Fix] ` prefix is added automatically), e.g.
  `Microsoft.Build.Engine.UnitTests.SomeClass.SomeMethod`.
- **Body must** include the visible **flaky-test key** near the top (so the detector and future fixer
  runs detect this in-flight PR and don't double-act on the test). A visible token survives gh-aw's
  output sanitization; the old hidden `<!-- -->` markers did not. Render it as a bold label followed by
  a fenced code block, using the normalized `testName` exactly:
  ````
  **Flaky-test key** (automated de-duplication — do not edit):
  ```text
  flaky-test-id: <testName>
  ```
  ````
  If you removed the `[ActiveIssue]` via Step 5b, state that explicitly in the PR body prose (see
  below) — do not rely on any hidden marker to convey it.
- Then:
  - **Root cause:** the concrete test-only nondeterminism you identified.
  - **Evidence:** the def-344 data that supports it — the dominant signature (message + the key stack
    frame), how often / over how many builds and days it recurred, and the affected legs/TFMs.
  - **The fix:** what the minimal change does and **why it preserves exactly what the test verifies**.
  - **`Tracked by #<NNNN>`** (never a closing keyword — `Fixes`/`Closes`/`Resolves` are forbidden
    here; the tracking issue must stay open even when you un-quarantine).
  - **The quarantine caveat — pick the variant matching what you did:**
    - **Kept `[ActiveIssue]` (default):** **"This is a candidate fix. The `[ActiveIssue]` quarantine is
      intentionally retained. Normal CI excludes this test; the PR-triggered quarantine pipeline
      (definition 344) can run it before merge. Inspect its individual test results, not just its job
      conclusion. After merge, main-branch quarantine runs supply the multi-day, multi-platform
      evidence the detector needs before un-quarantining.
      Please do not merge until a reviewer agrees the test's semantics are preserved."**
    - **Removed `[ActiveIssue]` (very high confidence, Step 5b):** **"High-confidence fix: the
      `[ActiveIssue]` quarantine has been removed in this PR, so normal PR CI now runs this test and its
      result here is *additional* pre-merge validation — list the CI legs/TFMs expected to cover the
      affected legs above. This is extra confidence, not final proof: it does not replace definition
      344's multi-day, multi-platform signal. Please confirm CI is green across the relevant legs and
      that the test's semantics are preserved before merging. The tracking issue is left open for
      follow-up."**
  - If the Step 7 build was blocked by the environment, say so.
- Declare **one** `add_comment` on that test's tracking issue (`#<NNNN>`) summarizing the proposed fix
  and using the matching temporary reference (`#aw_fix1` in this example). Safe outputs resolve it
  after creating the PR. Do not poll for a real PR number or claim that downstream creation succeeded.

## Important

- Respect the caps: at most **3** fix PRs per run (`create-pull-request` max 3), at most **3**
  issue comments (`add-comment`, one per fix PR). Prioritize the strongest, most consistently-failing
  candidates.
- **Quarantine handling.** By default **keep every `[ActiveIssue]` in place**; remove one **only** in
  the very-high-confidence un-quarantine path (Step 5b), and even then only the single `[ActiveIssue]`
  line on the fixed test. This workflow **never** files or closes issues, and **never** edits product
  code, shared test helpers, `.github/**`, or root manifests. Each PR edits exactly **one** test source
  file.
- Every PR is a **ready-for-review** PR based on `main`, and is a **candidate** fix pending human review — validated
  by def 344 after merge (default), or additionally by the PR's own CI when you un-quarantined (Step
  5b). Never write a closing keyword before the tracking-issue reference.
- The JSON report is the **only** source of truth — never invent failures, evidence, or test names.
