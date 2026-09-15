param(
    [Parameter(Mandatory)][string]$SdkDirectory,
    [Parameter(Mandatory)][string[]]$Assemblies,
    [Parameter(Mandatory)][string[]]$SearchDirectories,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateRange(3,100)][int]$Iterations = 20
)
$ErrorActionPreference = 'Stop'
if (Test-Path $OutputRoot) { throw 'Choose a fresh output directory.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path $OutputRoot).Path
$SdkDirectory = (Resolve-Path $SdkDirectory).Path
$items = foreach ($assembly in $Assemblies) {
    '<Assembly Include="' + [Security.SecurityElement]::Escape((Resolve-Path $assembly).Path) + '" />'
}
$search = @('{RawFileName}') + @($SearchDirectories | ForEach-Object { (Resolve-Path $_).Path })
$project = Join-Path $OutputRoot 'Resolve.proj'
@"
<Project>
  <ItemGroup>$($items -join '')</ItemGroup>
  <Target Name="Resolve">
    <ResolveAssemblyReference Assemblies="@(Assembly)"
      SearchPaths="$([Security.SecurityElement]::Escape($search -join ';'))"
      StateFile="$([Security.SecurityElement]::Escape((Join-Path $OutputRoot 'references.cache')))"
      AutoUnify="true" FindDependencies="true" FindSatellites="true" FindRelatedFiles="true"
      EnableResultsCache="`$(Cache)">
      <Output TaskParameter="ResolvedFiles" ItemName="Resolved" />
      <Output TaskParameter="ResolvedDependencyFiles" ItemName="Dependencies" />
      <Output TaskParameter="ResultsCacheHit" PropertyName="Hit" />
    </ResolveAssemblyReference>
    <Error Condition="'@(Resolved)' == ''" Text="Resolution produced no primary outputs." />
  </Target>
</Project>
"@ | Set-Content $project
$probe = Join-Path (Split-Path $PSScriptRoot) 'Invoke-Probe.ps1'
$hostBefore = $env:DOTNET_HOST_PATH
try {
    $env:DOTNET_HOST_PATH = 'C:\Program Files\dotnet\dotnet.exe'
    foreach ($enabled in @('false','true')) {
        $runs = foreach ($i in 1..$Iterations) {
            "<Run Include=`"$([Security.SecurityElement]::Escape($project))`"><Properties>Cache=$enabled;Iteration=$i</Properties></Run>"
        }
        $driver = Join-Path $OutputRoot "$enabled.proj"
        "<Project><ItemGroup>$($runs -join '')</ItemGroup><Target Name=`"Build`"><MSBuild Projects=`"@(Run)`" Properties=`"%(Run.Properties)`" Targets=`"Resolve`" BuildInParallel=`"false`" /></Target></Project>" | Set-Content $driver
        $log = Join-Path $OutputRoot "$enabled.binlog"
        & $env:DOTNET_HOST_PATH (Join-Path $SdkDirectory 'MSBuild.dll') $driver -nologo -v:q -m:1 -nr:false "-bl:$log"
        if ($LASTEXITCODE -ne 0) { throw 'Task benchmark failed.' }
        & $probe -Sdk 'C:\Program Files\dotnet\sdk\10.0.400' -ProbeArguments @('analyze',$log,(Join-Path $OutputRoot "$enabled.json"))
    }
    $rows = foreach ($enabled in @('false','true')) {
        $data = Get-Content (Join-Path $OutputRoot "$enabled.json") -Raw | ConvertFrom-Json
        $rar = $data.taskTimings | Where-Object name -eq ResolveAssemblyReference
        [pscustomobject]@{cache=$enabled;invocations=$rar.Count;rarMilliseconds=$rar.Milliseconds;hits=[int]$data.rarCache.hit;warnings=$data.warnings}
    }
    $rows | Export-Csv (Join-Path $OutputRoot 'summary.csv') -NoTypeInformation
    $rows | Format-Table
}
finally {
    $env:DOTNET_HOST_PATH = $hostBefore
}
