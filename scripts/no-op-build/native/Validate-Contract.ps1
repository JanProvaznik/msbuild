param(
    [Parameter(Mandatory)][string]$ArtifactsDirectory,
    [string]$ProbeScript = (Join-Path (Split-Path $PSScriptRoot) 'Invoke-Probe.ps1'),
    [string]$Sdk = 'C:\Program Files\dotnet\sdk\10.0.400'
)

$ErrorActionPreference = 'Stop'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
if (Test-Path $ArtifactsDirectory) { throw 'Choose a fresh artifact directory.' }
New-Item -ItemType Directory -Path $ArtifactsDirectory | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'fixture') $ArtifactsDirectory -Recurse
@{ sdk = @{ version = (Split-Path $Sdk -Leaf); rollForward = 'disable' } } |
    ConvertTo-Json | Set-Content (Join-Path $ArtifactsDirectory 'global.json')
$fixture = Join-Path $ArtifactsDirectory 'fixture'
$consumer = Join-Path $fixture 'Consumer\Consumer.csproj'
$producer = Join-Path $fixture 'Producer\Producer.csproj'
$source = Join-Path $fixture 'Producer\ValueProvider.cs'
$payload = Join-Path $fixture 'Producer\payload.txt'
$consumerBin = Join-Path $fixture 'Consumer\bin\Debug\net10.0'
$producerBin = Join-Path $fixture 'Producer\bin\Debug\net10.0'
$sourceOriginal = [IO.File]::ReadAllText($source)
$payloadOriginal = [IO.File]::ReadAllText($payload)
$producerOriginal = [IO.File]::ReadAllText($producer)
$rows = [Collections.Generic.List[object]]::new()
$oldLocation = Get-Location
$oldServer = $env:MSBUILDUSESERVER

function Build-Case([string]$Name, [string[]]$Extra = @(), [switch]$ExpectFailure)
{
    $log = Join-Path $ArtifactsDirectory "contract-$Name.binlog"
    Push-Location $fixture
    try
    {
        & $dotnet build $consumer --no-restore -m:2 -nologo -v:q `
            "-p:CustomAfterMicrosoftCommonTargets=$PSScriptRoot\inject.targets" -p:NativeCoreBuild=true "-bl:$log" @Extra | Out-Host
        $code = $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }
    if (($code -ne 0) -ne $ExpectFailure.IsPresent) { throw "Unexpected exit $code for $Name" }
    $summary = Join-Path $ArtifactsDirectory "contract-$Name.json"
    if (!$ExpectFailure)
    {
        & $ProbeScript -Sdk $Sdk -Build:($rows.Count -eq 0) -ProbeArguments @('analyze', $log, $summary) | Out-Host
        $stats = Get-Content $summary -Raw | ConvertFrom-Json
        $csc = [int](($stats.taskTimings | Where-Object name -eq 'Csc' | Measure-Object Count -Sum).Sum)
        $rar = [int](($stats.taskTimings | Where-Object name -eq 'ResolveAssemblyReference' | Measure-Object Count -Sum).Sum)
        $copy = [int](($stats.taskTimings | Where-Object name -eq 'Copy' | Measure-Object Count -Sum).Sum)
    }
    else
    {
        $csc = -1
        $rar = -1
        $copy = -1
    }
    $row = [pscustomobject]@{name=$Name;exitCode=$code;csc=$csc;rar=$rar;copy=$copy}
    $rows.Add($row)
    $rows | ConvertTo-Json | Set-Content (Join-Path $ArtifactsDirectory 'contract-results.json')
    return $row
}

function Assert-Run([string]$Value, [string]$Content)
{
    $output = & $dotnet (Join-Path $consumerBin 'Consumer.dll')
    if ($LASTEXITCODE -ne 0 -or $output[0] -ne $Value -or $output[1] -ne $Content)
    {
        throw "Unexpected runtime output: $($output -join ' / ')"
    }
}

try
{
    Set-Location $fixture
    $env:MSBUILDUSESERVER = '0'
    & $dotnet restore $consumer -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Fixture restore failed.' }
    Build-Case 'initial' | Out-Host
    Build-Case 'settle' | Out-Host
    $hook = (Get-Item (Join-Path $consumerBin 'after-build.log')).LastWriteTimeUtc
    $result = Build-Case 'noop'
    if ($result.csc -ne 0 -or $result.rar -ne 0 -or $result.copy -ne 0) { throw 'No-op did real build work.' }
    if ((Get-Item (Join-Path $consumerBin 'after-build.log')).LastWriteTimeUtc -le $hook) { throw 'AfterBuild hook was suppressed.' }
    Assert-Run 'one' 'initial payload'

    $reference = Join-Path $fixture 'Producer\obj\Debug\net10.0\ref\Producer.dll'
    $referenceHash = (Get-FileHash $reference).Hash
    [IO.File]::WriteAllText($source, $sourceOriginal.Replace('"one"', '"two"'))
    $result = Build-Case 'method-body'
    if ($result.csc -ne 1) { throw "Body edit compiled $($result.csc) projects instead of 1." }
    if ((Get-FileHash $reference).Hash -ne $referenceHash) { throw 'Reference assembly unexpectedly changed.' }
    Assert-Run 'two' 'initial payload'

    [IO.File]::WriteAllText($payload, 'updated transitive payload')
    $result = Build-Case 'transitive-content'
    if ($result.csc -ne 0 -or $result.copy -eq 0) { throw 'Content change was not copy-only.' }
    Assert-Run 'two' 'updated transitive payload'

    $extraSource = Join-Path $fixture 'Producer\Added.cs'
    [IO.File]::WriteAllText($extraSource, 'namespace NativeFixture; public sealed class Added { }')
    $result = Build-Case 'add-source'
    if ($result.csc -eq 0) { throw 'Source addition was missed.' }
    Remove-Item -LiteralPath $extraSource
    $result = Build-Case 'remove-source'
    if ($result.csc -eq 0) { throw 'Source removal was missed.' }

    $producerDll = Join-Path $producerBin 'Producer.dll'
    $producerHash = (Get-FileHash $producerDll).Hash
    Remove-Item -LiteralPath $producerDll
    $result = Build-Case 'missing-producer'
    if ($result.csc -ne 0 -or (Get-FileHash $producerDll).Hash -ne $producerHash) { throw 'Producer output was not repaired without compilation.' }
    $copyDll = Join-Path $consumerBin 'Producer.dll'
    Remove-Item -LiteralPath $copyDll
    $result = Build-Case 'missing-copy'
    if ($result.csc -ne 0 -or (Get-FileHash $copyDll).Hash -ne $producerHash) { throw 'Transitive DLL copy was not repaired.' }

    $result = Build-Case 'define' @('-p:DefineConstants=NATIVE_PROBE_DEFINE')
    if ($result.csc -ne 2) { throw 'Compiler property change was missed.' }
    Assert-Run 'defined' 'updated transitive payload'
    Build-Case 'reset-define' | Out-Host

    Build-Case 'runtime-config-property' @('-p:GenerateRuntimeConfigurationFiles=true') | Out-Host
    if (!(Test-Path (Join-Path $producerBin 'Producer.runtimeconfig.json'))) { throw 'Requested runtime configuration was not generated.' }
    Build-Case 'reset-runtime-config' | Out-Host

    [IO.File]::WriteAllText($extraSource, '#error deliberate native incremental failure')
    Build-Case 'failure' -ExpectFailure | Out-Host
    foreach ($project in @('Producer', 'Consumer'))
    {
        if (Test-Path (Join-Path $fixture "$project\obj\Debug\net10.0\$project.csproj.NativeCoreBuild.complete"))
        {
            throw "Failed build left a completion stamp in $project."
        }
    }
    Remove-Item -LiteralPath $extraSource
    Build-Case 'recover' | Out-Host
    Assert-Run 'two' 'updated transitive payload'

    & $dotnet clean $consumer -m:2 -nologo -v:q `
        "-p:CustomAfterMicrosoftCommonTargets=$PSScriptRoot\inject.targets" -p:NativeCoreBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'Clean failed.' }
    if (Test-Path (Join-Path $consumerBin 'Consumer.dll')) { throw 'Clean left primary output.' }
    foreach ($project in @('Producer', 'Consumer'))
    {
        if (Test-Path (Join-Path $fixture "$project\obj\Debug\net10.0\$project.csproj.NativeCoreBuild.complete"))
        {
            throw "Clean left completion stamp in $project."
        }
    }
    $result = Build-Case 'after-clean'
    if ($result.csc -ne 2) { throw 'Build after clean did not compile both projects.' }
    Build-Case 'final-noop' | Out-Host
    Assert-Run 'two' 'updated transitive payload'

    Build-Case 'no-build-guard' @('-p:NoBuild=true') -ExpectFailure | Out-Host
    $result = Build-Case 'ci-fallback' @('-p:ContinuousIntegrationBuild=true')
    if ($result.rar -ne 2) { throw 'CI build did not use the stock pipeline.' }

    $foreign = $producerOriginal.Replace('<Project Sdk="Microsoft.NET.Sdk">',
        '<Project Sdk="Microsoft.NET.Sdk" TreatAsLocalProperty="NativeCoreBuild">')
    $foreign = $foreign.Replace('<PropertyGroup>', '<PropertyGroup><NativeCoreBuild>false</NativeCoreBuild>')
    [IO.File]::WriteAllText($producer, $foreign)
    Build-Case 'foreign-reference-warm' | Out-Host
    $result = Build-Case 'foreign-reference'
    if ($result.rar -ne 2) { throw 'Non-participating reference did not force ordinary preparation.' }
}
finally
{
    [IO.File]::WriteAllText($source, $sourceOriginal)
    [IO.File]::WriteAllText($payload, $payloadOriginal)
    [IO.File]::WriteAllText($producer, $producerOriginal)
    $temporarySource = Join-Path $fixture 'Producer\Added.cs'
    if (Test-Path $temporarySource) { Remove-Item -LiteralPath $temporarySource }
    $env:MSBUILDUSESERVER = $oldServer
    Set-Location $oldLocation
}

Build-Case 'restored-sources' | Out-Host
$result = Build-Case 'restored-noop'
if ($result.csc -ne 0 -or $result.rar -ne 0 -or $result.copy -ne 0) { throw 'Restored fixture is not incremental.' }
Assert-Run 'one' 'initial payload'
$rows | Format-Table
