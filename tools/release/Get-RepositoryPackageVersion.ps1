<#
.SYNOPSIS
    Returns the four-part package version the repository declares, after proving every place that
    declares it agrees: csproj <FileVersion> and the Identity/@Version of both appxmanifests.

.DESCRIPTION
    Used by the CI package job (Vigil PR #51 L-1) to check the built MSIX with Assert-PackageIdentity.ps1
    against the version the repository says it is building, instead of a hard-coded value. On a tag,
    release-build.yml derives the same value from the tag and Assert-TagVersion.ps1 proves the same files
    agree with it; this is the tag-less counterpart. Fails closed on a missing file, a missing or
    malformed version, or any disagreement.
#>
[CmdletBinding()]
param([string] $RepositoryRoot = (Join-Path $PSScriptRoot '../..'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Xml([string] $Relative) {
    $path = Join-Path $RepositoryRoot $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Repository version: $Relative is missing." }
    try { return [xml] (Get-Content -LiteralPath $path -Raw) } catch { throw "Repository version: $Relative is not valid XML." }
}

$declared = [ordered] @{}
$csproj = Read-Xml 'src/ServerMonitor.App/ServerMonitor.App.csproj'
$nodes = @($csproj.SelectNodes("//*[local-name()='FileVersion']"))
if ($nodes.Count -ne 1) { throw 'Repository version: the csproj must declare exactly one <FileVersion>.' }
$declared['csproj FileVersion'] = $nodes[0].InnerText.Trim()
foreach ($relative in 'src/ServerMonitor.App/Package.appxmanifest', 'src/ServerMonitor.App/Package.Dev.appxmanifest') {
    $identity = @((Read-Xml $relative).SelectNodes("/*[local-name()='Package']/*[local-name()='Identity']"))
    if ($identity.Count -ne 1) { throw "Repository version: $relative must have exactly one <Identity>." }
    $declared["$relative Identity/@Version"] = $identity[0].GetAttribute('Version')
}

foreach ($key in $declared.Keys) {
    if ($declared[$key] -cnotmatch '\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z') {
        throw "Repository version: $key is not a canonical four-part version."
    }
}
$distinct = @($declared.Values | Sort-Object -Unique)
if ($distinct.Count -ne 1) {
    throw "Repository version: the declared package versions disagree ($(($declared.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))."
}
return [string] $distinct[0]
