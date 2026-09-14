#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Registry,
    [string[]]$Scenario = @(),
    [int]$MaxCpuCount = 2,
    [string]$SdkConfiguration = '',
    [string[]]$BuildProperties = @(),
    [string]$Variant = 'stock'
)

$ErrorActionPreference = 'Stop'
$registryPath = [IO.Path]::GetFullPath($Registry)
$matrix = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
$dotnet = $matrix.dotnetPath
$oldSdks = $env:MSBuildSDKsPath
$extraArguments = @($BuildProperties | ForEach-Object { "-p:$_" })
if ($SdkConfiguration) {
    $sdkConfig = Get-Content $SdkConfiguration -Raw | ConvertFrom-Json
    $extraArguments += "-p:NetCoreRoot=$($sdkConfig.NetCoreRoot)"
}
$reportDirectory = Join-Path $matrix.root 'validation'
New-Item -ItemType Directory -Force $reportDirectory | Out-Null
$events = [Collections.Generic.List[object]]::new()
$reportPath = Join-Path $reportDirectory 'results.json'
if (Test-Path $reportPath) {
    foreach ($event in (Get-Content -Raw $reportPath | ConvertFrom-Json)) { $events.Add($event) }
}
$runId = $Variant + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$runDirectory = Join-Path $reportDirectory $runId
New-Item -ItemType Directory -Path $runDirectory | Out-Null

function Save-Results {
    [IO.File]::WriteAllText($registryPath, ($matrix | ConvertTo-Json -Depth 40))
    [IO.File]::WriteAllText($reportPath, (@($events) | ConvertTo-Json -Depth 15))
}

function Invoke-BuildCommand([object]$Row, [string]$Stage, [string]$Verb = 'build', [string]$Entry = '') {
    if (!$Entry) { $Entry = $Row.solution }
    $stem = Join-Path $runDirectory "$($Row.id)-$Stage"
    $arguments = @($Verb, $Entry, '-v:q', '-nologo', '-noconlog', "-flp:Verbosity=quiet;LogFile=$stem.log", "-bl:$stem.binlog")
    if ($Verb -eq 'build') { $arguments += @('--no-restore', "-m:$MaxCpuCount") }
    if ($Verb -eq 'restore') { $arguments += '--disable-parallel' }
    $arguments += $extraArguments
    & $dotnet @arguments | Out-Host
    $exit = $LASTEXITCODE
    $diagnostic = if (Test-Path "$stem.log") { [IO.File]::ReadAllText("$stem.log") } else { '' }
    $event = [pscustomobject]@{
        scenario = $Row.id; stage = $Stage; runId = $runId; variant = $Variant; entry = $Entry; exitCode = $exit
        arguments = $arguments; diagnosticLog = "$stem.log"; binlog = "$stem.binlog"
        diagnostics = if ($exit -ne 0) { $diagnostic.Substring(0, [Math]::Min(10000, $diagnostic.Length)) } else { '' }
    }
    $events.Add($event)
    Save-Results
    return $event
}

function Build-Success([object]$Row, [string]$Stage, [string]$Entry = '') {
    $event = Invoke-BuildCommand $Row $Stage -Entry $Entry
    if ($event.exitCode -ne 0) { throw "Build failed at $Stage (exit $($event.exitCode)). $($event.diagnostics)" }
}

function Assert-Outputs([object]$Row) {
    foreach ($project in $Row.projects) {
        foreach ($output in $project.outputs) {
            if (!(Test-Path -LiteralPath $output -PathType Leaf)) { throw "Expected framework output is missing: $output" }
        }
    }
}

function Assert-SourceLink([object]$Row) {
    $observed = @()
    foreach ($project in $Row.projects) {
        $output = & $dotnet msbuild $project.path -nologo '-getProperty:EnableSourceLink,TargetFramework,TargetFrameworks,NETCoreSdkVersion,MSBuildToolsPath,UseWPF,UseWindowsForms,CustomAfterMicrosoftCommonTargets,CustomAfterRazorSdkTargets' @extraArguments
        if ($LASTEXITCODE -ne 0) { throw "Property evaluation failed for $($project.path)." }
        $properties = (($output -join "`n") | ConvertFrom-Json).Properties
        if ($properties.NETCoreSdkVersion -ne $matrix.sdkVersion) { throw "Unexpected SDK $($properties.NETCoreSdkVersion) for $($project.path)." }
        if ($properties.EnableSourceLink -ne $Row.expectedEnableSourceLink) { throw "EnableSourceLink is '$($properties.EnableSourceLink)', expected '$($Row.expectedEnableSourceLink)' for $($project.path)." }
        $observed += [pscustomobject]@{ project = $project.path; properties = $properties }
    }
    $Row | Add-Member -Force NoteProperty evaluatedProjects $observed
}

function Assert-Http([object]$Row, [string]$Expected) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $baseUrl = "http://127.0.0.1:$port"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnet
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Row.primaryProject)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
    foreach ($argument in @($Row.primaryOutput, '--urls', $baseUrl)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (!$process.Start()) { throw 'Web fixture did not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if ($SdkConfiguration) { $env:MSBuildSDKsPath = $sdkConfig.MSBuildSDKsPath }
        $response = $null
        $lastError = ''
        for ($attempt = 0; $attempt -lt 50; $attempt++) {
            if ($process.HasExited) { throw "Web fixture exited: $($stderr.GetAwaiter().GetResult()) $($stdout.GetAwaiter().GetResult())" }
            try {
                $response = Invoke-WebRequest -Uri ($baseUrl + $Row.runtime.path) -TimeoutSec 2 -ErrorAction Stop
                break
            }
            catch {
                $lastError = $_.Exception.Message
                Start-Sleep -Milliseconds 200
            }
        }
        if ($null -eq $response) { throw "Web fixture was not responsive: $lastError" }
        if ($response.Content -cne $Expected) { throw "HTTP output '$($response.Content)' != '$Expected'." }
        if ($Row.runtime.assetPath) {
            $asset = Invoke-WebRequest -Uri ($baseUrl + $Row.runtime.assetPath) -TimeoutSec 5 -ErrorAction Stop
            $content = if ($asset.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($asset.Content) } else { [string]$asset.Content }
            if ($content -cne $Row.runtime.assetExpected) { throw "Static asset output '$content' != '$($Row.runtime.assetExpected)'." }
        }
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true) }
        $process.WaitForExit()
        $stdout.GetAwaiter().GetResult() | Out-Null
        $stderr.GetAwaiter().GetResult() | Out-Null
        $process.Dispose()
    }
}

function Assert-Runtime([object]$Row, [bool]$Edited = $false) {
    $expected = if ($Edited) { $Row.runtime.expectedAfter } else { $Row.runtime.expectedBefore }
    switch ($Row.runtime.kind) {
        'console' {
            $output = & $dotnet $Row.primaryOutput @($Row.runtime.arguments)
            if ($LASTEXITCODE -ne 0) { throw "Runtime exited $LASTEXITCODE for $($Row.id)." }
            $actual = ($output -join "`n").Replace("`r`n", "`n").TrimEnd("`r", "`n")
            if ($actual -cne $expected) { throw "Runtime '$actual' != '$expected' for $($Row.id)." }
        }
        'http' { Assert-Http $Row $expected }
        'library' { }
        default { throw "Unknown runtime kind '$($Row.runtime.kind)'." }
    }
}

Push-Location $matrix.root
try {
    $sdk = (& $dotnet --version | Out-String).Trim()
    if ($sdk -ne $matrix.sdkVersion) { throw "Registry requires SDK $($matrix.sdkVersion), actual '$sdk'." }
    foreach ($row in $matrix.scenarios) {
        if ($Scenario.Count -ne 0 -and $row.id -notin $Scenario) { continue }
        $row.validationStatus = 'running'
        $row.validationReason = ''
        Save-Results
        if (($row.prerequisites -like 'Windows*') -and !$IsWindows) {
            $row.validationStatus = 'unsupported'
            $row.validationReason = "Scenario requires Windows; current platform is $([Runtime.InteropServices.RuntimeInformation]::OSDescription)."
            $events.Add([pscustomobject]@{ scenario = $row.id; stage = 'validation-summary'; runId = $runId; status = $row.validationStatus; reason = $row.validationReason })
            Save-Results
            continue
        }
        if ($row.unsupportedReason) {
            $row.validationStatus = 'unsupported'
            $row.validationReason = $row.unsupportedReason
            $events.Add([pscustomobject]@{ scenario = $row.id; stage = 'validation-summary'; runId = $runId; status = $row.validationStatus; reason = $row.validationReason })
            Save-Results
            continue
        }
        Write-Host "Validating $($row.id) ($($row.projectCount) projects)"
        $original = $null
        $edited = $false
        try {
            $initial = Invoke-BuildCommand $row 'initial-no-restore'
            if ($initial.exitCode -ne 0) {
                if ($initial.diagnostics -notmatch 'NETSDK1004|project\.assets\.json.*not found') {
                    throw "Initial stock build failed for a reason other than missing assets: $($initial.diagnostics)"
                }
                $restore = Invoke-BuildCommand $row 'restore-after-missing-assets' -Verb restore
                if ($restore.exitCode -ne 0) { throw "Restore failed: $($restore.diagnostics)" }
                Build-Success $row 'baseline'
            }
            Assert-Outputs $row
            Assert-SourceLink $row
            Assert-Runtime $row
            Build-Success $row 'project-entry' -Entry $row.primaryProject

            $oldHashes = @{}
            foreach ($path in $row.edit.affectedOutputs) { $oldHashes[$path] = (Get-FileHash $path).Hash }
            $original = [IO.File]::ReadAllText($row.edit.source)
            if (!$original.Contains($row.edit.from)) { throw "Edit marker missing in $($row.edit.source)." }
            [IO.File]::WriteAllText($row.edit.source, $original.Replace($row.edit.from, $row.edit.to), [Text.UTF8Encoding]::new($false))
            $edited = $true
            Build-Success $row 'source-edit'
            foreach ($path in $row.edit.affectedOutputs) {
                if ((Get-FileHash $path).Hash -eq $oldHashes[$path]) { throw "Source edit did not update compiled output: $path" }
            }
            Assert-Runtime $row $true

            $outputHash = (Get-FileHash $row.primaryOutput).Hash
            Remove-Item -LiteralPath $row.primaryOutput
            Build-Success $row 'missing-primary-output'
            if (!(Test-Path $row.primaryOutput) -or (Get-FileHash $row.primaryOutput).Hash -ne $outputHash) { throw "Missing output was not repaired exactly: $($row.primaryOutput)" }
            Assert-Runtime $row $true

            [IO.File]::WriteAllText($row.edit.source, $original, [Text.UTF8Encoding]::new($false))
            $edited = $false
            Build-Success $row 'restore-baseline'
            Assert-Outputs $row
            Assert-Runtime $row
            $row.validationStatus = 'ready'
            $row.validationReason = "$Variant solution/project builds, source edit, runtime/library checks, output deletion/repair, and restored baseline passed."
            Write-Host "READY $($row.id); EnableSourceLink=$($row.expectedEnableSourceLink); $($row.coarseLabEligibility)"
        }
        catch {
            $row.validationStatus = 'failed'
            $row.validationReason = $_.Exception.Message
            Write-Warning "$($row.id): $($row.validationReason)"
        }
        finally {
            if ($edited -and $null -ne $original) {
                [IO.File]::WriteAllText($row.edit.source, $original, [Text.UTF8Encoding]::new($false))
                $row.validationReason += ' Source text was restored after failure; outputs may need a successful rebuild.'
            }
            $events.Add([pscustomobject]@{ scenario = $row.id; stage = 'validation-summary'; runId = $runId; status = $row.validationStatus; reason = $row.validationReason })
            Save-Results
        }
    }
}
finally {
    $env:MSBuildSDKsPath = $oldSdks
    Pop-Location
}

$matrix.scenarios | Select-Object id, projectCount, validationStatus, expectedEnableSourceLink, coarseLabEligibility | Format-Table
Write-Host "Registry: $registryPath"
Write-Host "Validation events: $reportPath"
if (@($matrix.scenarios | Where-Object { $_.validationStatus -eq 'failed' -and ($Scenario.Count -eq 0 -or $_.id -in $Scenario) }).Count -ne 0) {
    throw 'One or more scenarios failed. Exact reasons and build logs are retained in the registry/results.'
}
