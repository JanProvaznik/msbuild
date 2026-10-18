---
name: assessing-breaking-changes
description: 'Guides assessment of backward compatibility for MSBuild changes. Consult when modifying behavior, adding warnings or errors, changing defaults, altering target ordering, removing or deprecating features, deciding whether a change needs a ChangeWave, reviewing blast radius of behavioral changes, or when a PR introduces user-visible output differences.'
argument-hint: 'Describe the change and its potential compatibility impact.'
---

# Backward Compatibility in MSBuild

Backward compatibility is the default — any change that could alter existing build behavior must be explicitly justified.

This is the one place that decides whether a change is compatible and whether it needs a ChangeWave. Once the answer is "wave", the [changewaves skill](../changewaves/SKILL.md) has the mechanics; for the wording and severity of a new diagnostic, see `authoring-errors-and-warnings`.

## Core Philosophy

1. **Existing builds must not break.** If a project built successfully yesterday, it must build successfully today with identical semantics.
2. **New warnings are breaking changes.** Builds that use `-WarnAsError` or `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` will fail if you introduce a new warning. Always gate new warnings behind a ChangeWave or emit them as `Message` importance instead.
3. **Output changes are behavioral changes.** Even "improvements" to output formatting, file paths, or diagnostic text can break downstream consumers that parse MSBuild output.
4. **Removal is nearly impossible.** Never remove CLI switches, public APIs, or property names. Deprecate with warnings first, then gate removal behind a ChangeWave after multiple release cycles.

## Start With the Mechanical Scan

Run this before forming an opinion. It reads the working-tree diff and prints one `SIGNAL` line per compatibility lead, a `WAVE:` verdict, and a `RESULT:` line.

```powershell
.\.github\skills\assessing-breaking-changes\compat-scan.ps1   # pwsh .github/... on Linux/macOS, including the Copilot coding agent
```

| It prints | You do |
|-----------|--------|
| `RESULT: PASS` | Write "no user-visible behavior change" in the PR and move on. |
| `RESULT: REVIEW` | Answer every `SIGNAL` in the PR's Compatibility section, one line each: what changes for whom, and why that is acceptable or how it is gated. |
| `RESULT: FAIL` | A new warning, changed default, removed switch or API, or pinned infra file with no `ChangeWaves.AreFeaturesEnabled` gate. Either add the gate (load the `changewaves` skill) or, if the signal is inside new opt-in behavior, re-run with `-Acknowledge "<reason>"` so the reason is on record. |

The scan is a heuristic. `public` members on internal types and warnings inside new opt-in features are false positives; say so explicitly rather than silently ignoring them. The scan cannot see semantic changes that keep the same API shape, so still walk the checklist below.

## Blast Radius Checklist

Before merging any behavioral change, evaluate:

| Question | If Yes |
|----------|--------|
| Does this change what gets built or how? | ChangeWave required |
| Does this add a new warning? | ChangeWave required (WarnAsError impact) |
| Does this change a property default? | ChangeWave required (existing .csproj files depend on defaults) |
| Does this alter target execution order? | Test with real-world solutions; likely ChangeWave |
| Does this change console or binlog output format? | Consider downstream tool impact |
| Does this affect only internal code paths with no user-visible effect? | No ChangeWave needed |
| Is this a pure bug fix restoring documented behavior? | Usually no ChangeWave; use judgment on blast radius |

## The Warnings-as-Errors Rule

This is the most commonly missed compatibility concern:

```xml
<!-- Many enterprise builds set this globally -->
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
```

Any new `MSBxxxx` warning you add will **break these builds**. Options:
1. **Gate behind ChangeWave** — preferred for genuinely important warnings
2. **Use `MessageImportance.Low` or `Normal`** — for informational diagnostics
3. **Add as an error from the start** — if the condition is always wrong

## Deprecation Protocol

1. Add a deprecation warning (behind ChangeWave if broad impact)
2. Document the deprecation in release notes
3. Maintain the old behavior for at least two major .NET versions
4. Only remove after the ChangeWave has rotated out

## Compatibility Test Matrix

When testing backward compatibility, verify:

- **Multi-targeting projects** — `<TargetFrameworks>net472;net8.0</TargetFrameworks>`
- **Solution builds with mixed project types** — C#, F#, VB, C++/CLI
- **Incremental builds** — second build should be a no-op
- **Design-time builds** — Visual Studio calls different target contracts
- **Cross-platform** — path separator differences, case sensitivity on Linux
- **WarnAsError builds** — explicitly test with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`

## Decision Framework

```
Is the change user-visible?
├── No (internal refactor, perf work with identical semantics) → Ship it, no ChangeWave
└── Yes
    ├── Is it new behavior the user must opt into (new property, switch, or API)? → Ship it, no ChangeWave
    └── Does it change existing behavior?
        ├── Bug fix restoring clearly intended behavior, low blast radius → Ship it, add a regression test
        └── Behavioral change or new warning → Gate behind a ChangeWave, test the opt-out path
```

Write the decision and its reason in the PR's Compatibility section, one line per `SIGNAL` the scan printed, so reviewers can check it. The `changewaves` and `authoring-errors-and-warnings` checklists point back to this line.

For detailed ChangeWave mechanics, see [ChangeWaves-Dev.md](../../../documentation/wiki/ChangeWaves-Dev.md) and [ChangeWaves.md](../../../documentation/wiki/ChangeWaves.md).
