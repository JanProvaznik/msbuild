param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Sdk,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Dotnet = (Get-Command dotnet).Source,
    [ValidateRange(1, 50)][int]$Iterations = 5,
    [switch]$EnableExperimentalGate
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path $Root).Path
$Sdk = (Resolve-Path $Sdk).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($OutputDirectory.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
{
    throw 'Keep measurements outside the observed workload.'
}
if (Test-Path (Join-Path $OutputDirectory 'measurements.csv'))
{
    throw 'Use a fresh output directory to avoid overwriting earlier measurements.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$rows = [Collections.Generic.List[object]]::new()
$oldHost = $env:DOTNET_HOST_PATH
$oldLocation = Get-Location
$probe = Join-Path $PSScriptRoot 'Invoke-Probe.ps1'

function Invoke-MeasuredBuild([string]$Scenario, [string[]]$Extra, [int]$Iteration)
{
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $Dotnet (Join-Path $Sdk 'MSBuild.dll') $Project -m:8 -nologo -v:q @Extra
    $code = $LASTEXITCODE
    $rows.Add([pscustomobject]@{
        scenario = $Scenario
        iteration = $Iteration
        seconds = $watch.Elapsed.TotalSeconds
        exitCode = $code
    })
    $rows | Export-Csv (Join-Path $OutputDirectory 'measurements.csv') -NoTypeInformation
    if ($code -ne 0) { throw "Build failed: $code. Restore/build the workload before measuring no-op builds." }
}

try
{
    $env:DOTNET_HOST_PATH = $Dotnet
    Set-Location $Root
    Invoke-MeasuredBuild 'warmup' @() 0
    foreach ($iteration in 1..$Iterations)
    {
        Invoke-MeasuredBuild 'stock-no-restore-m8' @() $iteration
    }
    $binlog = Join-Path $OutputDirectory 'stock.binlog'
    & $Dotnet (Join-Path $Sdk 'MSBuild.dll') $Project -m:8 -nologo -v:q "-bl:$binlog"
    if ($LASTEXITCODE -ne 0) { throw 'Instrumented build failed.' }
    & $probe -Sdk $Sdk -Dotnet $Dotnet -Build -ProbeArguments @(
        'analyze', $binlog, (Join-Path $OutputDirectory 'stock-summary.json')
    )
    foreach ($iteration in 1..$Iterations)
    {
        Invoke-MeasuredBuild 'evaluation-only-not-a-build' @('-graphBuild:NoBuild') $iteration
    }

    if ($EnableExperimentalGate)
    {
        $dotnetRoot = Split-Path $Dotnet
        $config = @{
            Root = $Root
            Project = $Project
            Dotnet = $Dotnet
            Sdk = $Sdk
            StateFile = Join-Path $OutputDirectory 'gate-state.json'
            ResultsFile = Join-Path $OutputDirectory 'gate-results.jsonl'
            AssumePureFileBuild = $true
            Properties = @()
            ExternalRoots = @(
                (Join-Path $dotnetRoot 'packs'),
                (Join-Path $dotnetRoot 'shared'),
                (Join-Path $dotnetRoot 'host')
            )
        }
        $configPath = Join-Path $OutputDirectory 'gate-config.json'
        $config | ConvertTo-Json -Depth 5 | Set-Content $configPath
        & $probe -Sdk $Sdk -Dotnet $Dotnet -ProbeArguments @('gate', $configPath)
        foreach ($iteration in 1..$Iterations)
        {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            & $probe -Sdk $Sdk -Dotnet $Dotnet -ProbeArguments @('gate', $configPath)
            $seconds = $watch.Elapsed.TotalSeconds
            $result = Get-Content $config.ResultsFile -Tail 1 | ConvertFrom-Json
            if (!$result.cacheHit) { throw "Expected no-change cache hit; observed: $($result.reason)" }
            $rows.Add([pscustomobject]@{
                scenario = 'experimental-gate'
                iteration = $iteration
                seconds = $seconds
                exitCode = $result.exitCode
            })
            $rows | Export-Csv (Join-Path $OutputDirectory 'measurements.csv') -NoTypeInformation
        }
    }
    $rows | Where-Object scenario -ne 'warmup' | Group-Object scenario | ForEach-Object {
        $values = @($_.Group.seconds | Sort-Object)
        $middle = [int][Math]::Floor($values.Count / 2)
        $median = if ($values.Count % 2) { $values[$middle] } else { ($values[$middle - 1] + $values[$middle]) / 2 }
        [pscustomobject]@{
            scenario = $_.Name
            runs = $values.Count
            medianSeconds = [Math]::Round($median, 3)
            minSeconds = [Math]::Round($values[0], 3)
            maxSeconds = [Math]::Round($values[-1], 3)
        }
    } | Tee-Object -Variable summary | Format-Table
    $summary | Export-Csv (Join-Path $OutputDirectory 'summary.csv') -NoTypeInformation
}
finally
{
    $env:DOTNET_HOST_PATH = $oldHost
    Set-Location $oldLocation
}
