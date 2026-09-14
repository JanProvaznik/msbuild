#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$SdkVersion = '10.0.400',
    [string]$DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source
)

$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Choose a fresh OutputRoot; refusing to overwrite $OutputRoot." }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$rows = [Collections.Generic.List[object]]::new()

function Write-Fixture([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Xml([string]$Text) { [Security.SecurityElement]::Escape($Text) }

function New-Project(
    [string]$Scenario, [string]$Name, [string[]]$Frameworks = @('net10.0'),
    [string]$Sdk = 'Microsoft.NET.Sdk', [string]$OutputType = 'Library',
    [hashtable]$Properties = @{}, [string[]]$References = @(),
    [hashtable]$Files = @{}, [string]$ExtraItems = ''
) {
    $directory = Join-Path $Scenario $Name
    $path = Join-Path $directory "$Name.csproj"
    $frameworkProperty = if ($Frameworks.Count -gt 1) { 'TargetFrameworks' } else { 'TargetFramework' }
    $extraProperties = foreach ($key in $Properties.Keys | Sort-Object) { "<$key>$(Xml $Properties[$key])</$key>" }
    $referenceItems = foreach ($reference in $References) { "<ProjectReference Include=`"..\$reference\$reference.csproj`" />" }
    $project = @"
<Project Sdk="$Sdk">
  <PropertyGroup>
    <$frameworkProperty>$($Frameworks -join ';')</$frameworkProperty>
    <OutputType>$OutputType</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    $($extraProperties -join "`n    ")
  </PropertyGroup>
  <ItemGroup>
    $($referenceItems -join "`n    ")
    $ExtraItems
  </ItemGroup>
</Project>
"@
    Write-Fixture $path $project
    foreach ($file in $Files.Keys) { Write-Fixture (Join-Path $directory $file) $Files[$file] }
    return [pscustomobject]@{
        name = $Name; path = $path; relativePath = [IO.Path]::GetRelativePath($OutputRoot, $path)
        sdk = $Sdk; frameworks = @($Frameworks); references = @($References)
        outputs = @($Frameworks | ForEach-Object { Join-Path $directory "bin\Debug\$_\$Name.dll" })
    }
}

function Add-Scenario(
    [string]$Id, [string]$Title, [string]$Directory, [object[]]$Projects,
    [object]$Primary, [object]$EditProject, [string]$EditFile,
    [string]$EditFrom, [string]$EditTo, [hashtable]$Runtime,
    [hashtable]$Customizations = @{}, [string[]]$Assumptions = @(),
    [bool]$Trusted = $false, [string]$Requires = '', [string]$UnsupportedReason = ''
) {
    Push-Location $Directory
    try {
        & $DotNetPath new sln --format slnx --name Scenario | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Solution creation failed for $Id." }
        $solution = Join-Path $Directory 'Scenario.slnx'
        & $DotNetPath sln $solution add @($Projects.path) | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Solution population failed for $Id." }
    }
    finally { Pop-Location }
    $rows.Add([pscustomobject]@{
        id = $Id; title = $Title; directory = $Directory
        solution = $solution; solutionRelativePath = [IO.Path]::GetRelativePath($OutputRoot, $solution)
        primaryProject = $Primary.path; primaryFramework = $Primary.frameworks[0]
        primaryOutput = $Primary.outputs[0]
        projectCount = $Projects.Count; projects = @($Projects)
        expectedEnableSourceLink = (-not $Trusted).ToString().ToLowerInvariant()
        explicitlyTrustedLocal = $Trusted
        coarseLabEligibility = if ($Trusted) { 'explicitly-trusted-candidate-not-enabled' } else { 'stock-fallback-required' }
        fallbackReason = if ($Trusted) { 'Explicit user-requested EnableSourceLink=false counterpart; baseline still runs unmodified SDK.' } else { 'Default EnableSourceLink must remain true. Do not disable it to qualify a coarse optimization.' }
        customizations = $Customizations
        baselineAssumptions = @(
            'Stock SDK only: no native hotpatch imports or optimization flags.'
            'No source-control provider is fabricated: fixtures are not a Git repository and have no fake remote.'
            'SourceLink is NOT disabled unless this is the explicitly trusted counterpart.'
            'NuGet source configuration is inherited normally; no test packages or additional package feeds are introduced.'
        ) + $Assumptions
        prerequisites = $Requires; unsupportedReason = $UnsupportedReason
        edit = [pscustomobject]@{
            source = (Join-Path ([IO.Path]::GetDirectoryName($EditProject.path)) $EditFile)
            from = $EditFrom; to = $EditTo; affectedOutputs = @($EditProject.outputs)
        }
        runtime = $Runtime
        validationStatus = 'generated'; validationReason = ''
    })
}

Write-Fixture (Join-Path $OutputRoot 'global.json') (@{
    sdk = @{ version = $SdkVersion; rollForward = 'disable'; allowPrerelease = $false }
} | ConvertTo-Json)
# Stop discovery of unrelated ancestor customizations, not SDK defaults.
Write-Fixture (Join-Path $OutputRoot 'Directory.Build.props') '<Project />'
Write-Fixture (Join-Path $OutputRoot 'Directory.Build.targets') '<Project />'
Push-Location $OutputRoot
try {
    $actualSdk = (& $DotNetPath --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $SdkVersion) { throw "Expected SDK $SdkVersion; actual '$actualSdk'." }
}
finally { Pop-Location }

foreach ($trusted in @($false, $true)) {
    $id = if ($trusted) { '02-trusted-console' } else { '01-default-console' }
    $directory = Join-Path $OutputRoot $id
    if ($trusted) {
        Write-Fixture "$directory\Directory.Build.props" '<Project><PropertyGroup><EnableSourceLink>false</EnableSourceLink></PropertyGroup></Project>'
    }
    $leaf = New-Project $directory Leaf -Files @{
        'Value.cs' = 'namespace MatrixFixture; public static class LeafValue { public static string Text => "matrix-baseline"; }'
        'payload.txt' = 'payload-baseline'
    } -ExtraItems '<Content Include="payload.txt" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />'
    $middle = New-Project $directory Middle -References @('Leaf') -Files @{
        'Value.cs' = 'namespace MatrixFixture; public static class MiddleValue { public static string Text => LeafValue.Text + ":middle"; }'
    }
    $app = New-Project $directory App -OutputType Exe -References @('Middle') -Files @{
        'Program.cs' = 'Console.WriteLine(MatrixFixture.MiddleValue.Text); Console.WriteLine(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payload.txt")));'
    }
    Add-Scenario $id 'Console with a two-edge library reference chain' $directory @($leaf, $middle, $app) $app $leaf 'Value.cs' 'matrix-baseline' 'matrix-changed' @{
        kind = 'console'; arguments = @()
        expectedBefore = "matrix-baseline:middle`npayload-baseline"; expectedAfter = "matrix-changed:middle`npayload-baseline"
    } -Trusted $trusted -Customizations $(if ($trusted) { @{ EnableSourceLink = 'false' } } else { @{} }) -Assumptions @(
        'The app references Middle, which references Leaf; transitive payload copying is exercised at runtime.'
    )
}

$directory = Join-Path $OutputRoot '03-solution-24'
$dependencies = @{}
$graphProjects = @()
for ($i = 0; $i -lt 24; $i++) {
    $name = 'Graph{0:D2}' -f $i
    $deps = if ($i -lt 4) { @() } elseif ($i -eq 23) { @(20, 21, 22, 19) } else {
        $start = ([int][Math]::Floor($i / 4) - 1) * 4
        @(($start + ($i % 4)), ($start + (($i + 1) % 4)))
    }
    $dependencies[$i] = @($deps)
    $names = @($deps | ForEach-Object { 'Graph{0:D2}' -f $_ })
    $expression = if ($deps.Count -eq 0) { '1' } else { (@($deps | ForEach-Object { 'ScenarioGraph.Node{0:D2}.Value()' -f $_ }) -join ' + ') }
    if ($i -eq 23) {
        $files = @{ 'Program.cs' = "Console.WriteLine($expression);" }
        $graphProjects += New-Project $directory $name -OutputType Exe -References $names -Files $files
    }
    else {
        $files = @{ 'Node.cs' = "namespace ScenarioGraph; public static class Node$('{0:D2}' -f $i) { public static int Value() { return $expression; } }" }
        $graphProjects += New-Project $directory $name -References $names -Files $files
    }
}
function Get-GraphValue([int]$Node, [bool]$Changed) {
    if ($Node -lt 4) { if ($Changed -and $Node -eq 0) { return 101 } else { return 1 } }
    $sum = 0
    foreach ($dependency in $dependencies[$Node]) { $sum += Get-GraphValue $dependency $Changed }
    return $sum
}
Add-Scenario '03-solution-24' 'Genuine 24-project, seven-level solution DAG' $directory $graphProjects $graphProjects[23] $graphProjects[0] 'Node.cs' 'return 1;' 'return 101;' @{
    kind = 'console'; arguments = @(); expectedBefore = "$(Get-GraphValue 23 $false)"; expectedAfter = "$(Get-GraphValue 23 $true)"
} -Assumptions @('All 24 projects are reachable from Graph23. Diamond references and seven levels exercise solution/project scheduling separately.')

$directory = Join-Path $OutputRoot '04-multitarget'
$shared = New-Project $directory Shared -Frameworks @('net8.0', 'net10.0') -Files @{
    'Value.cs' = @'
namespace MultiFixture;
public static class Value
{
    public static string Text => "matrix-baseline";
#if NET8_0
    public static string Framework => "net8.0";
#else
    public static string Framework => "net10.0";
#endif
}
'@
}
$app = New-Project $directory App -OutputType Exe -References @('Shared') -Files @{
    'Program.cs' = 'Console.WriteLine(MultiFixture.Value.Text + ":" + MultiFixture.Value.Framework);'
}
Add-Scenario '04-multitarget' 'net8.0/net10.0 library consumed by net10.0 app' $directory @($shared, $app) $app $shared 'Value.cs' 'matrix-baseline' 'matrix-changed' @{
    kind = 'console'; arguments = @(); expectedBefore = 'matrix-baseline:net10.0'; expectedAfter = 'matrix-changed:net10.0'
} -Assumptions @('Solution builds both library TFMs; runtime output proves net10.0 project-reference selection.')

$directory = Join-Path $OutputRoot '05-web'
$web = New-Project $directory WebApp -Sdk Microsoft.NET.Sdk.Web -OutputType Exe -Files @{
    'Program.cs' = 'var app = WebApplication.CreateBuilder(args).Build(); app.MapGet("/matrix", () => WebFixture.Value.Text); app.Run();'
    'Value.cs' = 'namespace WebFixture; public static class Value { public static string Text => "matrix-baseline"; }'
}
Add-Scenario '05-web' 'Minimal Web SDK application' $directory @($web) $web $web 'Value.cs' 'matrix-baseline' 'matrix-changed' @{
    kind = 'http'; path = '/matrix'; expectedBefore = 'matrix-baseline'; expectedAfter = 'matrix-changed'
} -Assumptions @('Runtime validation uses an ephemeral loopback port and terminates its own server process.')

$directory = Join-Path $OutputRoot '06-razor-static-assets'
$razor = New-Project $directory RazorLibrary -Sdk Microsoft.NET.Sdk.Razor -ExtraItems '<FrameworkReference Include="Microsoft.AspNetCore.App" />' -Files @{
    'Value.cs' = 'namespace RazorFixture; public static class Value { public static string Text => "matrix-baseline"; }'
    'MatrixComponent.razor' = "@namespace RazorFixture`n<div class=`"matrix`">@Value.Text</div>"
    'MatrixComponent.razor.css' = '.matrix { color: navy; }'
    'wwwroot\matrix.txt' = 'asset-baseline'
}
$web = New-Project $directory WebApp -Sdk Microsoft.NET.Sdk.Web -OutputType Exe -References @('RazorLibrary') -Files @{
    'Program.cs' = 'var builder = WebApplication.CreateBuilder(args); builder.WebHost.UseStaticWebAssets(); var app = builder.Build(); app.MapStaticAssets(); app.MapGet("/matrix", () => RazorFixture.Value.Text); app.Run();'
}
Add-Scenario '06-razor-static-assets' 'Razor class library and static assets consumed by Web SDK app' $directory @($razor, $web) $web $razor 'Value.cs' 'matrix-baseline' 'matrix-changed' @{
    kind = 'http'; path = '/matrix'; expectedBefore = 'matrix-baseline'; expectedAfter = 'matrix-changed'
    assetPath = '/_content/RazorLibrary/matrix.txt'; assetExpected = 'asset-baseline'
} -Customizations @{ FrameworkReference = 'Microsoft.AspNetCore.App'; RuntimeEnvironment = 'Development' } -Assumptions @(
    'Real .razor component, scoped CSS and wwwroot content; no component or test NuGet package is added.'
    'Runtime checks both a compiled-library endpoint and a real RCL static asset.'
)

$directory = Join-Path $OutputRoot '07-wpf'
$wpf = New-Project $directory WpfApp -Frameworks @('net10.0-windows') -OutputType WinExe -Properties @{ UseWPF = 'true'; RootNamespace = 'WpfFixture' } -Files @{
    'App.xaml' = '<Application x:Class="WpfFixture.App" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" StartupUri="MainWindow.xaml" />'
    'App.xaml.cs' = 'using System.Windows; namespace WpfFixture; public partial class App : Application { protected override void OnStartup(StartupEventArgs e) { if (e.Args.Contains("--verify")) { Console.WriteLine(Value.Text); Shutdown(); return; } base.OnStartup(e); } }'
    'MainWindow.xaml' = '<Window x:Class="WpfFixture.MainWindow" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Matrix WPF" Width="320" Height="160"><TextBlock Text="Real WPF XAML fixture" /></Window>'
    'MainWindow.xaml.cs' = 'using System.Windows; namespace WpfFixture; public partial class MainWindow : Window { public MainWindow() { InitializeComponent(); } }'
    'Value.cs' = 'namespace WpfFixture; public static class Value { public static string Text => "matrix-baseline"; }'
}
Add-Scenario '07-wpf' 'WPF with generated XAML entry point and window' $directory @($wpf) $wpf $wpf 'Value.cs' 'matrix-baseline' 'matrix-changed' @{
    kind = 'console'; arguments = @('--verify'); expectedBefore = 'matrix-baseline'; expectedAfter = 'matrix-changed'
} -Requires Windows -Customizations @{ UseWPF = 'true'; RuntimeVerification = '--verify exits before opening a window' }

$directory = Join-Path $OutputRoot '08-winforms'
$forms = New-Project $directory FormsApp -Frameworks @('net10.0-windows') -OutputType WinExe -Properties @{ UseWindowsForms = 'true'; RootNamespace = 'FormsFixture' } -Files @{
    'Program.cs' = 'namespace FormsFixture; internal static class Program { [STAThread] private static void Main(string[] args) { if (args.Contains("--verify")) { Console.WriteLine(Value.Text); return; } ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); } }'
    'MainForm.cs' = 'namespace FormsFixture; public sealed class MainForm : Form { public MainForm() { Text = "Matrix WinForms"; Controls.Add(new Label { Text = Value.Text, AutoSize = true }); } }'
    'Value.cs' = 'namespace FormsFixture; public static class Value { public static string Text => "matrix-baseline"; }'
}
Add-Scenario '08-winforms' 'Windows Forms with generated ApplicationConfiguration and real Form' $directory @($forms) $forms $forms 'Value.cs' 'matrix-baseline' 'matrix-changed' @{
    kind = 'console'; arguments = @('--verify'); expectedBefore = 'matrix-baseline'; expectedAfter = 'matrix-changed'
} -Requires Windows -Customizations @{ UseWindowsForms = 'true'; RuntimeVerification = '--verify exits before opening a window' }

$directory = Join-Path $OutputRoot '09-net472'
$referenceDirectory = if ($IsWindows -and ${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2' } else { '' }
$unsupported = if ($referenceDirectory -and (Test-Path "$referenceDirectory\mscorlib.dll")) { '' } else { "Installed .NET Framework 4.7.2 reference assemblies are unavailable at '$referenceDirectory'; this row intentionally does not download a replacement reference-assembly package." }
$framework = New-Project $directory FullFramework -Frameworks @('net472') -Properties @{ LangVersion = 'latest'; AutomaticallyUseReferenceAssemblyPackages = 'false' } -Files @{
    'Value.cs' = 'namespace FrameworkFixture; public static class Value { public static string Text => "matrix-baseline"; }'
}
Add-Scenario '09-net472' 'SDK-style .NET Framework 4.7.2 library' $directory @($framework) $framework $framework 'Value.cs' 'matrix-baseline' 'matrix-changed' @{
    kind = 'library'; arguments = @()
} -Requires WindowsNet472References -UnsupportedReason $unsupported -Customizations @{
    LangVersion = 'latest'; AutomaticallyUseReferenceAssemblyPackages = 'false'; ReferenceAssemblyDirectory = $referenceDirectory
} -Assumptions @('Uses installed .NET Framework reference assemblies. Library edit checks compare DLL hashes; no cross-CLR runtime equivalence is claimed.')

$registry = [pscustomobject]@{
    schemaVersion = 1; root = $OutputRoot; sdkVersion = $SdkVersion; dotnetPath = $DotNetPath
    createdUtc = [DateTime]::UtcNow.ToString('O')
    setupScript = $PSCommandPath; validationMode = 'stock-sdk-correctness-not-performance'
    solutionCount = $rows.Count; scenarioCount = $rows.Count
    packageReferencesAdded = @()
    scenarios = @($rows)
}
$registryPath = Join-Path $OutputRoot 'registry.json'
Write-Fixture $registryPath ($registry | ConvertTo-Json -Depth 30)
Write-Host "Scenario registry: $registryPath"
Write-Host "Created $($rows.Count) scenarios/solutions and $((@($rows | ForEach-Object { $_.projects }).Count)) projects."
return $registryPath
