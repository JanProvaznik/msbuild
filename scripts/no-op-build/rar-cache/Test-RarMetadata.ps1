param(
    [Parameter(Mandatory)][string]$SdkDirectory,
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
if (Test-Path $OutputRoot) { throw 'Choose a fresh output directory.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$AssemblyPath = (Resolve-Path $AssemblyPath).Path
$probe = (Resolve-Path (Join-Path $PSScriptRoot 'Metadata-Probe.proj')).Path
$state = Join-Path $OutputRoot 'metadata.cache'
$entries = foreach ($case in @(
    @{phase='disk-before';synthetic='false';cache='false';version='1.0.0.0';hit='false'},
    @{phase='synthetic-fill';synthetic='true';cache='true';version='2.0.0.0';hit='false'},
    @{phase='disk-after';synthetic='false';cache='false';version='1.0.0.0';hit='false'},
    @{phase='synthetic-hit';synthetic='true';cache='true';version='2.0.0.0';hit='true'}
)) {
    $properties = "Phase=$($case.phase);Synthetic=$($case.synthetic);UseResultCache=$($case.cache);ExpectedVersion=$($case.version);ExpectedHit=$($case.hit);ProbeAssembly=$AssemblyPath;ProbeStateFile=$state"
    "<Probe Include=`"$([Security.SecurityElement]::Escape($probe))`"><Properties>$([Security.SecurityElement]::Escape($properties))</Properties></Probe>"
}
$driver = Join-Path $OutputRoot 'driver.proj'
"<Project><ItemGroup>$($entries -join '')</ItemGroup><Target Name=`"Build`"><MSBuild Projects=`"@(Probe)`" Properties=`"%(Probe.Properties)`" Targets=`"Resolve`" BuildInParallel=`"false`" /></Target></Project>" |
    Set-Content $driver
$oldHost = $env:DOTNET_HOST_PATH
try {
    $env:DOTNET_HOST_PATH = 'C:\Program Files\dotnet\dotnet.exe'
    & $env:DOTNET_HOST_PATH (Join-Path $SdkDirectory 'MSBuild.dll') $driver -nologo -v:m "-bl:$OutputRoot\metadata.binlog"
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic/disk cache isolation failed.' }
}
finally {
    $env:DOTNET_HOST_PATH = $oldHost
}
