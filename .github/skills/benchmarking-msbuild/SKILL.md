---
name: benchmarking-msbuild
description: Guide for benchmarking dotnet build / MSBuild performance across SDK versions. Use this when asked to measure build performance, compare SDK versions, evaluate -mt flag impact, or investigate build regressions. Covers SDK acquisition, environment isolation, ColdFull/HotIncr methodology, and common pitfalls.
---

# Benchmarking MSBuild / .NET SDK Build Performance

This skill captures hard-won knowledge for benchmarking `dotnet build` performance across SDK versions, including `-mt` (multi-threaded) mode evaluation. It covers SDK acquisition, environment isolation, benchmark methodology, and common pitfalls that produce misleading numbers.

## 1. Acquiring SDK Versions

### Daily CDN Builds (preferred for apples-to-apples comparison)

The .NET VMR (Virtual Monolithic Repository — `dotnet/dotnet`) publishes daily SDK builds to `ci.dot.net`. These are the best source for timeline comparisons because they use the same build pipeline and optimization settings.

**Version scheme:** `11.0.100-preview.N.YMMDD.RRR`
- `Y` = last digit of year (6 = 2026)
- `MMDD` = commit date (e.g., `0213` = Feb 13)
- `RRR` = revision within that day (multiple builds possible)

**Discovery pattern — probe the CDN:**
```powershell
$base = "https://ci.dot.net/public/Sdk"
$ver = "11.0.100-preview.2.26113.117"
# Check existence:
Invoke-WebRequest "$base/$ver/sdk-productVersion.txt" -Method Head
# Get VMR commit:
Invoke-RestMethod "$base/$ver/productCommit-win-x64.json"
# Download:
$url = "$base/$ver/dotnet-sdk-$ver-win-x64.zip"
```

Multiple revisions per day may exist. Probe systematically (revisions 101–120) to find what's available. There is **no directory listing API** — you must probe by version string.

**Installation via dotnet-install.ps1:**
```powershell
.\dotnet-install.ps1 -Version "11.0.100-preview.2.26113.117" -InstallDir "Q:\bench\sdk-feb13" -Architecture x64
```

### AzDO Pipeline Artifacts (fallback for older builds)

Some SDK versions only exist as AzDO build artifacts from the `dotnet-unified-build` pipeline (definition 278, project `dnceng-public/public`). These produce `-ci` suffixed versions.

**⚠️ CRITICAL PITFALL:** AzDO `-ci` builds and CDN `preview.N` builds **are not directly comparable**. They use different build configurations and produce DLLs with different sizes (~5-9% difference). Mixing them in a timeline comparison creates a confound.

```powershell
# Find builds in a date range:
$url = "https://dev.azure.com/dnceng-public/public/_apis/build/builds?definitions=278&branchName=refs/heads/main&minTime=2026-02-10T00:00:00Z&maxTime=2026-02-15T00:00:00Z&statusFilter=completed&api-version=7.1"
# Most main-branch builds FAIL overall, but individual jobs (Windows_x64) often SUCCEED
# Check for artifacts:
$url = "https://dev.azure.com/dnceng-public/public/_apis/build/builds/{buildId}/artifacts?api-version=7.1"
# Windows_x64_Artifacts is ~4.8 GB (entire build output)
# SDK zip is buried at: Windows_x64_Artifacts/assets/Release/Sdk/11.0.100-preview.1/dotnet-sdk-11.0.100-ci-win-x64.zip
```

The VerticalManifests artifact is small (~1 MB, Container type, direct download) and contains the VMR commit hash — useful for verification without downloading the full 4.8 GB artifact.

### Verifying What You Got

Always verify after installation:
```powershell
& "$sdkDir\dotnet.exe" --version           # SDK version string
& "$sdkDir\dotnet.exe" msbuild --version   # MSBuild version
# VMR commit from .version file:
Get-Content "$sdkDir\sdk\*\.version"
# Roslyn version:
[System.Diagnostics.FileVersionInfo]::GetVersionInfo("$sdkDir\sdk\*\Roslyn\bincore\Microsoft.CodeAnalysis.dll").ProductVersion
# DLL sizes as R2R proxy:
Get-ChildItem "$sdkDir\sdk\*\Microsoft.Build.dll" | Select-Object Length
```

---

## 2. Environment Isolation

### SDK Pinning (MANDATORY)

Without pinning, `dotnet build` may resolve a globally-installed SDK instead of the one you intend. **Every benchmark process must set these environment variables:**

```powershell
$psi.EnvironmentVariables["DOTNET_ROOT"] = $sdkDir
$psi.EnvironmentVariables["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"] = $sdkDir
$psi.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"] = "0"
```

### Process Isolation

MSBuild runs a persistent server process (`dotnet build-server`) that caches state across builds. This is desirable for HotIncr benchmarks but must be controlled:

- **ColdFull scenario:** Kill ALL dotnet/MSBuild/VBCSCompiler processes before each run
- **HotIncr scenario:** Keep processes alive between runs, but kill and restart when switching SDK configs
- **Between configs:** Always `dotnet build-server shutdown` + `Stop-Process` for dotnet/MSBuild/VBCSCompiler

```powershell
Get-Process -Name "dotnet","MSBuild","msbuild","VBCSCompiler" -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2  # Wait for process tree teardown
```

---

## 3. Benchmark Scenarios

### ColdFull (Cold Full Build)

Measures end-to-end build time from a completely clean state.

1. Kill all dotnet/MSBuild/VBCSCompiler processes
2. Delete ALL `bin/` and `obj/` directories recursively under the project
3. Run `dotnet build` (includes implicit restore)
4. Time the entire `dotnet build` invocation

**⚠️ PITFALL: `dotnet clean` is NOT a true clean.** It leaves `obj/` cache files (project.assets.json, .nuget.*, etc.) that affect subsequent build times. Always delete bin/obj directories directly:

```powershell
Get-ChildItem $projectRoot -Directory -Recurse -Include "bin","obj" |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
```

### HotIncr (Hot Incremental / No-op Rebuild)

Measures the overhead of MSBuild determining "nothing changed." This is highly sensitive to SDK infrastructure changes (up-to-date checks, target evaluation, Roslyn analyzer loading).

1. Start from a successful build (all outputs present)
2. **Run 2-3 warmup builds** (discarded) to stabilize JIT and MSBuild server
3. Run `dotnet build` with no file changes
4. Time the invocation

The warmup is critical — the first no-op after server start pays JIT cost for MSBuild server + Roslyn + analyzers. Without warmup, your first 1-2 data points will be outliers.

### What About `--no-restore`?

For ColdFull: **Do NOT use `--no-restore`** unless you're specifically isolating build-vs-restore time. Users experience the full `dotnet build` including restore.

For HotIncr: Restore is a no-op anyway (packages already cached), so `--no-restore` makes negligible difference.

If you need to measure restore separately, run `dotnet restore` as a timed operation on its own.

---

## 4. The `-mt` Flag

`-mt` enables MSBuild's multi-threaded project building mode (parallel project graph execution). It's passed directly to `dotnet build`:

```
dotnet build MySolution.slnx -mt
```

**NOT the same as `-m`** (which is `/maxcpucount` — parallel *task* execution within a project). `-mt` is about building multiple *projects* simultaneously.

### What We Found About `-mt`

- `-mt` was always slower on OrchardCore — overhead ranges from +5% to +87% depending on scenario and SDK version
- The overhead is worst for HotIncr (no-op rebuilds) because `-mt` adds coordination overhead with no actual parallelism benefit
- For ColdFull, the overhead is smaller but still positive — the project dependency graph limits parallelism
- **`-mt` with raw MSBuild.exe (not `dotnet build`) has a concurrency crash bug** — `Dictionary.Insert` in `WorkerNodeTelemetryData`. 80-100% crash rate.

---

## 5. Statistical Methodology

### Minimum 10 Runs Per Configuration

Build times have natural variance (3-9% CV for large projects). With fewer than 10 runs, you can't distinguish real regressions from noise.

### Use Mean, Not Median

For reporting: use **mean**. Median is more robust to outliers but less sensitive to real shifts. If you have outliers, report them separately rather than hiding them behind median.

### Report Stdev and CI95

```powershell
$ci95 = 1.96 * $stdev / [math]::Sqrt($n)
```

Two configs are significantly different if their CI95 intervals don't overlap (conservative test).

### Coefficient of Variation (CV%)

`CV = stdev / mean * 100`. For build benchmarks:
- CV < 3%: Excellent (small projects, HotIncr with warm server)
- CV 3-9%: Acceptable (large projects, ColdFull)
- CV > 10%: Investigate — background interference, thermal throttling, or methodological issue

### Randomize Config Order

Running all 10 runs of config A, then all 10 of config B introduces time-of-day bias (thermal state, background processes, Windows updates). Randomize which config runs when:

```powershell
$shuffled = $configs | Get-Random -Count $configs.Count
```

### Retry on Failure

Build failures happen (transient NuGet errors, file locks). The benchmark should retry until it gets N successful runs, not fail after N attempts:

```powershell
while ($successCount -lt $target -and $attemptNum -lt $maxAttempts) {
    $attemptNum++
    $r = Run-Build ...
    Write-Row $label $scenario $attemptNum $r  # Log ALL attempts including failures
    if ($r.Ok) { $successCount++ }
}
```

---

## 6. Common Pitfalls & Lessons Learned

### Pitfall: Comparing SDKs from Different Build Pipelines

AzDO `-ci` builds vs CDN `preview.N` builds produce different binaries. DLL sizes differ, R2R compilation may differ, optimization flags may differ. **Always compare SDKs from the same source** (all CDN or all AzDO).

In our benchmarks, the jan27 AzDO `-ci` SDK was ~5-15% slower than feb11-14 CDN `preview.2` SDKs — this difference disappeared between feb11-14, confirming it was a pipeline artifact, not a real regression.

### Pitfall: `dotnet clean` Leaves Cache Files

`dotnet clean` removes build outputs but leaves `obj/project.assets.json`, NuGet lock files, and other cached state. Subsequent "clean" builds are faster than truly cold builds because restore metadata is cached. **Delete bin/obj directories for true cold benchmarks.**

### Pitfall: ReadToEndAsync Deadlock

When running `dotnet build` via `Process.Start()` with redirected stdout/stderr, you **must** consume the output asynchronously. If the output buffer fills up, the child process blocks forever:

```powershell
$outTask = $p.StandardOutput.ReadToEndAsync()
$errTask = $p.StandardError.ReadToEndAsync()
$p.WaitForExit($timeout)
# MUST drain even if you don't need the output:
[void]$outTask.Result
[void]$errTask.Result
$p.Dispose()
```

### Pitfall: `$args` is a Reserved Variable in PowerShell

`$args` is an automatic variable in PowerShell. Using it as a local variable name silently shadows it, causing subtle bugs. Use `$buildArgs` or `$cmdArgs` instead.

### Pitfall: MSBuild Server Persistence Across Configs

The MSBuild server process is SDK-version-specific, but if you don't explicitly kill it between config switches, a stale server from a previous SDK may interfere. Always kill and wait:

```powershell
& $dotnet build-server shutdown
Start-Sleep -Seconds 2
```

### Pitfall: JIT Overhead for Non-R2R DLLs

SDK-shipped MSBuild DLLs are ReadyToRun (crossgen'd, ~2x larger). If you swap in locally-built MSBuild DLLs (for mixed-SDK experiments), they're plain IL and pay JIT cost on first load. This adds ~0-8% overhead for incremental builds. **Always note this confound in results.**

### Pitfall: OrchardCore Targets Preview TFMs

OrchardCore targets `net10.0` / `net11.0` which requires preview SDK versions. Older SDKs may not support the TFM. Verify the project builds successfully with each SDK before benchmarking.

---

## 7. Isolating Regression Source

When you find a regression between SDK versions, the question is: **which component caused it?**

### Mixed SDK Technique

Swap individual component DLLs between SDK installations to isolate the source:

1. **newsdk-oldmsb**: Take new SDK, replace MSBuild DLLs with old ones → tests if SDK infra (minus MSBuild) regressed
2. **oldsdk-newmsb**: Take old SDK, replace MSBuild DLLs with new ones → tests if new MSBuild regressed

MSBuild DLLs to swap (5 files in `sdk/{version}/`):
- `Microsoft.Build.dll`
- `Microsoft.Build.Framework.dll`
- `Microsoft.Build.Tasks.Core.dll`
- `Microsoft.Build.Utilities.Core.dll`
- `MSBuild.dll`

**Caveat:** Swapped DLLs are plain IL (not R2R), introducing JIT overhead. Note this in results.

### Timeline Bisection

Install daily builds across the regression window and benchmark each. Example from our investigation:
- Jan 27, Feb 11, 12, 13, 14, Feb 24 → found regression is between Feb 14 and Feb 24
- Feb 11-14 form a stable plateau (within noise)
- Next step would be to install Feb 15-23 daily builds to further narrow

---

## 8. Interpreting Results

### What a "Regression" Looks Like

- ColdFull regression = slower compilation or restore
- HotIncr regression = slower up-to-date checks, target evaluation, or analyzer loading
- If HotIncr regresses more than ColdFull, the issue is in evaluation/checking overhead, not compilation speed
- If ColdFull regresses but HotIncr doesn't, the issue is in actual compilation

### Separating Restore from Build

Run `dotnet restore` separately and time it. In our benchmarks, restore accounted for only ~6% of the total regression — the rest was compilation.

### `-mt` Overhead Pattern

If `-mt` overhead *decreases* over time (e.g., +20% → +5%), check whether it's because `-mt` improved OR because no-mt got slower (making the relative overhead smaller). The latter was the case in our benchmarks.

## See Also

- [Reference benchmark script](bench_final.ps1) — Complete benchmark script used in the investigation
- [Bootstrap Documentation](https://github.com/dotnet/msbuild/blob/main/documentation/wiki/Bootstrap.md)
