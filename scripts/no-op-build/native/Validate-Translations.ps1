param(
    [Parameter(Mandatory)][string]$ArtifactsDirectory,
    [string]$Sdk = 'C:\Program Files\dotnet\sdk\10.0.400'
)

$ErrorActionPreference = 'Stop'
$ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
if (Test-Path $ArtifactsDirectory) { throw 'Choose a fresh artifact directory.' }
$app = Join-Path $ArtifactsDirectory 'App'
$packageA = Join-Path $ArtifactsDirectory 'package-a'
$packageB = Join-Path $ArtifactsDirectory 'package-b'
New-Item -ItemType Directory -Path $app,"$packageA\cs","$packageB\cs" | Out-Null
@{sdk=@{version=(Split-Path $Sdk -Leaf);rollForward='disable'}} |
    ConvertTo-Json | Set-Content (Join-Path $ArtifactsDirectory 'global.json')
@'
<Project>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <EnableSourceLink>false</EnableSourceLink>
    <DefaultItemExcludes>$(DefaultItemExcludes);Localization\**</DefaultItemExcludes>
    <CollectTranslationsBefore Condition="'$(CollectTranslationsBefore)' == ''">BeforeBuild</CollectTranslationsBefore>
  </PropertyGroup>
  <Target Name="CollectTranslations" BeforeTargets="$(CollectTranslationsBefore)"
          Condition="'$(TranslationPackageDirectory)' != ''">
    <ItemGroup>
      <PackageTranslationFiles Include="$(TranslationPackageDirectory)\**\*.po" />
    </ItemGroup>
  </Target>
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
  <Import Project="$(NativeTranslationTargets)" Condition="'$(NativeTranslationTargets)' != ''" />
</Project>
'@ | Set-Content (Join-Path $app 'App.csproj')
'public sealed class TranslationFixture { }' | Set-Content (Join-Path $app 'Fixture.cs')
'newer package contents' | Set-Content "$packageA\cs\Messages.po"
'older package contents with different length' | Set-Content "$packageB\cs\Messages.po"
[IO.File]::SetLastWriteTimeUtc("$packageA\cs\Messages.po", [DateTime]::UtcNow.AddDays(-2))
[IO.File]::SetLastWriteTimeUtc("$packageB\cs\Messages.po", [DateTime]::UtcNow.AddDays(-10))
$destination = Join-Path $app 'Localization\cs\Messages.po'
$probe = Join-Path (Split-Path $PSScriptRoot) 'Invoke-Probe.ps1'
$rows = [Collections.Generic.List[object]]::new()

function Build-Translations([string]$Name, [string]$Package, [string]$CollectBefore = 'BeforeBuild')
{
    $log = Join-Path $ArtifactsDirectory "$Name.binlog"
    dotnet build App.csproj --no-restore -nologo -v:q `
        "-p:CustomAfterMicrosoftCommonTargets=$PSScriptRoot\inject.targets" `
        "-p:NativeTranslationTargets=$PSScriptRoot\translations-products.targets" `
        "-p:TranslationPackageDirectory=$Package" -p:NativeCoreBuild=true `
        "-p:CollectTranslationsBefore=$CollectBefore" `
        -p:NativeCoreBuildContract=DeclaredInputsAndHooksV1 "-bl:$log" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Failed $Name" }
    $summary = Join-Path $ArtifactsDirectory "$Name.json"
    & $probe -Sdk $Sdk -Build:($rows.Count -eq 0) -ProbeArguments @('analyze',$log,$summary) | Out-Host
    $data = Get-Content $summary -Raw | ConvertFrom-Json
    $row = [pscustomobject]@{
        name=$Name
        csc=[int](($data.taskTimings|Where-Object name -eq Csc|Measure-Object Count -Sum).Sum)
        copy=[int](($data.taskTimings|Where-Object name -eq Copy|Measure-Object Count -Sum).Sum)
    }
    $rows.Add($row)
    if ((Get-FileHash $destination).Hash -ne (Get-FileHash "$Package\cs\Messages.po").Hash)
    {
        throw "Translation content differs in $Name"
    }
    return $row
}

Push-Location $app
try
{
    dotnet restore App.csproj -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    Build-Translations 'initial' $packageA | Out-Host
    $result = Build-Translations 'noop' $packageA
    if ($result.csc -ne 0 -or $result.copy -ne 0) { throw 'No-op did real work.' }
    $result = Build-Translations 'package-downgrade' $packageB
    if ($result.copy -eq 0) { throw 'Backdated package mapping change was skipped.' }
    $result = Build-Translations 'after-downgrade' $packageB
    if ($result.copy -ne 0) { throw 'Downgraded package did not settle.' }
    'newer corrupted destination' | Set-Content $destination
    $result = Build-Translations 'newer-destination' $packageB
    if ($result.copy -eq 0) { throw 'Newer modified destination was not repaired.' }
    Remove-Item -LiteralPath $destination
    $result = Build-Translations 'deleted-destination' $packageB
    if ($result.copy -eq 0) { throw 'Missing destination was not repaired.' }
    'added translation' | Set-Content "$packageB\cs\Added.po"
    Build-Translations 'added-source' $packageB | Out-Host
    if (!(Test-Path (Join-Path $app 'Localization\cs\Added.po'))) { throw 'New translation was not copied.' }
    Remove-Item -LiteralPath "$packageB\cs\Added.po"
    Build-Translations 'removed-source' $packageB | Out-Host
    if (!(Test-Path (Join-Path $app 'Localization\cs\Added.po'))) { throw 'Removed source changed stock orphan policy.' }
    $result = Build-Translations 'final-noop' $packageB
    if ($result.copy -ne 0 -or $result.csc -ne 0) { throw 'Final build was not a no-op.' }
    Build-Translations 'before-copy-v1' $packageA 'CopyPackageTranslationFiles' | Out-Host
    $result = Build-Translations 'before-copy-v2' $packageB 'CopyPackageTranslationFiles'
    if ($result.copy -eq 0) { throw 'BeforeTargets mapping change was missed.' }
    $result = Build-Translations 'before-copy-noop' $packageB 'CopyPackageTranslationFiles'
    if ($result.copy -ne 0) { throw 'Execution-time mapping did not settle.' }
    $rows | ConvertTo-Json | Set-Content (Join-Path $ArtifactsDirectory 'results.json')
    $rows | Format-Table
}
finally
{
    Pop-Location
}
