#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Registry,
    [Parameter(Mandatory)][string]$SdkConfiguration,
    [Parameter(Mandatory)][string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$matrix = Get-Content -LiteralPath $Registry -Raw | ConvertFrom-Json
$configuration = Get-Content -LiteralPath $SdkConfiguration -Raw | ConvertFrom-Json
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path $OutputRoot) { throw "Choose a fresh OutputRoot: $OutputRoot already exists." }
foreach ($project in $matrix.scenarios.projects) {
    $directory = [IO.Path]::GetDirectoryName($project.path) + [IO.Path]::DirectorySeparatorChar
    if ($OutputRoot.StartsWith($directory, [StringComparison]::OrdinalIgnoreCase)) { throw 'Outputs must be outside all fixture project globs.' }
}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'logs') | Out-Null
$reportPath = Join-Path $OutputRoot 'results.json'
$dotnet = $matrix.dotnetPath
$commands = [Collections.Generic.List[object]]::new()
$rows = @(
    [pscustomobject]@{ id = 'publish-console'; status = 'pending'; reason = ''; details = $null }
    [pscustomobject]@{ id = 'publish-razor-web'; status = 'pending'; reason = ''; details = $null }
    [pscustomobject]@{ id = 'pack-razor-library'; status = 'pending'; reason = ''; details = $null }
    [pscustomobject]@{ id = 'git-sourcelink-commit-change'; status = 'pending'; reason = ''; details = $null }
)
$report = [pscustomobject]@{
    schemaVersion = 1; outputRoot = $OutputRoot; sdkConfiguration = $SdkConfiguration
    sdkVersion = $matrix.sdkVersion; MSBuildSDKsPath = $configuration.MSBuildSDKsPath
    mode = 'correctness-only'; SourceLinkDisabled = $false; NativeCoreImported = $false
    stockFlag = 'EnableIncrementalTargetOptimizations=false'; safeFlag = 'EnableIncrementalTargetOptimizations=true'
    rows = $rows; commands = $commands
    boundaries = @(
        'Both variants use the same private SDK with the optimization flag off/on; this is not a comparison against another SDK version.'
        'No SourceLink opt-out or native CoreBuild import is supplied.'
        'Publish output relative paths and SHA-256 hashes are compared; filesystem timestamps are not required to match.'
        'NuGet payloads, nuspec semantics and normalized entry sets are compared, not ZIP archive bytes/timestamps.'
        'NuGet core-properties part GUIDs, relationship IDs, and core-properties creation/modification times are packaging metadata, not payload parity claims.'
        'The Git repository is real and local. Its synthetic GitHub remote only selects the SourceLink provider; no network fetch/push or remote URL reachability check occurs.'
    )
    fixtureSourcesUnchanged = $false
}

function Save-Report {
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 50))
}

function Write-Generated([string]$Path, [string]$Content) {
    New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Run-Sdk([string]$Name, [string]$Verb, [string]$Project, [bool]$Safe, [string[]]$Extra = @()) {
    $stem = Join-Path $OutputRoot "logs\$Name"
    $flag = $Safe.ToString().ToLowerInvariant()
    $arguments = @($Verb, $Project, '-nologo', '-v:q', '-noconlog',
        "-flp:Verbosity=quiet;LogFile=$stem.log", "-bl:$stem.binlog",
        "-p:NetCoreRoot=$($configuration.NetCoreRoot)", "-p:EnableIncrementalTargetOptimizations=$flag")
    if ($Verb -ne 'restore') { $arguments += @('--no-restore', '-c', 'Debug', '-m:2') }
    else { $arguments += '--disable-parallel' }
    $arguments += $Extra
    $output = @(& $dotnet @arguments)
    $exit = $LASTEXITCODE
    $diagnostics = if (Test-Path "$stem.log") { [IO.File]::ReadAllText("$stem.log") } else { '' }
    $commands.Add([pscustomobject]@{
        name = $Name; arguments = $arguments; exitCode = $exit; log = "$stem.log"; binlog = "$stem.binlog"
        diagnostics = if ($exit -ne 0) { $diagnostics.Substring(0, [Math]::Min(10000, $diagnostics.Length)) } else { '' }
    })
    Save-Report
    return [pscustomobject]@{ exitCode = $exit; output = ($output -join "`n"); diagnostics = $diagnostics }
}

function Require-Success([object]$Result, [string]$Name) {
    if ($Result.exitCode -ne 0) { throw "$Name failed (exit $($Result.exitCode)): $($Result.diagnostics)" }
}

function Assert-DefaultPolicy([string]$Project, [bool]$Safe) {
    $flag = $Safe.ToString().ToLowerInvariant()
    $output = & $dotnet msbuild $Project -nologo "-p:NetCoreRoot=$($configuration.NetCoreRoot)" "-p:EnableIncrementalTargetOptimizations=$flag" '-getProperty:EnableSourceLink,NativeCoreBuild,MSBuildSDKsPath,NETCoreSdkVersion,EnableIncrementalTargetOptimizations'
    if ($LASTEXITCODE -ne 0) { throw "Policy evaluation failed: $Project" }
    $properties = (($output -join "`n") | ConvertFrom-Json).Properties
    if ($properties.EnableSourceLink -ne 'true' -or $properties.NativeCoreBuild -eq 'true') { throw "Default SourceLink/native Core policy violated: $Project" }
    if ($properties.NETCoreSdkVersion -ne $matrix.sdkVersion -or $properties.MSBuildSDKsPath.TrimEnd('\', '/') -ne $configuration.MSBuildSDKsPath.TrimEnd('\', '/')) { throw "Wrong SDK selection: $Project" }
    if ($properties.EnableIncrementalTargetOptimizations -ne $flag) { throw 'Optimization flag did not reach the project.' }
    return $properties
}

function File-Inventory([string]$Root) {
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse | ForEach-Object {
        [pscustomobject]@{ path = [IO.Path]::GetRelativePath($Root, $_.FullName); length = $_.Length; sha256 = (Get-FileHash $_.FullName).Hash }
    } | Sort-Object path)
}

function Assert-Inventory([object[]]$Stock, [object[]]$Safe, [string]$Label) {
    $left = ConvertTo-Json -Depth 15 -Compress $Stock
    $right = ConvertTo-Json -Depth 15 -Compress $Safe
    if ($left -cne $right) {
        $difference = Compare-Object ($Stock | ForEach-Object { ConvertTo-Json -Compress $_ }) ($Safe | ForEach-Object { ConvertTo-Json -Compress $_ })
        throw "$Label content/entry mismatch: $(($difference | Format-Table | Out-String).Trim())"
    }
}

function Run-Console([string]$Assembly, [string[]]$Arguments = @()) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnet
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Assembly)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($Assembly) + $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (!$process.Start()) { throw "Could not start $Assembly." }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (!$process.WaitForExit(30000)) { throw "Runtime timed out: $Assembly" }
        $text = $stdout.GetAwaiter().GetResult().Replace("`r`n", "`n").TrimEnd("`r", "`n")
        $errors = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Runtime exit $($process.ExitCode): $errors" }
        return $text
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Assert-PublishedWeb([string]$Root, [object]$Scenario) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $url = "http://127.0.0.1:$port"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnet
    $start.WorkingDirectory = $Root
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
    foreach ($argument in @((Join-Path $Root ([IO.Path]::GetFileName($Scenario.primaryOutput))), '--urls', $url)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (!$process.Start()) { throw 'Published Web fixture did not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        $response = $null
        $lastError = ''
        for ($attempt = 0; $attempt -lt 50; $attempt++) {
            if ($process.HasExited) { throw "Published Web exited: $($stderr.GetAwaiter().GetResult()) $($stdout.GetAwaiter().GetResult())" }
            try {
                $response = Invoke-WebRequest -Uri ($url + $Scenario.runtime.path) -TimeoutSec 2 -ErrorAction Stop
                break
            }
            catch { $lastError = $_.Exception.Message; Start-Sleep -Milliseconds 200 }
        }
        if ($null -eq $response) { throw "Published Web did not become responsive: $lastError" }
        if ($response.Content -cne $Scenario.runtime.expectedBefore) { throw 'Published Web compiled endpoint has stale content.' }
        $asset = Invoke-WebRequest -Uri ($url + $Scenario.runtime.assetPath) -TimeoutSec 5 -ErrorAction Stop
        $text = if ($asset.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($asset.Content) } else { [string]$asset.Content }
        if ($text -cne $Scenario.runtime.assetExpected) { throw 'Published RCL static asset has stale content.' }
        return [pscustomobject]@{ environment = 'Production'; endpoint = $Scenario.runtime.path; response = $response.Content; asset = $Scenario.runtime.assetPath; assetResponse = $text }
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true) }
        $process.WaitForExit()
        $stdout.GetAwaiter().GetResult() | Out-Null
        $stderr.GetAwaiter().GetResult() | Out-Null
        $process.Dispose()
    }
}

function Normalize-CorePart([string]$Value) {
    return [regex]::Replace($Value, 'package/services/metadata/core-properties/[0-9a-fA-F-]+\.psmdcp', 'package/services/metadata/core-properties/CORE.psmdcp')
}

function Package-Inventory([string]$Package) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $entries = @()
        $nuspec = $null
        foreach ($entry in $archive.Entries) {
            $stream = $entry.Open()
            $memory = [IO.MemoryStream]::new()
            try { $stream.CopyTo($memory); $bytes = $memory.ToArray() }
            finally { $stream.Dispose(); $memory.Dispose() }
            $name = Normalize-CorePart $entry.FullName
            $semantic = $null
            if ($name -match '\.nuspec$|\.rels$|\.psmdcp$|^\[Content_Types\]\.xml$') {
                $xml = [Xml.XmlDocument]::new()
                $xml.LoadXml((Normalize-CorePart ([Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF))))
                if ($name -eq '_rels/.rels') {
                    $relationships = @($xml.DocumentElement.ChildNodes | Where-Object NodeType -eq 'Element' | ForEach-Object {
                        [pscustomobject]@{ type = $_.Type; target = $_.Target; targetMode = $_.TargetMode }
                    } | Sort-Object type, target)
                    $semantic = ConvertTo-Json -Compress $relationships
                }
                else {
                    if ($name.EndsWith('.psmdcp')) {
                        foreach ($node in @($xml.SelectNodes("//*[local-name()='created' or local-name()='modified']"))) { $node.ParentNode.RemoveChild($node) | Out-Null }
                    }
                    $semantic = $xml.DocumentElement.OuterXml
                }
                if ($name.EndsWith('.nuspec')) {
                    $metadata = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
                    $nuspec = [pscustomobject]@{ id = $metadata.id; version = $metadata.version; xml = $metadata.OuterXml }
                }
            }
            $entries += [pscustomobject]@{
                path = $name
                sha256 = if ($null -eq $semantic) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) } else { $null }
                semantic = $semantic
            }
        }
        return [pscustomobject]@{
            package = $Package; rawEntries = @($archive.Entries.FullName | Sort-Object)
            entries = @($entries | Sort-Object path); nuspec = $nuspec
        }
    }
    finally { $archive.Dispose() }
}

function Read-PdbSourceLink([string]$Pdb) {
    $stream = [IO.File]::OpenRead($Pdb)
    $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($stream)
    try {
        $reader = $provider.GetMetadataReader()
        foreach ($handle in $reader.CustomDebugInformation) {
            $information = $reader.GetCustomDebugInformation($handle)
            if ($reader.GetGuid($information.Kind) -eq [Guid]'CC110556-A091-4D38-9FEC-25AB9A351A6A') {
                return [Text.Encoding]::UTF8.GetString($reader.GetBlobBytes($information.Value))
            }
        }
        throw "Portable PDB has no SourceLink record: $Pdb"
    }
    finally { $provider.Dispose(); $stream.Dispose() }
}

function Invoke-Case([object]$Row, [scriptblock]$Body) {
    $Row.status = 'running'
    Save-Report
    try {
        $Row.details = & $Body
        $Row.status = 'passed'
        Write-Host "PASS $($Row.id)"
    }
    catch {
        $Row.status = 'failed'
        $Row.reason = $_.Exception.Message
        Write-Warning "$($Row.id): $($Row.reason)"
    }
    Save-Report
}

$console = $matrix.scenarios | Where-Object id -eq '01-default-console'
$web = $matrix.scenarios | Where-Object id -eq '06-razor-static-assets'
$library = $web.projects | Where-Object name -eq 'RazorLibrary'
$sourceHashes = @{}
foreach ($project in @($console.projects) + @($web.projects)) {
    $directory = [IO.Path]::GetDirectoryName($project.path)
    foreach ($file in Get-ChildItem $directory -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
        $sourceHashes[$file.FullName] = (Get-FileHash $file.FullName).Hash
    }
}
$originalSdksPath = $env:MSBuildSDKsPath
Push-Location $matrix.root
try {
    $env:MSBuildSDKsPath = $configuration.MSBuildSDKsPath
    Save-Report
    foreach ($pair in @(@($rows[0], $console), @($rows[1], $web))) {
        $row = $pair[0]
        $scenario = $pair[1]
        Invoke-Case $row {
            $variants = @()
            foreach ($safe in @($false, $true)) {
                $variant = if ($safe) { 'safe' } else { 'stock' }
                $destination = Join-Path $OutputRoot "$($row.id)\$variant"
                $policies = @($scenario.projects | ForEach-Object { Assert-DefaultPolicy $_.path $safe })
                $result = Run-Sdk "$($row.id)-$variant" publish $scenario.primaryProject $safe @('-o', $destination)
                Require-Success $result "$($row.id) $variant"
                $inventory = File-Inventory $destination
                if ($scenario.runtime.kind -eq 'console') {
                    $runtime = Run-Console (Join-Path $destination ([IO.Path]::GetFileName($scenario.primaryOutput)))
                    if ($runtime -cne $scenario.runtime.expectedBefore) { throw "Published console output '$runtime' is wrong." }
                }
                else { $runtime = Assert-PublishedWeb $destination $scenario }
                $variants += [pscustomobject]@{ variant = $variant; output = $destination; policy = $policies; files = $inventory; runtime = $runtime }
            }
            Assert-Inventory $variants[0].files $variants[1].files $row.id
            return [pscustomobject]@{ fileSetAndHashesEqual = $true; variants = $variants }
        }
    }

    Invoke-Case $rows[2] {
        $variants = @()
        foreach ($safe in @($false, $true)) {
            $variant = if ($safe) { 'safe' } else { 'stock' }
            $destination = Join-Path $OutputRoot "pack-razor-library\$variant"
            $policy = Assert-DefaultPolicy $library.path $safe
            $result = Run-Sdk "pack-$variant" pack $library.path $safe @('-o', $destination)
            Require-Success $result "pack $variant"
            $packages = @(Get-ChildItem $destination -File -Filter '*.nupkg')
            if ($packages.Count -ne 1) { throw "Expected one NuGet package, found $($packages.Count)." }
            $inventory = Package-Inventory $packages[0].FullName
            if ($inventory.nuspec.id -ne 'RazorLibrary' -or $inventory.nuspec.version -ne '1.0.0') { throw 'Unexpected nuspec identity/version.' }
            $dll = $inventory.entries | Where-Object path -eq 'lib/net10.0/RazorLibrary.dll'
            if ($null -eq $dll -or $dll.sha256 -ne (Get-FileHash $library.outputs[0]).Hash) { throw 'Packed DLL differs from the compiled library.' }
            if (!@($inventory.entries | Where-Object path -eq 'staticwebassets/matrix.txt').Count) { throw 'The package is missing the RCL static asset.' }
            $variants += [pscustomobject]@{ variant = $variant; policy = $policy; inventory = $inventory }
        }
        Assert-Inventory $variants[0].inventory.entries $variants[1].inventory.entries 'NuGet normalized entries and contents'
        return [pscustomobject]@{
            payloadAndNuspecEqual = $true; zipByteParityClaimed = $false
            rawEntrySetsEqual = (($variants[0].inventory.rawEntries -join "`n") -ceq ($variants[1].inventory.rawEntries -join "`n"))
            variants = $variants
        }
    }

    Invoke-Case $rows[3] {
        $repository = Join-Path $OutputRoot 'git-repository'
        $projectDirectory = Join-Path $repository 'GitProbe'
        $project = Join-Path $projectDirectory 'GitProbe.csproj'
        $hooks = Join-Path $OutputRoot 'empty-git-hooks-and-template'
        New-Item -ItemType Directory -Path $hooks | Out-Null
        Write-Generated "$repository\global.json" (@{ sdk = @{ version = $matrix.sdkVersion; rollForward = 'disable'; allowPrerelease = $false } } | ConvertTo-Json)
        Write-Generated "$repository\Directory.Build.props" '<Project />'
        Write-Generated "$repository\Directory.Build.targets" '<Project />'
        Write-Generated "$repository\.gitignore" "**/bin/`n**/obj/`n"
        Write-Generated "$repository\README.txt" 'Local SourceLink protocol, commit A.'
        Write-Generated $project '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><Version>2.3.4</Version><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>'
        Write-Generated "$projectDirectory\Program.cs" 'using System.Reflection; Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);'
        function Local-Git([string[]]$Arguments) {
            $output = & git -C $repository -c "core.hooksPath=$hooks" -c "init.templateDir=$hooks" -c core.autocrlf=false -c commit.gpgsign=false -c 'user.name=Scenario Matrix Protocol' -c 'user.email=scenario-matrix@example.invalid' @Arguments
            if ($LASTEXITCODE -ne 0) { throw "Local Git command failed: $($Arguments -join ' ')" }
            return $output
        }
        Local-Git @('init', '--initial-branch=main') | Out-Null
        Local-Git @('remote', 'add', 'origin', 'https://github.com/copilot-scenario-fixtures/local-source-link-protocol.git') | Out-Null
        $trailer = 'Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>'
        $states = @()
        $programHash = (Get-FileHash "$projectDirectory\Program.cs").Hash
        Push-Location $repository
        try {
            foreach ($state in @('A', 'B')) {
                if ($state -eq 'B') { Write-Generated "$repository\README.txt" 'Local SourceLink protocol, commit B; Program.cs unchanged.' }
                Local-Git @('add', '--all') | Out-Null
                Local-Git @('commit', '-m', "Local SourceLink fixture $state", '-m', $trailer) | Out-Null
                $sha = (Local-Git @('rev-parse', 'HEAD') | Out-String).Trim()
                $variants = @()
                foreach ($safe in @($false, $true)) {
                    $variant = if ($safe) { 'safe' } else { 'stock' }
                    $destination = Join-Path $OutputRoot "git-products\$state\$variant"
                    # Property queries do not imply target execution, even under "dotnet build".
                    $extra = @('-o', $destination, '-t:Build', '-getProperty:SourceRevisionId,InformationalVersion,EnableSourceLink,SourceLink,NativeCoreBuild,EnableIncrementalTargetOptimizations')
                    $build = Run-Sdk "git-$state-$variant" build $project $safe $extra
                    if ($state -eq 'A' -and !$safe -and $build.exitCode -ne 0 -and $build.diagnostics -match 'NETSDK1004') {
                        Require-Success (Run-Sdk 'git-restore-after-missing-assets' restore $project $false) 'Git fixture restore'
                        $build = Run-Sdk "git-$state-$variant-after-restore" build $project $safe $extra
                    }
                    Require-Success $build "Git $state $variant"
                    $properties = ($build.output | ConvertFrom-Json).Properties
                    if ($properties.EnableSourceLink -ne 'true' -or $properties.NativeCoreBuild -eq 'true') { throw 'Git SourceLink/native Core defaults changed.' }
                    if ($properties.SourceRevisionId -cne $sha -or !$properties.InformationalVersion.Contains($sha)) { throw "Git revision/informational version did not update to $sha." }
                    $runtime = Run-Console (Join-Path $destination 'GitProbe.dll')
                    if ($runtime -cne $properties.InformationalVersion) { throw 'Compiled informational version differs from MSBuild property.' }
                    $pdbSourceLink = Read-PdbSourceLink (Join-Path $destination 'GitProbe.pdb')
                    $sourceLink = $pdbSourceLink | ConvertFrom-Json
                    $urls = @($sourceLink.documents.PSObject.Properties.Value)
                    if ($urls.Count -eq 0 -or @($urls | Where-Object { !$_.Contains("/$sha/") }).Count -ne 0) { throw 'PDB SourceLink does not encode the current commit.' }
                    $variants += [pscustomobject]@{
                        variant = $variant; output = $destination; revision = $properties.SourceRevisionId
                        informationalVersion = $runtime; sourceLink = $sourceLink
                    }
                }
                if ($variants[0].informationalVersion -cne $variants[1].informationalVersion -or
                    (ConvertTo-Json -Depth 10 -Compress $variants[0].sourceLink) -cne (ConvertTo-Json -Depth 10 -Compress $variants[1].sourceLink)) { throw "Stock/safe Git metadata differs at commit $state." }
                if ((Local-Git @('status', '--porcelain') | Out-String).Trim()) { throw 'Build dirtied tracked Git fixture inputs.' }
                $states += [pscustomobject]@{ state = $state; commit = $sha; variants = $variants }
            }
            if ($states[0].commit -eq $states[1].commit -or $states[0].variants[0].informationalVersion -eq $states[1].variants[0].informationalVersion) { throw 'The commit transition did not change informational version.' }
            if ((Get-FileHash "$projectDirectory\Program.cs").Hash -ne $programHash) { throw 'Program.cs unexpectedly changed between commits.' }
        }
        finally { Pop-Location }
        return [pscustomobject]@{
            repository = $repository; states = $states; programSourceUnchanged = $true
            embeddedPdbSourceLinkVerified = $true; networkAccess = 'none: no fetch/push'
        }
    }
}
finally {
    try {
        foreach ($path in $sourceHashes.Keys) {
            if (!(Test-Path $path) -or (Get-FileHash $path).Hash -ne $sourceHashes[$path]) {
                $report.fixtureSourcesUnchanged = $false
                Save-Report
                throw "Fixture source changed: $path"
            }
        }
        $report.fixtureSourcesUnchanged = $true
        Save-Report
    }
    finally {
        $env:MSBuildSDKsPath = $originalSdksPath
        Pop-Location
    }
}

$rows | Select-Object id, status, reason | Format-Table
Write-Host "Protocol results: $reportPath"
if (@($rows | Where-Object status -ne 'passed').Count) { throw 'Extra protocols have failures; exact reasons and logs were retained.' }
