<#
.SYNOPSIS
    store-certify, after the commit: waits (bounded) while Partner Center processes the committed submission,
    then proves that the package it ingested is the verified one.

.DESCRIPTION
    STORE-PIPE-1. Before the commit Partner Center has not filled in the version of the uploaded package: the
    draft holds { fileStatus = PendingUpload, fileName } and Assert-StagedSubmission.ps1 binds it by file name.
    Only after the commit (status CommitStarted, then PreProcessing) is the upload ingested and its version
    filled in. This is therefore the one place where waiting for Partner Center is correct; waiting before the
    commit would always time out.

    Each read is one 'msstore submission get' (through -ReadSubmission, so the self-test never calls the Store):
      * the submission must be the committed one (same numeric id); anything else fails;
      * while the status is CommitStarted or PreProcessing it waits -IntervalSeconds and reads again, at most
        -MaxReads reads in all, and fails on timeout;
      * the first status that is not one of those ends the wait. Certification or PendingPublication goes on;
        CommitFailed, PreProcessingFailed, any other failure, any other status (including PendingCommit: the
        commit did not take) and an unreadable read fail;
      * then a RE-READ, which must still be the same submission in Certification or PendingPublication, in
        which the package with -ExpectedPackageFileName is 'Uploaded' with version == -ExpectedPackageVersion,
        no other package of that name or of a newer or equal version is live, and every fileStatus and version
        is a single string (PR #56 Relay L-4).

    A failure leaves the submission as it is. Nothing can go live: the commit kept the publishing hold. Recovery
    is R4 (cancel the submission in Partner Center).

    Output: status, id and the packages' fileName / fileStatus / version only. The submission JSON also holds
    fileUploadUrl, a SAS URI with write access, and is never written to disk or printed by this script.

.PARAMETER ProductId
    The Store product id (msstore submission get <ProductId>).

.PARAMETER ReadSubmission
    Test seam: a scriptblock returning the raw text of one 'msstore submission get'. Not for the workflow.

.PARAMETER Sleep
    Test seam: a scriptblock taking the seconds to wait. Not for the workflow.
#>
[CmdletBinding(DefaultParameterSetName = 'Wait')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Wait')] [string] $ProductId,
    [Parameter(Mandatory, ParameterSetName = 'Wait')] [string] $ExpectedSubmissionId,
    [Parameter(Mandatory, ParameterSetName = 'Wait')] [string] $ExpectedPackageVersion,
    [Parameter(Mandatory, ParameterSetName = 'Wait')] [string] $ExpectedPackageFileName,
    [Parameter(ParameterSetName = 'Wait')] [ValidateRange(1, 90)] [int] $MaxReads = 30,
    [Parameter(ParameterSetName = 'Wait')] [ValidateRange(1, 120)] [int] $IntervalSeconds = 20,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$STATUS_IN_PROGRESS = @('CommitStarted', 'PreProcessing')
$STATUS_PROCESSED = @('Certification', 'PendingPublication')

function ConvertTo-LogSafe($Value) {
    <# STORE-PIPE-1 round 2 (Vigil L-1): a value read from Partner Center is printed only if it is plainly
       printable. A file name holding a newline followed by "::warning::..." would otherwise start a line
       that GitHub Actions runs as a workflow command. \A and \z, not ^ and $: $ also matches before a
       trailing newline. #>
    if ($null -eq $Value) { return '' }
    $text = [string] $Value
    if ($text -eq '') { return '' }
    if ($text -cmatch '\A[A-Za-z0-9._ -]{1,200}\z') { return $text }
    return '<unprintable>'
}

function Get-ScalarField($Object, [string] $Name) {
    <# A field that must be one JSON value: arrays and objects are refused (null), PR #56 Vigil N-3. #>
    if ($null -eq $Object -or $Object -isnot [pscustomobject]) { return $null }
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($null -eq $property) { return $null }
    if ($property.Value -is [string] -or $property.Value -is [ValueType]) { return $property.Value }
    return $null
}

function ConvertFrom-CliJson([string] $Raw) {
    if ([string]::IsNullOrWhiteSpace($Raw)) { throw 'the CLI returned nothing' }
    $start = $Raw.IndexOf('{'); $end = $Raw.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) { throw 'no JSON object in the CLI output' }
    try { return $Raw.Substring($start, $end - $start + 1) | ConvertFrom-Json }
    catch { throw 'the CLI output is not valid JSON' }
}

function Read-Committed([scriptblock] $Reader, [string] $ExpectedSubmissionId) {
    <# One read: the parsed submission and its status, or a failure reason. Never returns the raw text. #>
    $raw = try { [string] (& $Reader) } catch { return @{ Error = "the submission could not be read ($($_.Exception.Message))" } }
    try { $submission = ConvertFrom-CliJson $raw } catch { return @{ Error = $_.Exception.Message } }
    if ($submission -isnot [pscustomobject]) { return @{ Error = 'the CLI output is not a JSON object' } }
    $id = [string] (Get-ScalarField $submission 'id')
    if ($id -cnotmatch '\A[0-9]{1,30}\z') { return @{ Error = "the submission has no numeric id ('$(ConvertTo-LogSafe $id)')" } }
    if (-not [string]::Equals($id, $ExpectedSubmissionId, [StringComparison]::Ordinal)) {
        return @{ Error = "the product's current submission is $id, not the committed submission $ExpectedSubmissionId" }
    }
    $status = Get-ScalarField $submission 'status'
    if ($status -isnot [string] -or $status -eq '') { return @{ Error = 'the submission has no readable status' } }
    return @{ Submission = $submission; Status = $status }
}

function Test-ProcessedPackages($Submission, [string] $ExpectedPackageVersion, [string] $ExpectedPackageFileName) {
    <# Returns '' when the processed submission carries exactly the verified package, else the reason. #>
    $packagesProperty = $Submission.PSObject.Properties | Where-Object { $_.Name -ieq 'applicationPackages' } | Select-Object -First 1
    if ($null -eq $packagesProperty -or $null -eq $packagesProperty.Value) { return 'the submission has no applicationPackages' }
    if ($packagesProperty.Value -isnot [array]) { return 'applicationPackages is not a JSON array' }
    $expected = [version] $ExpectedPackageVersion
    $found = 0
    foreach ($package in $packagesProperty.Value) {
        if ($package -isnot [pscustomobject]) { return 'a package entry is not a JSON object' }
        foreach ($field in @('fileStatus', 'version', 'fileName')) {
            $property = $package.PSObject.Properties | Where-Object { $_.Name -ieq $field } | Select-Object -First 1
            if ($null -ne $property -and $null -ne $property.Value -and $property.Value -isnot [string]) { return "a package has an unreadable $field (not a single string)" }
        }
        $fileStatus = [string] (Get-ScalarField $package 'fileStatus')
        $fileName = [string] (Get-ScalarField $package 'fileName')
        $version = [string] (Get-ScalarField $package 'version')
        if ([string]::Equals($fileName, $ExpectedPackageFileName, [StringComparison]::Ordinal)) {
            $found++
            if (-not [string]::Equals($fileStatus, 'Uploaded', [StringComparison]::OrdinalIgnoreCase)) { return "the package $fileName is '$(ConvertTo-LogSafe $fileStatus)', not Uploaded, after processing" }
            if (-not [string]::Equals($version, $ExpectedPackageVersion, [StringComparison]::Ordinal)) { return "Partner Center read the package $fileName as version '$(ConvertTo-LogSafe $version)', not $ExpectedPackageVersion" }
            continue
        }
        if ([string]::Equals($fileStatus, 'PendingDelete', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z') { return "a live package ('$(ConvertTo-LogSafe $fileName)') has an unreadable version ('$(ConvertTo-LogSafe $version)')" }
        if (([version] $version) -ge $expected) { return "the submission carries a live package $version ('$(ConvertTo-LogSafe $fileName)') that is not older than $ExpectedPackageVersion; customers would get a package this run did not verify" }
    }
    if ($found -eq 0) { return "the submission has no package named $ExpectedPackageFileName after processing" }
    if ($found -gt 1) { return "the submission has more than one package named $ExpectedPackageFileName" }
    return ''
}

function Wait-CommittedSubmission {
    <# Returns a result object instead of throwing, so the self-test exercises the same decision. #>
    param([scriptblock] $Reader, [scriptblock] $Sleeper, [string] $ExpectedSubmissionId, [string] $ExpectedPackageVersion, [string] $ExpectedPackageFileName, [int] $MaxReads, [int] $IntervalSeconds)

    $fail = { param($reason, $reads) [pscustomobject] @{ Ok = $false; Reason = $reason; Reads = $reads; Status = '' } }
    if ($ExpectedSubmissionId -cnotmatch '\A[0-9]{1,30}\z') { return & $fail 'the expected submission id is not numeric' 0 }
    if ($ExpectedPackageVersion -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z') { return & $fail 'the expected package version is not a four-part version' 0 }
    if ($ExpectedPackageFileName -cnotmatch '\AServerAlyzer_[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+_x64_[0-9a-f]{7}\.msix\z') { return & $fail 'the expected package file name is not ServerAlyzer_<version>_x64_<sha7>.msix' 0 }

    $reads = 0
    $status = ''
    while ($true) {
        $reads++
        $read = Read-Committed $Reader $ExpectedSubmissionId
        if ($read.ContainsKey('Error')) { return & $fail $read.Error $reads }
        $status = $read.Status
        Write-Host "  read $reads/$MaxReads  submission $ExpectedSubmissionId status $(ConvertTo-LogSafe $status)"
        if ($STATUS_IN_PROGRESS -ccontains $status) {
            if ($reads -ge $MaxReads) { return & $fail "Partner Center was still processing the commit (status $(ConvertTo-LogSafe $status)) after $reads reads, $IntervalSeconds s apart" $reads }
            & $Sleeper $IntervalSeconds
            continue
        }
        break
    }
    if ($STATUS_PROCESSED -cnotcontains $status) {
        return & $fail "the committed submission ended in status '$(ConvertTo-LogSafe $status)', not Certification or PendingPublication" $reads
    }

    # The re-read: the state the package proof is made on.
    $reads++
    $read = Read-Committed $Reader $ExpectedSubmissionId
    if ($read.ContainsKey('Error')) { return & $fail $read.Error $reads }
    if ($STATUS_PROCESSED -cnotcontains $read.Status) { return & $fail "on the re-read the submission is in status '$(ConvertTo-LogSafe $read.Status)', not Certification or PendingPublication" $reads }
    # STORE-PIPE-1 round 2 (Vigil, optional): the commit must have kept the publishing hold.
    $mode = Get-ScalarField $read.Submission 'targetPublishMode'
    if (-not ($mode -is [string] -and [string]::Equals($mode, 'Manual', [StringComparison]::OrdinalIgnoreCase))) {
        return & $fail "after the commit targetPublishMode is '$(ConvertTo-LogSafe $mode)', not 'Manual': the publishing hold is not in force; cancel the submission in Partner Center (R4)" $reads
    }
    $reason = Test-ProcessedPackages $read.Submission $ExpectedPackageVersion $ExpectedPackageFileName
    if ($reason) { return & $fail $reason $reads }
    $packages = @(($read.Submission.PSObject.Properties | Where-Object { $_.Name -ieq 'applicationPackages' } | Select-Object -First 1).Value)
    foreach ($package in $packages) {
        $shown = foreach ($field in @('fileName', 'fileStatus', 'version')) { $value = Get-ScalarField $package $field; if ($null -eq $value -or "$value" -eq '') { '-' } else { ConvertTo-LogSafe $value } }
        Write-Host "    package: $($shown -join ' / ')"
    }
    return [pscustomobject] @{ Ok = $true; Reason = "submission $ExpectedSubmissionId is in $($read.Status) with $ExpectedPackageFileName Uploaded as $ExpectedPackageVersion"; Reads = $reads; Status = $read.Status }
}

if ($SelfTest) {
    $id = '1152921505701819999'
    $file = 'ServerAlyzer_1.1.8.0_x64_744297c.msix'
    function New-Read([string] $Status, [object[]] $Packages = $null, [string] $Id = $id, [object] $Mode = 'Manual') {
        if ($null -eq $Packages) {
            $Packages = @(
                [ordered] @{ fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; fileStatus = 'PendingDelete'; version = '1.1.7.0' }
                [ordered] @{ fileName = $file; fileStatus = 'PendingUpload' }
            )
        }
        $json = [ordered] @{ id = $Id; status = $Status; targetPublishMode = $Mode; fileUploadUrl = 'https://example.invalid/upload?sig=REDACTED'; applicationPackages = $Packages } | ConvertTo-Json -Depth 10
        return "Found Pending Submission.`n$json"
    }
    $processed = @(
        [ordered] @{ fileName = $file; fileStatus = 'Uploaded'; version = '1.1.8.0'; architecture = 'X64' }
    )
    $cases = @(
        @{ Name = 'CommitStarted -> PreProcessing -> Certification, version filled in'; Reads = @((New-Read 'CommitStarted'), (New-Read 'PreProcessing'), (New-Read 'Certification' $processed), (New-Read 'Certification' $processed)); Ok = $true; ExpectReads = 4 }
        @{ Name = 'already PendingPublication'; Reads = @((New-Read 'PendingPublication' $processed), (New-Read 'PendingPublication' $processed)); Ok = $true; ExpectReads = 2 }
        @{ Name = 'PascalCase package fields'; Reads = @((New-Read 'Certification' @([ordered] @{ FileName = $file; FileStatus = 'Uploaded'; Version = '1.1.8.0' })), (New-Read 'Certification' @([ordered] @{ FileName = $file; FileStatus = 'Uploaded'; Version = '1.1.8.0' }))); Ok = $true; ExpectReads = 2 }
        @{ Name = 'old package left PendingDelete'; Reads = @((New-Read 'Certification' @($processed[0], [ordered] @{ fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; fileStatus = 'PendingDelete'; version = '1.1.7.0' })), (New-Read 'Certification' @($processed[0], [ordered] @{ fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; fileStatus = 'PendingDelete'; version = '1.1.7.0' }))); Ok = $true; ExpectReads = 2 }
        @{ Name = 'ends in PreProcessingFailed'; Reads = @((New-Read 'CommitStarted'), (New-Read 'PreProcessingFailed')); Ok = $false; ExpectReads = 2 }
        @{ Name = 'ends in CommitFailed'; Reads = @((New-Read 'CommitFailed')); Ok = $false; ExpectReads = 1 }
        @{ Name = 'ends in CertificationFailed'; Reads = @((New-Read 'PreProcessing'), (New-Read 'CertificationFailed')); Ok = $false; ExpectReads = 2 }
        @{ Name = 'still PendingCommit (the commit did not take)'; Reads = @((New-Read 'PendingCommit')); Ok = $false; ExpectReads = 1 }
        @{ Name = 'unknown status'; Reads = @((New-Read 'SomethingNew')); Ok = $false; ExpectReads = 1 }
        @{ Name = 'status in other case'; Reads = @((New-Read 'certification' $processed), (New-Read 'certification' $processed)); Ok = $false; ExpectReads = 1 }
        @{ Name = 'never leaves PreProcessing (timeout)'; Reads = @(1..10 | ForEach-Object { New-Read 'PreProcessing' }); Ok = $false; ExpectReads = 5 }
        @{ Name = 'version mismatch after processing'; Reads = @((New-Read 'Certification' @([ordered] @{ fileName = $file; fileStatus = 'Uploaded'; version = '1.1.9.0' })), (New-Read 'Certification' @([ordered] @{ fileName = $file; fileStatus = 'Uploaded'; version = '1.1.9.0' }))); Ok = $false; ExpectReads = 2 }
        @{ Name = 'version still not filled in'; Reads = @((New-Read 'Certification'), (New-Read 'Certification')); Ok = $false; ExpectReads = 2 }
        @{ Name = 'package not Uploaded'; Reads = @((New-Read 'Certification' @([ordered] @{ fileName = $file; fileStatus = 'PendingUpload'; version = '1.1.8.0' })), (New-Read 'Certification' @([ordered] @{ fileName = $file; fileStatus = 'PendingUpload'; version = '1.1.8.0' }))); Ok = $false; ExpectReads = 2 }
        @{ Name = 'package of another commit'; Reads = @((New-Read 'Certification' @([ordered] @{ fileName = 'ServerAlyzer_1.1.8.0_x64_deadbee.msix'; fileStatus = 'Uploaded'; version = '1.1.8.0' })), (New-Read 'Certification' @([ordered] @{ fileName = 'ServerAlyzer_1.1.8.0_x64_deadbee.msix'; fileStatus = 'Uploaded'; version = '1.1.8.0' }))); Ok = $false; ExpectReads = 2 }
        @{ Name = 'newer live package'; Reads = @((New-Read 'Certification' @($processed[0], [ordered] @{ fileName = 'x.msix'; fileStatus = 'Uploaded'; version = '1.1.9.0' })), (New-Read 'Certification' @($processed[0], [ordered] @{ fileName = 'x.msix'; fileStatus = 'Uploaded'; version = '1.1.9.0' }))); Ok = $false; ExpectReads = 2 }
        @{ Name = 'second package of the same name'; Reads = @((New-Read 'Certification' @($processed[0], $processed[0])), (New-Read 'Certification' @($processed[0], $processed[0]))); Ok = $false; ExpectReads = 2 }
        @{ Name = 'fileStatus as an array (L-4)'; Reads = @((New-Read 'Certification' @([ordered] @{ fileName = $file; fileStatus = @('Uploaded'); version = '1.1.8.0' })), (New-Read 'Certification' @([ordered] @{ fileName = $file; fileStatus = @('Uploaded'); version = '1.1.8.0' }))); Ok = $false; ExpectReads = 2 }
        @{ Name = 'version as an array'; Reads = @((New-Read 'Certification' @([ordered] @{ fileName = $file; fileStatus = 'Uploaded'; version = @('1.1.8.0') })), (New-Read 'Certification' @([ordered] @{ fileName = $file; fileStatus = 'Uploaded'; version = @('1.1.8.0') }))); Ok = $false; ExpectReads = 2 }
        @{ Name = 're-read regressed to a failure'; Reads = @((New-Read 'Certification' $processed), (New-Read 'CertificationFailed' $processed)); Ok = $false; ExpectReads = 2 }
        @{ Name = 'another submission (id)'; Reads = @((New-Read 'Certification' $processed '1152921505701810000')); Ok = $false; ExpectReads = 1 }
        @{ Name = 'another submission on the re-read'; Reads = @((New-Read 'Certification' $processed), (New-Read 'Published' $processed '1152921505701810000')); Ok = $false; ExpectReads = 2 }
        @{ Name = 'status as a one-element array'; Reads = @(((New-Read 'Certification' $processed) -replace '"status":\s*"Certification"', '"status": ["Certification"]')); Ok = $false; ExpectReads = 1 }
        @{ Name = 'id as a JSON number'; Reads = @(((New-Read 'Certification' $processed) -replace '"id":\s*"1152921505701819999"', '"id": 1152921505701819999'), ((New-Read 'Certification' $processed) -replace '"id":\s*"1152921505701819999"', '"id": 1152921505701819999')); Ok = $true; ExpectReads = 2 }
        @{ Name = 'empty output'; Reads = @(''); Ok = $false; ExpectReads = 1 }
        @{ Name = 'not JSON'; Reads = @('error: could not reach the service'); Ok = $false; ExpectReads = 1 }
        @{ Name = 'the read throws'; Reads = @('THROW'); Ok = $false; ExpectReads = 1 }
        # STORE-PIPE-1 round 2: the publishing hold after the commit (Vigil, optional), and no injected workflow command (Vigil L-1).
        @{ Name = 'hold lost after the commit (Immediate)'; Reads = @((New-Read 'Certification' $processed), (New-Read 'Certification' $processed -Mode 'Immediate')); Ok = $false; ExpectReads = 2 }
        @{ Name = 'hold missing after the commit'; Reads = @((New-Read 'Certification' $processed), (New-Read 'Certification' $processed -Mode $null)); Ok = $false; ExpectReads = 2 }
        @{ Name = 'hold as a one-element array'; Reads = @((New-Read 'Certification' $processed), (New-Read 'Certification' $processed -Mode @('Manual'))); Ok = $false; ExpectReads = 2 }
        @{ Name = 'injection: old package name (passes)'; Reads = @((New-Read 'Certification' @($processed[0], [ordered] @{ fileName = "old.msix`n::warning::injected"; fileStatus = 'PendingDelete'; version = '1.1.7.0' })), (New-Read 'Certification' @($processed[0], [ordered] @{ fileName = "old.msix`n::warning::injected"; fileStatus = 'PendingDelete'; version = '1.1.7.0' }))); Ok = $true; ExpectReads = 2 }
        @{ Name = 'injection: status (refused)'; Reads = @((New-Read "Certification`n::warning::injected" $processed)); Ok = $false; ExpectReads = 1 }
        @{ Name = 'injection: processed fileStatus (refused)'; Reads = @((New-Read 'Certification' @([ordered] @{ fileName = $file; fileStatus = "Uploaded`n::warning::injected"; version = '1.1.8.0' })), (New-Read 'Certification' @([ordered] @{ fileName = $file; fileStatus = "Uploaded`n::warning::injected"; version = '1.1.8.0' }))); Ok = $false; ExpectReads = 2 }
        @{ Name = 'injection: live package name (refused)'; Reads = @((New-Read 'Certification' @($processed[0], [ordered] @{ fileName = "x.msix`n::warning::injected"; fileStatus = 'Uploaded'; version = '1.1.9.0' })), (New-Read 'Certification' @($processed[0], [ordered] @{ fileName = "x.msix`n::warning::injected"; fileStatus = 'Uploaded'; version = '1.1.9.0' }))); Ok = $false; ExpectReads = 2 }
    )
    $failedNames = @()
    foreach ($case in $cases) {
        $queue = [System.Collections.Generic.Queue[string]]::new()
        foreach ($text in $case.Reads) { $queue.Enqueue($text) }
        $slept = [System.Collections.Generic.List[int]]::new()
        $reader = { if ($queue.Count -eq 0) { throw 'read past the canned sequence' }; $next = $queue.Dequeue(); if ($next -ceq 'THROW') { throw 'msstore submission get failed' }; $next }.GetNewClosure()
        $sleeper = { param($seconds) $slept.Add($seconds) }.GetNewClosure()
        $printed = & { Wait-CommittedSubmission -Reader $reader -Sleeper $sleeper -ExpectedSubmissionId $id -ExpectedPackageVersion '1.1.8.0' -ExpectedPackageFileName $file -MaxReads 5 -IntervalSeconds 20 } 6>&1
        $result = @($printed | Where-Object { $_ -isnot [System.Management.Automation.InformationRecord] -and $_.PSObject.Properties['Ok'] })[0]
        # Everything the wait printed (Write-Host is the information stream): the SAS upload URL must never be in it.
        $text = (@($printed | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { [string] $_.MessageData }) -join "`n")
        $hostLines = @($text -split "`n" | Where-Object { $_ }).Count
        $ok = ($result.Ok -eq $case.Ok) -and ($result.Reads -eq $case.ExpectReads) -and ($slept.Count -le 4) -and (-not $result.Ok -or $hostLines -ge 2) -and -not $text.Contains('example.invalid') -and -not @(($text + "`n" + $result.Reason) -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('::') }).Count
        if (-not $ok) { $failedNames += $case.Name }
        Write-Host ("  {0}  {1,-64} ok={2,-5} reads={3}  {4}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $case.Name, $result.Ok, $result.Reads, $result.Reason)
    }
    if ($failedNames.Count -gt 0) { throw "Committed submission cases failed: $($failedNames -join '; ')" }
    Write-Host "All $($cases.Count) committed-submission cases behaved as required."
    exit 0  # explicit success for the Actions wrapper (PR #53 B-1)
}

if ($ProductId -cnotmatch '\A[0-9A-Z]{12}\z') { throw 'STOP: -ProductId is not a 12-character Store product id.' }
$productIdForRead = $ProductId
$reader = {
    $output = & msstore submission get $productIdForRead 2>$null | Out-String
    if ($LASTEXITCODE -ne 0) { throw "msstore submission get exited $LASTEXITCODE" }
    $output
}.GetNewClosure()
$sleeper = { param($seconds) Start-Sleep -Seconds $seconds }

Write-Host "Waiting for Partner Center to process submission $ExpectedSubmissionId (at most $MaxReads reads, $IntervalSeconds s apart)"
$result = Wait-CommittedSubmission -Reader $reader -Sleeper $sleeper -ExpectedSubmissionId $ExpectedSubmissionId -ExpectedPackageVersion $ExpectedPackageVersion -ExpectedPackageFileName $ExpectedPackageFileName -MaxReads $MaxReads -IntervalSeconds $IntervalSeconds
if (-not $result.Ok) {
    throw @"
STOP: $($result.Reason).

The submission was committed and is left as it is. Nothing can go live without a person: the commit kept the
publishing hold. Look at the submission in Partner Center (or run store-status); to stop it, cancel it there (R4).
"@
}
Write-Host "  ok    $($result.Reason)"
if ($env:GITHUB_STEP_SUMMARY) {
    @('', "Partner Center processed the commit: submission ``$ExpectedSubmissionId`` is in ``$($result.Status)`` with ``$ExpectedPackageFileName`` read as ``$ExpectedPackageVersion``.") | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
exit 0
