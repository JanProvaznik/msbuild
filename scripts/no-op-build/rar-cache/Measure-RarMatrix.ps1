param(
    [Parameter(Mandatory)][string]$Registry,
    [Parameter(Mandatory)][string]$SdkDirectory,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateRange(3,30)][int]$Iterations = 3
)
$ErrorActionPreference = 'Stop'
if (Test-Path $OutputRoot) { throw 'Choose a fresh result directory.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$registryData = Get-Content $Registry -Raw | ConvertFrom-Json
$rows = [Collections.Generic.List[object]]::new()
$oldHost = $env:DOTNET_HOST_PATH
$oldSdks = $env:MSBuildSDKsPath
function Snapshot($scenario) {
    $files = @{}
    foreach ($project in $scenario.projects) {
        $bin = Join-Path (Split-Path $project.path) 'bin'
        if (Test-Path $bin) {
            foreach ($file in Get-ChildItem $bin -Recurse -File) {
                $files[$file.FullName] = (Get-FileHash $file.FullName).Hash
            }
        }
    }
    return $files
}
try {
    $env:DOTNET_HOST_PATH = 'C:\Program Files\dotnet\dotnet.exe'
    $env:MSBuildSDKsPath = Join-Path $SdkDirectory 'Sdks'
    foreach ($scenario in $registryData.scenarios) {
        & $env:DOTNET_HOST_PATH (Join-Path $SdkDirectory 'MSBuild.dll') $scenario.solution -nologo -v:q -m:8 `
            '-p:NetCoreRoot=C:\Program Files\dotnet\' -p:EnableRARResultsCache=false
        if ($LASTEXITCODE -ne 0) { throw "Baseline failed: $($scenario.id)" }
        $before = Snapshot $scenario
        $output = Join-Path $OutputRoot $scenario.id
        & (Join-Path $PSScriptRoot 'Measure-RarCache.ps1') -SdkDirectory $SdkDirectory -Root $scenario.directory `
            -Project $scenario.solution -OutputDirectory $output -Iterations $Iterations
        $after = Snapshot $scenario
        if ($before.Count -ne $after.Count) { throw "Output path set changed: $($scenario.id)" }
        foreach ($path in $before.Keys) {
            if ($after[$path] -ne $before[$path]) { throw "Output changed: $path" }
        }
        $summary = Import-Csv (Join-Path $output 'summary.csv')
        $hit = Get-Content (Join-Path $output 'cache-hit.json') -Raw | ConvertFrom-Json
        $rows.Add([pscustomobject]@{
            scenario=$scenario.id;projects=$scenario.projectCount
            off=($summary|Where-Object cache -eq False).median
            on=($summary|Where-Object cache -eq True).median
            hits=[int]$hit.rarCache.hit;bypasses=[int]$hit.rarCache.bypass
            identicalOutputFiles=$before.Count
        })
        $rows | Export-Csv (Join-Path $OutputRoot 'summary.csv') -NoTypeInformation
    }
}
finally {
    $env:DOTNET_HOST_PATH = $oldHost
    $env:MSBuildSDKsPath = $oldSdks
}
