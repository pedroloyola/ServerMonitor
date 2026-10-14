<# Fails closed on invalid MSIX versions and on a missing or invalid tracked floor. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [AllowEmptyString()] [string] $PackageVersion,
    [string] $FloorPath = (Join-Path $PSScriptRoot '../../release/min-package-version.txt')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-PackageVersion([string] $Value) {
    if ($Value -cnotmatch '\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z') {
        throw 'Package version must be exactly four canonical decimal components.'
    }
    $parsed = [version] $Value
    if ($parsed.Major -eq 0 -or @($parsed.Major, $parsed.Minor, $parsed.Build, $parsed.Revision).Where({ $_ -gt 65535 }).Count -gt 0) {
        throw 'Package version components must be within MSIX bounds (major 1..65535, others 0..65535).'
    }
    if ($parsed.Revision -ne 0) { throw 'Store package Revision must be zero.' }
    return $parsed
}

if (-not (Test-Path -LiteralPath $FloorPath -PathType Leaf)) { throw 'Tracked package version floor is missing.' }
# Only full-line comments and blank lines are ignored; exactly one version is required.
$lines = @(Get-Content -LiteralPath $FloorPath | Where-Object { $_ -notmatch '^\s*(#.*)?$' })
if ($lines.Count -ne 1) { throw 'Tracked package version floor must contain exactly one version.' }
$floor = ConvertTo-PackageVersion $lines[0]
$candidate = ConvertTo-PackageVersion $PackageVersion
if ($candidate -lt $floor) { throw "Package version $candidate is below the tracked floor $floor." }
Write-Host "PACKAGE VERSION FLOOR PASSED: $candidate >= $floor; Revision = 0"
