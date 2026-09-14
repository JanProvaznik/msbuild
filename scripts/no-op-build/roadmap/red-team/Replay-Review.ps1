[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$NativeTargetsDirectory,
    [string]$DotNet = 'C:\Program Files\dotnet\dotnet.exe',
    [string]$SdkVersion = '10.0.400',
    [ValidateSet('1', '2', '3', '4', '5', '6', '7')]
    [string[]]$Cases = @('1', '2', '3', '4', '5', '6', '7'),
    [string[]]$NativeExtraArgs = @(),
    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory
)

# Replays existing findings only. No source repository or original fixture is edited.
# New opt-in arguments can be supplied with NativeExtraArgs when testing a fork.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$NativeTargetsDirectory = (Resolve-Path $NativeTargetsDirectory).Path
$inject = Join-Path $NativeTargetsDirectory 'inject.targets'
if (!(Test-Path $inject)) { throw "Missing target import: $inject" }
$ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
if (Test-Path $ArtifactsDirectory) { throw 'Use a fresh ArtifactsDirectory; existing evidence is never overwritten.' }
New-Item -ItemType Directory -Path $ArtifactsDirectory, (Join-Path $ArtifactsDirectory 'logs') | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$commands = [Collections.Generic.List[object]]::new()
$findings = [Collections.Generic.List[object]]::new()
$activation = [Collections.Generic.List[object]]::new()
$activationChecked = [Collections.Generic.HashSet[string]]::new()
$resultFile = Join-Path $ArtifactsDirectory 'results.json'
$report = [ordered]@{
    description = 'Replay of already-established native CoreBuild compatibility counterexamples'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    sdkVersion = $SdkVersion
    nativeTargetsDirectory = $NativeTargetsDirectory
    nativeExtraArgs = $NativeExtraArgs
    casesRequested = $Cases
    activation = $activation
    targetHashes = @('inject.targets', 'core-products.targets', 'translations-products.targets') | ForEach-Object {
        [ordered]@{ file = $_; sha256 = (Get-FileHash (Join-Path $NativeTargetsDirectory $_) -Algorithm SHA256).Hash }
    }
    findings = $findings
    commands = $commands
}

function Save-Results {
    [IO.File]::WriteAllText($resultFile, (ConvertTo-Json -InputObject $report -Depth 15), $utf8)
}

function Copy-Scenario([string]$Template, [string]$Destination, [string[]]$Files) {
    foreach ($file in $Files) {
        $target = Join-Path $ArtifactsDirectory "$Destination\$file"
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "$Template\$file") -Destination $target
    }
}

function Invoke-DotNet([string]$Name, [string[]]$Arguments) {
    $output = @(& $DotNet @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $code = $LASTEXITCODE
    $record = [pscustomobject]@{ name = $Name; exitCode = $code; arguments = $Arguments; output = $output }
    $commands.Add($record)
    Save-Results
    Write-Host ("{0}: exit {1}" -f $Name, $code)
    return $record
}

function Restore-Project([string]$Project) {
    $result = Invoke-DotNet "restore-$Project" @('restore', $Project, '-v:q')
    if ($result.exitCode -ne 0) { throw "Restore failed: $Project. See results.json." }
}

function Build-Project([string]$Name, [string]$Project, [switch]$Native, [string[]]$Extra = @()) {
    if ($Native -and $activationChecked.Add($Project)) {
        $probeArgs = @('msbuild', $Project, '-nologo', '-v:q',
            '-getProperty:_NativeCompletion,NativeCoreBuildContract',
            "-p:CustomAfterMicrosoftCommonTargets=$inject", '-p:NativeCoreBuild=true') + $NativeExtraArgs + $Extra
        $probe = Invoke-DotNet "activation-$Project" $probeArgs
        if ($probe.exitCode -ne 0) { throw "Native activation probe failed for $Project." }
        $properties = (($probe.output -join "`n") | ConvertFrom-Json).Properties
        $activation.Add([pscustomobject]@{
            project = $Project
            completion = $properties._NativeCompletion
            contract = $properties.NativeCoreBuildContract
        })
        Save-Results
        if ([string]::IsNullOrWhiteSpace($properties._NativeCompletion)) {
            throw "Native boundary is not active for $Project. Supply the actual opt-in\acknowledgment properties with NativeExtraArgs; do not mistake stock fallback for a fix."
        }
    }
    $mode = $Native.IsPresent.ToString().ToLowerInvariant()
    $arguments = @('build', $Project, '--no-restore', '-nologo', '-m:1', '-nr:false', '-v:minimal',
        "-p:CustomAfterMicrosoftCommonTargets=$inject", "-p:NativeCoreBuild=$mode",
        "-bl:$(Join-Path $ArtifactsDirectory "logs\$Name.binlog")")
    if ($Native) { $arguments += $NativeExtraArgs }
    $arguments += $Extra
    return Invoke-DotNet $Name $arguments
}

function Warm-Project([string]$Name, [string]$Project, [string[]]$Extra = @()) {
    foreach ($iteration in 1..3) {
        $result = Build-Project "$Name-$iteration" $Project -Native -Extra $Extra
        if ($result.exitCode -ne 0) { throw "Warm build failed: $Name. See results.json." }
    }
}

function Run-App([string]$Name, [string]$Assembly) {
    $result = Invoke-DotNet $Name @($Assembly)
    if ($result.exitCode -ne 0) { throw "Application failed: $Assembly. See results.json." }
    return ($result.output -join "`n").Trim()
}

function Add-Finding([string]$Id, [string]$Title, [string]$Location, $Native, $Stock, [bool]$Reproduced) {
    $findings.Add([pscustomobject]@{
        id = $Id; title = $Title; sourceLocation = $Location
        native = $Native; stock = $Stock; reproducedOriginalFailure = $Reproduced
    })
    Save-Results
}

$oldServer = $env:MSBUILDUSESERVER
$oldSdkPath = $env:MSBuildSDKsPath
Push-Location $ArtifactsDirectory
try {
    $env:MSBUILDUSESERVER = '0'
    $env:MSBuildSDKsPath = $null
    [IO.File]::WriteAllText((Join-Path $ArtifactsDirectory 'global.json'),
        (ConvertTo-Json -InputObject @{ sdk = @{ version = $SdkVersion; rollForward = 'disable' } }), $utf8)
    Copy-Item (Join-Path $PSScriptRoot 'NuGet.Config') (Join-Path $ArtifactsDirectory 'NuGet.Config')
    $version = Invoke-DotNet 'sdk-version' @('--version')
    if (($version.output -join '').Trim() -ne $SdkVersion) { throw 'The requested SDK was not selected.' }

    # 1: A real prebuild target, not the PreBuildEvent command property.
    if ('1' -in $Cases) {
    Copy-Scenario 'ordering' 'ordering' @('App\App.csproj', 'App\Program.cs', 'Producer\Producer.csproj')
    Restore-Project 'ordering\App\App.csproj'
    $native = Build-Project '01-ordering-native' 'ordering\App\App.csproj' -Native
    $generatedBeforeStock = Test-Path 'ordering\Producer\Generated.cs'
    $stock = Build-Project '01-ordering-stock' 'ordering\App\App.csproj'
    $value = Run-App '01-ordering-run' 'ordering\App\bin\Debug\net10.0\App.dll'
    Add-Finding '1' 'PreBuildEvent hook executes after project references' 'core-products.targets:15-16' `
        @{ exitCode = $native.exitCode; generated = $generatedBeforeStock } `
        @{ exitCode = $stock.exitCode; runtime = $value } `
        ($native.exitCode -ne 0 -and !$generatedBeforeStock -and $stock.exitCode -eq 0 -and $value -eq '42')
    }

    # 2: Reachability and state on warm builds; dirty-state observation is retained in command output.
    if ('2' -in $Cases) {
    Copy-Scenario 'core' 'hooks' @('App.csproj', 'Program.cs')
    Restore-Project 'hooks\App.csproj'
    Warm-Project '02-hooks-warm' 'hooks\App.csproj'
    $native = Build-Project '02-hooks-native' 'hooks\App.csproj' -Native -Extra @('-p:HookFailure=true')
    $stock = Build-Project '02-hooks-stock' 'hooks\App.csproj' -Extra @('-p:HookFailure=true')
    $observer = @($native.output | Where-Object { $_ -match 'OBSERVE:' })
    Add-Finding '2' 'Warm payload suppresses validation hook and SDK\custom state' 'core-products.targets:88-92' `
        @{ exitCode = $native.exitCode; observer = $observer } @{ exitCode = $stock.exitCode } `
        ($native.exitCode -eq 0 -and $stock.exitCode -ne 0 -and ($stock.output -join "`n") -match 'HOOK-VALIDATION-FAILED')
    }

    # 3: The external binary reference's primary assembly retains its bytes and timestamp.
    if ('3' -in $Cases) {
    Copy-Scenario 'references' 'references' @('App\App.csproj', 'App\Program.cs', 'Lib\Lib.csproj', 'Lib\Value.cs', 'Dep\Dep.csproj', 'Dep\Value.cs')
    [IO.File]::WriteAllText((Join-Path $ArtifactsDirectory 'references\Dep\Value.cs'),
        'public static class Dep { public static string Value => "old"; }', $utf8)
    Restore-Project 'references\Lib\Lib.csproj'
    $null = Build-Project '03-reference-library-initial' 'references\Lib\Lib.csproj'
    Restore-Project 'references\App\App.csproj'
    Warm-Project '03-reference-warm' 'references\App\App.csproj'
    $primaryPath = 'references\Lib\bin\Debug\net10.0\Lib.dll'
    $primaryHash = (Get-FileHash $primaryPath).Hash
    $primaryTime = (Get-Item $primaryPath).LastWriteTimeUtc
    [IO.File]::WriteAllText((Join-Path $ArtifactsDirectory 'references\Dep\Value.cs'),
        'public static class Dep { public static string Value => "new"; }', $utf8)
    $null = Build-Project '03-reference-library-updated' 'references\Lib\Lib.csproj'
    $primaryUnchanged = (Get-FileHash $primaryPath).Hash -eq $primaryHash -and (Get-Item $primaryPath).LastWriteTimeUtc -eq $primaryTime
    $native = Build-Project '03-reference-native' 'references\App\App.csproj' -Native
    $nativeValue = Run-App '03-reference-native-run' 'references\App\bin\Debug\net10.0\App.dll'
    $stock = Build-Project '03-reference-stock' 'references\App\App.csproj'
    $stockValue = Run-App '03-reference-stock-run' 'references\App\bin\Debug\net10.0\App.dll'
    Add-Finding '3' 'Transitive binary reference source change is missed' 'core-products.targets:26-34' `
        @{ exitCode = $native.exitCode; runtime = $nativeValue; primaryUnchanged = $primaryUnchanged } `
        @{ exitCode = $stock.exitCode; runtime = $stockValue } `
        ($primaryUnchanged -and $nativeValue -eq 'old' -and $stockValue -eq 'new')
    }

    # 4: Ordinary SDK runtimeconfig semantics.
    if ('4' -in $Cases) {
    Copy-Scenario 'sdk-contracts' 'runtime' @('App.csproj', 'Program.cs')
    Restore-Project 'runtime\App.csproj'
    Warm-Project '04-runtime-warm' 'runtime\App.csproj'
    $native = Build-Project '04-runtime-native' 'runtime\App.csproj' -Native -Extra @('-p:ServerGarbageCollection=true')
    $nativeConfig = Get-Content 'runtime\bin\Debug\net10.0\App.runtimeconfig.json' -Raw | ConvertFrom-Json
    $stock = Build-Project '04-runtime-stock' 'runtime\App.csproj' -Extra @('-p:ServerGarbageCollection=true')
    $stockConfig = Get-Content 'runtime\bin\Debug\net10.0\App.runtimeconfig.json' -Raw | ConvertFrom-Json
    $nativeGC = $nativeConfig.runtimeOptions.configProperties.'System.GC.Server'
    $stockGC = $stockConfig.runtimeOptions.configProperties.'System.GC.Server'
    Add-Finding '4' 'ServerGarbageCollection runtimeconfig change is missed' 'core-products.targets:35-61' `
        @{ exitCode = $native.exitCode; serverGC = $nativeGC } @{ exitCode = $stock.exitCode; serverGC = $stockGC } `
        ($nativeGC -ne $true -and $stockGC -eq $true)
    }

    # 5: Both package versions exist before any completion stamp; no timestamp manipulation.
    if ('5' -in $Cases) {
    Copy-Scenario 'translation-late' 'translations' @('App\OrchardCore.Cms.Web.csproj', 'App\Value.cs', 'packages\v1\cs\messages.po', 'packages\v2\cs\messages.po')
    Restore-Project 'translations\App\OrchardCore.Cms.Web.csproj'
    Warm-Project '05-translations-warm' 'translations\App\OrchardCore.Cms.Web.csproj' @('-p:NativeIncrementalTranslations=true')
    $native = Build-Project '05-translations-native' 'translations\App\OrchardCore.Cms.Web.csproj' -Native `
        -Extra @('-p:NativeIncrementalTranslations=true', '-p:TranslationVersion=v2')
    $nativeText = (Get-Content 'translations\App\Localization\cs\messages.po' -Raw).Trim()
    $stock = Build-Project '05-translations-stock' 'translations\App\OrchardCore.Cms.Web.csproj' `
        -Extra @('-p:NativeIncrementalTranslations=false', '-p:TranslationVersion=v2')
    $stockText = (Get-Content 'translations\App\Localization\cs\messages.po' -Raw).Trim()
    Add-Finding '5' 'Execution-time translation mapping change is missed' 'translations-products.targets:5-7,22-25' `
        @{ exitCode = $native.exitCode; content = $nativeText } @{ exitCode = $stock.exitCode; content = $stockText } `
        ($nativeText -eq 'translation from package one' -and $stockText -eq 'different translation from package two')
    }

    # Follow-up A: An SDK-generated compiler input, without custom targets.
    if ('6' -in $Cases) {
    Copy-Scenario 'sdk-contracts' 'company' @('App.csproj', 'Program.cs')
    Restore-Project 'company\App.csproj'
    Warm-Project '06-company-warm' 'company\App.csproj' @('-p:Company=OriginalCompany')
    $native = Build-Project '06-company-native' 'company\App.csproj' -Native -Extra @('-p:Company=NewCompany')
    $nativeValue = Run-App '06-company-native-run' 'company\bin\Debug\net10.0\App.dll'
    $stock = Build-Project '06-company-stock' 'company\App.csproj' -Extra @('-p:Company=NewCompany')
    $stockValue = Run-App '06-company-stock-run' 'company\bin\Debug\net10.0\App.dll'
    Add-Finding '6' 'Company change leaves stale compiled assembly attribute' 'core-products.targets:35-61' `
        @{ exitCode = $native.exitCode; company = $nativeValue } @{ exitCode = $stock.exitCode; company = $stockValue } `
        ($nativeValue -eq 'OriginalCompany' -and $stockValue -eq 'NewCompany')
    }

    # Follow-up B: Standard Compile metadata; query after Build demonstrates the public target remains usable.
    if ('7' -in $Cases) {
    Copy-Scenario 'sdk-contracts' 'copy' @('App.csproj', 'Program.cs')
    Restore-Project 'copy\App.csproj'
    Warm-Project '07-copy-warm' 'copy\App.csproj'
    $native = Build-Project '07-copy-native' 'copy\App.csproj' -Native -Extra @('-p:SourceCopyMode=PreserveNewest')
    $nativeExists = Test-Path 'copy\bin\Debug\net10.0\Program.cs'
    $queryArgs = @('msbuild', 'copy\App.csproj', '-nologo', '-v:q', '-t:GetCopyToOutputDirectoryItems',
        '-getTargetResult:GetCopyToOutputDirectoryItems', "-p:CustomAfterMicrosoftCommonTargets=$inject",
        '-p:NativeCoreBuild=true', '-p:SourceCopyMode=PreserveNewest',
        "-bl:$(Join-Path $ArtifactsDirectory 'logs\07-copy-query.binlog')") + $NativeExtraArgs
    $query = Invoke-DotNet '07-copy-query' $queryArgs
    $queryJson = ($query.output -join "`n") | ConvertFrom-Json
    $queryItems = @($queryJson.TargetResults.GetCopyToOutputDirectoryItems.Items |
        Where-Object { $_.TargetPath -eq 'Program.cs' } |
        Select-Object Identity, TargetPath, CopyToOutputDirectory)
    $stock = Build-Project '07-copy-stock' 'copy\App.csproj' -Extra @('-p:SourceCopyMode=PreserveNewest')
    $stockExists = Test-Path 'copy\bin\Debug\net10.0\Program.cs'
    Add-Finding '7' 'Compile CopyToOutputDirectory metadata change is missed' 'core-products.targets:36,67-70' `
        @{ exitCode = $native.exitCode; copiedSourceExists = $nativeExists; queryItems = $queryItems } `
        @{ exitCode = $stock.exitCode; copiedSourceExists = $stockExists } `
        (!$nativeExists -and $stockExists -and $queryItems.Count -eq 1)
    }

    $report['completedUtc'] = [DateTime]::UtcNow.ToString('o')
    $report['reproducedOriginalFailures'] = @($findings | Where-Object reproducedOriginalFailure).Count
    $report['note'] = 'False means the original counterexample did not reproduce; it is not a general compatibility certification. Inspect command logs and active opt-in gates.'
    Save-Results
    $findings | Select-Object id, title, reproducedOriginalFailure | Format-Table -AutoSize
    Write-Host "Results: $resultFile"
}
catch {
    $report['runnerError'] = $_.ToString()
    Save-Results
    throw
}
finally {
    $env:MSBUILDUSESERVER = $oldServer
    $env:MSBuildSDKsPath = $oldSdkPath
    Pop-Location
}
