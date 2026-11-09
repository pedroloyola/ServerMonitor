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

# STORE-PIPE-1: the packages of a REAL uncommitted draft, as msstore publish --noCommit leaves it (msstore-cli
# v0.4.3 PrepareBundleAsync; SVA-9 STAGE run 42478961083): the published package marked PendingDelete, and the
# upload as { fileStatus = PendingUpload, fileName } WITHOUT a version, which Partner Center fills in only after
# the commit. The fixture before STORE-PIPE-1 gave the PendingUpload entry a version, a shape that does not exist
# before the commit, so the old rule passed here and failed live. Replace with an anonymised real capture once one
# exists (fileUploadUrl, a SAS write URI, removed before anything is written).
$expectedVersion = '1.1.8.0'
$expectedFile = 'ServerAlyzer_1.1.8.0_x64_744297c.msix'
$packageArgs = @{ ExpectedPackageVersion = $expectedVersion; ExpectedPackageFileName = $expectedFile }
$oldPackage = [ordered] @{ fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; fileStatus = 'PendingDelete'; id = '1152921504000000001'; version = '1.1.7.0'; architecture = 'X64' }
function New-Upload([hashtable] $Change = @{}) {
    $upload = [ordered] @{ fileName = $expectedFile; fileStatus = 'PendingUpload' }
    foreach ($key in $Change.Keys) { $upload[$key] = $Change[$key] }
    return $upload
}

function New-Submission {
    <# A cut-down app submission resource whose packages have the real uncommitted-draft shape. #>
    param(
        [string] $PublishMode = 'Manual',
        [switch] $OmitPublishMode,
        [string] $Visibility = 'Private',
        [object[]] $Packages = @($oldPackage, (New-Upload)),
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
        $submission['ApplicationPackages'] = @(foreach ($package in $Packages) {
            $pascal = [ordered] @{}
            foreach ($key in $package.Keys) { $pascal[$key.Substring(0, 1).ToUpperInvariant() + $key.Substring(1)] = $package[$key] }
            $pascal
        })
        $submission['FileUploadUrl'] = 'https://example.invalid/upload?sig=REDACTED'
    }
    else {
        $submission['id'] = '1152921505701819999'
        $submission['status'] = 'PendingCommit'
        $submission['visibility'] = $Visibility
        $submission['listings'] = $listings
        $submission['applicationPackages'] = $Packages
        $submission['fileUploadUrl'] = 'https://example.invalid/upload?sig=REDACTED'
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
        ExpectedPackageVersion  = $expectedVersion
        ExpectedPackageFileName = $expectedFile
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
    Invoke-Case -Name 'baseline: real draft shape, Manual hold, notes injected' -Json (New-Submission) -Expected 'pass' `
        -ExtraArgs ($packageArgs + @{ ExpectedVisibility = 'Private' }) `
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

    # ------------------------------- STORE-PIPE-1: the packages of the staged draft
    Invoke-Case -Name 'upload carries the expected version (tolerated)' -Json (New-Submission -Packages @($oldPackage, (New-Upload @{ version = $expectedVersion }))) `
        -Expected 'pass' -ExtraArgs $packageArgs
    Invoke-Case -Name 'upload carries another version' -Json (New-Submission -Packages @($oldPackage, (New-Upload @{ version = '1.1.9.0' }))) `
        -Expected 'fail' -ExpectedMessagePattern "pending upload carries version '1\.1\.9\.0'" -ExtraArgs $packageArgs
    Invoke-Case -Name 'upload version as a one-element array' -Json (New-Submission -Packages @($oldPackage, (New-Upload @{ version = @($expectedVersion) }))) `
        -Expected 'fail' -ExpectedMessagePattern 'unreadable version \(not a single string\)' -ExtraArgs $packageArgs
    Invoke-Case -Name 'upload of another commit (sha7)' -Json (New-Submission -Packages @($oldPackage, (New-Upload @{ fileName = 'ServerAlyzer_1.1.8.0_x64_deadbee.msix' }))) `
        -Expected 'fail' -ExpectedMessagePattern "pending upload is 'ServerAlyzer_1\.1\.8\.0_x64_deadbee\.msix'" -ExtraArgs $packageArgs
    Invoke-Case -Name 'upload of another version (file name)' -Json (New-Submission -Packages @($oldPackage, (New-Upload @{ fileName = 'ServerAlyzer_1.1.7.0_x64_744297c.msix' }))) `
        -Expected 'fail' -ExpectedMessagePattern "pending upload is 'ServerAlyzer_1\.1\.7\.0_x64_744297c\.msix'" -ExtraArgs $packageArgs
    Invoke-Case -Name 'upload file name in other case' -Json (New-Submission -Packages @($oldPackage, (New-Upload @{ fileName = 'ServerAlyzer_1.1.8.0_X64_744297C.msix' }))) `
        -Expected 'fail' -ExpectedMessagePattern 'pending upload is' -ExtraArgs $packageArgs
    Invoke-Case -Name 'upload file name as a one-element array' -Json (New-Submission -Packages @($oldPackage, (New-Upload @{ fileName = @($expectedFile) }))) `
        -Expected 'fail' -ExpectedMessagePattern "pending upload is ''" -ExtraArgs $packageArgs
    Invoke-Case -Name 'two pending uploads' -Json (New-Submission -Packages @($oldPackage, (New-Upload), (New-Upload))) `
        -Expected 'fail' -ExpectedMessagePattern 'more than one package waiting for upload' -ExtraArgs $packageArgs
    Invoke-Case -Name 'untouched clone: no pending upload' -Json (New-Submission -Packages @([ordered] @{ fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; fileStatus = 'Uploaded'; version = '1.1.7.0' })) `
        -Expected 'fail' -ExpectedMessagePattern 'no package waiting for upload' -ExtraArgs $packageArgs
    Invoke-Case -Name 'old package still Uploaded (older, allowed)' -Json (New-Submission -Packages @([ordered] @{ fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; fileStatus = 'Uploaded'; version = '1.1.7.0' }, (New-Upload))) `
        -Expected 'pass' -ExtraArgs $packageArgs
    Invoke-Case -Name 'newer live package next to the upload' -Json (New-Submission -Packages @([ordered] @{ fileName = 'ServerAlyzer_1.1.9.0_x64_abcdef0.msix'; fileStatus = 'Uploaded'; version = '1.1.9.0' }, (New-Upload))) `
        -Expected 'fail' -ExpectedMessagePattern 'live package 1\.1\.9\.0' -ExtraArgs $packageArgs
    Invoke-Case -Name 'second live package of the same version' -Json (New-Submission -Packages @((New-Upload), [ordered] @{ fileName = 'manual-upload.msix'; fileStatus = 'Uploaded'; version = $expectedVersion })) `
        -Expected 'fail' -ExpectedMessagePattern 'live package 1\.1\.8\.0' -ExtraArgs $packageArgs
    Invoke-Case -Name 'newer package marked PendingDelete (allowed)' -Json (New-Submission -Packages @((New-Upload), [ordered] @{ version = '1.1.9.0'; fileStatus = 'PendingDelete' })) `
        -Expected 'pass' -ExtraArgs $packageArgs
    Invoke-Case -Name 'live package without a version' -Json (New-Submission -Packages @((New-Upload), [ordered] @{ fileName = 'x.msix'; fileStatus = 'Uploaded' })) `
        -Expected 'fail' -ExpectedMessagePattern "unreadable version \(''\)" -ExtraArgs $packageArgs
    Invoke-Case -Name 'fileStatus as an array (L-4)' -Json (New-Submission -Packages @((New-Upload), [ordered] @{ version = '1.1.9.0'; fileStatus = @('PendingDelete') })) `
        -Expected 'fail' -ExpectedMessagePattern 'unreadable fileStatus' -ExtraArgs $packageArgs
    Invoke-Case -Name 'fileStatus as a number (L-4)' -Json (New-Submission -Packages @((New-Upload), [ordered] @{ version = '1.1.9.0'; fileStatus = 3 })) `
        -Expected 'fail' -ExpectedMessagePattern 'unreadable fileStatus' -ExtraArgs $packageArgs
    Invoke-Case -Name 'applicationPackages as a single object' -Json ((New-Submission) -replace '(?s)"applicationPackages":\s*\[.*?\]\s*,', ('"applicationPackages": ' + ((New-Upload) | ConvertTo-Json -Compress) + ',')) `
        -Expected 'fail' -ExpectedMessagePattern 'not a JSON array' -ExtraArgs $packageArgs
    # Vigil L-3: both package parameters are mandatory. An empty value is refused by the binder (an omitted one
    # would prompt in an interactive host and fail in a non-interactive one), so the package check cannot be skipped.
    Invoke-Case -Name 'version given without a file name' -Json (New-Submission) `
        -Expected 'fail' -ExpectedMessagePattern "argument to parameter 'ExpectedPackageFileName' because it is an empty string" -ExtraArgs @{ ExpectedPackageFileName = '' }
    Invoke-Case -Name 'file name given without a version' -Json (New-Submission) `
        -Expected 'fail' -ExpectedMessagePattern "argument to parameter 'ExpectedPackageVersion' because it is an empty string" -ExtraArgs @{ ExpectedPackageVersion = '' }
    Invoke-Case -Name 'neither package parameter (check not skipped, Vigil L-3)' -Json (New-Submission) `
        -Expected 'fail' -ExpectedMessagePattern 'because it is an empty string' -ExtraArgs @{ ExpectedPackageVersion = ''; ExpectedPackageFileName = '' }
    Invoke-Case -Name 'malformed expected file name' -Json (New-Submission) `
        -Expected 'fail' -ExpectedMessagePattern 'ExpectedPackageFileName is missing or not' -ExtraArgs @{ ExpectedPackageFileName = "..\$expectedFile" }
    Invoke-Case -Name 'real shape: SAS upload URL passes through untouched' -Json (New-Submission) -Expected 'pass' -ExtraArgs $packageArgs `
        -Then {
            param($path)
            $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if ($result.fileUploadUrl -ne 'https://example.invalid/upload?sig=REDACTED') { throw 'fileUploadUrl was changed' }
            if (@($result.applicationPackages).Count -ne 2 -or $result.applicationPackages[1].fileStatus -ne 'PendingUpload') { throw 'applicationPackages were changed' }
        }

    # ------------------------------------------------------------ other guards

    Invoke-Case -Name 'visibility changed underneath us' -Json (New-Submission -Visibility 'Public') `
        -Expected 'fail' -ExpectedMessagePattern 'visibility is' -ExtraArgs @{ ExpectedVisibility = 'Private' }

    Invoke-Case -Name 'listing language missing from the submission' -Json (New-Submission -Languages @('en-us', 'pt-br')) `
        -Expected 'fail' -ExpectedMessagePattern 'no listing for'

    Invoke-Case -Name 'release notes longer than the Store limit' -Json (New-Submission) `
        -Expected 'fail' -ExpectedMessagePattern 'the Store limit is' -ExtraArgs @{ MaxReleaseNotesLength = 50 }

    Invoke-Case -Name 'empty submission JSON' -Json '' -Expected 'fail' -ExpectedMessagePattern 'is empty'

    # ------------------------------------------------------ property-name casing
    Invoke-Case -Name 'PascalCase JSON is understood' -Json (New-Submission -PascalCase) -Expected 'pass' `
        -ExtraArgs $packageArgs `
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

    # ------------------- STORE-PIPE-1 hardening: the SAS upload URL never reaches the output
    $leakIn = Join-Path $workDir 'leak-in.json'
    $leakOut = Join-Path $workDir 'leak-out.json'
    Set-Content -LiteralPath $leakIn -Value (New-Submission) -Encoding UTF8
    $leakDetail = ''
    try {
        $printed = & $updateScript -SubmissionPath $leakIn -OutputPath $leakOut -StoreListingRoot $realListingRoot @packageArgs *>&1 | Out-String
        if ($printed.Contains('example.invalid') -or $printed.Contains('sig=')) { $leakDetail = 'the SAS upload URL appeared in the output' }
    }
    catch { $leakDetail = "the real draft shape was refused: $($_.Exception.Message)" }
    $results.Add([pscustomobject] @{ Case = 'fileUploadUrl never printed'; Expected = 'pass'; Actual = $(if ($leakDetail) { 'fail' } else { 'pass' }); Verdict = $(if ($leakDetail) { 'TEST ERROR' } else { 'OK' }); Detail = $leakDetail }) | Out-Null

    # ------------------- STORE-PIPE-1 round 2 (Vigil L-1): no workflow command injected from Partner Center values
    # A value that holds a newline followed by "::warning::..." would start a log line GitHub Actions runs as a
    # workflow command. Values read from the submission are printed only when plainly printable.
    $injected = "`n::warning::injected"
    $injection = @(
        @{ Name = 'injection: pending upload file name (refused)'; Json = (New-Submission -Packages @($oldPackage, (New-Upload @{ fileName = "$expectedFile$injected" }))) }
        @{ Name = 'injection: untouched listing key (passes)'; Json = (New-Submission -Languages @('en-us', 'pt-br', 'pt-pt', "zz$injected")) }
        @{ Name = 'injection: publish mode (refused)'; Json = (New-Submission -PublishMode "Immediate$injected") }
        @{ Name = 'injection: visibility (refused)'; Json = (New-Submission -Visibility "Public$injected") }
        @{ Name = 'injection: live package file name (refused)'; Json = (New-Submission -Packages @((New-Upload), [ordered] @{ fileName = "new.msix$injected"; fileStatus = 'Uploaded'; version = '1.1.9.0' })) }
    )
    foreach ($case in $injection) {
        $inPath = Join-Path $workDir ("inj-$([guid]::NewGuid().ToString('n')).json")
        $outPath = Join-Path $workDir ("inj-out-$([guid]::NewGuid().ToString('n')).json")
        Set-Content -LiteralPath $inPath -Value $case.Json -Encoding UTF8
        $text = ''
        try { $text = & $updateScript -SubmissionPath $inPath -OutputPath $outPath -StoreListingRoot $realListingRoot @packageArgs -ExpectedVisibility 'Private' *>&1 | Out-String }
        catch { $text += "`n" + $_.Exception.Message }
        $bad = @($text -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('::') })
        $results.Add([pscustomobject] @{ Case = $case.Name; Expected = 'pass'; Actual = $(if ($bad.Count) { 'fail' } else { 'pass' }); Verdict = $(if ($bad.Count) { 'TEST ERROR' } else { 'OK' }); Detail = $(if ($bad.Count) { "an output line starts with '::': $($bad[0])" } else { '' }) }) | Out-Null
    }

    # ------------------- regression of the SVA-9 incident (STAGE run 42478961083)
    # The rule before STORE-PIPE-1, verbatim in substance: the versions of every package that is not PendingDelete.
    # On the real draft shape it yields [] (the upload has no version yet), which is why the live run stopped with
    # "carries package version(s) [] but 1.1.8.0 was expected". Kept as the reason for the change; if the fixture
    # ever stops reproducing it, the fixture has drifted from the real shape.
    $legacy = @()
    foreach ($package in @(((New-Submission) | ConvertFrom-Json).applicationPackages)) {
        if ($package.fileStatus -ieq 'PendingDelete') { continue }
        if ($package.PSObject.Properties['version'] -and $package.version) { $legacy += $package.version }
    }
    $reproduced = ($legacy.Count -eq 0)
    $results.Add([pscustomobject] @{ Case = 'regression: old rule reads the real draft as []'; Expected = 'pass'; Actual = $(if ($reproduced) { 'pass' } else { 'fail' }); Verdict = $(if ($reproduced) { 'OK' } else { 'TEST ERROR' }); Detail = $(if ($reproduced) { '' } else { "the old rule now reads [$($legacy -join ', ')]: the fixture drifted from the real draft shape" }) }) | Out-Null
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
# Explicit success. A native command or a stub inside the suite may have left $LASTEXITCODE non-zero,
# and the GitHub Actions pwsh wrapper ends a step with `exit $LASTEXITCODE` (PR #53 B-1).
exit 0
