<#
.SYNOPSIS
    Proves that a release tag, the commit it points at, and every version string in the
    tree agree with each other, before anything is built.

.DESCRIPTION
    A release is only reproducible if the tag is not a moving target. This script fails
    closed on every mismatch and prints what it compared, so a red run says which of the
    five sources disagreed rather than just "version mismatch".

    Sources compared:
      1. the tag name                        (vMAJOR.MINOR.PATCH)
      2. the commit the tag resolves to      (must equal the checked-out commit)
      3. ServerMonitor.App.csproj            Version / FileVersion / InformationalVersion
      4. Package.appxmanifest                Identity/@Version
      5. Package.Dev.appxmanifest            Identity/@Version

.NOTES
    AssemblyVersion is deliberately NOT compared: it is pinned at 1.0.0.0 so that binding
    redirects stay stable across releases. Changing that is a separate decision.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $ExpectedCommit,
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$failures = New-Object System.Collections.Generic.List[string]
function Add-Failure([string] $message) { $failures.Add($message) | Out-Null; Write-Host "  FAIL  $message" }
function Add-Pass([string] $message) { Write-Host "  ok    $message" }

Write-Host "Tag/version coherence check"
Write-Host "  repository root: $RepositoryRoot"

# ---------------------------------------------------------------- 1. tag shape
if ($Tag -notmatch '^v(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$') {
    throw "Tag '$Tag' is not of the form vMAJOR.MINOR.PATCH. Refusing to guess."
}
$version = $Tag.Substring(1)
$packageVersion = "$version.0"
Add-Pass "tag '$Tag' parses to product version $version, package version $packageVersion"

# ------------------------------------------------------- 2. tag -> commit identity
Push-Location $RepositoryRoot
try {
    $resolved = (& git rev-parse "$Tag^{commit}" 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Tag '$Tag' does not exist in this checkout: $resolved" }
    $resolved = "$resolved".Trim()
}
finally { Pop-Location }

if ($resolved -ne $ExpectedCommit) {
    Add-Failure "tag '$Tag' points at $resolved but the checked-out commit is $ExpectedCommit"
}
else {
    Add-Pass "tag '$Tag' resolves to the checked-out commit $ExpectedCommit"
}

# ------------------------------------------------------------- 3. project versions
$csprojPath = Join-Path $RepositoryRoot 'src/ServerMonitor.App/ServerMonitor.App.csproj'
if (-not (Test-Path $csprojPath)) { throw "Project file not found: $csprojPath" }
[xml] $csproj = Get-Content -LiteralPath $csprojPath -Raw

function Get-CsprojValue([string] $name) {
    $node = $csproj.SelectSingleNode("//*[local-name()='$name']")
    if ($null -eq $node) { return $null }
    return $node.InnerText.Trim()
}

$expectations = @{
    'Version'              = $version
    'FileVersion'          = $packageVersion
    'InformationalVersion' = $version
}
foreach ($name in $expectations.Keys | Sort-Object) {
    $actual = Get-CsprojValue $name
    if ($null -eq $actual) { Add-Failure "csproj has no <$name> element" }
    elseif ($actual -ne $expectations[$name]) { Add-Failure "csproj <$name> is '$actual', expected '$($expectations[$name])'" }
    else { Add-Pass "csproj <$name> = $actual" }
}

# ------------------------------------------------------------ 4/5. appx manifests
$manifests = @(
    'src/ServerMonitor.App/Package.appxmanifest',
    'src/ServerMonitor.App/Package.Dev.appxmanifest'
)
foreach ($relative in $manifests) {
    $path = Join-Path $RepositoryRoot $relative
    if (-not (Test-Path $path)) { Add-Failure "manifest not found: $relative"; continue }
    [xml] $manifest = Get-Content -LiteralPath $path -Raw
    $identity = $manifest.SelectSingleNode("//*[local-name()='Identity']")
    if ($null -eq $identity) { Add-Failure "$relative has no <Identity> element"; continue }
    $actual = $identity.GetAttribute('Version')
    if ($actual -ne $packageVersion) { Add-Failure "$relative Identity/@Version is '$actual', expected '$packageVersion'" }
    else { Add-Pass "$relative Identity/@Version = $actual" }
}

# ----------------------------------------------------------------------- verdict
if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "TAG/VERSION CHECK FAILED ($($failures.Count) mismatch(es))"
    throw ($failures -join '; ')
}

Write-Host ''
Write-Host "TAG/VERSION CHECK PASSED"

if ($env:GITHUB_OUTPUT) {
    "version=$version"               | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "packageVersion=$packageVersion" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
