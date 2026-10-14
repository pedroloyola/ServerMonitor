<# Only a single, directly contained, safely named regular MSIX may cross the workflow boundary. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $DownloadDirectory,
    [string] $PackagePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Get-Item -LiteralPath $DownloadDirectory -Force
if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Download directory must be a regular directory.' }
$resolvedRoot = (Resolve-Path -LiteralPath $root.FullName).ProviderPath
$entries = @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force)
if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Artifact must not contain reparse points.' }
$packages = @($entries | Where-Object { -not $_.PSIsContainer -and $_.Extension -ieq '.msix' })
if ($packages.Count -ne 1) { throw 'Artifact must contain exactly one MSIX.' }
$package = $packages[0]
if ($package.Name -cnotmatch '\A[A-Za-z0-9._-]+\.msix\z') { throw 'Unsafe MSIX file name.' }
$resolved = (Resolve-Path -LiteralPath $package.FullName).ProviderPath
if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolved), $resolvedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'MSIX must be directly inside the download directory.'
}
if ($PSBoundParameters.ContainsKey('PackagePath')) {
    if ([string]::IsNullOrWhiteSpace($PackagePath) -or -not [IO.Path]::IsPathFullyQualified($PackagePath)) { throw 'Package path must be absolute.' }
    if (-not [string]::Equals($PackagePath, $resolved, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path does not match the validated artifact.' }
}
return $resolved
