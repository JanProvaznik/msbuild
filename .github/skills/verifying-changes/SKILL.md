---
name: verifying-changes
description: 'Run the scoped build-and-test verification for a working-tree change in dotnet/msbuild and read its verdict. Use before claiming any change is done, when asked "does this build", "run the tests for my change", or when another skill says to run verify-change.sh. Builds touched projects with warnings as errors and runs the mapped unit test projects.'
argument-hint: 'Optional: a test name filter such as *MyFeature*.'
---

# Verifying a Change

One command, one verdict. Do not substitute ad-hoc `dotnet build` or `dotnet test` calls for it when you are deciding whether a change is done.

```bash
.github/skills/verifying-changes/verify-change.sh --filter "*MyTest*"
```

The script:

1. Lists files changed relative to `HEAD` (tracked and untracked).
2. Maps them to test projects. `src/Shared/**` maps to Build, Tasks, and Utilities tests because the code is linked into all three.
3. Builds each test project (and so its dependencies) with `-warnaserror`. Official builds treat warnings as errors, so a local warning is a CI failure. NuGet audit advisories (`NU1901`-`NU1904`) are excluded because they change with the vulnerability feed, not with your code; if you see one, mention it to the user but do not try to fix it in your change.
4. Runs the tests with the xUnit v3 method filter, if given.
5. Prints `RESULT: PASS` or `RESULT: FAIL` as the last line.

## How to read the output

- Quote the `RESULT:` line and each `Test run summary` line in your message. That is the evidence the user needs.
- `BUILD FAILED` with a `warning` line: fix the warning. Do not pass `--no-warnaserror`; there is no such flag on purpose.
- `WARNING: the filter matched zero tests`: your filter is wrong or your test did not compile into the project. Fix it; a zero-test pass is not a pass.
- A rise in `Skipped` compared to a run before your change means a test stopped applying. Find out why.
- The script keeps logs in `artifacts/log/verify-change/`. Read the named log for detail; do not re-run with output redirected elsewhere.

## Scope rules

| Change | Run |
|--------|-----|
| Iterating on one test | `--filter "*TestName*"` |
| Bug fix, before saying done | No filter; whole mapped projects |
| Engine, Framework, or Shared change | No filter, then `./build.sh --test` if the diff touches more than one assembly |
| Docs or `.github` only | Script prints PASS with nothing to build; that is correct |

Options: `--build-only` (compile check), `--projects a.csproj,b.csproj` (override the mapping), `--all-tfms` (Windows only: add net472).

## Prerequisites

- One full `./build.sh` (or `.\build.cmd`) has been run on this clone so `.dotnet/` and `artifacts/sdk-build-env.sh` exist.
- If the build reports "Could not resolve SDK", run `source artifacts/sdk-build-env.sh` and retry.
- If the build reports locked files, run `./artifacts/bin/bootstrap/core/dotnet build-server shutdown`.

For end-to-end verification against a repro project, use the `use-bootstrap-msbuild` skill after this one passes.
