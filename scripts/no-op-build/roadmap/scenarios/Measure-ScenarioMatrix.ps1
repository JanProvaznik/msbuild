#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Registry,
    [Parameter(Mandatory)][string]$SdkConfiguration,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(3, 30)][int]$Iterations = 5,
    [int]$MaxCpuCount = 8,
    [string[]]$Scenario = @(),
    [string]$OrchardRoot = ''
)

$ErrorActionPreference = 'Stop'
$matrix = Get-Content $Registry -Raw | ConvertFrom-Json
$configuration = Get-Content $SdkConfiguration -Raw | ConvertFrom-Json
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) { throw 'Choose a fresh measurement directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$cases = @($matrix.scenarios | Where-Object { $Scenario.Count -eq 0 -or $_.id -in $Scenario })
if ($OrchardRoot) {
    $cases += [pscustomobject]@{
        id='10-orchard-cms'; directory=$OrchardRoot; solution=''
        primaryProject=(Join-Path $OrchardRoot 'src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj')
        validationStatus='ready'; projectCount=202; expectedEnableSourceLink=''
    }
}
$samples = [Collections.Generic.List[object]]::new()
$counters = [Collections.Generic.List[object]]::new()
$oldSdks = $env:MSBuildSDKsPath
$oldServer = $env:MSBUILDUSESERVER
$probe = Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'Invoke-Probe.ps1'
$probeBuilt = $false

function Build-Variant($Case, [string]$Entry, [string]$Route, [string]$Variant, [int]$Iteration, [string]$Log = '') {
    $enabled = if ($Variant -eq 'safe') { 'true' } else { 'false' }
    $extra = @()
    if ($Log) { $extra += "-bl:$Log" }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $matrix.dotnetPath build $Entry --no-restore -nologo -v:q "-m:$MaxCpuCount" `
        "-p:NetCoreRoot=$($configuration.NetCoreRoot)" "-p:EnableIncrementalTargetOptimizations=$enabled" @extra | Out-Host
    $exit = $LASTEXITCODE
    if ($Iteration -gt 0) {
        $samples.Add([pscustomobject]@{
            scenario=$Case.id;route=$Route;variant=$Variant;iteration=$Iteration
            seconds=$watch.Elapsed.TotalSeconds;exitCode=$exit
        })
        $samples | Export-Csv (Join-Path $OutputDirectory 'samples.csv') -NoTypeInformation
    }
    if ($exit -ne 0) { throw "Build failure: $($Case.id), $Route, $Variant, exit $exit" }
}

try {
    $env:MSBuildSDKsPath = $configuration.MSBuildSDKsPath
    $env:MSBUILDUSESERVER = '0'
    foreach ($case in $cases) {
        if ($case.validationStatus -ne 'ready') { throw "Scenario is not validated: $($case.id)" }
        Push-Location $case.directory
        try {
            foreach ($route in @('project', 'solution')) {
                $entry = if ($route -eq 'project') { $case.primaryProject } else { $case.solution }
                if (!$entry) { continue }
                Write-Host "Measuring $($case.id) / $route"
                foreach ($variant in @('stock','safe')) { Build-Variant $case $entry $route $variant 0 }
                foreach ($iteration in 1..$Iterations) {
                    # Alternate which variant runs first, not just two blocks of measurements.
                    $order = @('stock','safe')
                    if ($iteration % 2 -eq 0) { $order = @('safe','stock') }
                    foreach ($variant in $order) { Build-Variant $case $entry $route $variant $iteration }
                }
                foreach ($variant in @('stock','safe')) {
                    $stem = Join-Path $OutputDirectory "$($case.id)-$route-$variant"
                    Build-Variant $case $entry $route $variant 0 "$stem.binlog"
                    & $probe -Sdk $configuration.hostSdk -Build:(!$probeBuilt) `
                        -ProbeArguments @('analyze',"$stem.binlog","$stem.json") | Out-Host
                    $probeBuilt = $true
                    $data = Get-Content "$stem.json" -Raw | ConvertFrom-Json
                    $counters.Add([pscustomobject]@{
                        scenario=$case.id;route=$route;variant=$variant;projects=$data.projects
                        evaluations=$data.evaluations;tasks=$data.tasks;targets=$data.targets
                        csc=[int](($data.taskTimings|Where-Object name -eq Csc|Measure-Object Count -Sum).Sum)
                        rar=[int](($data.taskTimings|Where-Object name -eq ResolveAssemblyReference|Measure-Object Count -Sum).Sum)
                        packs=[int](($data.taskTimings|Where-Object name -eq GetPackageDirectory|Measure-Object Count -Sum).Sum)
                        developmentManifest=[int](($data.taskTimings|Where-Object name -eq GenerateStaticWebAssetsDevelopmentManifest|Measure-Object Count -Sum).Sum)
                    })
                    $counters | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'counters.json')
                }
            }
        }
        finally { Pop-Location }
    }
    $summary = $samples | Group-Object scenario,route,variant | ForEach-Object {
        $values = @($_.Group.seconds | Sort-Object)
        $middle = [int][Math]::Floor($values.Count / 2)
        $median = if ($values.Count % 2) { $values[$middle] } else { ($values[$middle-1]+$values[$middle])/2 }
        [pscustomobject]@{
            scenario=$_.Group[0].scenario;route=$_.Group[0].route;variant=$_.Group[0].variant
            n=$values.Count;median=$median;min=$values[0];max=$values[-1]
        }
    }
    $summary | Export-Csv (Join-Path $OutputDirectory 'summary.csv') -NoTypeInformation
    $summary | Format-Table
}
finally {
    $env:MSBuildSDKsPath = $oldSdks
    $env:MSBUILDUSESERVER = $oldServer
}
