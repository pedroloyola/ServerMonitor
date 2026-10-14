[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$guard = Join-Path $PSScriptRoot 'Get-ValidatedPackagePath.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('package-path-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
# Synthetic fixtures are retained; this fix round performs no deletions.
$cases = @('valid', 'empty', 'two packages', 'quote', 'semicolon', 'dollar', 'space', 'nested', 'outside path', 'relative path', 'empty path')
foreach ($case in $cases) {
    $dir = Join-Path $temp $case
    New-Item -ItemType Directory -Path $dir | Out-Null
    $name = switch ($case) { 'quote' { "x'.msix" } 'semicolon' { 'x;.msix' } 'dollar' { 'x$.msix' } 'space' { 'x y.msix' } default { 'valid.msix' } }
    $target = $dir
    if ($case -eq 'nested') { $target = Join-Path $dir 'nested'; New-Item -ItemType Directory -Path $target | Out-Null }
    if ($case -ne 'empty') { Set-Content -LiteralPath (Join-Path $target $name) -Value 'synthetic' }
    if ($case -eq 'two packages') { Set-Content -LiteralPath (Join-Path $dir 'second.msix') -Value 'synthetic' }
    $arguments = @{ DownloadDirectory=$dir }
    if ($case -eq 'outside path') { $arguments.PackagePath = Join-Path $temp 'outside.msix' }
    if ($case -eq 'relative path') { $arguments.PackagePath = 'valid.msix' }
    if ($case -eq 'empty path') { $arguments.PackagePath = '' }
    $accepted = $true
    try { $result = & $guard @arguments } catch { $accepted = $false }
    if ($accepted -ne ($case -eq 'valid')) { throw "Unexpected package path result: $case" }
    if ($case -eq 'valid') {
        $again = & $guard -DownloadDirectory $dir -PackagePath $result
        if ($again -ne $result) { throw 'Validated path changed.' }
    }
    Write-Host "PASS package path: $case"
}
Write-Host 'All 11 package path cases passed; fixtures retained in temporary storage.'
