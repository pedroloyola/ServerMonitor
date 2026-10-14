<#
.SYNOPSIS
    Fails unless the Store submission's targetPublishMode is Manual (G6).

.DESCRIPTION
    Manual is the publishing hold: certification can pass, and nothing reaches customers until a
    person presses "Publish now" in Partner Center. Immediate (or SpecificDate) means the
    submission publishes itself. store-submit already refuses to commit unless the read-back says
    Manual; store-status used to only warn, so a mode changed in Partner Center while a submission
    sat in certification went unnoticed. store-status now fails the run instead.

    The comparison is exact apart from letter case: no trimming, no prefix match, no default.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [AllowEmptyString()] [AllowNull()] [string] $PublishMode,
    [AllowEmptyString()] [string] $Status = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($PublishMode) -or -not [string]::Equals($PublishMode, 'Manual', [StringComparison]::OrdinalIgnoreCase)) {
    $shown = if ([string]::IsNullOrEmpty($PublishMode)) { '<empty>' } else { $PublishMode }
    throw "targetPublishMode is '$shown', not 'Manual' (submission status '$Status'). This submission would publish itself once certification passes. Set the publishing hold in Partner Center, or confirm the mode is intended, before anything else."
}
Write-Host 'PUBLISH MODE PASSED: targetPublishMode = Manual'
