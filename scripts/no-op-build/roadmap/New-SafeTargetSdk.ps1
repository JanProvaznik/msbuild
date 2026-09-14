param(
    [Parameter(Mandatory)][string]$VmrSource,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Sdk = 'C:\Program Files\dotnet\sdk\10.0.400'
)

$ErrorActionPreference = 'Stop'
$VmrSource = (Resolve-Path $VmrSource).Path
$Sdk = (Resolve-Path $Sdk).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) { throw 'Use a new SDK-copy directory.' }
$sha = git -C $VmrSource rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sha -ne '14fbf8d5271c98133561eb55185fdb05b286f578')
{
    throw 'Use the VMR snapshot matching SDK 10.0.400; review the patch before applying to a different revision.'
}
$mapping = @{
    'src\sdk\src\Tasks\Microsoft.NET.Build.Tasks\targets\Microsoft.NET.Sdk.FrameworkReferenceResolution.targets' =
        'Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.Sdk.FrameworkReferenceResolution.targets'
    'src\sdk\src\StaticWebAssetsSdk\Targets\Microsoft.NET.Sdk.StaticWebAssets.targets' =
        'Sdks\Microsoft.NET.Sdk.StaticWebAssets\targets\Microsoft.NET.Sdk.StaticWebAssets.targets'
}
foreach ($source in $mapping.Keys)
{
    if ([IO.File]::ReadAllText((Join-Path $VmrSource $source)) -notmatch 'EnableIncrementalTargetOptimizations')
    {
        throw 'Apply patches\sdk-safe-task-elision.patch to the VMR snapshot first.'
    }
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Get-ChildItem $Sdk -Force | Copy-Item -Destination $OutputDirectory -Recurse
foreach ($source in $mapping.Keys)
{
    Copy-Item (Join-Path $VmrSource $source) (Join-Path $OutputDirectory $mapping[$source])
}
@{
    sourceVmr = $sha
    hostSdk = $Sdk
    MSBuildSDKsPath = Join-Path $OutputDirectory 'Sdks'
    NetCoreRoot = (Split-Path (Split-Path $Sdk)) + [IO.Path]::DirectorySeparatorChar
    activation = '-p:EnableIncrementalTargetOptimizations=true'
    optOuts = @('EnableEmptyPackTaskElision=false', 'EnableStaticWebAssetsTaskElision=false')
} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'configuration.json')
Get-Content (Join-Path $OutputDirectory 'configuration.json')
