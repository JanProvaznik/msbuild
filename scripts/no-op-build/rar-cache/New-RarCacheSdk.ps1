param(
    [Parameter(Mandatory)][string]$MSBuildSource,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Sdk = 'C:\Program Files\dotnet\sdk\10.0.400'
)

$ErrorActionPreference = 'Stop'
$MSBuildSource = (Resolve-Path $MSBuildSource).Path
$Sdk = (Resolve-Path $Sdk).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) { throw 'Choose a fresh private SDK directory.' }
$tasks = Join-Path $MSBuildSource 'artifacts\bin\Microsoft.Build.Tasks\Release\net10.0\Microsoft.Build.Tasks.Core.dll'
if (!(Test-Path $tasks)) { throw 'Build Microsoft.Build.Tasks.csproj for net10.0 / Release first.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$sdkDirectory = Join-Path $OutputDirectory ('sdk\' + (Split-Path $Sdk -Leaf))
New-Item -ItemType Directory -Path $sdkDirectory | Out-Null
Get-ChildItem $Sdk -Force | Copy-Item -Destination $sdkDirectory -Recurse
Copy-Item $tasks (Join-Path $sdkDirectory 'Microsoft.Build.Tasks.Core.dll') -Force
$symbols = [IO.Path]::ChangeExtension($tasks, '.pdb')
if (Test-Path $symbols) { Copy-Item $symbols $sdkDirectory -Force }

$common = Join-Path $sdkDirectory 'Microsoft.Common.CurrentVersion.targets'
$document = [xml]::new()
$document.PreserveWhitespace = $true
$document.Load($common)
$rar = $document.SelectSingleNode("//*[local-name()='Target' and @Name='ResolveAssemblyReferences']/*[local-name()='ResolveAssemblyReference']")
if ($null -eq $rar) { throw 'ResolveAssemblyReference invocation was not found.' }
$rar.SetAttribute('EnableResultsCache', '$(EnableRARResultsCache)')
$output = $document.CreateElement('Output', $document.DocumentElement.NamespaceURI)
$output.SetAttribute('TaskParameter', 'ResultsCacheHit')
$output.SetAttribute('PropertyName', '_RARResultsCacheHit')
$rar.AppendChild($output) | Out-Null
# The off/on control must not have IncrementalClean delete the experiment's cache between samples.
$group = $document.CreateElement('ItemGroup', $document.DocumentElement.NamespaceURI)
$write = $document.CreateElement('FileWrites', $document.DocumentElement.NamespaceURI)
$write.SetAttribute('Include', '$(ResolveAssemblyReferencesStateFile).results')
$write.SetAttribute('Condition', "'`$(ResolveAssemblyReferencesStateFile)' != '' and Exists('`$(ResolveAssemblyReferencesStateFile).results')")
$group.AppendChild($write) | Out-Null
$rar.ParentNode.AppendChild($group) | Out-Null
$document.Save($common)

@{
    sdk = $sdkDirectory
    source = $MSBuildSource
    hostSdk = $Sdk
    netCoreRoot = (Split-Path (Split-Path $Sdk)) + [IO.Path]::DirectorySeparatorChar
    tasksSha256 = (Get-FileHash $tasks).Hash
    activation = 'EnableRARResultsCache=true'
} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'rar-cache-configuration.json')
Get-Content (Join-Path $OutputDirectory 'rar-cache-configuration.json')
