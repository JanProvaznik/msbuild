param(
    [Parameter(Mandatory)][string]$Sdk,
    [Parameter(Mandatory)][string[]]$ProbeArguments,
    [string]$Dotnet = (Get-Command dotnet).Source,
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$Sdk = (Resolve-Path $Sdk).Path
$project = Join-Path $PSScriptRoot 'BuildProbe.csproj'
$probe = Join-Path $PSScriptRoot 'bin\Debug\net10.0\BuildProbe.dll'
$oldHost = $env:DOTNET_HOST_PATH
$oldSdks = $env:MSBuildSDKsPath
try
{
    $env:DOTNET_HOST_PATH = $Dotnet
    if ($Build)
    {
        $env:MSBuildSDKsPath = Join-Path $Sdk 'Sdks'
        & $Dotnet (Join-Path $Sdk 'MSBuild.dll') $project -restore -v:q -nologo `
            -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false
        if ($LASTEXITCODE -ne 0) { throw "Probe build failed: $LASTEXITCODE" }
        $env:MSBuildSDKsPath = $oldSdks
    }
    & $Dotnet exec --depsfile (Join-Path $Sdk 'MSBuild.deps.json') `
        --runtimeconfig (Join-Path $Sdk 'MSBuild.runtimeconfig.json') $probe @ProbeArguments
    if ($LASTEXITCODE -ne 0) { throw "Probe failed: $LASTEXITCODE" }
}
finally
{
    $env:DOTNET_HOST_PATH = $oldHost
    $env:MSBuildSDKsPath = $oldSdks
}
