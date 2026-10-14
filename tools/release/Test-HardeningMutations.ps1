<# Mutate temporary copies only, require detection for the stated reason, then rerun the untouched guards.
   Covers the REL.0C hardening guards:
     * release run provenance (G4) and the publishing hold (G6): each check is weakened in a copy and the
       suite must fail on the NAMED case;
     * the self-testing workflow guards (G8 pins, G2/G4/G6 wiring): each guard is weakened in a copy and
       its own -SelfTest must then report the surviving mutation. #>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('hardening-mutations-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
$repositoryRoot = Join-Path $PSScriptRoot '../..'

$provenance = @{ Guard = 'Assert-ReleaseRunProvenance.ps1'; Suite = 'Test-ReleaseRunProvenance.ps1'; Marker = 'Provenance cases failed:' }
$publish = @{ Guard = 'Assert-ManualPublishMode.ps1'; Suite = 'Test-ManualPublishMode.ps1'; Marker = 'Publish mode cases failed:' }
$mutations = @(
    $provenance + @{ Name = 'head sha binding removed'; Edits = @(, @('if ($headSha -cne $tagCommit)', 'if ($false)')); Case = 'head sha mismatch' }
    # The weakening shown in review: prefix comparison and no format check (empty/short head_sha accepted).
    $provenance + @{ Name = 'head sha prefix compare'; Edits = @(
        @("if (`$headSha -cnotmatch `$sha) { throw 'Provenance: the run head_sha is not a full lowercase commit sha.' }", ''),
        @('if ($headSha -cne $tagCommit)', 'if (-not $tagCommit.StartsWith($headSha))')); Case = 'head sha empty' }
    $provenance + @{ Name = 'workflow path check removed'; Edits = @(, @("if ((Get-Field `$run 'path') -cne `$WorkflowPath)", 'if ($false)')); Case = 'other workflow' }
    $provenance + @{ Name = 'completed check removed'; Edits = @(, @("if ((Get-Field `$run 'status') -cne 'completed')", 'if ($false)')); Case = 'completed status missing' }
    $provenance + @{ Name = 'push-only check removed'; Edits = @(, @("if ((Get-Field `$run 'event') -cne 'push')", 'if ($false)')); Case = 'dispatch run on the tag ref' }
    $provenance + @{ Name = 'tag push check removed'; Edits = @(, @("if ((Get-Field `$run 'head_branch') -cne `$Tag)", 'if ($false)')); Case = 'other tag push' }
    $provenance + @{ Name = 'conclusion check removed'; Edits = @(, @("if ((Get-Field `$run 'conclusion') -cne 'success')", 'if ($false)')); Case = 'run failed' }
    $provenance + @{ Name = 'fork check removed'; Edits = @(, @("if (`$headRepositoryName -cne `$Repository)", 'if ($false)')); Case = 'fork head repository' }
    $provenance + @{ Name = 'main containment removed'; Edits = @(, @("if ((`$status -cnotin @('identical', 'ahead')) -or -not `$behindIsZero)", 'if ($false)')); Case = 'tag not on main (diverged)' }
    $provenance + @{ Name = 'package/tag binding removed'; Edits = @(, @('if ($PackageVersion -cne $expectedPackage)', 'if ($false)')); Case = 'package version mismatch' }
    $provenance + @{ Name = 'annotated tag name check removed'; Edits = @(, @("if ((Get-Field `$tagObject 'tag') -cne `$Tag)", 'if ($false)')); Case = 'annotated object names another tag' }
    $provenance + @{ Name = 'tag regex end anchor \z -> $'; Edits = @(, @('(0|[1-9][0-9]{0,4})\z'') { throw ''Provenance: the tag', '(0|[1-9][0-9]{0,4})$'') { throw ''Provenance: the tag')); Case = 'tag with trailing newline' }
    $publish + @{ Name = 'publishing hold check removed'; Edits = @(, @("`$isManual = -not [string]::IsNullOrEmpty(`$PublishMode) -and [string]::Equals(`$PublishMode, 'Manual', [StringComparison]::OrdinalIgnoreCase)", '$isManual = $true')); Case = 'Immediate' }
    $publish + @{ Name = 'in-flight treated as idle'; Edits = @(, @('if ($idle -ccontains $Status)', 'if ($true)')); Case = 'Immediate' }
    $publish + @{ Name = 'idle status case-insensitive'; Edits = @(, @('if ($idle -ccontains $Status)', 'if ($idle -contains $Status)')); Case = 'Immediate with status in other case' }
)

# Guards that test themselves (-SelfTest): weaken the guard, its own self-test must catch it.
$selfTesting = @(
    @{ Name = 'pin guard accepts a tag with a release comment (review S50-11)'; Guard = 'Test-WorkflowActionPins.ps1'
       Old = '@[0-9a-f]{40}\s+#\s*v[0-9]+(?:'; New = '@(?:[0-9a-f]{40}|v[0-9][0-9.]*)\s+#\s*v[0-9]+(?:'; Marker = 'Pin mutation survived' }
    @{ Name = 'pin guard accepts uppercase hex'; Guard = 'Test-WorkflowActionPins.ps1'
       Old = '@[0-9a-f]{40}\s+#\s*v[0-9]+(?:'; New = '@[0-9a-fA-F]{40}\s+#\s*v[0-9]+(?:'; Marker = 'Synthetic pin case survived' }
    @{ Name = 'G2 condition matched loosely (review R50-14, || true)'; Guard = 'Test-ReleaseWorkflowShape.ps1'
       Old = '$conditions[0].Value -cnotmatch "\A    if:\s*\$\{\{\s*github\.event_name == ''workflow_dispatch'' && inputs\.create_release == true\s*\}\}\s*\z"'
       New = '$conditions[0].Value -notmatch "if:.*github\.event_name == ''workflow_dispatch''.*&&.*inputs\.create_release == true"'; Marker = 'SURVIVED: release-build.yml / tautology' }
    # REL.0E P-1: the stage-only and certification wiring.
    @{ Name = 'P-1 upload checked for --noCommit anywhere, not on the upload line'; Guard = 'Test-ReleaseWorkflowShape.ps1'
       Old = "if (`$uploads[0].Value -notmatch '\s--noCommit\s*`$')"; New = "if (`$all -notmatch '--noCommit')"; Marker = 'SURVIVED: store-submit.yml / upload without --noCommit' }
    @{ Name = 'P-1 certify proof accepted without the submission id'; Guard = 'Test-ReleaseWorkflowShape.ps1'
       Old = "`$staged = Get-StagedCall `$certify '-ExpectedSubmissionId `$env:SUBMISSION_ID ``'"; New = "`$staged = `$certify.IndexOf('./tools/release/Assert-StagedSubmission.ps1 ``')"; Marker = 'SURVIVED: store-certify.yml / staged draft not bound by id' }
    # PR #56 fix round 1.
    @{ Name = 'P-1 staged-call matcher ignores the arguments (file name, id)'; Guard = 'Test-ReleaseWorkflowShape.ps1'
       Old = "'(?s)\./tools/release/Assert-StagedSubmission\.ps1 ``(?:\s*\n\s+-[A-Za-z]+[^\n]*``)*?\s*\n\s+' + [regex]::Escape(`$Argument)"; New = "'(?s)\./tools/release/Assert-StagedSubmission\.ps1 ``'"; Marker = 'SURVIVED: store-submit.yml / read-back file name not bound to the tag commit' }
    @{ Name = 'P-1 in-flight call matched by substring (extra -AllowedStatuses accepted)'; Guard = 'Test-ReleaseWorkflowShape.ps1'
       Old = "-cnotmatch '\A\s+\./tools/release/Assert-NoPendingSubmission\.ps1 -SubmissionPath before\.json\s*\z'"; New = "-cnotmatch 'Assert-NoPendingSubmission\.ps1 -SubmissionPath before\.json'"; Marker = 'SURVIVED: store-submit.yml / W3 call site lets PendingCommit through' }
    @{ Name = 'P-1 SUBMISSION_ID reassignment not refused'; Guard = 'Test-ReleaseWorkflowShape.ps1'
       Old = "if (`$all -match '(?i)\`$env:SUBMISSION_ID\s*[-+*/]?=(?!=)|"; New = "if (`$false -and `$all -match '(?i)\`$env:SUBMISSION_ID\s*[-+*/]?=(?!=)|"; Marker = 'SURVIVED: store-certify.yml / W4 id reassigned from the draft' }
    @{ Name = 'P-1 any step may be conditional'; Guard = 'Test-ReleaseWorkflowShape.ps1'
       Old = "if (`$step -match '(?m)^        if:' -and"; New = "if (`$false -and"; Marker = 'SURVIVED: store-submit.yml / upload runs after a failed guard' }
)

function Assert-Suites {
    foreach ($suite in @($provenance.Suite, $publish.Suite)) { & (Join-Path $PSScriptRoot $suite) | Out-Null }
    foreach ($guard in @('Test-WorkflowActionPins.ps1', 'Test-ReleaseWorkflowShape.ps1')) { & (Join-Path $PSScriptRoot $guard) -SelfTest | Out-Null }
}

try {
    Assert-Suites
    foreach ($mutation in $mutations) {
        $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot $mutation.Guard) -Raw
        foreach ($edit in $mutation.Edits) {
            if (-not $source.Contains($edit[0])) { throw "Mutation target missing: $($mutation.Name)" }
            $source = $source.Replace($edit[0], $edit[1])
        }
        $copy = Join-Path $temp $mutation.Guard
        Set-Content -LiteralPath $copy -Value $source
        $detected = $false
        try { & (Join-Path $PSScriptRoot $mutation.Suite) -CheckScript $copy | Out-Null }
        catch {
            # A parser/setup error does not count as killing a mutation.
            if ($_.Exception.Message.Contains($mutation.Marker) -and $_.Exception.Message.Contains($mutation.Case)) { $detected = $true }
            else { throw }
        }
        if (-not $detected) { throw "SURVIVED: $($mutation.Name)" }
        Write-Host "MUTATION DETECTED: $($mutation.Name) [$($mutation.Case)]"
    }
    foreach ($mutation in $selfTesting) {
        $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot $mutation.Guard) -Raw
        if (-not $source.Contains($mutation.Old)) { throw "Mutation target missing: $($mutation.Name)" }
        $copy = Join-Path $temp $mutation.Guard
        Set-Content -LiteralPath $copy -Value $source.Replace($mutation.Old, $mutation.New)
        $detected = $false
        try { & $copy -SelfTest -RepositoryRoot $repositoryRoot | Out-Null }
        catch {
            if ($_.Exception.Message.Contains($mutation.Marker)) { $detected = $true }
            else { throw }
        }
        if (-not $detected) { throw "SURVIVED: $($mutation.Name)" }
        Write-Host "MUTATION DETECTED: $($mutation.Name) [$($mutation.Marker)]"
    }
    Assert-Suites
    Write-Host "All $($mutations.Count + $selfTesting.Count) hardening mutations detected; original guards passed again."
}
finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
# Explicit success. A native command or a stub inside the suite may have left $LASTEXITCODE non-zero,
# and the GitHub Actions pwsh wrapper ends a step with `exit $LASTEXITCODE` (PR #53 B-1).
exit 0
