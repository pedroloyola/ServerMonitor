<#
.SYNOPSIS
    Fails when a submission that is still on its way to customers is not on the publishing hold (G6).

.DESCRIPTION
    Manual is the publishing hold: certification can pass, and nothing reaches customers until a
    person presses "Publish now" in Partner Center. Immediate (or SpecificDate) means the
    submission publishes itself. store-submit already refuses to commit unless the read-back says
    Manual; store-status used to only warn, so a mode changed in Partner Center while a submission
    sat in certification went unnoticed.

    The hold only matters while a submission can still be published. So:
      * IN FLIGHT (PendingCommit, CommitStarted, PreProcessing, Certification, Release,
        PendingPublication, Publishing): the mode must be Manual, or this fails.
      * IDLE (None, Published, Canceled, and the five *Failed states): nothing is waiting to be
        published, so a different mode on that last submission is reported, not failed. This is
        what lets store-status serve as the "expect idle" check before a new submission, whose own
        mode store-submit sets and verifies.
      * Any other status, including an empty one, is unknown and treated as IN FLIGHT (fail closed).

    The mode comparison is exact apart from letter case: no trimming, no prefix match, no default.
    The status comparison is exact and case-sensitive, as the API spells it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [AllowEmptyString()] [AllowNull()] [string] $PublishMode,
    [AllowEmptyString()] [string] $Status = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$idle = @('None', 'Published', 'Canceled', 'CommitFailed', 'PreProcessingFailed', 'CertificationFailed', 'ReleaseFailed', 'PublishFailed')
$isManual = -not [string]::IsNullOrEmpty($PublishMode) -and [string]::Equals($PublishMode, 'Manual', [StringComparison]::OrdinalIgnoreCase)
$shown = if ([string]::IsNullOrEmpty($PublishMode)) { '<empty>' } else { $PublishMode }

if ($isManual) {
    Write-Host 'PUBLISH MODE PASSED: targetPublishMode = Manual'
    return
}
if ($idle -ccontains $Status) {
    Write-Host "PUBLISH MODE NOT ENFORCED (idle, status '$Status'): the last submission's targetPublishMode is '$shown'. Nothing is waiting to be published; a new submission sets and verifies Manual itself."
    return
}
throw "targetPublishMode is '$shown', not 'Manual', while the submission is in flight (status '$Status'). It would publish itself once certification passes. Set the publishing hold in Partner Center, or confirm the mode is intended, before anything else."
