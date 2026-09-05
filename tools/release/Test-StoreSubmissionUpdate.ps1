<#
.SYNOPSIS
    Counterproof for Update-StoreSubmission.ps1, with the publishing hold as the headline
    case: prove the script refuses to prepare a submission that would publish itself.

.DESCRIPTION
    Builds synthetic Partner Center submission JSON, runs the real script against it, and
    asserts the outcome for each case. Nothing here talks to Partner Center.

    The cases that matter most are the three ways the hold can be lost: targetPublishMode
    set to Immediate, set to SpecificDate, or absent altogether. All three must stop the
    pipeline before anything is committed.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$updateScript = Join-Path $PSScriptRoot 'Update-StoreSubmission.ps1'
if (-not (Test-Path -LiteralPath $updateScript)) { throw "Cannot find $updateScript" }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$realListingRoot = Join-Path $repoRoot 'release/store'

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("store-submission-checks-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

function New-Submission {
    <# A cut-down but structurally faithful app submission resource. #>
    param(
        [string] $PublishMode = 'Manual',
        [switch] $OmitPublishMode,
        [string] $Visibility = 'Private',
        [string] $PackageVersion = '1.1.2.0',
        [string[]] $Languages = @('en-us', 'pt-br', 'pt-pt'),
        [switch] $PascalCase
    )

    $listings = [ordered] @{}
    foreach ($language in $Languages) {
        $listings[$language] = if ($PascalCase) {
            [ordered] @{
                BaseListing = [ordered] @{
                    Description  = "description for $language"
                    ReleaseNotes = 'previous notes'
                    Features     = @('one', 'two')
                }
            }
        }
        else {
            [ordered] @{
                baseListing = [ordered] @{
                    description  = "description for $language"
                    releaseNotes = 'previous notes'
                    features     = @('one', 'two')
                }
            }
        }
    }

    $submission = [ordered] @{}
    if (-not $OmitPublishMode) {
        if ($PascalCase) { $submission['TargetPublishMode'] = $PublishMode } else { $submission['targetPublishMode'] = $PublishMode }
    }

    if ($PascalCase) {
        $submission['Id'] = '1152921505701819999'
        $submission['Status'] = 'PendingCommit'
        $submission['Visibility'] = $Visibility
        $submission['Listings'] = $listings
        $submission['ApplicationPackages'] = @([ordered] @{ Version = $PackageVersion; Architecture = 'X64'; FileStatus = 'PendingUpload' })
    }
    else {
        $submission['id'] = '1152921505701819999'
        $submission['status'] = 'PendingCommit'
        $submission['visibility'] = $Visibility
        $submission['listings'] = $listings
        $submission['applicationPackages'] = @([ordered] @{ version = $PackageVersion; architecture = 'X64'; fileStatus = 'PendingUpload' })
    }

    return ($submission | ConvertTo-Json -Depth 100)
}

$results = New-Object System.Collections.Generic.List[object]

function Invoke-Case {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [AllowEmptyString()] [string] $Json = '',
        [Parameter(Mandatory)] [ValidateSet('pass', 'fail')] [string] $Expected,
        [string] $ExpectedMessagePattern,
        [string] $ListingRoot = $realListingRoot,
        [hashtable] $ExtraArgs = @{},
        [scriptblock] $Then
    )

    $inputPath  = Join-Path $workDir ("in-$([guid]::NewGuid().ToString('n')).json")
    $outputPath = Join-Path $workDir ("out-$([guid]::NewGuid().ToString('n')).json")
    Set-Content -LiteralPath $inputPath -Value $Json -Encoding UTF8

    $arguments = @{
        SubmissionPath   = $inputPath
        OutputPath       = $outputPath
        StoreListingRoot = $ListingRoot
    }
    foreach ($key in $ExtraArgs.Keys) { $arguments[$key] = $ExtraArgs[$key] }

    $outcome = 'pass'
    $message = ''
    try { & $updateScript @arguments *> $null }
    catch {
        $outcome = 'fail'
        $message = $_.Exception.Message
    }

    $verdict = if ($outcome -eq $Expected) { 'OK' } else { 'TEST ERROR' }

    if ($verdict -eq 'OK' -and $Expected -eq 'fail' -and $ExpectedMessagePattern) {
        if ($message -notmatch $ExpectedMessagePattern) {
            $verdict = 'TEST ERROR'
            $message = "rejected for the wrong reason: $message"
        }
    }

    if ($verdict -eq 'OK' -and $Expected -eq 'pass' -and $Then) {
        try { & $Then $outputPath }
        catch {
            $verdict = 'TEST ERROR'
            $message = "post-condition failed: $($_.Exception.Message)"
        }
    }

    $results.Add([pscustomobject] @{ Case = $Name; Expected = $Expected; Actual = $outcome; Verdict = $verdict; Detail = $message }) | Out-Null
}

try {
    # ---------------------------------------------------------------- baseline
    Invoke-Case -Name 'baseline: Manual hold, notes injected' -Json (New-Submission) -Expected 'pass' `
        -ExtraArgs @{ ExpectedPackageVersion = '1.1.2.0'; ExpectedVisibility = 'Private' } `
        -Then {
            param($path)
            $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if ($result.targetPublishMode -ne 'Manual') { throw "targetPublishMode became $($result.targetPublishMode)" }

            $expected = ((Get-Content -LiteralPath (Join-Path $realListingRoot 'pt-PT/whats-new.txt') -Raw) -replace "`r`n", "`n").TrimEnd()
            if ($result.listings.'pt-pt'.baseListing.releaseNotes -ne $expected) { throw 'pt-pt release notes were not written verbatim' }
            if ($result.listings.'en-us'.baseListing.releaseNotes -eq 'previous notes') { throw 'en-us release notes were not replaced' }
            if ($result.listings.'pt-br'.baseListing.description -ne 'description for pt-br') { throw 'an unrelated listing field was modified' }
            if ($result.visibility -ne 'Private') { throw 'visibility was modified' }
        }

    # ------------------------------------------------- the publishing hold cases
    Invoke-Case -Name 'HOLD LOST: targetPublishMode = Immediate' -Json (New-Submission -PublishMode 'Immediate') `
        -Expected 'fail' -ExpectedMessagePattern "targetPublishMode is 'Immediate'"

    Invoke-Case -Name 'HOLD LOST: targetPublishMode = SpecificDate' -Json (New-Submission -PublishMode 'SpecificDate') `
        -Expected 'fail' -ExpectedMessagePattern "targetPublishMode is 'SpecificDate'"

    Invoke-Case -Name 'HOLD LOST: targetPublishMode absent' -Json (New-Submission -OmitPublishMode) `
        -Expected 'fail' -ExpectedMessagePattern 'no targetPublishMode field'

    # ------------------------------------------------------------ other guards
    Invoke-Case -Name 'wrong package version in the draft' -Json (New-Submission -PackageVersion '1.0.1.0') `
        -Expected 'fail' -ExpectedMessagePattern 'was expected' -ExtraArgs @{ ExpectedPackageVersion = '1.1.2.0' }

    Invoke-Case -Name 'visibility changed underneath us' -Json (New-Submission -Visibility 'Public') `
        -Expected 'fail' -ExpectedMessagePattern 'visibility is' -ExtraArgs @{ ExpectedVisibility = 'Private' }

    Invoke-Case -Name 'listing language missing from the submission' -Json (New-Submission -Languages @('en-us', 'pt-br')) `
        -Expected 'fail' -ExpectedMessagePattern 'no listing for'

    Invoke-Case -Name 'release notes longer than the Store limit' -Json (New-Submission) `
        -Expected 'fail' -ExpectedMessagePattern 'the Store limit is' -ExtraArgs @{ MaxReleaseNotesLength = 50 }

    Invoke-Case -Name 'empty submission JSON' -Json '' -Expected 'fail' -ExpectedMessagePattern 'is empty'

    # ------------------------------------------------------ property-name casing
    Invoke-Case -Name 'PascalCase JSON is understood' -Json (New-Submission -PascalCase) -Expected 'pass' `
        -ExtraArgs @{ ExpectedPackageVersion = '1.1.2.0' } `
        -Then {
            param($path)
            $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if ($result.TargetPublishMode -ne 'Manual') { throw 'PascalCase hold not preserved' }
            if ($result.Listings.'pt-pt'.BaseListing.ReleaseNotes -eq 'previous notes') { throw 'PascalCase release notes were not replaced' }
        }

    Invoke-Case -Name 'PascalCase JSON with Immediate is still refused' -Json (New-Submission -PascalCase -PublishMode 'Immediate') `
        -Expected 'fail' -ExpectedMessagePattern "targetPublishMode is 'Immediate'"

    # ---------------------------------------------- a language folder with no notes
    $emptyRoot = Join-Path $workDir 'listings-missing-file'
    New-Item -ItemType Directory -Path (Join-Path $emptyRoot 'pt-PT') -Force | Out-Null
    Invoke-Case -Name 'language folder without whats-new.txt' -Json (New-Submission) `
        -Expected 'fail' -ExpectedMessagePattern 'has no whats-new.txt' -ListingRoot $emptyRoot
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

$results | Format-Table -AutoSize Case, Expected, Actual, Verdict | Out-String -Width 220 | Write-Host

$broken = @($results | Where-Object { $_.Verdict -ne 'OK' })
if ($broken.Count -gt 0) {
    foreach ($case in $broken) { Write-Host "TEST ERROR  $($case.Case): expected $($case.Expected), got $($case.Actual). $($case.Detail)" }
    throw "$($broken.Count) of $($results.Count) counterproof case(s) did not behave as required."
}

Write-Host "All $($results.Count) counterproof cases behaved as required."
