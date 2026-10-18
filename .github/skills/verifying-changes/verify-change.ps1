<#
.SYNOPSIS
  Scoped verification of a working-tree change in dotnet/msbuild.
  Maps changed files to test projects, builds them with warnings as errors, runs the tests,
  and prints "RESULT: PASS" or "RESULT: FAIL" as the last line.
.EXAMPLE
  .\.github\skills\verifying-changes\verify-change.ps1 -Filter "*MyTest*"
  On macOS/Linux: pwsh .github/skills/verifying-changes/verify-change.ps1
#>
[CmdletBinding()]
param(
    [string]$Filter = '',        # xUnit v3 method filter, wildcards allowed. Omit to run every test in the mapped projects.
    [switch]$AllTfms,            # Windows only: run net472 as well as net11.0.
    [switch]$BuildOnly,          # Build with warnings as errors, skip the test run.
    [string[]]$Projects = @()    # Override the file-to-project mapping with explicit test projects.
)
$ErrorActionPreference = 'Continue'
Set-Location (git rev-parse --show-toplevel)

$dotnet = 'dotnet'
if (Test-Path .\.dotnet\dotnet.exe) { $dotnet = '.\.dotnet\dotnet.exe' } elseif (Test-Path ./.dotnet/dotnet) { $dotnet = './.dotnet/dotnet' }
$logDir = 'artifacts/log/verify-change'
New-Item -ItemType Directory -Force $logDir | Out-Null
Remove-Item "$logDir/*.log" -ErrorAction SilentlyContinue
function Finish([string]$Result) { Write-Host "RESULT: $Result"; if ($Result -eq 'PASS') { exit 0 } else { exit 1 } }

# 1. Changed files relative to HEAD, tracked and untracked.
$changed = @(@(git diff --name-only HEAD) + @(git ls-files --others --exclude-standard) | Sort-Object -Unique)
if (-not $changed -and -not $Projects) { Write-Host 'No changed files relative to HEAD and no -Projects given.'; Finish FAIL }
Write-Host '== Changed files'; $changed | ForEach-Object { Write-Host "   $_" }

# 2. Map files to test projects. src/Shared is linked into Build, Tasks and Utilities, so it maps to all three.
$T = @{
    Build      = 'src/Build.UnitTests/Microsoft.Build.Engine.UnitTests.csproj'
    BuildOM    = 'src/Build.OM.UnitTests/Microsoft.Build.Engine.OM.UnitTests.csproj'
    BuildCheck = 'src/BuildCheck.UnitTests/Microsoft.Build.BuildCheck.UnitTests.csproj'
    Framework  = 'src/Framework.UnitTests/Microsoft.Build.Framework.UnitTests.csproj'
    Tasks      = 'src/Tasks.UnitTests/Microsoft.Build.Tasks.UnitTests.csproj'
    Utilities  = 'src/Utilities.UnitTests/Microsoft.Build.Utilities.UnitTests.csproj'
    MSBuild    = 'src/MSBuild.UnitTests/Microsoft.Build.CommandLine.UnitTests.csproj'
    StringTools = 'src/StringTools.UnitTests/StringTools.UnitTests.csproj'
}
$mapped = foreach ($f in $changed) {
    switch -Regex ($f) {
        '^src/Build\.UnitTests/'              { $T.Build }
        '^src/Build\.OM\.UnitTests/'          { $T.BuildOM }
        '^src/BuildCheck\.UnitTests/'         { $T.BuildCheck }
        '^src/Framework\.UnitTests/'          { $T.Framework }
        '^src/Tasks\.UnitTests/'              { $T.Tasks }
        '^src/Utilities\.UnitTests/'          { $T.Utilities }
        '^src/MSBuild\.UnitTests/'            { $T.MSBuild }
        '^src/StringTools\.UnitTests/'        { $T.StringTools }
        '^src/(Build|Framework)/BuildCheck/'  { $T.BuildCheck }
        '^src/Build/'                         { $T.Build }
        '^src/Framework/'                     { $T.Framework; $T.Build }
        '^src/Tasks/'                         { $T.Tasks }
        '^src/Utilities/'                     { $T.Utilities }
        '^src/MSBuild/'                       { $T.MSBuild }
        '^src/StringTools/'                   { $T.StringTools; $T.Build }
        '^src/Shared/'                        { $T.Build; $T.Tasks; $T.Utilities }
        '^src/UnitTests\.Shared/'             { $T.Build }
    }
}
if ($Projects) { $mapped = $Projects }
$projects = @($mapped | Sort-Object -Unique)
if (-not $projects) { Write-Host '== No source under src/ changed; nothing to build or test.'; Finish PASS }
Write-Host '== Test projects'; $projects | ForEach-Object { Write-Host "   $_" }

# Non-Windows test projects target only net11.0; on Windows default to net11.0 unless -AllTfms.
$tfm = @()
if ($env:OS -eq 'Windows_NT' -and -not $AllTfms) { $tfm = @('-f', 'net11.0') }
$failed = $false

# 3. Build with warnings as errors. NuGet audit advisories (NU1901-NU1904) change with the feed, not the code, so they stay warnings.
Write-Host '== Build (warnings are errors)'
foreach ($p in $projects) {
    $log = "$logDir/build-$([IO.Path]::GetFileNameWithoutExtension($p)).log"
    Write-Host "-- $p"
    & $dotnet build $p @tfm -v:q -nologo -warnaserror -warnnotaserror:NU1901,NU1902,NU1903,NU1904 -p:NuGetAudit=false -clp:"ErrorsOnly;Summary" -flp:"v=q;LogFile=$log"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "BUILD FAILED: $p (exit $LASTEXITCODE). First errors:"
        Select-String -Path $log -Pattern 'error|warning' | Select-Object -First 20 | ForEach-Object { Write-Host "   $($_.Line)" }
        Write-Host "   full log: $log"
        $failed = $true
    }
}
if ($failed) { Finish FAIL }
if ($BuildOnly) { Finish PASS }

# 4. Run tests. Everything after "--" goes to the xUnit v3 / MTP runner (see the running-unit-tests skill).
Write-Host "== Tests$(if ($Filter) { " (filter: $Filter)" })"
$runner = @('--no-progress')
if ($Filter) { $runner = @('--filter-method', $Filter) + $runner }
foreach ($p in $projects) {
    $log = "$logDir/test-$([IO.Path]::GetFileNameWithoutExtension($p)).log"
    Write-Host "-- $p"
    & $dotnet test $p @tfm --no-build -- @runner | Tee-Object -FilePath $log |
        Select-String -Pattern '^\s*(total|failed|passed|skipped|error|duration|Test run summary)|^\s*failed [\w.]+ \(|Exception|Shouldly' |
        Select-Object -First 80 | ForEach-Object { Write-Host $_.Line }
    $rc = $LASTEXITCODE
    $summary = (Select-String -Path $log -Pattern 'Test run summary' | Select-Object -Last 1).Line
    $counts = (Select-String -Path $log -Pattern '^\s*(total|failed|passed|skipped):' | ForEach-Object { $_.Line.Trim() }) -join ' '
    if ($rc -ne 0) {
        Write-Host "TESTS FAILED: $p (exit $rc)"; Write-Host "   $summary $counts"; Write-Host "   full log: $log"
        $failed = $true
    } else {
        if (-not $summary) { $summary = "(no summary line found; see $log)" }
        Write-Host "   $summary $counts"
        if ($Filter -and (Select-String -Path $log -Pattern 'total: 0\b|Zero tests ran' -Quiet)) { Write-Host '   WARNING: the filter matched zero tests in this project.' }
    }
}
if ($failed) { Finish FAIL } else { Finish PASS }
