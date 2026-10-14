[CmdletBinding()]
param([string] $CheckScript = (Join-Path $PSScriptRoot 'Assert-ManualPublishMode.ps1'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $CheckScript -PathType Leaf)) { throw 'Guard script missing.' }
$cases = @(
    # In flight: the hold is enforced.
    @{ Name = 'Manual in certification'; Value = 'Manual'; Status = 'Certification'; Pass = $true }
    @{ Name = 'manual (case) in certification'; Value = 'manual'; Status = 'Certification'; Pass = $true }
    @{ Name = 'Immediate'; Value = 'Immediate'; Status = 'Certification'; Pass = $false }
    @{ Name = 'Immediate pending publication'; Value = 'Immediate'; Status = 'PendingPublication'; Pass = $false }
    @{ Name = 'Immediate pending commit'; Value = 'Immediate'; Status = 'PendingCommit'; Pass = $false }
    @{ Name = 'SpecificDate in release'; Value = 'SpecificDate'; Status = 'Release'; Pass = $false }
    @{ Name = 'empty in certification'; Value = ''; Status = 'Certification'; Pass = $false }
    @{ Name = 'null in certification'; Value = $null; Status = 'Certification'; Pass = $false }
    @{ Name = 'trailing space'; Value = 'Manual '; Status = 'Certification'; Pass = $false }
    @{ Name = 'prefix'; Value = 'ManualPublish'; Status = 'Certification'; Pass = $false }
    @{ Name = 'newline'; Value = "Manual`n"; Status = 'Certification'; Pass = $false }
    # Unknown status: fail closed.
    @{ Name = 'Immediate with empty status'; Value = 'Immediate'; Status = ''; Pass = $false }
    @{ Name = 'Immediate with unknown status'; Value = 'Immediate'; Status = 'SomethingNew'; Pass = $false }
    @{ Name = 'Immediate with status in other case'; Value = 'Immediate'; Status = 'published'; Pass = $false }
    @{ Name = 'Immediate with padded status'; Value = 'Immediate'; Status = 'Published '; Pass = $false }
    # Idle: reported, not failed (REL.0D "expect idle").
    @{ Name = 'Immediate published (idle)'; Value = 'Immediate'; Status = 'Published'; Pass = $true }
    @{ Name = 'no submission (idle)'; Value = ''; Status = 'None'; Pass = $true }
    @{ Name = 'Immediate canceled (idle)'; Value = 'Immediate'; Status = 'Canceled'; Pass = $true }
    @{ Name = 'Immediate certification failed (idle)'; Value = 'Immediate'; Status = 'CertificationFailed'; Pass = $true }
    @{ Name = 'Manual published'; Value = 'Manual'; Status = 'Published'; Pass = $true }
)
$failed = @()
foreach ($case in $cases) {
    $accepted = $true
    try { & $CheckScript -PublishMode $case.Value -Status $case.Status | Out-Null }
    catch {
        # Only the guard's own refusal counts; anything else is a broken test.
        if (-not $_.Exception.Message.StartsWith('targetPublishMode is')) { throw "Case '$($case.Name)' failed for a reason that is not the guard's: $($_.Exception.Message)" }
        $accepted = $false
    }
    if ($accepted -ne $case.Pass) { $failed += $case.Name; Write-Host "FAIL $($case.Name)" }
    else { Write-Host "ok $($case.Name)" }
}
if ($failed.Count) { throw "Publish mode cases failed: $($failed -join ', ')" }
Write-Host "All $($cases.Count) publish mode cases passed."
