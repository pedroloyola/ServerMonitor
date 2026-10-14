<# REL.0E P-1 counterproofs (BOSS section 10) for the draft guards the Store workflows rely on: the staged-draft
   guard (store-submit read-back, store-certify last read before the commit) and the in-flight guard
   (store-submit refuses to re-stage over a draft, because msstore publish deletes it). Baseline: each
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
}
foreach ($name in $guards.Keys) { if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)) { throw "$name is missing." } }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('stage-certify-mutations-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $temp | Out-Null

$mutations = @(
    @{ Name = 'any status accepted (a published submission would be committed)'
       Old = "if (-not [string]::Equals(`$status, 'PendingCommit', [StringComparison]::Ordinal)) {"; New = 'if ($false) {'; Case = 'no draft: last published returned' }
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
       Old = "if ([string]::Equals([string] (Get-Field `$package 'fileStatus'), 'PendingDelete', [StringComparison]::OrdinalIgnoreCase)) { continue }"; New = ''; Case = 'expected package only PendingDelete' }
    @{ Name = 'expected package not required'
       Old = 'if ($versions -cnotcontains $ExpectedPackageVersion -or -not $namedPackage) {'; New = 'if ($false) {'; Case = 'expected package absent' }
    @{ Name = 'empty CLI output parsed as nothing to refuse'
       Old = "if ([string]::IsNullOrWhiteSpace(`$Raw)) { throw 'the CLI returned nothing' }"; New = "if ([string]::IsNullOrWhiteSpace(`$Raw)) { return [pscustomobject] @{ status = 'PendingCommit'; id = '1152921505701819999'; targetPublishMode = 'Manual'; visibility = 'Public'; applicationPackages = @([pscustomobject] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix' }) } }"; Case = 'empty output' }
    # PR #56 fix round 1 (Vigil L-1): the package must be the file uploaded from the tag commit.
    @{ Name = 'package file name not bound to the tag commit'
       Old = "if (-not [string]::Equals(`$fileName, `$ExpectedPackageFileName, [StringComparison]::Ordinal)) {"; New = 'if ($false) {'; Case = 'package of another commit (same version)' }
    @{ Name = 'package file name compared case-insensitively'
       Old = "[string]::Equals(`$fileName, `$ExpectedPackageFileName, [StringComparison]::Ordinal)"; New = "[string]::Equals(`$fileName, `$ExpectedPackageFileName, [StringComparison]::OrdinalIgnoreCase)"; Case = 'package file name in other case' }
    # PR #56 fix round 1 (Relay M-1, W5): the in-flight guard's DEFAULT, which store-submit relies on.
    @{ Name = 'W5 default allowed statuses widened to PendingCommit'; Guard = 'Assert-NoPendingSubmission.ps1'
       Old = "[string[]] `$AllowedStatuses = @('None', 'Published', 'Canceled')"; New = "[string[]] `$AllowedStatuses = @('None', 'Published', 'Canceled', 'PendingCommit')"; Case = 'draft a human is still editing' }
    @{ Name = 'default allowed statuses widened to CommitStarted'; Guard = 'Assert-NoPendingSubmission.ps1'
       Old = "[string[]] `$AllowedStatuses = @('None', 'Published', 'Canceled')"; New = "[string[]] `$AllowedStatuses = @('None', 'Published', 'Canceled', 'CommitStarted')"; Case = 'commit under way' }
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
