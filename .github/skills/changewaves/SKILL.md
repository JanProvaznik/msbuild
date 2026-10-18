---
name: changewaves
description: 'Manage MSBuild Change Waves: create new waves, condition features behind opt-out flags, write tests for wave-gated features, document change waves in ChangeWaves.md, and retire expired waves. Use when adding changes that need an opt-out or rotating out old change waves. Whether a change needs a wave at all is decided in assessing-breaking-changes.'
argument-hint: 'Add, query, or remove changewaves and changewave checks.'
---

# Managing MSBuild Change Waves

A Change Wave is an opt-out flag that groups risky features together. Users disable features by setting the environment variable `MSBUILDDISABLEFEATURESFROMVERSION` to the wave version. This skill is the mechanics only: creating a wave, conditioning code on it, testing, documenting, and retiring.

Whether a change needs a wave at all is decided in [assessing-breaking-changes](../assessing-breaking-changes/SKILL.md), starting from its `compat-scan.sh`; come here once that answer is "yes".

Background for the steps below: [ChangeWaves-Dev.md](../../../documentation/wiki/ChangeWaves-Dev.md) (developer-facing) and [ChangeWaves.md](../../../documentation/wiki/ChangeWaves.md) (public-facing).

## Step 1: Determine the Correct Wave Version

The wave version is the `VersionPrefix` in `eng/Versions.props`, truncated to major.minor:

```bash
grep -m1 '<VersionPrefix>' eng/Versions.props
grep -nE 'Wave[0-9]+_[0-9]+ = new Version|AllWaves' src/Framework/ChangeWaves.cs
```

If `WaveMM_NN` for that version already exists in `src/Framework/ChangeWaves.cs`, use it. Otherwise add a new `internal static readonly Version WaveMM_NN = new Version(MM, NN);` field after the last one and append it to `AllWaves` (the array must stay in ascending order). Do not pick a wave older than the current version: a wave that has already shipped cannot gate new behavior.

## Step 2: Condition Your Feature on the Wave

C# and MSBuild examples are in [ChangeWaves-Dev.md](../../../documentation/wiki/ChangeWaves-Dev.md). Prefer to put checks inline rather than abstracting out a method to host the check.

```csharp
if (ChangeWaves.AreFeaturesEnabled(ChangeWaves.Wave18_11))
{
    // new behavior
}
else
{
    // behavior exactly as before this change
}
```

The `else` branch must be the old code, unchanged. Do not "improve" it while you are there; that defeats the opt-out.

`.github/skills/assessing-breaking-changes/compat-scan.sh` detects the gate by the `ChangeWaves.AreFeaturesEnabled` text in added product lines and checks that `documentation/wiki/ChangeWaves.md` changed too. Re-run it after Step 4 and expect `RESULT: REVIEW` or `PASS`, not `FAIL`.

## Step 3: Write Tests

Write normal tests for the new behavior. Then add at least one test that verifies the old behavior is **preserved** when the wave is opted out. The pattern used across the test projects:

```csharp
using TestEnvironment env = TestEnvironment.Create(_output);
ChangeWaves.ResetStateForTests();
env.SetEnvironmentVariable("MSBUILDDISABLEFEATURESFROMVERSION", ChangeWaves.Wave18_11.ToString());
BuildEnvironmentHelper.ResetInstance_ForUnitTestsOnly();
// ... build or evaluate, then assert the OLD behavior ...
```

All three lines are needed: ChangeWaves caches its state statically, so a stale value leaks between tests otherwise. `TestEnvironment.Dispose` clears the variable and resets the cache again. The canonical helper is `SetChangeWave` in `src/Build.UnitTests/ChangeWaves_Tests.cs`; copy its shape. Run both tests with `.github/skills/verifying-changes/verify-change.sh --filter "*YourTestName*"`.

## Step 4: Document the Feature

Add an entry to the **Current Rotation of Change Waves** section in [ChangeWaves.md](../../../documentation/wiki/ChangeWaves.md).

- If a heading for this wave already exists, add a bullet under it.
- If not, add a new `### {Major}.{Minor}` heading at the top of the current rotation list.

Each entry is a bullet with a link to the PR:

```markdown
### 18.6
- [Short description of the change.](https://github.com/dotnet/msbuild/pull/NNNNN)
```

## Only when explicitly requested: Retire an Expired Wave

Waves rotate out when a new major .NET version will be released. When retiring a wave, follow the detailed description in [ChangeWaves-Dev.md](../../../documentation/wiki/ChangeWaves-Dev.md).

## Checklist

- [ ] Wave decision recorded per `assessing-breaking-changes` (PR Compatibility section)
- [ ] Correct wave version chosen from `eng/Versions.props`
- [ ] Wave field and `AllWaves` entry added in `ChangeWaves.cs` (if new wave)
- [ ] Feature code wrapped with `AreFeaturesEnabled`
- [ ] Test verifying opt-out disables the feature
- [ ] Entry added to `documentation/wiki/ChangeWaves.md`
