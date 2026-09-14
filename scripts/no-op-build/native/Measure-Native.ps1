param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$PrivateSdk,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Sdk = 'C:\Program Files\dotnet\sdk\10.0.400',
    [ValidateRange(1, 50)][int]$Iterations = 5,
    [switch]$IncludeServer,
    [switch]$AcknowledgeExperimentalCoreContract
)

$ErrorActionPreference = 'Stop'
if (!$AcknowledgeExperimentalCoreContract) {
    throw 'The coarse CoreBuild experiment has known hook/state incompatibilities. Use the roadmap safe-tier matrix, or explicitly acknowledge the laboratory contract.'
}
$Root = (Resolve-Path $Root).Path
$PrivateSdk = (Resolve-Path $PrivateSdk).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) { throw 'Choose a fresh output directory.' }
if ($OutputDirectory.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
{
    throw 'Keep measurements outside the workload.'
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$configuration = Get-Content (Join-Path $PrivateSdk 'configuration.json') -Raw | ConvertFrom-Json
$variants = @('stock-off', 'native-off')
if ($IncludeServer) { $variants += @('stock-on', 'native-on') }
$rows = [Collections.Generic.List[object]]::new()
$oldSdkPath = $env:MSBuildSDKsPath
$oldServer = $env:MSBUILDUSESERVER
$oldLocation = Get-Location

function Invoke-Variant([string]$Variant, [int]$Iteration, [string]$Binlog = '')
{
    $enabled = if ($Variant.StartsWith('native')) { 'true' } else { 'false' }
    $env:MSBUILDUSESERVER = if ($Variant.EndsWith('-on')) { '1' } else { '0' }
    $extra = @()
    if ($Binlog) { $extra = @("-bl:$Binlog") }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    dotnet build $Project --no-restore -m:8 -nologo -v:q `
        "-p:NetCoreRoot=$($configuration.NetCoreRoot)" `
        "-p:CustomAfterMicrosoftCommonTargets=$PSScriptRoot\inject.targets" `
        "-p:NativeCoreBuild=$enabled" "-p:NativeIncrementalTranslations=$enabled" `
        -p:NativeCoreBuildContract=DeclaredInputsAndHooksV1 `
        "-p:NativeSkipEmptyFrameworkPacks=$enabled" @extra
    $code = $LASTEXITCODE
    if ($Iteration -gt 0)
    {
        $rows.Add([pscustomobject]@{scenario=$Variant;iteration=$Iteration;seconds=$watch.Elapsed.TotalSeconds;exitCode=$code})
        $rows | Export-Csv (Join-Path $OutputDirectory 'measurements.csv') -NoTypeInformation
    }
    if ($code -ne 0) { throw "Build failed: $Variant, exit $code" }
}

try
{
    Set-Location $Root
    $env:MSBuildSDKsPath = $configuration.MSBuildSDKsPath
    foreach ($variant in $variants) { Invoke-Variant $variant 0 }
    foreach ($iteration in 1..$Iterations)
    {
        foreach ($variant in $variants) { Invoke-Variant $variant $iteration }
    }
    $buildProbe = $true
    foreach ($variant in $variants)
    {
        $log = Join-Path $OutputDirectory "$variant.binlog"
        Invoke-Variant $variant 0 $log
        # Read with the host SDK rather than an older viewer that may drop event types.
        & (Join-Path (Split-Path $PSScriptRoot) 'Invoke-Probe.ps1') -Sdk $Sdk -Build:$buildProbe `
            -ProbeArguments @('analyze', $log, (Join-Path $OutputDirectory "$variant.json"))
        $buildProbe = $false
    }
    $summary = $rows | Group-Object scenario | ForEach-Object {
        $values = @($_.Group.seconds | Sort-Object)
        $middle = [int][Math]::Floor($values.Count / 2)
        $median = if ($values.Count % 2) { $values[$middle] } else { ($values[$middle - 1] + $values[$middle]) / 2 }
        [pscustomobject]@{
            scenario=$_.Name; n=$values.Count; medianSeconds=$median
            minSeconds=$values[0]; maxSeconds=$values[-1]
        }
    }
    $summary | Export-Csv (Join-Path $OutputDirectory 'summary.csv') -NoTypeInformation
    $summary | Format-Table
}
finally
{
    $env:MSBuildSDKsPath = $oldSdkPath
    $env:MSBUILDUSESERVER = $oldServer
    Set-Location $oldLocation
}
