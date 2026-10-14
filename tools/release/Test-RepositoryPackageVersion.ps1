<#
.SYNOPSIS
    Counterproof for Get-RepositoryPackageVersion.ps1 on synthetic repository trees: the version is
    returned only when csproj and both manifests agree on one canonical four-part value, and every
    rejection carries its reason. Also runs once on this repository.
#>
[CmdletBinding()]
param([string] $Script = (Join-Path $PSScriptRoot 'Get-RepositoryPackageVersion.ps1'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Script -PathType Leaf)) { throw 'Script missing.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('repo-version-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $work | Out-Null
$results = New-Object System.Collections.Generic.List[object]

function New-Tree([string] $FileVersion = '1.1.7.0', [string] $Store = '1.1.7.0', [string] $Dev = '1.1.7.0', [switch] $NoDev, [switch] $TwoFileVersions) {
    $root = Join-Path $work ([guid]::NewGuid().ToString('n'))
    $app = Join-Path $root 'src/ServerMonitor.App'
    New-Item -ItemType Directory -Force -Path $app | Out-Null
    $extra = if ($TwoFileVersions) { "<FileVersion>9.9.9.0</FileVersion>" } else { '' }
    Set-Content -LiteralPath (Join-Path $app 'ServerMonitor.App.csproj') -Value "<Project><PropertyGroup><Version>1.1.7</Version><FileVersion>$FileVersion</FileVersion>$extra</PropertyGroup></Project>"
    $manifest = { param($v) "<?xml version=`"1.0`"?><Package xmlns=`"http://schemas.microsoft.com/appx/manifest/foundation/windows10`"><Identity Name=`"X`" Publisher=`"CN=X`" Version=`"$v`" /></Package>" }
    Set-Content -LiteralPath (Join-Path $app 'Package.appxmanifest') -Value (& $manifest $Store)
    if (-not $NoDev) { Set-Content -LiteralPath (Join-Path $app 'Package.Dev.appxmanifest') -Value (& $manifest $Dev) }
    return $root
}

function Invoke-Case([string] $Name, [string] $Root, [string] $Expected, [string] $Reason) {
    $value = $null; $failure = $null
    try { $value = & $Script -RepositoryRoot $Root } catch { $failure = $_.Exception.Message }
    $ok = if ($Reason) { $null -ne $failure -and $failure -match $Reason } else { $null -eq $failure -and $value -ceq $Expected }
    $results.Add([pscustomobject] @{ Case = $Name; Verdict = $(if ($ok) { 'OK' } else { 'TEST ERROR' }); Detail = "$value $failure" }) | Out-Null
}

try {
    Invoke-Case 'all agree' (New-Tree) '1.1.7.0'
    Invoke-Case 'store manifest differs' (New-Tree -Store '1.1.8.0') '' 'Repository version: the declared package versions disagree'
    Invoke-Case 'dev manifest differs' (New-Tree -Dev '1.1.6.0') '' 'disagree'
    Invoke-Case 'csproj differs' (New-Tree -FileVersion '1.1.6.0') '' 'disagree'
    Invoke-Case 'three parts' (New-Tree -FileVersion '1.1.7' -Store '1.1.7' -Dev '1.1.7') '' 'csproj FileVersion is not a canonical four-part version'
    Invoke-Case 'leading zero' (New-Tree -FileVersion '1.01.7.0') '' 'csproj FileVersion is not a canonical'
    Invoke-Case 'empty manifest version' (New-Tree -Store '') '' 'Package\.appxmanifest Identity/@Version is not a canonical'
    Invoke-Case 'dev manifest missing' (New-Tree -NoDev) '' 'Package\.Dev\.appxmanifest is missing'
    Invoke-Case 'two FileVersion elements' (New-Tree -TwoFileVersions) '' 'exactly one <FileVersion>'
    $repo = & $Script
    if ($repo -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z') { throw "This repository's version is malformed: $repo" }
    Write-Host "This repository declares package version $repo."
}
finally {
    $resolved = [IO.Path]::GetFullPath($work)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
$results | Format-Table -AutoSize Case, Verdict, Detail | Out-String -Width 200 | Write-Host
$broken = @($results | Where-Object Verdict -ne 'OK')
if ($broken.Count -gt 0) { throw "$($broken.Count) of $($results.Count) repository version case(s) did not behave as required." }
Write-Host "All $($results.Count) repository version cases behaved as required."
