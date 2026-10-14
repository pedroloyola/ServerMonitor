<# REL.0C hardening wiring, checked on the workflow text (no third-party YAML dependency):
   G2  release-build: a GitHub Release only from a manual dispatch with create_release=true (default
       false), in its own job that needs the build; the build job is read-only.
   G4  store-submit: a provenance job with no environment and no secret proves the run; the submit job
       needs it, binds the package name to the tag commit, and re-runs floor, identity, privacy and
       runtime gates (runtime fails closed when its script is missing) before the Store CLI is configured.
   G6  store-status: a publish mode other than Manual fails the run.
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

function Assert-StoreSubmit([string] $Text) {
    $jobs = Get-Jobs $Text
    if (-not $jobs.Contains('provenance') -or -not $jobs.Contains('submit')) { throw 'Shape G4: provenance and submit jobs are required.' }
    $provenance = Strip-Comments $jobs['provenance']
    if ($provenance -match '(?m)^    environment:') { throw 'Shape G4: the provenance job must not join an environment.' }
    if ($provenance -match 'secrets\.') { throw 'Shape G4: the provenance job must not read a secret.' }
    if ($provenance -notmatch 'Assert-ReleaseRunProvenance\.ps1') { throw 'Shape G4: the provenance job must run Assert-ReleaseRunProvenance.ps1.' }
    $submit = Strip-Comments $jobs['submit']
    if ($submit -notmatch '(?m)^    needs:\s*provenance\s*$') { throw 'Shape G4: the submit job must need the provenance job.' }
    $configure = $submit.IndexOf('msstore reconfigure')
    if ($configure -lt 0) { throw 'Shape: the submit job no longer configures the Store CLI; review this guard.' }
    foreach ($marker in @('Assert-PackageVersionFloor.ps1', 'Assert-PackageIdentity.ps1', 'Assert-DistributionPrivacy.ps1', 'Assert-PackageRuntime.ps1', '$expectedName')) {
        $at = $submit.IndexOf($marker)
        if ($at -lt 0) { throw "Shape: the submit job must run $marker." }
        if ($at -gt $configure) { throw "Shape: $marker must run before the Store CLI is configured." }
    }
    if ($submit -notmatch "(?s)if \(-not \(Test-Path -LiteralPath \`$runtimeGate -PathType Leaf\)\)\s*\{\s*throw") { throw 'Shape: a missing runtime gate must fail the submission, never skip it.' }
}

function Assert-StoreStatus([string] $Text) {
    $body = Strip-Comments $Text
    if ($body -notmatch 'Assert-ManualPublishMode\.ps1 -PublishMode \$publishMode') { throw 'Shape G6: store-status must check the publish mode.' }
    if ($body -notmatch 'if \(\$publishModeError\) \{ throw') { throw 'Shape G6: a submission in flight off the publishing hold must fail the run.' }
}

$checks = [ordered]@{
    'release-build.yml' = ${function:Assert-ReleaseBuild}
    'store-submit.yml'  = ${function:Assert-StoreSubmit}
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
