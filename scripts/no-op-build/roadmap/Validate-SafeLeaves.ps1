param(
    [Parameter(Mandatory)][string]$SafeSdk,
    [Parameter(Mandatory)][string]$ArtifactsDirectory,
    [string]$Sdk = 'C:\Program Files\dotnet\sdk\10.0.400'
)

$ErrorActionPreference = 'Stop'
$ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
if (Test-Path $ArtifactsDirectory) { throw 'Use a fresh fixture directory.' }
New-Item -ItemType Directory -Path $ArtifactsDirectory | Out-Null
$dotnet = (Get-Command dotnet).Source
$probe = Join-Path (Split-Path $PSScriptRoot) 'Invoke-Probe.ps1'
$rows = [Collections.Generic.List[object]]::new()
$oldHost = $env:DOTNET_HOST_PATH
$env:DOTNET_HOST_PATH = $dotnet
try
{
foreach ($variant in @('stock', 'safe', 'optout'))
{
    $sourceSdk = if ($variant -eq 'stock') { $Sdk } else { $SafeSdk }
    $sourceTarget = Join-Path $sourceSdk 'Sdks\Microsoft.NET.Sdk.StaticWebAssets\targets\Microsoft.NET.Sdk.StaticWebAssets.targets'
    $xml = [xml](Get-Content $sourceTarget -Raw)
    $target = $xml.SelectSingleNode("//*[local-name()='Target' and @Name='GenerateStaticWebAssetsManifest']")
    $task = $target.SelectSingleNode("*[local-name()='GenerateStaticWebAssetsDevelopmentManifest']")
    $pathPreparation = $target.SelectSingleNode("*[local-name()='PropertyGroup'][*[local-name()='_DevelopmentManifestPathForElision']]")
    $root = Join-Path $ArtifactsDirectory "$variant space's path"
    New-Item -ItemType Directory -Path "$root\wwwroot" | Out-Null
    $definition = "<Project>$($target.OuterXml)<Target Name=`"DevelopmentOnly`" DependsOnTargets=`"SetPattern`">$($pathPreparation.OuterXml)$($task.OuterXml)</Target></Project>"
    $definition | Set-Content "$root\definitions.targets"
    $assembly = Join-Path $Sdk 'Sdks\Microsoft.NET.Sdk.StaticWebAssets\tasks\net10.0\Microsoft.NET.Sdk.StaticWebAssets.Tasks.dll'
    @'
<Project>
  <PropertyGroup>
    <PackageId Condition="'$(PackageId)' == ''">Probe</PackageId>
    <StaticWebAssetBasePath>/</StaticWebAssetBasePath>
    <StaticWebAssetProjectMode>Root</StaticWebAssetProjectMode>
    <StaticWebAssetBuildManifestPath>$(MSBuildProjectDirectory)\build.json</StaticWebAssetBuildManifestPath>
    <StaticWebAssetsBuildManifestCacheFilePath>$(MSBuildProjectDirectory)\build.cache</StaticWebAssetsBuildManifestCacheFilePath>
    <StaticWebAssetDevelopmentManifestPath>$(MSBuildProjectDirectory)\development.json</StaticWebAssetDevelopmentManifestPath>
    <StaticWebAssetEndpointsBuildManifestPath>$(MSBuildProjectDirectory)\endpoints.json</StaticWebAssetEndpointsBuildManifestPath>
    <StaticWebAssetEndpointsBuildExclusionPatternsCachePath>$(MSBuildProjectDirectory)\exclusions.cache</StaticWebAssetEndpointsBuildExclusionPatternsCachePath>
    <ProbePattern Condition="'$(ProbePattern)' == ''">**</ProbePattern>
  </PropertyGroup>
  <UsingTask TaskName="GenerateStaticWebAssetsManifest" AssemblyFile="$(TasksAssembly)" />
  <UsingTask TaskName="GenerateStaticWebAssetEndpointsManifest" AssemblyFile="$(TasksAssembly)" />
  <UsingTask TaskName="GenerateStaticWebAssetsDevelopmentManifest" AssemblyFile="$(TasksAssembly)" />
  <Import Project="definitions.targets" />
  <Target Name="SetPattern" BeforeTargets="GenerateStaticWebAssetsManifest">
    <PropertyGroup><HookOrder>$(HookOrder)B</HookOrder></PropertyGroup>
    <ItemGroup>
      <StaticWebAssetDiscoveryPattern Include="ProbePattern" Condition="'$(NoPattern)' != 'true'">
        <Name>ProbePattern</Name>
        <Source>Probe</Source>
        <ContentRoot>$(MSBuildProjectDirectory)\wwwroot\</ContentRoot>
        <BasePath>/</BasePath>
        <Pattern>$(ProbePattern)</Pattern>
      </StaticWebAssetDiscoveryPattern>
    </ItemGroup>
  </Target>
  <Target Name="ObserveManifest" AfterTargets="GenerateStaticWebAssetsManifest">
    <PropertyGroup><HookOrder>$(HookOrder)A</HookOrder></PropertyGroup>
    <Error Condition="'$(HookOrder)' != 'BA'" Text="Before/After hook ordering changed." />
    <Error Condition="!Exists('$(StaticWebAssetDevelopmentManifestPath)')" Text="Before hook inputs were not consumed." />
    <Error Condition="'@(_CachedBuildStaticWebAssetDiscoveryPatterns)' == ''" Text="Cached item output disappeared." />
    <Error Condition="'@(FileWrites->Count())' != '5'" Text="FileWrites contract changed." />
  </Target>
</Project>
'@ | Set-Content "$root\probe.proj"

    foreach ($stage in @('initial', 'noop', 'relative-paths', 'changed-before-hook', 'deleted-output', 'equal-time', 'missing-cache'))
    {
        $extra = @()
        $targetName = 'GenerateStaticWebAssetsManifest'
        $pattern = '**'
        if ($stage -in @('changed-before-hook', 'deleted-output')) { $pattern = '*.css' }
        if ($stage -eq 'deleted-output') { Remove-Item -LiteralPath "$root\development.json" }
        if ($stage -eq 'equal-time')
        {
            $targetName = 'DevelopmentOnly'
            $pattern = '*.js'
            $stamp = [DateTime]::UtcNow.AddMinutes(-2)
            [IO.File]::SetLastWriteTimeUtc("$root\development.json", $stamp)
            [IO.File]::SetLastWriteTimeUtc("$root\build.json", $stamp)
        }
        if ($stage -eq 'missing-cache')
        {
            $targetName = 'DevelopmentOnly'
            $pattern = '*.js'
            Remove-Item -LiteralPath "$root\build.json"
        }
        if ($variant -ne 'stock') { $extra += '-p:EnableStaticWebAssetsTaskElision=true' }
        if ($variant -eq 'optout') { $extra = @('-p:EnableIncrementalTargetOptimizations=true', '-p:EnableStaticWebAssetsTaskElision=false') }
        if ($stage -eq 'relative-paths') {
            $extra += '-p:StaticWebAssetDevelopmentManifestPath=development.json'
            $extra += '-p:StaticWebAssetBuildManifestPath=build.json'
        }
        $log = Join-Path $ArtifactsDirectory "$variant-$stage.binlog"
        & $dotnet (Join-Path $Sdk 'MSBuild.dll') "$root\probe.proj" -nologo -v:q `
            "-p:TasksAssembly=$assembly" "-p:ProbePattern=$pattern" "-t:$targetName" "-bl:$log" @extra | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$variant / $stage failed." }
        $manifest = Get-Content "$root\development.json" -Raw | ConvertFrom-Json
        if ($manifest.Root.Patterns[0].Pattern -ne $pattern) { throw "Stale pattern: $variant / $stage" }
        $summary = Join-Path $ArtifactsDirectory "$variant-$stage.json"
        & $probe -Sdk $Sdk -Build:($rows.Count -eq 0) -ProbeArguments @('analyze', $log, $summary) | Out-Host
        $data = Get-Content $summary -Raw | ConvertFrom-Json
        $writers = [int](($data.taskTimings | Where-Object name -eq GenerateStaticWebAssetsDevelopmentManifest | Measure-Object Count -Sum).Sum)
        if ($variant -eq 'safe' -and $stage -in @('noop','relative-paths','missing-cache') -and $writers -ne 0)
        {
            throw 'Expected native task-condition elision.'
        }
        if ($stage -eq 'equal-time' -and $writers -ne 1) { throw 'Equality must execute the writer.' }
        $rows.Add([pscustomobject]@{variant=$variant;stage=$stage;writers=$writers;passed=$true})
    }
    # Even an otherwise up-to-date file must not hide required-parameter validation.
    $invalidLog = Join-Path $ArtifactsDirectory "$variant-required-source.binlog"
    & $dotnet (Join-Path $Sdk 'MSBuild.dll') "$root\probe.proj" -nologo -v:q `
        "-p:TasksAssembly=$assembly" '-p:PackageId=' '-t:DevelopmentOnly' "-bl:$invalidLog" @extra | Out-Host
    if ($LASTEXITCODE -eq 0) { throw 'Required Source validation was suppressed.' }
    $rows.Add([pscustomobject]@{variant=$variant;stage='required-source-error';writers=-1;passed=$true})
}
}
finally
{
    $env:DOTNET_HOST_PATH = $oldHost
}
$rows | ConvertTo-Json | Set-Content (Join-Path $ArtifactsDirectory 'results.json')
$rows | Format-Table
