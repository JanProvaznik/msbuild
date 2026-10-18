---
name: verifying-changes
description: 'Run the scoped build-and-test verification for a working-tree change in dotnet/msbuild and read its verdict. Use before claiming any change is done, when asked "does this build", "run the tests for my change", or when another skill says to run verify-change.ps1. Builds touched projects with warnings as errors and runs the mapped unit test projects.'
argument-hint: 'Optional: a test name filter such as *MyFeature*.'
---

# Verifying a Change

One command, one verdict. When deciding whether a change builds and its tests pass, run this instead of ad-hoc `dotnet build` or `dotnet test` calls:

```powershell
.\.github\skills\verifying-changes\verify-change.ps1 -Filter "*MyTest*"   # pwsh .github/... on Linux/macOS, including the Copilot coding agent
```

Whether a change is *done* is defined in one place, Phase 4 of `msbuild-change-workflow`: this script with no `-Filter`, then `compat-scan.ps1`. This skill explains the script.

## What the script does

1. Lists files changed relative to `HEAD` (tracked and untracked).
2. Maps them to test projects. `src/Shared/**` maps to Build, Tasks, and Utilities tests because the code is linked into all three.
3. Builds each test project (and so its dependencies) with `-warnaserror`. Official builds treat warnings as errors, so a local warning is a CI failure. NuGet audit advisories (`NU1901`-`NU1904`) stay warnings because they change with the vulnerability feed, not with your code; mention one if you see it, but do not fix it in your change.
4. Runs the tests with the xUnit v3 method filter, if given.
5. Prints `RESULT: PASS` or `RESULT: FAIL` as the last line.

## How to read the output

- Quote the `RESULT:` line and each `Test run summary` line in your message. That is the evidence the user needs.
- `BUILD FAILED` with a `warning` line: fix the warning. The script has no switch to downgrade warnings because CI has none either.
- `WARNING: the filter matched zero tests`: your filter is wrong or your test did not compile into the project. A zero-test pass is not a pass.
- A rise in `skipped` compared to a run before your change means a test stopped applying. Find out why.
- Logs go to `artifacts\log\verify-change\`. Read the named log for detail; do not re-run with output redirected elsewhere.

## Parameters

| Parameter | Use |
|-----------|-----|
| `-Filter "*Name*"` | Iterate on one test. Omit it for the Gate 4 run so the whole mapped projects execute. |
| `-BuildOnly` | Compile check with warnings as errors, no test run. |
| `-Projects a.csproj,b.csproj` | Override the file-to-project mapping. |
| `-AllTfms` | Windows only: run `net472` as well as `net11.0`. |

A docs-only or `.github`-only change maps to nothing; the script prints `RESULT: PASS` with nothing to build, which is correct.

## Prerequisites

- One full `.\build.cmd` (or `./build.sh`) has been run on this clone so `.dotnet\` and the bootstrap exist.
- If the build reports "Could not resolve SDK", run `.\artifacts\msbuild-build-env.ps1` (`source artifacts/sdk-build-env.sh` on macOS/Linux) and retry.
- If the build reports locked files, run `.\artifacts\bin\bootstrap\core\dotnet build-server shutdown`.

## Related

- End-to-end verification against a repro project: `use-bootstrap-msbuild`, after this passes.
- Test-runner mechanics beyond this script (MTP flags, running a test exe directly, dev-loop speedups): `running-unit-tests`.
