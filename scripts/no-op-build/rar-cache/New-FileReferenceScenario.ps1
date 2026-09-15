param(
    [Parameter(Mandatory)][string]$OrchardAssembly,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateRange(1,50)][int]$LibraryCount = 10
)
$ErrorActionPreference = 'Stop'
$OrchardAssembly = (Resolve-Path $OrchardAssembly).Path
if (Test-Path $OutputRoot) { throw 'Choose a fresh scenario directory.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path $OutputRoot).Path
function Write-Source([string]$relative, [string]$text) {
    $path = Join-Path $OutputRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}
Write-Source 'global.json' '{"sdk":{"version":"10.0.400","rollForward":"disable"}}'
Write-Source 'Directory.Build.props' '<Project />'
Write-Source 'Directory.Build.targets' '<Project />'
$projects = [Collections.Generic.List[string]]::new()
$references = [Collections.Generic.List[string]]::new()
$calls = [Collections.Generic.List[string]]::new()
foreach ($i in 1..$LibraryCount) {
    $name = "Lib$i"
    Write-Source "$name\$name.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><Reference Include="OrchardCore.ContentManagement"><HintPath>$([Security.SecurityElement]::Escape($OrchardAssembly))</HintPath></Reference></ItemGroup>
</Project>
"@
    Write-Source "$name\Value.cs" "public static class $name { public static string Value => typeof(OrchardCore.ContentManagement.DefaultContentManagerSession).FullName!; }"
    $projects.Add("<Project Path=`"$name\$name.csproj`" />")
    $references.Add("<ProjectReference Include=`"..\$name\$name.csproj`" />")
    $calls.Add("$name.Value")
}
Write-Source 'App\App.csproj' "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><Nullable>enable</Nullable></PropertyGroup><ItemGroup>$($references -join '')</ItemGroup></Project>"
Write-Source 'App\Program.cs' "System.Console.WriteLine(string.Join(`"|`", $($calls -join ', ')));"
$projects.Add('<Project Path="App\App.csproj" />')
Write-Source 'Scenario.slnx' "<Solution>$($projects -join '')</Solution>"
@{assembly=$OrchardAssembly;projects=$LibraryCount+1;expected=(1..$LibraryCount|ForEach-Object{'OrchardCore.ContentManagement.DefaultContentManagerSession'}) -join '|'} |
    ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'scenario.json')
