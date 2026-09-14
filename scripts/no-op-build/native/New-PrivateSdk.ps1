param(
    [Parameter(Mandatory)][string]$Sdk,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$Sdk = (Resolve-Path $Sdk).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) { throw 'Choose a new private SDK directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
# SDK targets import siblings such as Roslyn, runtime graphs, and NuGet.Build.Tasks.Pack.
# Copy the complete SDK directory so the hotpatch cannot silently lose those imports.
Get-ChildItem $Sdk -Force | Copy-Item -Destination $OutputDirectory -Recurse

$targetPath = Join-Path $OutputDirectory 'Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.Sdk.FrameworkReferenceResolution.targets'
$document = [xml]::new()
$document.PreserveWhitespace = $true
$document.Load($targetPath)
$tasks = @($document.SelectNodes("//*[local-name()='Target' and @Name='ResolveFrameworkReferences']/*[local-name()='GetPackageDirectory']"))
if ($tasks.Count -ne 10) { throw "Expected the SDK 10.0.400 shape (10 pack-resolution tasks), found $($tasks.Count)." }
foreach ($task in $tasks)
{
    if ($task.HasAttribute('Condition')) { throw 'Review the existing condition before patching a different SDK version.' }
    $items = $task.GetAttribute('Items')
    $task.SetAttribute('Condition', "'`$(NativeSkipEmptyFrameworkPacks)' != 'true' or '$items' != ''")
}
$document.Save($targetPath)
[pscustomobject]@{
    MSBuildSDKsPath = Join-Path $OutputDirectory 'Sdks'
    NetCoreRoot = (Split-Path (Split-Path $Sdk)) + [IO.Path]::DirectorySeparatorChar
    Flag = '-p:NativeSkipEmptyFrameworkPacks=true'
    PatchedTasks = $tasks.Count
} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'configuration.json')
Get-Content (Join-Path $OutputDirectory 'configuration.json')
