param(
    [Parameter(Mandatory)][string]$SdkDirectory,
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(3, 30)][int]$Iterations = 5,
    [int]$MaxCpuCount = 8,
    [string[]]$AdditionalArguments = @(),
    [string]$Dotnet = 'C:\Program Files\dotnet\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$SdkDirectory = (Resolve-Path $SdkDirectory).Path
$Root = (Resolve-Path $Root).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) { throw 'Use a fresh result directory.' }
if ($OutputDirectory.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep logs and results outside the workload.'
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$rows = [Collections.Generic.List[object]]::new()
$oldHost = $env:DOTNET_HOST_PATH
$oldSdks = $env:MSBuildSDKsPath
$probe = Join-Path (Split-Path $PSScriptRoot) 'Invoke-Probe.ps1'
$logger = Join-Path (Split-Path $PSScriptRoot) 'bin\Debug\net10.0\BuildProbe.dll'
if (!(Test-Path $logger)) { throw 'Build scripts\no-op-build\BuildProbe.csproj first (see README).' }
@{
    sdk=$SdkDirectory;root=$Root;project=$Project;iterations=$Iterations;maxCpuCount=$MaxCpuCount
    additionalArguments=$AdditionalArguments;tasksSha256=(Get-FileHash (Join-Path $SdkDirectory 'Microsoft.Build.Tasks.Core.dll')).Hash
} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'configuration.json')
$call = 0

function Build-Once([bool]$Cache, [int]$Iteration, [string]$Log = '') {
    $extra = @()
    if ($Log) { $extra += "-bl:$Log" }
    $script:call++
    $metrics = Join-Path $OutputDirectory "call-$script:call.json"
    $extra += "-logger:RarCacheLogger,$logger;$metrics"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $Dotnet (Join-Path $SdkDirectory 'MSBuild.dll') $Project -nologo -v:q "-m:$MaxCpuCount" `
        '-p:NetCoreRoot=C:\Program Files\dotnet\' "-p:EnableRARResultsCache=$Cache" @AdditionalArguments @extra
    $code = $LASTEXITCODE
    $elapsed = $watch.Elapsed.TotalSeconds
    $data = Get-Content $metrics -Raw | ConvertFrom-Json
    $counts = $data.counts
    if ($Iteration -gt 0) {
        $rows.Add([pscustomobject]@{cache=$Cache;iteration=$Iteration;seconds=$elapsed;exitCode=$code;hits=[int]$counts.hit;misses=[int]$counts.miss;bypasses=[int]$counts.bypass;rarMilliseconds=$data.rarMilliseconds})
        $rows | Export-Csv (Join-Path $OutputDirectory 'samples.csv') -NoTypeInformation
    }
    if ($code -ne 0) { throw "RAR cache build failed: enabled=$Cache, exit=$code" }
}

Push-Location $Root
try {
    $env:DOTNET_HOST_PATH = $Dotnet
    $env:MSBuildSDKsPath = Join-Path $SdkDirectory 'Sdks'
    Build-Once $false 0
    Build-Once $true 0
    Build-Once $true 0
    foreach ($iteration in 1..$Iterations) {
        $order = @($false,$true)
        if ($iteration % 2 -eq 0) { $order = @($true,$false) }
        foreach ($cache in $order) { Build-Once $cache $iteration }
    }
    # Logging changes RAR's observable message policy, so establish a separate logged cache entry.
    Build-Once $true 0 (Join-Path $OutputDirectory 'cache-fill.binlog')
    Build-Once $true 0 (Join-Path $OutputDirectory 'cache-hit.binlog')
    Build-Once $false 0 (Join-Path $OutputDirectory 'control.binlog')
    $buildProbe = $true
    foreach ($name in @('cache-fill','cache-hit','control')) {
        & $probe -Sdk 'C:\Program Files\dotnet\sdk\10.0.400' -Build:$buildProbe `
            -ProbeArguments @('analyze',(Join-Path $OutputDirectory "$name.binlog"),(Join-Path $OutputDirectory "$name.json"))
        $buildProbe = $false
    }
    $summary = $rows | Group-Object cache | ForEach-Object {
        $values = @($_.Group.seconds | Sort-Object)
        $middle = [int][Math]::Floor($values.Count / 2)
        $median = if ($values.Count % 2) { $values[$middle] } else { ($values[$middle-1]+$values[$middle])/2 }
        [pscustomobject]@{cache=$_.Name;n=$values.Count;median=$median;min=$values[0];max=$values[-1]}
    }
    $summary | Export-Csv (Join-Path $OutputDirectory 'summary.csv') -NoTypeInformation
    $summary | Format-Table
}
finally {
    $env:DOTNET_HOST_PATH = $oldHost
    $env:MSBuildSDKsPath = $oldSdks
    Pop-Location
}
