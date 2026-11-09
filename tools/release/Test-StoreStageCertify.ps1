<# REL.0E P-1 counterproofs (BOSS section 10) for the draft guards the Store workflows rely on: the staged-draft
   guard (store-submit read-back, store-certify last read before the commit) and the in-flight guard
   (store-submit refuses to re-stage over a draft, because msstore publish deletes it), and the post-commit
   wait and package proof of store-certify (Wait-CommittedSubmission.ps1, STORE-PIPE-1). Baseline: each
   guard's own -SelfTest passes. Then each check is weakened in a temporary COPY, and the copy's -SelfTest
   must fail on the NAMED case; finally the untouched guards pass again. Synthetic JSON only; nothing talks
   to the Store. #>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$guards = @{
    'Assert-StagedSubmission.ps1'    = 'Staged submission cases failed:'
    'Assert-NoPendingSubmission.ps1' = 'Pending-submission cases failed:'
    'Wait-CommittedSubmission.ps1'   = 'Committed submission cases failed:'
}
foreach ($name in $guards.Keys) { if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)) { throw "$name is missing." } }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('stage-certify-mutations-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $temp | Out-Null

$mutations = @(
    @{ Name = 'any status accepted (a published submission would be committed)'
       Old = "if (-not (`$status -is [string] -and [string]::Equals(`$status, 'PendingCommit', [StringComparison]::Ordinal))) {"; New = 'if ($false) {'; Case = 'no draft: last published returned' }
    @{ Name = 'status compared case-insensitively'
       Old = "[string]::Equals(`$status, 'PendingCommit', [StringComparison]::Ordinal)"; New = "[string]::Equals(`$status, 'PendingCommit', [StringComparison]::OrdinalIgnoreCase)"; Case = 'status in other case' }
    @{ Name = 'staged submission id not bound'
       Old = 'if ($ExpectedSubmissionId -and -not [string]::Equals($id, $ExpectedSubmissionId, [StringComparison]::Ordinal)) {'; New = 'if ($false) {'; Case = 'another draft (recreated)' }
    @{ Name = 'submission id compared by prefix'
       Old = '-not [string]::Equals($id, $ExpectedSubmissionId, [StringComparison]::Ordinal)'; New = '-not $ExpectedSubmissionId.StartsWith($id)'; Case = 'id prefix of the staged id' }
    @{ Name = 'publishing hold not checked'
       Old = "if (-not (`$mode -is [string] -and [string]::Equals(`$mode, 'Manual', [StringComparison]::OrdinalIgnoreCase))) {"; New = 'if ($false) {'; Case = 'hold lost: Immediate' }
    @{ Name = 'hold matched loosely (-like Manual*)'
       Old = "[string]::Equals(`$mode, 'Manual', [StringComparison]::OrdinalIgnoreCase)"; New = "`$mode -like 'Manual*'"; Case = 'hold with trailing space' }
    @{ Name = 'visibility not checked'
       Old = 'if (-not ($visibility -is [string] -and [string]::Equals($visibility, $ExpectedVisibility, [StringComparison]::OrdinalIgnoreCase))) {'; New = 'if ($false) {'; Case = 'visibility changed' }
    @{ Name = 'newer live package tolerated'
       Old = 'if (([version] $text) -gt $expected) {'; New = 'if ($false) {'; Case = 'newer live package in the draft' }
    @{ Name = 'PendingDelete package counted as live'
       Old = "if ([string]::Equals(`$fileStatus, 'PendingDelete', [StringComparison]::OrdinalIgnoreCase)) { continue }"; New = ''; Case = 'newer package marked PendingDelete' }
    @{ Name = 'expected package not required'
       Old = 'if ($pendingUploads -eq 0) {'; New = 'if ($false) {'; Case = 'untouched clone: no pending upload' }
    @{ Name = 'empty CLI output parsed as nothing to refuse'
       Old = "if ([string]::IsNullOrWhiteSpace(`$Raw)) { throw 'the CLI returned nothing' }"; New = "if ([string]::IsNullOrWhiteSpace(`$Raw)) { return [pscustomobject] @{ status = 'PendingCommit'; id = '1152921505701819999'; targetPublishMode = 'Manual'; visibility = 'Public'; applicationPackages = @([pscustomobject] @{ fileName = 'ServerAlyzer_1.1.8.0_x64_744297c.msix'; fileStatus = 'PendingUpload' }) } }"; Case = 'empty output' }
    # PR #56 fix round 1 (Vigil L-1): the package must be the file uploaded from the tag commit.
    @{ Name = 'package file name not bound to the tag commit'
       Old = "if (-not (`$fileName -is [string] -and [string]::Equals(`$fileName, `$ExpectedPackageFileName, [StringComparison]::Ordinal))) {"; New = 'if ($false) {'; Case = 'upload of another commit (sha7)' }
    # PR #56 round 2 (Vigil N-3): the fields that must be one JSON string are read without pipeline unrolling.
    @{ Name = 'scalar fields read through the pipeline (one-element arrays unrolled)'
       Old = 'if ($property.Value -is [string] -or $property.Value -is [ValueType]) { return $property.Value }'; New = 'return $property.Value'; Case = 'package file name as a one-element array' }
    @{ Name = 'package file name read with Get-Field again'
       Old = "`$fileName = Get-ScalarField `$package 'fileName'"; New = "`$fileName = Get-Field `$package 'fileName'"; Case = 'package file name as a one-element array' }
    @{ Name = 'publishing hold read with Get-Field again'
       Old = "`$mode = Get-ScalarField `$submission 'targetPublishMode'"; New = "`$mode = Get-Field `$submission 'targetPublishMode'"; Case = 'hold as a one-element array' }
    @{ Name = 'package file name compared case-insensitively'
       Old = "[string]::Equals(`$fileName, `$ExpectedPackageFileName, [StringComparison]::Ordinal)"; New = "[string]::Equals(`$fileName, `$ExpectedPackageFileName, [StringComparison]::OrdinalIgnoreCase)"; Case = 'package file name in other case' }
    # PR #56 round 3 (Relay L-4, I-1, L-5): shapes that must make the draft or the product unreadable.
    @{ Name = 'non-string fileStatus read as not PendingDelete (Relay L-4)'
       Old = "if (`$null -ne `$fileStatusProperty -and `$null -ne `$fileStatusProperty.Value -and `$fileStatusProperty.Value -isnot [string]) {"; New = 'if ($false) {'; Case = 'package fileStatus as a one-element array' }
    @{ Name = 'applicationPackages accepted as a single object (Relay I-1)'
       Old = "if (`$packagesProperty.Value -isnot [array]) {"; New = 'if ($false) {'; Case = 'applicationPackages as a single object' }
    @{ Name = 'in-flight status read through [string] again (Relay L-5)'; Guard = 'Assert-NoPendingSubmission.ps1'
       Old = "if (`$null -ne `$statusProperty.Value -and `$statusProperty.Value -isnot [string]) {"; New = 'if ($false) {'; Case = 'status as a one-element array' }
    # PR #56 fix round 1 (Relay M-1, W5): the in-flight guard's DEFAULT, which store-submit relies on.
    @{ Name = 'W5 default allowed statuses widened to PendingCommit'; Guard = 'Assert-NoPendingSubmission.ps1'
       Old = "[string[]] `$AllowedStatuses = @('None', 'Published', 'Canceled')"; New = "[string[]] `$AllowedStatuses = @('None', 'Published', 'Canceled', 'PendingCommit')"; Case = 'draft a human is still editing' }
    @{ Name = 'default allowed statuses widened to CommitStarted'; Guard = 'Assert-NoPendingSubmission.ps1'
       Old = "[string[]] `$AllowedStatuses = @('None', 'Published', 'Canceled')"; New = "[string[]] `$AllowedStatuses = @('None', 'Published', 'Canceled', 'CommitStarted')"; Case = 'commit under way' }
    # STORE-PIPE-1 (SVA-9 STAGE run 42478961083): the real uncommitted-draft shape, where the upload has no version yet.
    @{ Name = 'old rule back: the upload must carry a version (the SVA-9 failure)'
       Old = "if ([string]::Equals(`$fileStatus, 'PendingUpload', [StringComparison]::OrdinalIgnoreCase)) {"; New = 'if ($false) {'; Case = 'real draft shape (no version on the upload)' }
    @{ Name = 'more than one pending upload tolerated'
       Old = 'if ($pendingUploads -gt 1) {'; New = 'if ($false) {'; Case = 'two pending uploads' }
    @{ Name = 'version on the pending upload not checked'
       Old = "if (`$text -ne '' -and -not [string]::Equals(`$text, `$ExpectedPackageVersion, [StringComparison]::Ordinal)) {"; New = 'if ($false) {'; Case = 'upload carries another version' }
    @{ Name = 'version that is not one string read as absent'
       Old = 'if ($null -ne $versionProperty -and $null -ne $versionProperty.Value -and $version -isnot [string]) {'; New = 'if ($false) {'; Case = 'upload version as a one-element array' }
    @{ Name = 'second live package of the expected version tolerated'
       Old = 'if (([version] $text) -eq $expected) {'; New = 'if ($false) {'; Case = 'second live package, same version' }
    # STORE-PIPE-1: store-certify after the commit (Wait-CommittedSubmission.ps1).
    @{ Name = 'commit in progress taken as the end state'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = 'if ($STATUS_IN_PROGRESS -ccontains $status) {'; New = 'if ($false) {'; Case = 'CommitStarted -> PreProcessing -> Certification, version filled in' }
    @{ Name = 'wait without a time limit'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = 'if ($reads -ge $MaxReads) {'; New = 'if ($false) {'; Case = 'never leaves PreProcessing (timeout)' }
    @{ Name = 'failed end state accepted'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = 'if ($STATUS_PROCESSED -cnotcontains $status) {'; New = 'if ($false) {'; Case = 'ends in PreProcessingFailed' }
    @{ Name = 're-read status not checked'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = 'if ($STATUS_PROCESSED -cnotcontains $read.Status) {'; New = 'if ($false) {'; Case = 're-read regressed to a failure' }
    @{ Name = 'committed submission id not bound'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = 'if (-not [string]::Equals($id, $ExpectedSubmissionId, [StringComparison]::Ordinal)) {'; New = 'if ($false) {'; Case = 'another submission (id)' }
    @{ Name = 'processed version not compared'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = 'if (-not [string]::Equals($version, $ExpectedPackageVersion, [StringComparison]::Ordinal)) {'; New = 'if ($false) {'; Case = 'version mismatch after processing' }
    @{ Name = 'processed package not required to be Uploaded'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = "if (-not [string]::Equals(`$fileStatus, 'Uploaded', [StringComparison]::OrdinalIgnoreCase)) {"; New = 'if ($false) {'; Case = 'package not Uploaded' }
    @{ Name = 'newer live package tolerated after processing'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = 'if (([version] $version) -ge $expected) {'; New = 'if ($false) {'; Case = 'newer live package' }
    # STORE-PIPE-1 round 2 (Vigil L-1, L-4 and the optional hold re-assert).
    @{ Name = 'Partner Center values printed raw (workflow command injection)'
       Old = "if (`$text -cmatch '\A[A-Za-z0-9._ -]{1,200}\z') { return `$text }"; New = 'return $text'; Case = 'main path: passing draft, SAS and injected names' }
    @{ Name = 'main path prints the submission JSON (SAS upload URL)'
       Old = "Write-Host '  packages in the draft (fileName / fileStatus / version):'"; New = "Write-Host `"  packages in the draft: `$raw`""; Case = 'main path: passing draft, SAS and injected names' }
    @{ Name = 'post-commit values printed raw (workflow command injection)'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = "if (`$text -cmatch '\A[A-Za-z0-9._ -]{1,200}\z') { return `$text }"; New = 'return $text'; Case = 'injection: status (refused)' }
    @{ Name = 'publishing hold not re-asserted after the commit'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = "if (-not (`$mode -is [string] -and [string]::Equals(`$mode, 'Manual', [StringComparison]::OrdinalIgnoreCase))) {"; New = 'if ($false) {'; Case = 'hold lost after the commit (Immediate)' }
    @{ Name = 'SAS upload URL printed (STORE-PIPE-1 hardening)'; Guard = 'Wait-CommittedSubmission.ps1'
       Old = 'Write-Host "    package: $($shown -join '' / '')"'; New = 'Write-Host "    package: $($shown -join '' / '') $($read.Submission.fileUploadUrl)"'; Case = 'CommitStarted -> PreProcessing -> Certification, version filled in' }
)

try {
    foreach ($name in $guards.Keys) { & (Join-Path $PSScriptRoot $name) -SelfTest 6> $null | Out-Null; Write-Host "BASELINE: $name -SelfTest passes." }
    foreach ($mutation in $mutations) {
        $name = if ($mutation.ContainsKey('Guard')) { $mutation.Guard } else { 'Assert-StagedSubmission.ps1' }
        $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Raw
        if (-not $source.Contains($mutation.Old)) { throw "Mutation target missing: $($mutation.Name)" }
        $copy = Join-Path $temp $name
        Set-Content -LiteralPath $copy -Value $source.Replace($mutation.Old, $mutation.New)
        $detected = $false
        try { & $copy -SelfTest 6> $null | Out-Null }
        catch {
            # Only a failure of the NAMED case counts; a parse error or another case does not kill the mutation.
            if ($_.Exception.Message.StartsWith($guards[$name]) -and $_.Exception.Message.Contains($mutation.Case)) { $detected = $true }
            else { throw "Mutation '$($mutation.Name)' failed for another reason: $($_.Exception.Message)" }
        }
        if (-not $detected) { throw "SURVIVED: $($mutation.Name)" }
        Write-Host "MUTATION DETECTED: $($mutation.Name) [$($mutation.Case)]"
    }
    foreach ($name in $guards.Keys) { & (Join-Path $PSScriptRoot $name) -SelfTest 6> $null | Out-Null }
    Write-Host "All $($mutations.Count) draft-guard mutations detected; the original guards passed again."
}
finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
# Explicit success. A native command or a stub inside the suite may have left $LASTEXITCODE non-zero,
# and the GitHub Actions pwsh wrapper ends a step with `exit $LASTEXITCODE` (PR #53 B-1).
exit 0
