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

    # PR #56 Relay L-5 (the N-3 rule): the status must be ONE JSON string. [string] of ["Published"] is "Published",
    # which would read a malformed answer as idle and let the upload (which deletes a draft) run.
    if ($null -ne $statusProperty.Value -and $statusProperty.Value -isnot [string]) {
        return [pscustomobject] @{ Idle = $false; Status = ''; Reason = 'the submission status is not a single string' }
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
    # REL.0E P-1 (Relay M-1): store-submit calls this guard WITHOUT -AllowedStatuses, so the cases run
    # through the parameter's DEFAULT, never through a list of the self-test's own. Widening the default
    # (for example to let a PendingCommit draft through, which msstore publish would then delete) fails here.
    $allowed = $AllowedStatuses
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
        @{ Name = 'status as a one-element array';     Raw = '{"status":["Published"]}';       Idle = $false }
        @{ Name = 'status as an object';               Raw = '{"status":{"v":"None"}}';        Idle = $false }
        @{ Name = 'status as a number';                Raw = '{"status":0}';                   Idle = $false }
    )

    $failedNames = @()
    # The default itself, exactly: only a finished or absent submission is idle.
    $defaultOk = (($allowed -join ',') -ceq 'None,Published,Canceled')
    if (-not $defaultOk) { $failedNames += 'default allowed statuses' }
    Write-Host ("  {0}  {1,-32} [{2}]" -f $(if ($defaultOk) { 'ok  ' } else { 'FAIL' }), 'default allowed statuses', ($allowed -join ', '))
    foreach ($case in $cases) {
        $result = Test-SubmissionIdle -Raw $case.Raw -AllowedStatuses $allowed
        $ok = ($result.Idle -eq $case.Idle)
        if (-not $ok) { $failedNames += $case.Name }
        $mark = if ($ok) { 'ok  ' } else { 'FAIL' }
        Write-Host ("  {0}  {1,-32} idle={2,-5}  {3}" -f $mark, $case.Name, $result.Idle, $result.Reason)
    }

    if ($failedNames.Count -gt 0) { throw "Pending-submission cases failed: $($failedNames -join '; ')" }
    Write-Host "All $($cases.Count + 1) pending-submission cases behaved as required (through the default allowed statuses)."
    exit 0  # explicit success for the Actions wrapper (PR #53 B-1)
}

if (-not (Test-Path -LiteralPath $SubmissionPath)) { throw "Submission output not found: $SubmissionPath" }
$raw = Get-Content -LiteralPath $SubmissionPath -Raw

$result = Test-SubmissionIdle -Raw $raw -AllowedStatuses $AllowedStatuses

if (-not $result.Idle) {
    # REL.0E P-1: a draft (PendingCommit) is most often one store-submit staged and a person is completing.
    $draftAdvice = if ($result.Status -ceq 'PendingCommit') {
        @"

A draft already exists. If store-submit staged it, do NOT stage again: send it to certification
with store-certify.yml (its submission id is in the store-submit run summary) once the listing is
reviewed. To start the staging over, a person deletes that draft in Partner Center first, after
exporting its listings if they hold work worth keeping.
"@
    } else { '' }
    throw @"
STOP: $($result.Reason).

This workflow will not touch a product that already has a submission in flight, because
'msstore publish' deletes the pending draft and rebuilds it from the last published
submission. Running now could discard work someone is doing in Partner Center.
$draftAdvice
Nothing has been changed. Let the current submission finish, or deal with it by hand in
Partner Center, then run this again.
"@
}

Write-Host "  ok    $($result.Reason)"
if ($env:GITHUB_OUTPUT) { "previousStatus=$($result.Status)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
