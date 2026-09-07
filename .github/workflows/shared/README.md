# Agentic workflow maintenance

This guide covers the workflow sources and their shared procedures independently of changes to
repository-wide instructions or skills. Reading it or invoking a reviewer locally does not authorize
GitHub writes, installs, or workflow runs.

## Entry points and runtime

| Source | Trigger and authorized result |
|---|---|
| [Review command](../review.agent.md) | Authorized `/review` PR comment; one coordinator publishes configured review outputs |
| [Review on open](../review-on-open.agent.md) | Authorized, non-draft opened/ready PR; trusted base checkout, API-read head code |
| [Detector](../flaky-test-detector.agent.md) | Upstream `main` schedule/manual run; bounded tracking issues/comments and one attribute-only test PR |
| [Fixer](../flaky-test-fixer.agent.md) | Upstream `main` schedule/manual run; up to three isolated one-test-file candidate PRs |
| [Dreaming](../dreaming.agent.md) | Upstream `main` weekly/manual run; small instruction proposals, no unsolicited mentions |
| [Stale PRs](../close-stale-prs.agent.md) | Repository schedule/manual run; warnings, then eligible closures after the grace period |

These run in gh-aw's Linux environment, with the tools declared in frontmatter and generated MCP
configuration. They do not inherit every plugin or MCP server loaded in a maintainer's local CLI.
The [Copilot setup workflow](../copilot-setup-steps.yml) configures Copilot cloud-agent development
sessions; it is not automatically a gh-aw engine setup step.

GitHub writes use safe outputs only. Successful declarations are applied later; use supported
temporary references for same-run links and do not poll, probe, push, or switch to direct API writes.
PR creation does not by itself prove CI ran. Quarantine CI's per-test results matter even when its
overall job succeeds.

## Focused procedural references

- [Detector evidence, issue matching, quarantine scope, and templates](references/flaky-test-detector.md).
- [Fix candidate gates, causal diagnosis, isolation, and caveats](references/flaky-test-fixer.md).
- [Learning-atom evidence, ownership, and atomic proposals](references/dreaming.md).
- [PAT pool lifecycle and configuration](pat_pool.README.md).

Read the section for the current phase, not all references. Keep scan/output gates in entry points;
retain detailed matching recipes, platform caveats, and diagnostic examples in references.

## Sources and generated wiring

Edit `.agent.md` and shared source files, not `.agent.lock.yml` or the action-lock JSON by hand.
Use the compiler version recorded in the existing lock metadata; check its actual help before
assuming current documentation applies to an older installed CLI.

```bash
gh aw --version
gh aw compile --help
gh aw compile <changed-workflow-ids> --no-emit --no-check-update --schedule-seed dotnet/msbuild
gh aw compile <changed-workflow-ids> --no-check-update --schedule-seed dotnet/msbuild
```

The first compile checks sources; the second regenerates their locks. A matching session-local
release compiler is preferable to silently downgrading locks or replacing global tooling.
If it is unavailable, report the exact missing version and do not describe source-only changes as
deployed. Inspect emitted triggers, trusted refs, imports, tool/output scopes, and action pins.
Do not use `gh aw run`, dispatch workflows, or create trial PRs without separate authorization.

With gh-aw v0.88.2, leave `--models` disabled. These workflows do not select fixed models; the
compiler skips external pricing lookup for an unset model or the `auto` alias. Recheck that behavior
before introducing a model override or changing compiler versions.

For PR-comment workflows, an initial trusted checkout is insufficient if a later generated step
checks out the PR head. The review command disables automatic checkout and supplies an explicit
trusted step; inspect the whole generated job when changing that configuration.

The paired direct/nested test-source allowlist patterns are intentional. Check the deployed glob
matcher before changing them; do not assume `**/` matches an empty directory segment. The fixer also
requires one candidate per source path and a one-file patch, not just a directory-name heuristic.

For documentation edits, inspect `git diff --check`, required frontmatter, and changed local
Markdown links. This workflow change does not depend on a separate context-validation workflow,
helper script, or skills rewrite. Local compilation does not exercise model inference, credentials,
the sandbox, or downstream GitHub mutations.

## Provider boundaries

Repository plugin settings, standalone agents, and skills are unchanged by this AW-only work.
Their presence or activation in a local CLI does not establish the tools available in a gh-aw job;
use its frontmatter and generated configuration. Do not install providers, edit caches, or change
personal settings to make a workflow appear functional.
