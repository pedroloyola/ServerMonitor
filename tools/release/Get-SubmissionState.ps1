<#
.SYNOPSIS
    Maps a Partner Center submission status onto the small set of states a human actually
    wants reported, without ever claiming more than the API said.

.DESCRIPTION
    The submission API returns fifteen possible status values. Two distinctions matter and
    are easy to get wrong:

    * 'PendingPublication' is what a submission looks like after certification has PASSED
      while the publishing hold is in force. It is the certified-and-waiting state, not a
      failure and not a pending certification.

    * 'Published' does not mean the product is publicly discoverable. A product whose
      visibility is Private is published to its audience and to nobody else. This script
      therefore only reports PUBLIC when visibility actually says Public.

    Run with -SelfTest to check the table without contacting anything.

.PARAMETER Status
    The 'status' field of the submission resource.

.PARAMETER Visibility
    The 'visibility' field of the submission resource.
#>
[CmdletBinding(DefaultParameterSetName = 'Map')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Map')] [string] $Status,
    [Parameter(ParameterSetName = 'Map')] [string] $Visibility = '',
    [Parameter(ParameterSetName = 'Map')] [string] $StatusDetails = '',
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SubmissionState {
    param([string] $Status, [string] $Visibility)

    switch -Regex ($Status) {
        '^None$'               { return 'NO SUBMISSION' }
        '^Canceled$'           { return 'CANCELED' }
        '^PendingCommit$'      { return 'DRAFT (not submitted)' }
        '^CommitStarted$'      { return 'SUBMITTED' }
        '^PreProcessing$'      { return 'CERTIFICATION IN PROGRESS' }
        '^Certification$'      { return 'CERTIFICATION IN PROGRESS' }
        '^Release$'            { return 'CERTIFICATION IN PROGRESS' }
        '^PendingPublication$' { return 'CERTIFIED (awaiting manual Publish now)' }
        '^Publishing$'         { return 'PUBLISHING' }
        '^Published$'          {
            if ($Visibility -ieq 'Public') { return 'PUBLIC' }
            if ($Visibility) { return "PUBLISHED (audience: $Visibility, not public)" }
            return 'PUBLISHED (audience unknown)'
        }
        '^(CommitFailed|PreProcessingFailed|CertificationFailed|ReleaseFailed|PublishFailed)$' { return "FAILED ($Status)" }
        default                { return "UNKNOWN ($Status)" }
    }
}

if ($SelfTest) {
    $cases = @(
        @{ Status = 'None';               Visibility = '';        Expected = 'NO SUBMISSION' }
        @{ Status = 'PendingCommit';      Visibility = 'Private'; Expected = 'DRAFT (not submitted)' }
        @{ Status = 'CommitStarted';      Visibility = 'Private'; Expected = 'SUBMITTED' }
        @{ Status = 'PreProcessing';      Visibility = 'Private'; Expected = 'CERTIFICATION IN PROGRESS' }
        @{ Status = 'Certification';      Visibility = 'Private'; Expected = 'CERTIFICATION IN PROGRESS' }
        @{ Status = 'Release';            Visibility = 'Private'; Expected = 'CERTIFICATION IN PROGRESS' }
        @{ Status = 'PendingPublication'; Visibility = 'Private'; Expected = 'CERTIFIED (awaiting manual Publish now)' }
        @{ Status = 'Publishing';         Visibility = 'Private'; Expected = 'PUBLISHING' }
        @{ Status = 'Published';          Visibility = 'Private'; Expected = 'PUBLISHED (audience: Private, not public)' }
        @{ Status = 'Published';          Visibility = 'Public';  Expected = 'PUBLIC' }
        @{ Status = 'Published';          Visibility = '';        Expected = 'PUBLISHED (audience unknown)' }
        @{ Status = 'CommitFailed';       Visibility = 'Private'; Expected = 'FAILED (CommitFailed)' }
        @{ Status = 'PreProcessingFailed';Visibility = 'Private'; Expected = 'FAILED (PreProcessingFailed)' }
        @{ Status = 'CertificationFailed';Visibility = 'Private'; Expected = 'FAILED (CertificationFailed)' }
        @{ Status = 'ReleaseFailed';      Visibility = 'Private'; Expected = 'FAILED (ReleaseFailed)' }
        @{ Status = 'PublishFailed';      Visibility = 'Private'; Expected = 'FAILED (PublishFailed)' }
        @{ Status = 'Canceled';           Visibility = 'Private'; Expected = 'CANCELED' }
        @{ Status = 'SomethingNew';       Visibility = 'Private'; Expected = 'UNKNOWN (SomethingNew)' }
    )

    $failed = 0
    foreach ($case in $cases) {
        $actual = Get-SubmissionState -Status $case.Status -Visibility $case.Visibility
        $ok = ($actual -eq $case.Expected)
        if (-not $ok) { $failed++ }
        $mark = if ($ok) { 'ok  ' } else { 'FAIL' }
        Write-Host ("  {0}  {1,-20} {2,-8} -> {3}" -f $mark, $case.Status, $case.Visibility, $actual)
    }

    if ($failed -gt 0) { throw "$failed of $($cases.Count) status mappings are wrong." }
    Write-Host "All $($cases.Count) status mappings behaved as required."
    return
}

$state = Get-SubmissionState -Status $Status -Visibility $Visibility

Write-Host "Submission status : $Status"
if ($Visibility) { Write-Host "Visibility        : $Visibility" }
Write-Host "Reported state    : $state"
if ($StatusDetails) { Write-Host "Status details    : $StatusDetails" }

if ($env:GITHUB_OUTPUT) {
    "state=$state"   | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "status=$Status" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

if ($env:GITHUB_STEP_SUMMARY) {
    @(
        '## ServerAlyzer — Microsoft Store submission'
        ''
        "| field | value |"
        "| --- | --- |"
        "| reported state | **$state** |"
        "| raw status | ``$Status`` |"
        "| visibility | ``$(if ($Visibility) { $Visibility } else { 'unknown' })`` |"
    ) | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
