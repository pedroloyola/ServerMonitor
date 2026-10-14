[CmdletBinding()]
param([string] $CheckScript = (Join-Path $PSScriptRoot 'Assert-ManualPublishMode.ps1'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $CheckScript -PathType Leaf)) { throw 'Guard script missing.' }
$cases = @(
    @{ Name = 'Manual'; Value = 'Manual'; Pass = $true }
    @{ Name = 'manual (case)'; Value = 'manual'; Pass = $true }
    @{ Name = 'Immediate'; Value = 'Immediate'; Pass = $false }
    @{ Name = 'SpecificDate'; Value = 'SpecificDate'; Pass = $false }
    @{ Name = 'empty'; Value = ''; Pass = $false }
    @{ Name = 'null'; Value = $null; Pass = $false }
    @{ Name = 'trailing space'; Value = 'Manual '; Pass = $false }
    @{ Name = 'prefix'; Value = 'ManualPublish'; Pass = $false }
    @{ Name = 'newline'; Value = "Manual`n"; Pass = $false }
)
$failed = @()
foreach ($case in $cases) {
    $accepted = $true
    try { & $CheckScript -PublishMode $case.Value -Status 'InCertification' | Out-Null } catch { $accepted = $false }
    if ($accepted -ne $case.Pass) { $failed += $case.Name; Write-Host "FAIL $($case.Name)" }
    else { Write-Host "ok $($case.Name)" }
}
if ($failed.Count) { throw "Publish mode cases failed: $($failed -join ', ')" }
Write-Host "All $($cases.Count) publish mode cases passed."
