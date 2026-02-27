# bench_final.ps1 — OrchardCore benchmark: SDK timeline × -mt flag
# Scenarios: ColdFull (kill processes, delete bin/obj, timed build) and HotIncr (no-op rebuild)
# Configs: each SDK × {mt, no-mt}
# Collects exactly $SuccessTarget successful runs per scenario+config (retries on failure).
param(
    [int]$SuccessTarget = 10,
    [int]$MaxAttempts = 20,      # safety valve per scenario+config
    [string]$Csv = "Q:\feb-bench\bench_final.csv"
)

$orchardSln = "Q:\feb-bench\projects\proj-orchardcore\OrchardCore.slnx"

$sdks = [ordered]@{
    "jan27" = "Q:\feb-bench\sdk-old-jan27\dotnet.exe"
    "feb11" = "Q:\feb-bench\sdk-feb11\dotnet.exe"
    "feb12" = "Q:\feb-bench\sdk-feb12\dotnet.exe"
    "feb13" = "Q:\feb-bench\sdk-feb13\dotnet.exe"
    "feb14" = "Q:\feb-bench\sdk-feb14\dotnet.exe"
    "feb24" = "Q:\feb-bench\sdk-new\dotnet.exe"
}

$mtModes = @(
    @{ Label = "mt";   Args = "-mt" }
    @{ Label = "nomt"; Args = $null }
)

# Build the full config matrix: sdk × mt
$configs = @()
foreach ($sdkName in $sdks.Keys) {
    foreach ($mode in $mtModes) {
        $configs += @{
            Label  = "$sdkName-$($mode.Label)"
            Dotnet = $sdks[$sdkName]
            Mt     = $mode.Args
        }
    }
}

$orchardRoot = Split-Path $orchardSln -Parent

function Kill-AllDotnet {
    Get-Process -Name "dotnet","MSBuild","msbuild","VBCSCompiler" -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}

function Remove-BinObj {
    Get-ChildItem $orchardRoot -Directory -Recurse -Include "bin","obj" |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

function Run-Build {
    param([string]$Dotnet, [string]$Sln, [string]$MtFlag, [int]$TimeoutMs = 600000)
    $buildArgs = "build `"$Sln`" --nologo -v:q"
    if ($MtFlag) { $buildArgs += " $MtFlag" }

    $sdkDir = Split-Path $Dotnet -Parent
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Dotnet
    $psi.Arguments = $buildArgs
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables["DOTNET_ROOT"] = $sdkDir
    $psi.EnvironmentVariables["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"] = $sdkDir
    $psi.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"] = "0"

    $p = [System.Diagnostics.Process]::new()
    $p.StartInfo = $psi
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p.Start() | Out-Null
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $finished = $p.WaitForExit($TimeoutMs)
    $sw.Stop()

    if (-not $finished) {
        try { Stop-Process -Id $p.Id -Force } catch {}
        try { $outTask.Wait(5000) } catch {}
        try { $errTask.Wait(5000) } catch {}
        $p.Dispose()
        return @{ Ok = $false; Ms = $sw.Elapsed.TotalMilliseconds; Code = -1 }
    }
    $code = $p.ExitCode
    try { [void]$outTask.Result } catch {}
    try { [void]$errTask.Result } catch {}
    $p.Dispose()
    return @{ Ok = ($code -eq 0); Ms = $sw.Elapsed.TotalMilliseconds; Code = $code }
}

function Write-Row {
    param([string]$Label, [string]$Scenario, [int]$Attempt, $Result)
    "$Label,$Scenario,$Attempt,$([math]::Round($Result.Ms,2)),$($Result.Ok),$($Result.Code),$(Get-Date -f 'yyyy-MM-dd HH:mm:ss')" |
        Out-File $Csv -Append -Encoding UTF8
}

# ---- MAIN ----
$startTime = Get-Date
Write-Host "=== OrchardCore Final Benchmark ===" -ForegroundColor Cyan
Write-Host "Solution : $orchardSln"
Write-Host "Target   : $SuccessTarget successful runs per scenario+config (max $MaxAttempts attempts)"
Write-Host "Configs  : $($configs.Count) ($($sdks.Count) SDKs x $($mtModes.Count) mt modes)"
Write-Host "Scenarios: ColdFull, HotIncr"
Write-Host "Output   : $Csv"
Write-Host ""

foreach ($sdkName in $sdks.Keys) {
    $ver = & $sdks[$sdkName] --version 2>$null
    Write-Host "  $sdkName : $ver"
}
Write-Host ""

# CSV header
if (-not (Test-Path $Csv) -or (Get-Item $Csv).Length -eq 0) {
    "Config,Scenario,Attempt,Duration_ms,Success,ExitCode,Timestamp" | Out-File $Csv -Encoding UTF8
}

# Randomize config order
$shuffled = $configs | Get-Random -Count $configs.Count

$cfgNum = 0
foreach ($cfg in $shuffled) {
    $cfgNum++
    $label = $cfg.Label
    $dotnet = $cfg.Dotnet
    $mt = $cfg.Mt

    Write-Host "[$cfgNum/$($configs.Count)] $label" -ForegroundColor Cyan

    # ========== ColdFull ==========
    $successCount = 0
    $attemptNum = 0
    Write-Host "  ColdFull ($SuccessTarget needed): " -NoNewline
    while ($successCount -lt $SuccessTarget -and $attemptNum -lt $MaxAttempts) {
        $attemptNum++
        Kill-AllDotnet
        Remove-BinObj
        $r = Run-Build $dotnet $orchardSln $mt
        Write-Row $label "ColdFull" $attemptNum $r
        $ms = [math]::Round($r.Ms)
        if ($r.Ok) {
            $successCount++
            Write-Host "$ms " -NoNewline -ForegroundColor Green
        } else {
            Write-Host "F($($r.Code)) " -NoNewline -ForegroundColor Red
        }
    }
    if ($successCount -lt $SuccessTarget) {
        Write-Host " GAVE UP ($successCount/$SuccessTarget)" -ForegroundColor Red
    } else {
        Write-Host " done" -ForegroundColor DarkGray
    }

    # ========== HotIncr ==========
    # Prep: full cold build + 2 warmup no-ops to stabilize MSBuild server and JIT
    Kill-AllDotnet
    Remove-BinObj
    $prep = Run-Build $dotnet $orchardSln $mt
    if (-not $prep.Ok) {
        Write-Host "  HotIncr prep FAILED (exit $($prep.Code)) — retrying once" -ForegroundColor Yellow
        Kill-AllDotnet
        Remove-BinObj
        $prep = Run-Build $dotnet $orchardSln $mt
    }
    if (-not $prep.Ok) {
        Write-Host "  HotIncr prep FAILED twice — skipping" -ForegroundColor Red
        Write-Row $label "HotIncr" 0 $prep
        continue
    }
    Run-Build $dotnet $orchardSln $mt | Out-Null
    Run-Build $dotnet $orchardSln $mt | Out-Null

    $successCount = 0
    $attemptNum = 0
    Write-Host "  HotIncr  ($SuccessTarget needed): " -NoNewline
    while ($successCount -lt $SuccessTarget -and $attemptNum -lt $MaxAttempts) {
        $attemptNum++
        $r = Run-Build $dotnet $orchardSln $mt
        Write-Row $label "HotIncr" $attemptNum $r
        $ms = [math]::Round($r.Ms)
        if ($r.Ok) {
            $successCount++
            Write-Host "$ms " -NoNewline -ForegroundColor Green
        } else {
            Write-Host "F($($r.Code)) " -NoNewline -ForegroundColor Red
            # If HotIncr fails, MSBuild server may be dead — restart it
            Kill-AllDotnet
            Remove-BinObj
            $reprep = Run-Build $dotnet $orchardSln $mt
            if ($reprep.Ok) {
                Run-Build $dotnet $orchardSln $mt | Out-Null
            }
        }
    }
    if ($successCount -lt $SuccessTarget) {
        Write-Host " GAVE UP ($successCount/$SuccessTarget)" -ForegroundColor Red
    } else {
        Write-Host " done" -ForegroundColor DarkGray
    }
}

$elapsed = (Get-Date) - $startTime
Write-Host "`n=== Done in $([math]::Round($elapsed.TotalMinutes,1)) minutes. Results in $Csv ===" -ForegroundColor Green
