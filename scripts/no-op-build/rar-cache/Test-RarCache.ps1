param(
    [Parameter(Mandatory)][string]$SdkDirectory,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$Dotnet = 'C:\Program Files\dotnet\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$SdkDirectory = (Resolve-Path $SdkDirectory).Path
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path $OutputRoot) { throw 'Choose a fresh artifact directory.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$logs = Join-Path $OutputRoot 'logs'
New-Item -ItemType Directory -Path $logs | Out-Null
$probe = Join-Path (Split-Path $PSScriptRoot) 'Invoke-Probe.ps1'
$rows = [Collections.Generic.List[object]]::new()
$oldHost = $env:DOTNET_HOST_PATH
$oldSdks = $env:MSBuildSDKsPath
$oldLocation = Get-Location
$msbuild = Join-Path $SdkDirectory 'MSBuild.dll'
$baseArgs = @('-nologo','-v:q','-m:1','-nr:false','-p:NetCoreRoot=C:\Program Files\dotnet\')
$outputItems = 'ReferencePath,ReferenceDependencyPaths,_ReferenceRelatedPaths,ReferenceSatellitePaths,_ReferenceSerializationAssemblyPaths,_ReferenceScatterPaths,ReferenceCopyLocalPaths,SuggestedBindingRedirects,ResolveAssemblyReferenceUnresolvedAssemblyConflicts'

function Write-Fixture([string]$Relative, [string]$Content) {
    $path = Join-Path $OutputRoot $Relative
    New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $Content, [Text.UTF8Encoding]::new($false))
}

function Run-Build([string]$Name, [string]$Project, [bool]$Cache = $true, [string[]]$Extra = @(), [switch]$Failure) {
    $stem = Join-Path $logs $Name
    & $Dotnet $msbuild $Project @baseArgs -t:Build "-p:EnableRARResultsCache=$Cache" `
        "-bl:$stem.binlog" "-getResultOutputFile:$stem.outputs.json" `
        "-getItem:$outputItems" '-getProperty:_RARResultsCacheHit,DependsOnSystemRuntime,_DependsOnNETStandard,BeforeRarRan,AfterRarRan' @Extra | Out-Host
    $exit = $LASTEXITCODE
    if (($exit -ne 0) -ne $Failure.IsPresent) { throw "Unexpected exit $exit in $Name." }
    if ($Failure) {
        $probeDll = Join-Path (Split-Path $PSScriptRoot) 'bin\Debug\net10.0\BuildProbe.dll'
        & $Dotnet exec --depsfile 'C:\Program Files\dotnet\sdk\10.0.400\MSBuild.deps.json' `
            --runtimeconfig 'C:\Program Files\dotnet\sdk\10.0.400\MSBuild.runtimeconfig.json' `
            $probeDll analyze "$stem.binlog" "$stem.summary.json" | Out-Host
        if (!(Test-Path "$stem.summary.json")) { throw 'Expected-failure log could not be analyzed.' }
    }
    else {
        & $probe -Sdk 'C:\Program Files\dotnet\sdk\10.0.400' -Build:($rows.Count -eq 0) `
            -ProbeArguments @('analyze',"$stem.binlog","$stem.summary.json") -ErrorAction Stop | Out-Host
    }
    $summary = Get-Content "$stem.summary.json" -Raw | ConvertFrom-Json
    $row = [pscustomobject]@{
        name=$Name;exitCode=$exit;hits=[int]$summary.rarCache.hit;misses=[int]$summary.rarCache.miss
        bypass=[int]$summary.rarCache.bypass;notStored=[int]$summary.rarCache.'not-stored'
        rarMilliseconds=($summary.taskTimings|Where-Object name -eq ResolveAssemblyReference).Milliseconds
        errors=$summary.errors;warnings=$summary.warnings
    }
    $rows.Add($row)
    $rows | ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'results.json')
    return $row
}

function Restore([string]$Project) {
    & $Dotnet $msbuild $Project @baseArgs -t:Restore | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $Project" }
}

function Assert-Output([string]$Assembly, [string]$Expected) {
    $actual = & $Dotnet $Assembly
    if ($LASTEXITCODE -ne 0 -or ($actual -join "`n") -ne $Expected) { throw "Runtime output '$actual' != '$Expected'." }
}

function Output-Contract([string]$Name) {
    $data = Get-Content (Join-Path $logs "$Name.outputs.json") -Raw | ConvertFrom-Json
    foreach ($group in $data.Items.PSObject.Properties) {
        $group.Value = @($group.Value | ForEach-Object {
            $item = [ordered]@{}
            foreach ($property in $_.PSObject.Properties | Sort-Object Name) {
                # Last-access timestamps can legitimately change because the test itself reads files.
                if ($property.Name -ne 'AccessedTime') { $item[$property.Name] = $property.Value }
            }
            $item
        })
    }
    $data.Properties.PSObject.Properties.Remove('_RARResultsCacheHit')
    return $data | ConvertTo-Json -Depth 30 -Compress
}

try {
    $env:DOTNET_HOST_PATH = $Dotnet
    $env:MSBuildSDKsPath = Join-Path $SdkDirectory 'Sdks'
    Set-Location $OutputRoot
    Write-Fixture 'global.json' '{"sdk":{"version":"10.0.400","rollForward":"disable"}}'
    Write-Fixture 'Directory.Build.props' '<Project />'
    Write-Fixture 'Directory.Build.targets' '<Project />'
    Write-Fixture 'Dep\Dep.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
    Write-Fixture 'Dep\Value.cs' 'public static class Dep { public static string Value => "old"; }'
    Write-Fixture 'Lib\Lib.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="..\Dep\Dep.csproj" /></ItemGroup></Project>'
    Write-Fixture 'Lib\Value.cs' 'public static class Lib { public static string Value => Dep.Value; }'
    Write-Fixture 'App\App.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><OutputType>Exe</OutputType><ProbeAlias Condition="'$(ProbeAlias)' == ''">global</ProbeAlias></PropertyGroup>
  <ItemGroup><Reference Include="..\Lib\bin\Debug\net10.0\Lib.dll" Aliases="$(ProbeAlias)" /><Reference Include="MissingCacheProbe" Condition="'$(ProbeMissing)' == 'true'" /></ItemGroup>
  <Target Name="BeforeRar" BeforeTargets="ResolveAssemblyReferences"><PropertyGroup><BeforeRarRan>true</BeforeRarRan></PropertyGroup></Target>
  <Target Name="AfterRar" AfterTargets="ResolveAssemblyReferences">
    <PropertyGroup><AfterRarRan>true</AfterRarRan></PropertyGroup>
    <Error Condition="'@(ReferencePath)' == ''" Text="Reference outputs were lost." />
    <Error Condition="'$(ProbeFailAfterRar)' == 'true'" Text="EXPECTED-RAR-HOOK-FAILURE" />
  </Target>
</Project>
'@
    Write-Fixture 'App\Program.cs' 'System.Console.WriteLine(Lib.Value);'
    Restore 'Lib\Lib.csproj'
    Run-Build 'library-initial' 'Lib\Lib.csproj' $false | Out-Host
    Restore 'App\App.csproj'
    Run-Build 'app-stock' 'App\App.csproj' $false | Out-Host
    Run-Build 'app-fill' 'App\App.csproj' | Out-Host
    $row = Run-Build 'app-hit' 'App\App.csproj'
    if ($row.hits -ne 1) { throw 'Expected a task-level cache hit.' }
    if ((Output-Contract 'app-stock') -cne (Output-Contract 'app-hit')) { throw 'RAR output arrays, order, or metadata changed on a hit.' }
    Assert-Output 'App\bin\Debug\net10.0\App.dll' 'old'
    Run-Build 'interleaved-control' 'App\App.csproj' $false | Out-Host
    $row = Run-Build 'hit-after-control' 'App\App.csproj'
    if ($row.hits -ne 1) { throw 'A legacy metadata-cache rewrite invalidated unchanged resolution inputs.' }
    Remove-Item -LiteralPath 'App\obj\Debug\net10.0\App.csproj.AssemblyReference.cache'
    $row = Run-Build 'legacy-cache-deleted' 'App\App.csproj'
    if ($row.hits -ne 0 -or !(Test-Path 'App\obj\Debug\net10.0\App.csproj.AssemblyReference.cache')) {
        throw 'A missing declared cache output was not recreated.'
    }

    $primary = (Get-FileHash 'Lib\bin\Debug\net10.0\Lib.dll').Hash
    Write-Fixture 'Dep\Value.cs' 'public static class Dep { public static string Value => "new"; }'
    Run-Build 'library-dependency-changed' 'Lib\Lib.csproj' $false | Out-Host
    if ((Get-FileHash 'Lib\bin\Debug\net10.0\Lib.dll').Hash -ne $primary) { throw 'Primary DLL unexpectedly changed in the transitive test.' }
    $row = Run-Build 'changed-transitive-source' 'App\App.csproj'
    if ($row.hits -ne 0) { throw 'Transitive source change incorrectly hit.' }
    Assert-Output 'App\bin\Debug\net10.0\App.dll' 'new'
    Run-Build 'transitive-settled' 'App\App.csproj' | Out-Host

    Write-Fixture 'Lib\bin\Debug\net10.0\Lib.xml' '<doc>new related file</doc>'
    $row = Run-Build 'new-related-file' 'App\App.csproj'
    if ($row.hits -ne 0 -or !(Test-Path 'App\bin\Debug\net10.0\Lib.xml')) { throw 'New related file was missed.' }
    Remove-Item -LiteralPath 'Lib\bin\Debug\net10.0\Lib.xml'
    $row = Run-Build 'removed-related-file' 'App\App.csproj'
    if ($row.hits -ne 0 -or (Test-Path 'App\bin\Debug\net10.0\Lib.xml')) { throw 'Removed related file was missed.' }

    $row = Run-Build 'metadata-change' 'App\App.csproj' $true @('-p:ProbeAlias=global%2cSecondAlias')
    if ($row.hits -ne 0) { throw 'Input metadata change incorrectly hit.' }
    Run-Build 'metadata-reset' 'App\App.csproj' | Out-Host
    $cache = 'App\obj\Debug\net10.0\App.csproj.AssemblyReference.cache.results'
    [IO.File]::WriteAllBytes((Join-Path $OutputRoot $cache), [byte[]](1,2,3))
    $row = Run-Build 'corrupt-cache' 'App\App.csproj'
    if ($row.hits -ne 0) { throw 'Corrupt cache was accepted.' }
    Run-Build 'cache-repaired' 'App\App.csproj' | Out-Host
    $cachePath = Join-Path $OutputRoot $cache
    $malformed = [byte[]](255,255,255,255,255)
    $stream = [IO.File]::Create($cachePath)
    $header = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
    $header.Write([int]0x52415243)
    $header.Write($malformed.Length)
    $header.Write([Security.Cryptography.SHA256]::HashData($malformed))
    $header.Dispose()
    $compressed = [IO.Compression.BrotliStream]::new($stream, [IO.Compression.CompressionLevel]::Fastest, $true)
    $compressed.Write($malformed, 0, $malformed.Length)
    $compressed.Dispose()
    $stream.Dispose()
    $row = Run-Build 'malformed-cache-string' 'App\App.csproj'
    if ($row.hits -ne 0) { throw 'Malformed cache string was accepted.' }
    $damaged = [IO.File]::ReadAllBytes($cachePath)
    $position = [int]($damaged.Length / 2)
    $damaged[$position] = $damaged[$position] -bxor 1
    [IO.File]::WriteAllBytes($cachePath, $damaged)
    $row = Run-Build 'corrupt-whole-contract' 'App\App.csproj'
    if ($row.hits -ne 0) { throw 'Corrupt observations/messages were accepted.' }
    foreach ($i in 1..2) {
        $row = Run-Build "warning-$i" 'App\App.csproj' $true @('-p:ProbeMissing=true')
        if ($row.warnings -eq 0 -or $row.hits -ne 0) { throw 'Warning diagnostics were cached or suppressed.' }
    }
    Run-Build 'warning-as-error' 'App\App.csproj' $true @('-p:ProbeMissing=true','-warnAsError:MSB3245') -Failure | Out-Host
    Run-Build 'warning-reset' 'App\App.csproj' | Out-Host
    $row = Run-Build 'after-rar-hook-error' 'App\App.csproj' $true @('-p:ProbeFailAfterRar=true') -Failure
    if ($row.hits -ne 1) { throw 'The hook failure was not exercised on a cache hit.' }
    Run-Build 'after-hook-recovery' 'App\App.csproj' | Out-Host

    Write-Fixture 'Satellite\Satellite.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Lib.resources</AssemblyName><GenerateAssemblyInfo>false</GenerateAssemblyInfo><Nullable>enable</Nullable></PropertyGroup></Project>'
    Write-Fixture 'Satellite\Value.cs' '[assembly: System.Reflection.AssemblyCulture("fr")] internal sealed class ResourceMarker { }'
    Restore 'Satellite\Satellite.csproj'
    Run-Build 'satellite-build' 'Satellite\Satellite.csproj' $false | Out-Host
    New-Item -ItemType Directory -Path 'Lib\bin\Debug\net10.0\fr' | Out-Null
    Copy-Item 'Satellite\bin\Debug\net10.0\Lib.resources.dll' 'Lib\bin\Debug\net10.0\fr\Lib.resources.dll'
    $row = Run-Build 'new-satellite-directory' 'App\App.csproj'
    if ($row.hits -ne 0 -or !(Test-Path 'App\bin\Debug\net10.0\fr\Lib.resources.dll')) { throw 'New satellite directory was missed.' }
    Run-Build 'satellite-settled' 'App\App.csproj' | Out-Host

    $configText = '<configuration><runtime><assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1"><dependentAssembly><assemblyIdentity name="UnusedConfigProbe" publicKeyToken="b03f5f7f11d50a3a" culture="neutral"/><bindingRedirect oldVersion="0.0.0.0-9.0.0.0" newVersion="1.0.0.0"/></dependentAssembly></assemblyBinding></runtime></configuration>'
    Write-Fixture 'App\App.config' $configText
    Run-Build 'config-fill' 'App\App.csproj' $true @('-p:AutoUnifyAssemblyReferences=false') | Out-Host
    $row = Run-Build 'config-hit' 'App\App.csproj' $true @('-p:AutoUnifyAssemblyReferences=false')
    if ($row.hits -ne 1) { throw 'Expected config-aware cache hit.' }
    $config = Join-Path $OutputRoot 'App\App.config'
    $timestamp = [IO.File]::GetLastWriteTimeUtc($config)
    [IO.File]::WriteAllText($config, $configText.Replace('newVersion="1.0.0.0"', 'newVersion="2.0.0.0"'), [Text.UTF8Encoding]::new($false))
    [IO.File]::SetLastWriteTimeUtc($config, $timestamp)
    $row = Run-Build 'config-content-preserved-time' 'App\App.csproj' $true @('-p:AutoUnifyAssemblyReferences=false')
    if ($row.hits -ne 0) { throw 'Config content change with preserved timestamp was missed.' }

    Write-Fixture 'Low\Low.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Pick</AssemblyName><Nullable>enable</Nullable></PropertyGroup></Project>'
    Write-Fixture 'Low\Value.cs' 'public static class Pick { public static string Value => "low"; }'
    Write-Fixture 'High\High.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Pick</AssemblyName><Nullable>enable</Nullable></PropertyGroup></Project>'
    Write-Fixture 'High\Value.cs' 'public static class Pick { public static string Value => "high"; }'
    Write-Fixture 'Search\Search.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><OutputType>Exe</OutputType>
    <AssemblySearchPaths>$(MSBuildProjectDirectory)\..\High\bin\Debug\net10.0;$(MSBuildProjectDirectory)\..\Low\bin\Debug\net10.0;{RawFileName}</AssemblySearchPaths>
  </PropertyGroup>
  <ItemGroup><Reference Include="Pick" /></ItemGroup>
</Project>
'@
    Write-Fixture 'Search\Program.cs' 'System.Console.WriteLine(Pick.Value);'
    Restore 'Low\Low.csproj'
    Run-Build 'low-build' 'Low\Low.csproj' $false | Out-Host
    Restore 'High\High.csproj'
    Restore 'Search\Search.csproj'
    Run-Build 'search-fill' 'Search\Search.csproj' | Out-Host
    $row = Run-Build 'search-hit' 'Search\Search.csproj'
    if ($row.hits -ne 1) { throw 'Expected search-path cache hit.' }
    Assert-Output 'Search\bin\Debug\net10.0\Search.dll' 'low'
    Run-Build 'higher-candidate-created' 'High\High.csproj' $false | Out-Host
    $row = Run-Build 'new-higher-search-candidate' 'Search\Search.csproj'
    if ($row.hits -ne 0) { throw 'A newly appearing higher-priority candidate was missed.' }
    Assert-Output 'Search\bin\Debug\net10.0\Search.dll' 'high'

    Write-Fixture 'Candidates\Candidates.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><OutputType>Exe</OutputType>
    <AssemblySearchPaths>{CandidateAssemblyFiles};{RawFileName}</AssemblySearchPaths>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Pick" />
    <Content Include="$(MSBuildProjectDirectory)\..\CandidateHigh\Pick.dll;$(MSBuildProjectDirectory)\..\Low\bin\Debug\net10.0\Pick.dll" />
  </ItemGroup>
</Project>
'@
    Write-Fixture 'Candidates\Program.cs' 'System.Console.WriteLine(Pick.Value);'
    Restore 'Candidates\Candidates.csproj'
    Run-Build 'candidate-fill' 'Candidates\Candidates.csproj' | Out-Host
    $row = Run-Build 'candidate-hit' 'Candidates\Candidates.csproj'
    if ($row.hits -ne 1) { throw 'Expected candidate-list cache hit.' }
    Assert-Output 'Candidates\bin\Debug\net10.0\Candidates.dll' 'low'
    New-Item -ItemType Directory -Path 'CandidateHigh' | Out-Null
    Copy-Item 'High\bin\Debug\net10.0\Pick.dll' 'CandidateHigh\Pick.dll'
    $row = Run-Build 'new-candidate-file' 'Candidates\Candidates.csproj'
    if ($row.hits -ne 0) { throw 'Previously missing CandidateAssemblyFiles input was missed.' }
    Assert-Output 'Candidates\bin\Debug\net10.0\Candidates.dll' 'high'
    $rows | Format-Table
}
finally {
    $env:DOTNET_HOST_PATH = $oldHost
    $env:MSBuildSDKsPath = $oldSdks
    Set-Location $oldLocation
}
