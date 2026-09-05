<#
.SYNOPSIS
    Refuses to start an automated submission while the product already has one in flight.

.DESCRIPTION
    This is the guard that matters most in the whole pipeline, because of a documented
    behaviour of the Store CLI:

        "If the app already has a published submission, msstore publish deletes the pending
         draft and creates a new one from the last published submission, discarding any
         metadata changes already staged in that draft."
        -- learn.microsoft.com/windows/apps/publish/msstore-dev-cli/commands

    In other words, running the pipeline while a human has work in progress in Partner
    Center destroys that work without asking. So the workflow calls this first, and stops
    unless the product is genuinely idle.

    It fails closed. Unparseable output, an unrecognised status, or an empty response are
    all treated as "something is there", never as "nothing is there".

.PARAMETER SubmissionPath
    Output of 'msstore submission get <productId>', saved to a file.

.PARAMETER AllowedStatuses
    Statuses that mean nothing is in flight. Only a fully finished or absent submission
    qualifies; anything mid-pipeline does not.
#>
[CmdletBinding(DefaultParameterSetName = 'Check')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Check')] [string] $SubmissionPath,
    [Parameter(ParameterSetName = 'Check')] [string[]] $AllowedStatuses = @('None', 'Published', 'Canceled'),
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-SubmissionIdle {
    <#
        Returns a result object rather than throwing, so the self-test can exercise the
        same decision the workflow uses.
    #>
    param([string] $Raw, [string[]] $AllowedStatuses)

    if ([string]::IsNullOrWhiteSpace($Raw)) {
        return [pscustomobject] @{ Idle = $false; Status = ''; Reason = 'the CLI returned nothing; cannot prove the product is idle' }
    }

    # The CLI prints human-readable lines around the JSON in some versions; take the JSON.
    $start = $Raw.IndexOf('{')
    $end = $Raw.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) {
        return [pscustomobject] @{ Idle = $false; Status = ''; Reason = 'no JSON object found in the CLI output' }
    }

    $json = $Raw.Substring($start, $end - $start + 1)
    try { $submission = $json | ConvertFrom-Json }
    catch { return [pscustomobject] @{ Idle = $false; Status = ''; Reason = "the CLI output could not be parsed as JSON: $($_.Exception.Message)" } }

    $statusProperty = $submission.PSObject.Properties | Where-Object { $_.Name -ieq 'status' } | Select-Object -First 1
    if ($null -eq $statusProperty) {
        return [pscustomobject] @{ Idle = $false; Status = ''; Reason = 'the submission JSON has no status field' }
    }

    $status = [string] $statusProperty.Value
    if ([string]::IsNullOrWhiteSpace($status)) {
        return [pscustomobject] @{ Idle = $false; Status = ''; Reason = 'the submission status is empty' }
    }

    if ($AllowedStatuses -contains $status) {
        return [pscustomobject] @{ Idle = $true; Status = $status; Reason = "status '$status' means nothing is in flight" }
    }

    return [pscustomobject] @{ Idle = $false; Status = $status; Reason = "a submission is in flight with status '$status'" }
}

if ($SelfTest) {
    $allowed = @('None', 'Published', 'Canceled')
    $cases = @(
        @{ Name = 'published, nothing pending';        Raw = '{"status":"Published"}';         Idle = $true }
        @{ Name = 'no submission at all';              Raw = '{"status":"None"}';              Idle = $true }
        @{ Name = 'canceled';                          Raw = '{"status":"Canceled"}';          Idle = $true }
        @{ Name = 'draft a human is still editing';    Raw = '{"status":"PendingCommit"}';     Idle = $false }
        @{ Name = 'commit under way';                  Raw = '{"status":"CommitStarted"}';     Idle = $false }
        @{ Name = 'pre-processing';                    Raw = '{"status":"PreProcessing"}';     Idle = $false }
        @{ Name = 'in certification (Submission 4)';   Raw = '{"status":"Certification"}';     Idle = $false }
        @{ Name = 'certified, awaiting Publish now';   Raw = '{"status":"PendingPublication"}';Idle = $false }
        @{ Name = 'publishing';                        Raw = '{"status":"Publishing"}';        Idle = $false }
        @{ Name = 'certification failed';              Raw = '{"status":"CertificationFailed"}';Idle = $false }
        @{ Name = 'PascalCase Status property';        Raw = '{"Status":"Certification"}';     Idle = $false }
        @{ Name = 'JSON wrapped in CLI chatter';       Raw = "Fetching...`n{`"status`":`"Published`"}`nDone."; Idle = $true }
        @{ Name = 'empty output';                      Raw = '';                               Idle = $false }
        @{ Name = 'whitespace only';                   Raw = "   `n  ";                        Idle = $false }
        @{ Name = 'not JSON at all';                   Raw = 'error: could not reach the service'; Idle = $false }
        @{ Name = 'malformed JSON';                    Raw = '{"status": ';                    Idle = $false }
        @{ Name = 'JSON without a status field';       Raw = '{"id":"123"}';                   Idle = $false }
        @{ Name = 'empty status value';                Raw = '{"status":""}';                  Idle = $false }
        @{ Name = 'unknown future status';             Raw = '{"status":"SomethingNew"}';      Idle = $false }
    )

    $failed = 0
    foreach ($case in $cases) {
        $result = Test-SubmissionIdle -Raw $case.Raw -AllowedStatuses $allowed
        $ok = ($result.Idle -eq $case.Idle)
        if (-not $ok) { $failed++ }
        $mark = if ($ok) { 'ok  ' } else { 'FAIL' }
        Write-Host ("  {0}  {1,-32} idle={2,-5}  {3}" -f $mark, $case.Name, $result.Idle, $result.Reason)
    }

    if ($failed -gt 0) { throw "$failed of $($cases.Count) pending-submission cases are wrong." }
    Write-Host "All $($cases.Count) pending-submission cases behaved as required."
    return
}

if (-not (Test-Path -LiteralPath $SubmissionPath)) { throw "Submission output not found: $SubmissionPath" }
$raw = Get-Content -LiteralPath $SubmissionPath -Raw

$result = Test-SubmissionIdle -Raw $raw -AllowedStatuses $AllowedStatuses

if (-not $result.Idle) {
    throw @"
STOP: $($result.Reason).

This workflow will not touch a product that already has a submission in flight, because
'msstore publish' deletes the pending draft and rebuilds it from the last published
submission. Running now could discard work someone is doing in Partner Center.

Nothing has been changed. Let the current submission finish, or deal with it by hand in
Partner Center, then run this again.
"@
}

Write-Host "  ok    $($result.Reason)"
if ($env:GITHUB_OUTPUT) { "previousStatus=$($result.Status)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
