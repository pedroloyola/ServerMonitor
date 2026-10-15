<# REL.0C hardening wiring, checked on the workflow text (no third-party YAML dependency):
   G2  release-build: a GitHub Release only from a manual dispatch with create_release=true (default
       false), in its own job that needs the build; the build job is read-only.
   G4  store-submit: a provenance job with no environment and no secret proves the run; the submit job
       needs it, binds the package name to the tag commit, and re-runs floor, identity, privacy and
       runtime gates (runtime fails closed when its script is missing) before the Store CLI is configured.
   G6  store-status: a publish mode other than Manual fails the run.
   P-1 store-submit only STAGES: confirmation STAGE, the in-flight refusal before the upload, every upload
       with --noCommit, the read-back proves an uncommitted draft, and nothing commits or deletes.
       store-certify is the separate certification gate: provenance (G4), the Store environment, a
       confirmation phrase bound to the version, the staged draft proven by id right before the single
       commit, and no upload/update/delete. No step of either job is conditional or allowed to fail
       open, and both share the store-submit concurrency group.
   Unsupported shapes fail closed. -SelfTest reintroduces each defect in memory and requires a failure. #>
[CmdletBinding()]
param([string] $RepositoryRoot = (Join-Path $PSScriptRoot '../..'), [switch] $SelfTest)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Jobs([string] $Text) {
    $jobs = [ordered]@{}
    $inJobs = $false
    $current = $null
    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -match '^jobs:\s*$') { $inJobs = $true; continue }
        if (-not $inJobs) { continue }
        if ($line -match '^\S') { break }
        if ($line -match '^  ([A-Za-z0-9_-]+):\s*$') { $current = $Matches[1]; $jobs[$current] = ''; continue }
        if ($null -ne $current) { $jobs[$current] += "$line`n" }
    }
    if ($jobs.Count -eq 0) { throw 'Shape: no jobs found.' }
    return $jobs
}

function Strip-Comments([string] $Text) { ($Text -split '\n' | Where-Object { $_ -notmatch '^\s*#' }) -join "`n" }

function Assert-ReleaseBuild([string] $Text) {
    if ($Text -notmatch '(?ms)^\s+create_release:\s*\n(?:\s+(?!default:)\S[^\n]*\n)*?\s+default:\s*false\s*$') { throw 'Shape G2: create_release must default to false.' }
    $jobs = Get-Jobs $Text
    $releaseJobs = @($jobs.Keys | Where-Object { (Strip-Comments $jobs[$_]) -match 'release.+create|gh @arguments' })
    if ($releaseJobs.Count -ne 1) { throw 'Shape G2: exactly one job may create the GitHub Release.' }
    $release = Strip-Comments $jobs[$releaseJobs[0]]
    if ($release -notmatch '(?m)^    needs:\s*build\s*$') { throw 'Shape G2: the release job must need the build job.' }
    # The whole condition, anchored: anything added around it ('|| true', another clause) is a different
    # condition and must be reviewed, not accepted because the expected words still appear somewhere.
    $conditions = @([regex]::Matches($release, '(?m)^    if:.*$'))
    if ($conditions.Count -ne 1 -or $conditions[0].Value -cnotmatch "\A    if:\s*\$\{\{\s*github\.event_name == 'workflow_dispatch' && inputs\.create_release == true\s*\}\}\s*\z") {
        throw 'Shape G2: the release job must run only on a dispatch with create_release == true (exact condition).'
    }
    foreach ($name in $jobs.Keys) {
        $body = Strip-Comments $jobs[$name]
        if ($name -ne $releaseJobs[0] -and $body -match 'contents:\s*write') { throw "Shape G2: job '$name' must not have contents: write." }
    }
}

function Assert-Provenance($Jobs, [string] $StoreJob) {
    if (-not $Jobs.Contains('provenance') -or -not $Jobs.Contains($StoreJob)) { throw "Shape G4: provenance and $StoreJob jobs are required." }
    $provenance = Strip-Comments $Jobs['provenance']
    if ($provenance -match '(?m)^    environment:') { throw 'Shape G4: the provenance job must not join an environment.' }
    if ($provenance -match 'secrets\.') { throw 'Shape G4: the provenance job must not read a secret.' }
    if ($provenance -notmatch 'Assert-ReleaseRunProvenance\.ps1') { throw 'Shape G4: the provenance job must run Assert-ReleaseRunProvenance.ps1.' }
    $store = Strip-Comments $Jobs[$StoreJob]
    if ($store -notmatch '(?m)^    needs:\s*provenance\s*$') { throw "Shape G4: the $StoreJob job must need the provenance job." }
    if ($store -notmatch '(?m)^    environment:\s*\n      name:\s*microsoft-store\s*$') { throw "Shape P-1: the $StoreJob job must run in the microsoft-store environment (required reviewer)." }
    # A conditional step or one allowed to fail would let a later Store call run past a failed guard.
    if ($store -match '(?m)^\s+continue-on-error:') { throw "Shape P-1: no step of the $StoreJob job may continue on error." }
    foreach ($step in ($store -split '(?m)^      - ')) {
        if ($step -match '(?m)^        if:' -and $step -notmatch '\Aname: Report the resulting state\s*\n') { throw "Shape P-1: only the report step of the $StoreJob job may carry a condition." }
    }
    return $store
}

function Assert-SharedConcurrency([string] $Text) {
    if ($Text -notmatch '(?m)^concurrency:\s*\n  group: store-submit\s*\n  cancel-in-progress: false\s*$') { throw 'Shape P-1: staging and certification must share the store-submit concurrency group.' }
}

# A Store CLI command rather than a mention of one: nothing quoted before it on its line (the throw
# messages name the commands too). Prefix for the patterns below; mentions still count as forbidden.
$command = '(?m)^[^''"\n]*?'

# Vigil P-1 L-1: the staged draft's package must be the file store-submit uploaded from the tag commit
# G4 proved, in BOTH reads of the draft (stage read-back, certify proof).
$tagBoundFileName = '-ExpectedPackageFileName "ServerAlyzer_${env:PACKAGE_VERSION}_x64_$($env:TAG_COMMIT.Substring(0, 7)).msix" `'

# Index of the Assert-StagedSubmission call whose continuation lines include $Argument, or -1.
function Get-StagedCall([string] $Text, [string] $Argument) {
    return Get-Index $Text ('(?s)\./tools/release/Assert-StagedSubmission\.ps1 `(?:\s*\n\s+-[A-Za-z]+[^\n]*`)*?\s*\n\s+' + [regex]::Escape($Argument))
}

function Get-Index([string] $Text, [string] $Pattern) {
    $match = [regex]::Match($Text, $Pattern)
    if (-not $match.Success) { return -1 }
    return $match.Index
}

function Assert-StoreSubmit([string] $Text) {
    $jobs = Get-Jobs $Text
    $submit = Assert-Provenance $jobs 'submit'
    Assert-SharedConcurrency $Text
    $configure = $submit.IndexOf('msstore reconfigure')
    if ($configure -lt 0) { throw 'Shape: the submit job no longer configures the Store CLI; review this guard.' }

    # P-1: stage only.
    $all = Strip-Comments $Text
    if ($all -match '\bmsstore\s+submission\s+(publish|delete|updateMetadata)\b') { throw 'Shape P-1: store-submit must never commit, delete or replace a submission; certification is store-certify.yml.' }
    $confirm = $submit.IndexOf("if (`$env:SUBMIT_CONFIRM -cne 'STAGE') {")
    if ($confirm -lt 0 -or $confirm -gt $configure) { throw 'Shape P-1: store-submit must require the phrase STAGE before the Store CLI is configured.' }
    # Vigil N-1: and in the provenance job, so a wrong phrase fails before the environment approval is requested.
    if ((Strip-Comments $jobs['provenance']) -notmatch "(?m)^\s+if \(\`$env:STAGE_CONFIRM -cne 'STAGE'\) \{ throw") { throw 'Shape P-1: the provenance job must refuse a phrase other than STAGE (before the approval).' }
    $uploads = @([regex]::Matches($all, "$command\bmsstore\s+publish\b[^\n]*$"))
    if ($uploads.Count -ne 1) { throw 'Shape P-1: store-submit must upload exactly once.' }
    if ($uploads[0].Value -notmatch '\s--noCommit\s*$') { throw 'Shape P-1: the upload must keep the submission in draft (--noCommit).' }
    $upload = Get-Index $submit "$command\bmsstore\s+publish\b"
    # Relay M-1: the exact call, with the guard's DEFAULT allowed statuses (its self-test pins that default).
    # An -AllowedStatuses here could let a PendingCommit draft through, and the upload would delete it.
    $pendingCalls = @([regex]::Matches($submit, '(?m)^.*Assert-NoPendingSubmission\.ps1.*$'))
    if ($pendingCalls.Count -ne 1 -or $pendingCalls[0].Value -cnotmatch '\A\s+\./tools/release/Assert-NoPendingSubmission\.ps1 -SubmissionPath before\.json\s*\z') {
        throw 'Shape P-1: the in-flight refusal must be exactly "./tools/release/Assert-NoPendingSubmission.ps1 -SubmissionPath before.json" (default allowed statuses).'
    }
    $pending = $pendingCalls[0].Index
    if ($pending -gt $upload) { throw 'Shape P-1: the in-flight refusal must run before the upload (msstore publish deletes a pending draft).' }
    $update = Get-Index $submit '\bmsstore\s+submission\s+update\b'
    $staged = $submit.IndexOf('./tools/release/Assert-StagedSubmission.ps1 `')
    if ($update -lt 0 -or $staged -lt $update) { throw 'Shape P-1: the read-back must prove an uncommitted draft after the metadata update.' }
    if ((Get-StagedCall $submit $tagBoundFileName) -lt 0) { throw 'Shape P-1: the read-back must bind the package file name to the tag commit.' }

    foreach ($marker in @('Assert-PackageVersionFloor.ps1', 'Assert-PackageIdentity.ps1', 'Assert-DistributionPrivacy.ps1', 'Assert-PackageRuntime.ps1', '$expectedName')) {
        $at = $submit.IndexOf($marker)
        if ($at -lt 0) { throw "Shape: the submit job must run $marker." }
        if ($at -gt $configure) { throw "Shape: $marker must run before the Store CLI is configured." }
    }
    if ($submit -notmatch "(?s)if \(-not \(Test-Path -LiteralPath \`$runtimeGate -PathType Leaf\)\)\s*\{\s*throw") { throw 'Shape: a missing runtime gate must fail the submission, never skip it.' }
}

function Assert-StoreCertify([string] $Text) {
    $jobs = Get-Jobs $Text
    $certify = Assert-Provenance $jobs 'certify'
    Assert-SharedConcurrency $Text
    $all = Strip-Comments $Text
    if ($all -match '\bmsstore\s+(publish|submission\s+(update|updateMetadata|delete|rollout))\b') { throw 'Shape P-1: store-certify must not upload, update, delete or roll out; it only commits the staged draft.' }
    $configure = $certify.IndexOf('msstore reconfigure')
    if ($configure -lt 0) { throw 'Shape: the certify job no longer configures the Store CLI; review this guard.' }
    $confirm = $certify.IndexOf('if ($env:CERTIFY_CONFIRM -cne "CERTIFY $env:PACKAGE_VERSION") {')
    if ($confirm -lt 0 -or $confirm -gt $configure) { throw 'Shape P-1: store-certify must require the phrase "CERTIFY <version>" before the Store CLI is configured.' }
    $commits = @([regex]::Matches($all, "$command\bmsstore\s+submission\s+publish\b"))
    if ($commits.Count -ne 1) { throw 'Shape P-1: store-certify must commit exactly once.' }
    $commit = Get-Index $certify "$command\bmsstore\s+submission\s+publish\b"
    $staged = Get-StagedCall $certify '-ExpectedSubmissionId $env:SUBMISSION_ID `'
    if ($staged -lt 0 -or $staged -lt $configure -or $staged -gt $commit) { throw 'Shape P-1: the staged draft must be proven by its submission id right before the commit.' }
    $named = Get-StagedCall $certify $tagBoundFileName
    if ($named -ne $staged) { throw 'Shape P-1: the certify proof must bind the package file name to the tag commit (L-1).' }

    # Relay W4: the id binding comes only from the dispatch input. SUBMISSION_ID is declared once per job,
    # as the input, and nothing reassigns it (no assignment, no GITHUB_ENV, no environment API).
    # PR #56 round 2 (Relay L-3, W11): no Env: provider path at all, other than the $env:NAME / ${env:NAME} reads.
    # Every provider write (Set-Item and its alias si, New-Item/ni, Set-Content/sc, Copy-Item, Remove-Item, ...)
    # has to name an Env: path, so the rule closes the class instead of listing cmdlets and aliases. Only a line
    # that is exactly "<indent>env:" (a YAML key) is exempt: an Env: at the end of a command ("cd env:",
    # "Push-Location Env:") followed by a relative "si SUBMISSION_ID" is refused (PR #56 Vigil L-4).
    foreach ($name in @('provenance', 'certify')) {
        $declared = @([regex]::Matches((Strip-Comments $jobs[$name]), '(?m)^\s+SUBMISSION_ID:[^\n]*$'))
        if ($declared.Count -ne 1 -or $declared[0].Value -cnotmatch '\A\s+SUBMISSION_ID: \$\{\{ inputs\.submission_id \}\}\s*\z') { throw "Shape P-1: the $name job must take SUBMISSION_ID once, from inputs.submission_id." }
    }
    if ($all -match '(?i)\$env:SUBMISSION_ID\s*[-+*/]?=(?!=)|\$\{env:SUBMISSION_ID\}\s*=(?!=)|GITHUB_ENV|SetEnvironmentVariable|(?m)^(?![^\S\n]*env:[^\S\n]*$)[^\n]*(?<![$\{\w])env:') {
        throw 'Shape P-1: store-certify must never reassign SUBMISSION_ID or write the job environment.'
    }

    # Vigil N-1: phrase and id refused in the provenance job too, before the environment approval is requested.
    $provenance = Strip-Comments $jobs['provenance']
    if ($provenance -notmatch '(?m)^\s+if \(\$env:SUBMISSION_ID -cnotmatch ''\\A\[0-9\]\{1,30\}\\z''\) \{ throw') { throw 'Shape P-1: the provenance job must refuse a malformed submission_id (before the approval).' }
    if ($provenance -notmatch '(?m)^\s+if \(\$env:CERTIFY_CONFIRM -cne "CERTIFY \$env:PACKAGE_VERSION"\) \{ throw') { throw 'Shape P-1: the provenance job must refuse a phrase other than "CERTIFY <version>" (before the approval).' }
}

function Assert-StoreStatus([string] $Text) {
    $body = Strip-Comments $Text
    if ($body -notmatch 'Assert-ManualPublishMode\.ps1 -PublishMode \$publishMode') { throw 'Shape G6: store-status must check the publish mode.' }
    if ($body -notmatch 'if \(\$publishModeError\) \{ throw') { throw 'Shape G6: a submission in flight off the publishing hold must fail the run.' }
}

$checks = [ordered]@{
    'release-build.yml' = ${function:Assert-ReleaseBuild}
    'store-submit.yml'  = ${function:Assert-StoreSubmit}
    'store-certify.yml' = ${function:Assert-StoreCertify}
    'store-status.yml'  = ${function:Assert-StoreStatus}
}
$mutations = @{
    'release-build.yml' = @(
        @{ Old = "default: false`n        type: boolean`n      attach_package"; New = "default: true`n        type: boolean`n      attach_package"; Name = 'create_release defaults to true' }
        @{ Old = "if: `${{ github.event_name == 'workflow_dispatch' && inputs.create_release == true }}"; New = "if: `${{ inputs.create_release != false }}"; Name = 'release on tag push' }
        @{ Old = "inputs.create_release == true }}"; New = "inputs.create_release == true || true }}"; Name = 'tautology appended (|| true)' }
        @{ Old = "if: `${{ github.event_name"; New = "if: `${{ always() || github.event_name"; Name = 'tautology prepended (always() ||)' }
        @{ Old = "if: `${{ github.event_name == 'workflow_dispatch' && inputs.create_release == true }}"; New = "if: `${{ github.event_name == 'workflow_dispatch' && inputs.create_release == true }}`n    if: `${{ true }}"; Name = 'second condition line' }
        @{ Old = "contents: read         # building"; New = "contents: write        # building"; Name = 'build job writable' }
        @{ Old = "    needs: build`n"; New = ''; Name = 'release without the build' }
    )
    'store-submit.yml' = @(
        @{ Old = "  submit:`n    needs: provenance`n"; New = "  submit:`n"; Name = 'submit without provenance' }
        @{ Old = "      GH_TOKEN: `${{ github.token }}`n    outputs:"; New = "      GH_TOKEN: `${{ github.token }}`n    environment: microsoft-store`n    outputs:"; Name = 'provenance in the Store environment' }
        @{ Old = "throw 'STOP: tools/release/Assert-PackageRuntime.ps1 is missing"; New = "Write-Warning 'tools/release/Assert-PackageRuntime.ps1 is missing"; Name = 'missing runtime gate skipped' }
        @{ Old = 'Assert-ReleaseRunProvenance.ps1 `'; New = 'Write-Host `'; Name = 'provenance check removed' }
        @{ Old = "      - name: Report the resulting state"; New = "      - name: Commit`n        shell: pwsh`n        run: msstore submission publish `${{ env.STORE_PRODUCT_ID }}`n`n      - name: Report the resulting state"; Name = 'commit step re-added (P-1)' }
        @{ Old = "-id `${{ env.STORE_PRODUCT_ID }} --noCommit"; New = "-id `${{ env.STORE_PRODUCT_ID }}"; Name = 'upload without --noCommit' }
        @{ Old = "if (`$env:SUBMIT_CONFIRM -cne 'STAGE') {"; New = "if (`$env:SUBMIT_CONFIRM -cne 'SUBMIT') {"; Name = 'old SUBMIT phrase' }
        @{ Old = './tools/release/Assert-NoPendingSubmission.ps1 -SubmissionPath before.json'; New = "Write-Host 'skipped'"; Name = 'in-flight refusal removed' }
        @{ Old = "      - name: Refuse to run if a submission is already in flight`n        shell: pwsh"; New = "      - name: Refuse to run if a submission is already in flight`n        continue-on-error: true`n        shell: pwsh"; Name = 'in-flight refusal allowed to fail' }
        @{ Old = "      - name: Upload the package, leaving the submission in draft`n"; New = "      - name: Upload the package, leaving the submission in draft`n        if: always()`n"; Name = 'upload runs after a failed guard' }
        @{ Old = './tools/release/Assert-StagedSubmission.ps1 `'; New = 'Write-Host `'; Name = 'read-back draft proof removed' }
        @{ Old = "  group: store-submit`n"; New = "  group: store-submit-`${{ github.run_id }}`n"; Name = 'concurrency group not shared' }
        @{ Old = "    environment:`n      name: microsoft-store`n"; New = ''; Name = 'submit outside the Store environment' }
        # PR #56 fix round 1.
        @{ Old = './tools/release/Assert-NoPendingSubmission.ps1 -SubmissionPath before.json'; New = './tools/release/Assert-NoPendingSubmission.ps1 -SubmissionPath before.json -AllowedStatuses None,Published,Canceled,PendingCommit'; Name = 'W3 call site lets PendingCommit through (Relay M-1)' }
        @{ Old = "          if (`$env:STAGE_CONFIRM -cne 'STAGE') { throw"; New = "          if (`$false) { throw"; Name = 'phrase not checked before the approval (Vigil N-1)' }
        @{ Old = '$($env:TAG_COMMIT.Substring(0, 7)).msix" `'; New = '0000000.msix" `'; Name = 'read-back file name not bound to the tag commit (Vigil L-1)' }
    )
    'store-certify.yml' = @(
        @{ Old = "  certify:`n    needs: provenance`n"; New = "  certify:`n"; Name = 'certify without provenance' }
        @{ Old = 'Assert-ReleaseRunProvenance.ps1 `'; New = 'Write-Host `'; Name = 'provenance check removed' }
        @{ Old = "    environment:`n      name: microsoft-store`n"; New = ''; Name = 'certify outside the Store environment' }
        @{ Old = 'if ($env:CERTIFY_CONFIRM -cne "CERTIFY $env:PACKAGE_VERSION") {'; New = 'if ($env:CERTIFY_CONFIRM -cnotlike "CERTIFY*") {'; Name = 'phrase not bound to the version' }
        @{ Old = "            -ExpectedSubmissionId `$env:SUBMISSION_ID ```n"; New = ''; Name = 'staged draft not bound by id' }
        @{ Old = './tools/release/Assert-StagedSubmission.ps1 `'; New = 'Write-Host `'; Name = 'staged draft proof removed' }
        @{ Old = "      - name: Commit the staged draft to certification`n"; New = "      - name: Commit the staged draft to certification`n        if: always()`n"; Name = 'commit runs after a failed proof' }
        @{ Old = "      - name: Prove the draft is still the staged draft`n        shell: pwsh"; New = "      - name: Prove the draft is still the staged draft`n        continue-on-error: true`n        shell: pwsh"; Name = 'staged proof allowed to fail' }
        @{ Old = "      - name: Prove the draft is still the staged draft`n"; New = "      - name: Early commit`n        shell: pwsh`n        run: msstore submission publish x`n`n      - name: Prove the draft is still the staged draft`n"; Name = 'commit before the draft is proven' }
        @{ Old = "          msstore submission publish `${{ env.STORE_PRODUCT_ID }}`n"; New = "          msstore publish incoming -id `${{ env.STORE_PRODUCT_ID }} --noCommit`n          msstore submission publish `${{ env.STORE_PRODUCT_ID }}`n"; Name = 'certify uploads (re-creates the draft)' }
        @{ Old = "  group: store-submit`n"; New = "  group: store-certify`n"; Name = 'concurrency group not shared' }
        # PR #56 fix round 1.
        @{ Old = '$($env:TAG_COMMIT.Substring(0, 7)).msix" `'; New = '0000000.msix" `'; Name = 'proof file name not bound to the tag commit (Vigil L-1)' }
        @{ Old = "          if (`$env:SUBMISSION_ID -cnotmatch '\A[0-9]{1,30}\z') { throw 'submission_id must be the numeric id from the store-submit summary.' }`n          if (`$env:CERTIFY_CONFIRM"; New = "          if (`$env:CERTIFY_CONFIRM"; Name = 'id not checked before the approval (Vigil N-1)' }
        @{ Old = '          if ($env:CERTIFY_CONFIRM -cne "CERTIFY $env:PACKAGE_VERSION") { throw "Confirmation'; New = '          if ($false) { throw "Confirmation'; Name = 'phrase not checked before the approval (Vigil N-1)' }
        @{ Old = "          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; New = "          `$env:SUBMISSION_ID = (Get-Content -Raw draft.json | ConvertFrom-Json).id`n          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; Name = 'W4 id reassigned from the draft (Relay W4)' }
        @{ Old = "        run: ./tools/release/Assert-PackageVersionFloor.ps1 -PackageVersion `$env:PACKAGE_VERSION"; New = "        run: |`n          ./tools/release/Assert-PackageVersionFloor.ps1 -PackageVersion `$env:PACKAGE_VERSION`n          'SUBMISSION_ID=1' | Out-File -Append `$env:GITHUB_ENV"; Name = 'W4 id rewritten through GITHUB_ENV' }
        @{ Old = "      - name: Prove the draft is still the staged draft`n        shell: pwsh"; New = "      - name: Prove the draft is still the staged draft`n        env:`n          SUBMISSION_ID: `${{ steps.draft.outputs.id }}`n        shell: pwsh"; Name = 'W4 id redefined in a step env' }
        # PR #56 round 2 (Relay L-3, W11): Env: provider writes, by alias and by cmdlet.
        @{ Old = "          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; New = "          si env:SUBMISSION_ID (Get-Content -Raw draft.json | ConvertFrom-Json).id`n          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; Name = 'W11 id rewritten through the si alias' }
        @{ Old = "          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; New = "          ni Env:\SUBMISSION_ID -Value 1 -Force | Out-Null`n          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; Name = 'W11 id rewritten through the ni alias' }
        @{ Old = "          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; New = "          sc env:SUBMISSION_ID 1`n          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; Name = 'W11 id rewritten through Set-Content (sc)' }
        @{ Old = "          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; New = "          Set-Item -Path Env:SUBMISSION_ID -Value 1`n          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; Name = 'W11 id rewritten through Set-Item -Path' }
        # PR #56 round 3 (Vigil L-4): Env: as the LAST token of a command, then a relative path.
        @{ Old = "          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; New = "          cd env:`n          si SUBMISSION_ID 1`n          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; Name = 'W11 id rewritten after cd env:' }
        @{ Old = "          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; New = "          Push-Location Env:`n          Set-Item SUBMISSION_ID 1`n          Pop-Location`n          ./tools/release/Assert-StagedSubmission.ps1 ```n            -SubmissionPath draft.json"; Name = 'W11 id rewritten after Push-Location Env:' }
    )
    'store-status.yml' = @(
        @{ Old = 'if ($publishModeError) { throw'; New = 'if ($publishModeError) { Write-Warning'; Name = 'publish mode only warns' }
    )
}

$total = 0
foreach ($name in $checks.Keys) {
    $text = (Get-Content -LiteralPath (Join-Path $RepositoryRoot ".github/workflows/$name") -Raw) -replace "`r`n", "`n"
    & $checks[$name] $text
    Write-Host "PASS workflow shape: $name"
    if ($SelfTest) {
        foreach ($mutation in $mutations[$name]) {
            if (-not $text.Contains($mutation.Old)) { throw "Mutation target missing: $name / $($mutation.Name)" }
            $caught = $false
            try { & $checks[$name] $text.Replace($mutation.Old, $mutation.New) } catch {
                if (-not $_.Exception.Message.StartsWith('Shape')) { throw }
                $caught = $true
            }
            if (-not $caught) { throw "SURVIVED: $name / $($mutation.Name)" }
            Write-Host "MUTATION DETECTED: $name / $($mutation.Name)"
            $total++
        }
        & $checks[$name] $text
    }
}
if ($SelfTest) { Write-Host "All $total workflow shape mutations detected; original workflows passed again." }
# Explicit success. A native command or a stub inside the suite may have left $LASTEXITCODE non-zero,
# and the GitHub Actions pwsh wrapper ends a step with `exit $LASTEXITCODE` (PR #53 B-1).
exit 0
