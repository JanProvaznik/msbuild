---
name: reviewing-msbuild-code
description: "Reviews an MSBuild diff, branch, or PR by running narrow reviewer agents in parallel (compatibility, concurrency, performance, tests, correctness) and verifying every finding against the code before reporting it. Use for code review, PR review, self-review before opening a PR, or when asked whether a change is safe to merge."
argument-hint: 'Optional: PR number, branch, or "working tree" (default).'
---

# Reviewing MSBuild Code

Five reviewer agents under `.github/agents/` (`msbuild-*-reviewer.agent.md`) each own a few of the repo's review dimensions; `msbuild-finding-verifier` then checks every finding against the code. The full 24-dimension methodology lives in `.github/agents/expert-reviewer.agent.md` and is the source of the rules the agents carry; consult it when a finding does not fit any of the five.

## Step 1: Get the diff in the working tree

- Working tree (default): nothing to do.
- A branch: `git fetch origin <branch> && git checkout <branch>`.
- A PR: `gh pr checkout <number>`. Also run `gh pr view <number> --json title,body,url` and keep the description; the reviewers need the stated intent.

Confirm with `git status --short` that the diff you are about to review is the one the user means.

## Step 2: Pick reviewers from the touched folders

Always run `msbuild-correctness-reviewer` and `msbuild-test-reviewer`. Add the others by folder:

| Touched path | Add |
|--------------|-----|
| `src/Build/BackEnd/**`, `src/Build/Instance/**`, `src/Shared/**`, anything with `static`, `lock`, cache, or `ITranslatable` | `msbuild-concurrency-reviewer` |
| `src/Build/Evaluation/**`, `src/Shared/**`, `src/StringTools/**`, `TaskExecutionHost.cs`, `FileMatcher.cs` | `msbuild-perf-reviewer` |
| `src/Build/**`, `src/Tasks/**`, `src/MSBuild/**`, `src/Framework/**`, `*.targets`, `*.props`, `Strings.resx` | `msbuild-compat-reviewer` |

When in doubt, run all five. They are cheap compared with a missed BLOCKING finding.

## Step 3: Launch them in parallel

Run each one as a subagent with the `agent` tool (`runSubagent`), naming the custom agent, and issue all the calls in one turn so they run concurrently. Give every agent the same brief:

> Review the current working tree diff against HEAD. PR intent: <one paragraph from the PR description or the user>. Report in your fixed format. Do not fix anything.

Wait for all of them. Do not start reading results until every agent has returned; partial results tempt you to skip the rest.

## Step 4: Verify every ISSUE

For each `ISSUE` block from any reviewer, launch one `msbuild-finding-verifier` with the block pasted verbatim. Launch them in parallel. Keep only `CONFIRMED` findings. Record `DISPUTED` findings with their evidence in a short appendix so the user can see what was examined.

Two reviewers often report the same root cause from different angles. Group by `FILE` + `LINES` before launching verifiers, and verify each root cause once.

## Step 5: Report

Order by severity: BLOCKING, MAJOR, MODERATE, NIT. For each confirmed finding give the file and line as a clickable reference, the scenario or input, and the recommendation. Then:

```
Reviewers run: correctness, tests, compat, concurrency, perf
Findings: N confirmed, M disputed (see appendix)
Verdict: REQUEST_CHANGES | COMMENT
```

Any BLOCKING finding means REQUEST_CHANGES. Never say APPROVE; an agent review does not count as approval in this repo.

If asked to post to a PR: write the report to a file, show it to the user, then post it with `gh pr review <number> --comment --body-file report.md` (or `--request-changes`). Never resolve or dismiss threads opened by humans.

## Step 6: If you are also the author

When reviewing your own change (Phase 5 of `msbuild-change-workflow`), fix every CONFIRMED BLOCKING or MAJOR finding, re-run `.github/skills/verifying-changes/verify-change.sh`, and re-run only the reviewers whose dimension you touched. List the rest under "Notes" in the PR description.
