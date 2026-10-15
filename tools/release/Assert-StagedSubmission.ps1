<#
.SYNOPSIS
    Proves that the product's pending submission is a draft that is safe to hand to a person (after
    store-submit stages it) or to commit to certification (store-certify), and reports what it holds.

.DESCRIPTION
    REL.0E P-1. Staging and certification are two separate dispatches. Both look at the same thing,
    the output of 'msstore submission get <productId>', and both must see:

      * status exactly 'PendingCommit': a draft that has not been committed. 'submission get' falls
        back to the LAST PUBLISHED submission when there is no draft, and 'submission publish' commits
        whatever 'submission get' would return, so "there is a draft" must be proven, never assumed;
      * a numeric submission id, and, when -ExpectedSubmissionId is given (store-certify), exactly the
        draft that store-submit staged and a person reviewed. A draft deleted and recreated in the
        meantime has another id and is refused;
      * targetPublishMode 'Manual' (the publishing hold): certification never means publication;
      * the expected visibility, asserted and never written;
      * the expected package version among the draft's live packages, and NO newer live package. The
        draft keeps the packages of the last published submission, which are older; a newer one would
        be the one customers get, and it is not the package this run vouches for;
      * that package under the file name store-submit uploaded, ServerAlyzer_<version>_x64_<tag commit
        first 7>.msix, which binds the draft to the tag commit G4 proved (Vigil P-1 L-1). msstore publish
        writes the uploaded file's name into the draft's applicationPackages[].fileName
        (msstore-cli PublishAsync: new ApplicationPackage { FileStatus = PendingUpload, FileName = file.Name }).
        Another package of the same version under another name is refused too.

    It fails closed: empty output, unparseable JSON, missing fields and unknown values are refusals.

    With -ListingReport it also prints, per listing, what will go to certification (field lengths,
    counts, and whether the release notes still equal release/store/<lang>/whats-new.txt). The
    report never fails the run: the listing is reviewed by a person in Partner Center.

.PARAMETER SubmissionPath
    Output of 'msstore submission get <productId>', saved to a file.

.PARAMETER ExpectedSubmissionId
    store-certify: the submission id that store-submit reported for the staged draft.

.PARAMETER ExpectedPackageFileName
    The file name store-submit uploaded: ServerAlyzer_<version>_x64_<first 7 of the tag commit>.msix.
#>
[CmdletBinding(DefaultParameterSetName = 'Check')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Check')] [string] $SubmissionPath,
    [Parameter(Mandatory, ParameterSetName = 'Check')] [string] $ExpectedPackageVersion,
    [Parameter(Mandatory, ParameterSetName = 'Check')] [string] $ExpectedVisibility,
    [Parameter(ParameterSetName = 'Check')] [string] $ExpectedSubmissionId = '',
    [Parameter(Mandatory, ParameterSetName = 'Check')] [string] $ExpectedPackageFileName,
    [Parameter(ParameterSetName = 'Check')] [switch] $ListingReport,
    [Parameter(ParameterSetName = 'Check')] [string] $StoreListingRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path 'release/store'),
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Field($Object, [string] $Name) {
    if ($null -eq $Object -or $Object -isnot [pscustomobject]) { return $null }
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-ScalarField($Object, [string] $Name) {
    <# PR #56 Vigil N-3: a field that must be a single JSON value. Get-Field returns through the pipeline, which
       unrolls a one-element array into its element, so ["Manual"] or ["<the right file name>"] would pass for
       the right value. Here the value is inspected where it is read: arrays and objects are refused (null). #>
    if ($null -eq $Object -or $Object -isnot [pscustomobject]) { return $null }
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($null -eq $property) { return $null }
    if ($property.Value -is [string] -or $property.Value -is [ValueType]) { return $property.Value }
    return $null
}

function ConvertFrom-CliJson([string] $Raw) {
    if ([string]::IsNullOrWhiteSpace($Raw)) { throw 'the CLI returned nothing' }
    # Some CLI versions print status lines around the JSON; take the outermost object.
    $start = $Raw.IndexOf('{'); $end = $Raw.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) { throw 'no JSON object in the CLI output' }
    try { return $Raw.Substring($start, $end - $start + 1) | ConvertFrom-Json }
    catch { throw "the CLI output is not valid JSON: $($_.Exception.Message)" }
}

function Test-StagedSubmission {
    <# Returns a result object instead of throwing, so the self-test exercises the same decision. #>
    param([string] $Raw, [string] $ExpectedPackageVersion, [string] $ExpectedVisibility, [string] $ExpectedSubmissionId, [string] $ExpectedPackageFileName)

    $fail = { param($reason) [pscustomobject] @{ Ok = $false; Reason = $reason; Id = ''; Versions = @() } }

    if ($ExpectedPackageVersion -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z') { return & $fail 'the expected package version is not a four-part version' }
    if ([string]::IsNullOrWhiteSpace($ExpectedVisibility)) { return & $fail 'no expected visibility was given' }
    if ($ExpectedSubmissionId -and $ExpectedSubmissionId -cnotmatch '\A[0-9]{1,30}\z') { return & $fail 'the expected submission id is not numeric' }
    if ($ExpectedPackageFileName -cnotmatch '\AServerAlyzer_[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+_x64_[0-9a-f]{7}\.msix\z') { return & $fail 'the expected package file name is not ServerAlyzer_<version>_x64_<sha7>.msix' }

    try { $submission = ConvertFrom-CliJson $Raw } catch { return & $fail $_.Exception.Message }
    if ($submission -isnot [pscustomobject]) { return & $fail 'the CLI output is not a JSON object' }

    $status = Get-ScalarField $submission 'status'
    if (-not ($status -is [string] -and [string]::Equals($status, 'PendingCommit', [StringComparison]::Ordinal))) {
        return & $fail "the product has no uncommitted draft (status '$status', expected 'PendingCommit')"
    }

    $id = [string] (Get-ScalarField $submission 'id')
    if ($id -cnotmatch '\A[0-9]{1,30}\z') { return & $fail "the draft has no numeric submission id ('$id')" }
    if ($ExpectedSubmissionId -and -not [string]::Equals($id, $ExpectedSubmissionId, [StringComparison]::Ordinal)) {
        return & $fail "the pending draft is submission $id, not the staged submission $ExpectedSubmissionId"
    }

    $mode = Get-ScalarField $submission 'targetPublishMode'
    if (-not ($mode -is [string] -and [string]::Equals($mode, 'Manual', [StringComparison]::OrdinalIgnoreCase))) {
        return & $fail "targetPublishMode is '$mode', not 'Manual': the publishing hold is not in force"
    }

    $visibility = Get-ScalarField $submission 'visibility'
    if (-not ($visibility -is [string] -and [string]::Equals($visibility, $ExpectedVisibility, [StringComparison]::OrdinalIgnoreCase))) {
        return & $fail "visibility is '$visibility', expected '$ExpectedVisibility'"
    }

    # Read where it is, not through Get-Field: the pipeline would hide whether it is an array (PR #56 Relay I-1).
    $packagesProperty = $submission.PSObject.Properties | Where-Object { $_.Name -ieq 'applicationPackages' } | Select-Object -First 1
    if ($null -eq $packagesProperty -or $null -eq $packagesProperty.Value) { return & $fail 'the draft has no applicationPackages' }
    if ($packagesProperty.Value -isnot [array]) { return & $fail 'applicationPackages is not a JSON array' }
    $packages = $packagesProperty.Value
    $expected = [version] $ExpectedPackageVersion
    $versions = @()
    $namedPackage = $false
    foreach ($package in $packages) {
        # PR #56 Relay L-4: a fileStatus that is present but not one string ("[PendingDelete]") makes the package
        # unreadable. Read as "not PendingDelete" it would count a package being removed as the live one.
        $fileStatusProperty = if ($package -is [pscustomobject]) { $package.PSObject.Properties | Where-Object { $_.Name -ieq 'fileStatus' } | Select-Object -First 1 }
        if ($null -ne $fileStatusProperty -and $null -ne $fileStatusProperty.Value -and $fileStatusProperty.Value -isnot [string]) {
            return & $fail 'a package has an unreadable fileStatus (not a single string)'
        }
        if ([string]::Equals([string] (Get-ScalarField $package 'fileStatus'), 'PendingDelete', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $version = Get-ScalarField $package 'version'
        $text = if ($version -is [string]) { $version } else { '' }
        if ($text -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z') { return & $fail "a live package has an unreadable version ('$text')" }
        $versions += $text
        if ([string]::Equals($text, $ExpectedPackageVersion, [StringComparison]::Ordinal)) {
            # A JSON string only (PR #56 Vigil N-3): a one-element array holding the right name is refused.
            $fileName = Get-ScalarField $package 'fileName'
            if (-not ($fileName -is [string] -and [string]::Equals($fileName, $ExpectedPackageFileName, [StringComparison]::Ordinal))) {
                return & $fail "the draft's package $text is '$fileName', not '$ExpectedPackageFileName' uploaded from the tag commit"
            }
            $namedPackage = $true
        }
        if (([version] $text) -gt $expected) {
            return & $fail "the draft carries package $text, newer than $ExpectedPackageVersion; customers would get a package this run did not verify"
        }
    }
    if ($versions -cnotcontains $ExpectedPackageVersion -or -not $namedPackage) {
        return & $fail "the draft carries package version(s) [$($versions -join ', ')], not $ExpectedPackageVersion"
    }

    return [pscustomobject] @{ Ok = $true; Reason = "draft $id is uncommitted, on the publishing hold, visibility $visibility, package $ExpectedPackageVersion ($ExpectedPackageFileName)"; Id = $id; Versions = $versions }
}

function Write-ListingReport($Submission, [string] $ListingRoot) {
    $listings = Get-Field $Submission 'listings'
    if ($null -eq $listings -or $listings -isnot [pscustomobject]) { Write-Host '  info  the draft has no listings object to report'; return }
    $rows = foreach ($property in $listings.PSObject.Properties) {
        $base = Get-Field $property.Value 'baseListing'
        $notes = [string] (Get-Field $base 'releaseNotes')
        $repoNotes = Join-Path $ListingRoot "$($property.Name)/whats-new.txt"
        $matchesRepo = 'no repo file'
        $folder = Get-ChildItem -LiteralPath $ListingRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -ieq $property.Name } | Select-Object -First 1
        if ($folder) {
            $repoNotes = Join-Path $folder.FullName 'whats-new.txt'
            if (Test-Path -LiteralPath $repoNotes -PathType Leaf) {
                $repoText = ((Get-Content -LiteralPath $repoNotes -Raw) -replace "`r`n", "`n").TrimEnd()
                $matchesRepo = if ([string]::Equals(($notes -replace "`r`n", "`n").TrimEnd(), $repoText, [StringComparison]::Ordinal)) { 'yes' } else { 'NO (edited after staging)' }
            }
        }
        [pscustomobject] @{
            Language        = $property.Name
            Description     = ([string] (Get-Field $base 'description')).Length
            ShortDescription = ([string] (Get-Field $base 'shortDescription')).Length
            Features        = @(Get-Field $base 'features' | Where-Object { $_ }).Count
            Keywords        = @(Get-Field $base 'keywords' | Where-Object { $_ }).Count
            Images          = @(Get-Field $base 'images' | Where-Object { $_ }).Count
            ReleaseNotes    = $notes.Length
            NotesMatchRepo  = $matchesRepo
        }
    }
    Write-Host ''
    Write-Host 'Listing content in the draft (lengths and counts; the text itself is reviewed in Partner Center):'
    $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    if ($env:GITHUB_STEP_SUMMARY) {
        $lines = @('', '| language | description | short description | features | keywords | images | release notes | notes = repo |', '| --- | --- | --- | --- | --- | --- | --- | --- |')
        foreach ($row in $rows) { $lines += "| $($row.Language) | $($row.Description) | $($row.ShortDescription) | $($row.Features) | $($row.Keywords) | $($row.Images) | $($row.ReleaseNotes) | $($row.NotesMatchRepo) |" }
        $lines | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    }
}

if ($SelfTest) {
    function New-Draft([hashtable] $Change = @{}, [string[]] $Remove = @(), [object[]] $Packages = $null, [switch] $Pascal) {
        $draft = [ordered] @{
            id                  = '1152921505701819999'
            status              = 'PendingCommit'
            targetPublishMode   = 'Manual'
            visibility          = 'Public'
            applicationPackages = @(
                [ordered] @{ version = '1.1.2.0'; fileStatus = 'Uploaded' }
                [ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix'; fileStatus = 'PendingUpload' }
            )
            listings            = [ordered] @{ 'en-us' = [ordered] @{ baseListing = [ordered] @{ description = 'd'; releaseNotes = 'n' } } }
        }
        if ($null -ne $Packages) { $draft.applicationPackages = $Packages }
        foreach ($key in $Change.Keys) { $draft[$key] = $Change[$key] }
        foreach ($key in $Remove) { $draft.Remove($key) }
        if ($Pascal) {
            $pascalDraft = [ordered] @{}
            foreach ($key in $draft.Keys) { $pascalDraft[$key.Substring(0, 1).ToUpperInvariant() + $key.Substring(1)] = $draft[$key] }
            $draft = $pascalDraft
        }
        return ($draft | ConvertTo-Json -Depth 10)
    }
    $id = '1152921505701819999'
    $cases = @(
        @{ Name = 'staged draft (stage, no id bound)';    Raw = (New-Draft); Id = '';  Ok = $true }
        @{ Name = 'staged draft, id bound (certify)';     Raw = (New-Draft); Id = $id; Ok = $true }
        @{ Name = 'PascalCase fields';                    Raw = (New-Draft -Pascal); Id = $id; Ok = $true }
        @{ Name = 'JSON wrapped in CLI chatter';          Raw = "Retrieving Submission`n$(New-Draft)`nDone."; Id = $id; Ok = $true }
        @{ Name = 'manual (case) hold';                   Raw = (New-Draft @{ targetPublishMode = 'manual' }); Id = $id; Ok = $true }
        @{ Name = 'newer package marked PendingDelete';   Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix'; fileStatus = 'PendingUpload' }, [ordered] @{ version = '1.1.9.0'; fileStatus = 'PendingDelete' })); Id = $id; Ok = $true }
        @{ Name = 'no draft: last published returned';    Raw = (New-Draft @{ status = 'Published' }); Id = $id; Ok = $false }
        @{ Name = 'already committed';                    Raw = (New-Draft @{ status = 'CommitStarted' }); Id = $id; Ok = $false }
        @{ Name = 'in certification';                     Raw = (New-Draft @{ status = 'Certification' }); Id = $id; Ok = $false }
        @{ Name = 'commit failed draft';                  Raw = (New-Draft @{ status = 'CommitFailed' }); Id = $id; Ok = $false }
        @{ Name = 'status in other case';                 Raw = (New-Draft @{ status = 'pendingcommit' }); Id = $id; Ok = $false }
        @{ Name = 'status missing';                       Raw = (New-Draft -Remove 'status'); Id = $id; Ok = $false }
        @{ Name = 'another draft (recreated)';            Raw = (New-Draft @{ id = '1152921505701810000' }); Id = $id; Ok = $false }
        @{ Name = 'id prefix of the staged id';           Raw = (New-Draft @{ id = '115292150570181999' }); Id = $id; Ok = $false }
        @{ Name = 'id missing';                           Raw = (New-Draft -Remove 'id'); Id = ''; Ok = $false }
        @{ Name = 'id not numeric';                       Raw = (New-Draft @{ id = 'abc' }); Id = ''; Ok = $false }
        @{ Name = 'expected id not numeric';              Raw = (New-Draft); Id = "$id "; Ok = $false }
        @{ Name = 'hold lost: Immediate';                 Raw = (New-Draft @{ targetPublishMode = 'Immediate' }); Id = $id; Ok = $false }
        @{ Name = 'hold lost: SpecificDate';              Raw = (New-Draft @{ targetPublishMode = 'SpecificDate' }); Id = $id; Ok = $false }
        @{ Name = 'hold missing';                         Raw = (New-Draft -Remove 'targetPublishMode'); Id = $id; Ok = $false }
        @{ Name = 'hold with trailing space';             Raw = (New-Draft @{ targetPublishMode = 'Manual ' }); Id = $id; Ok = $false }
        @{ Name = 'visibility changed';                   Raw = (New-Draft @{ visibility = 'Private' }); Id = $id; Ok = $false }
        @{ Name = 'visibility missing';                   Raw = (New-Draft -Remove 'visibility'); Id = $id; Ok = $false }
        @{ Name = 'no packages field';                    Raw = (New-Draft -Remove 'applicationPackages'); Id = $id; Ok = $false }
        @{ Name = 'expected package absent';              Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.2.0'; fileStatus = 'Uploaded' })); Id = $id; Ok = $false }
        @{ Name = 'expected package only PendingDelete';  Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix'; fileStatus = 'PendingDelete' })); Id = $id; Ok = $false }
        @{ Name = 'newer live package in the draft';      Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix'; fileStatus = 'Uploaded' }, [ordered] @{ version = '1.1.10.0'; fileStatus = 'PendingUpload' })); Id = $id; Ok = $false }
        @{ Name = 'unreadable package version';           Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix'; fileStatus = 'Uploaded' }, [ordered] @{ version = 'v2'; fileStatus = 'Uploaded' })); Id = $id; Ok = $false }
        @{ Name = 'package of another commit (same version)'; Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_abcdef0.msix'; fileStatus = 'PendingUpload' })); Id = $id; Ok = $false }
        @{ Name = 'package file name missing';            Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileStatus = 'PendingUpload' })); Id = $id; Ok = $false }
        @{ Name = 'hold as a one-element array';           Raw = (New-Draft @{ targetPublishMode = @('Manual') }); Id = $id; Ok = $false }
        @{ Name = 'visibility as a one-element array';     Raw = (New-Draft @{ visibility = @('Public') }); Id = $id; Ok = $false }
        @{ Name = 'status as a one-element array';         Raw = (New-Draft @{ status = @('PendingCommit') }); Id = $id; Ok = $false }
        @{ Name = 'id as a one-element array';             Raw = (New-Draft @{ id = @('1152921505701819999') }); Id = $id; Ok = $false }
        @{ Name = 'id as a JSON number';                   Raw = ((New-Draft) -replace '"id":\s*"1152921505701819999"', '"id": 1152921505701819999'); Id = $id; Ok = $true }
        @{ Name = 'package version as a one-element array'; Raw = (New-Draft -Packages @([ordered] @{ version = @('1.1.7.0'); fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix'; fileStatus = 'PendingUpload' })); Id = $id; Ok = $false }
        @{ Name = 'package fileStatus as a one-element array'; Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix'; fileStatus = @('PendingDelete') })); Id = $id; Ok = $false }
        @{ Name = 'package without a fileStatus';          Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix' })); Id = $id; Ok = $true }
        @{ Name = 'applicationPackages as a single object'; Raw = (New-Draft @{ applicationPackages = [ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix'; fileStatus = 'PendingUpload' } }); Id = $id; Ok = $false }
        @{ Name = 'package file name as a one-element array'; Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = @('ServerAlyzer_1.1.7.0_x64_300116d.msix'); fileStatus = 'PendingUpload' })); Id = $id; Ok = $false }
        @{ Name = 'package file name in other case';      Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_X64_300116D.msix'; fileStatus = 'PendingUpload' })); Id = $id; Ok = $false }
        @{ Name = 'second live package, same version';    Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_300116d.msix'; fileStatus = 'PendingUpload' }, [ordered] @{ version = '1.1.7.0'; fileName = 'manual-upload.msix'; fileStatus = 'Uploaded' })); Id = $id; Ok = $false }
        @{ Name = 'expected file name malformed';         Raw = (New-Draft); Id = $id; File = '..\ServerAlyzer_1.1.7.0_x64_300116d.msix'; Ok = $false }
        @{ Name = 'empty output';                         Raw = ''; Id = $id; Ok = $false }
        @{ Name = 'not JSON';                             Raw = 'error: could not reach the service'; Id = $id; Ok = $false }
        @{ Name = 'malformed JSON';                       Raw = '{"status": '; Id = $id; Ok = $false }
    )
    $failedNames = @()
    foreach ($case in $cases) {
        $file = if ($case.ContainsKey('File')) { $case.File } else { 'ServerAlyzer_1.1.7.0_x64_300116d.msix' }
        $result = Test-StagedSubmission -Raw $case.Raw -ExpectedPackageVersion '1.1.7.0' -ExpectedVisibility 'Public' -ExpectedSubmissionId $case.Id -ExpectedPackageFileName $file
        $ok = ($result.Ok -eq $case.Ok)
        if (-not $ok) { $failedNames += $case.Name }
        Write-Host ("  {0}  {1,-38} ok={2,-5}  {3}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $case.Name, $result.Ok, $result.Reason)
    }
    if ($failedNames.Count -gt 0) { throw "Staged submission cases failed: $($failedNames -join '; ')" }
    Write-Host "All $($cases.Count) staged-submission cases behaved as required."
    exit 0  # explicit success for the Actions wrapper (PR #53 B-1)
}

if (-not (Test-Path -LiteralPath $SubmissionPath -PathType Leaf)) { throw "Submission output not found: $SubmissionPath" }
$raw = Get-Content -LiteralPath $SubmissionPath -Raw
$result = Test-StagedSubmission -Raw $raw -ExpectedPackageVersion $ExpectedPackageVersion -ExpectedVisibility $ExpectedVisibility -ExpectedSubmissionId $ExpectedSubmissionId -ExpectedPackageFileName $ExpectedPackageFileName
if (-not $result.Ok) {
    throw @"
STOP: $($result.Reason).

Nothing has been committed. A draft is only ever committed when it is the uncommitted draft that was
staged, still on the publishing hold, with the audience unchanged and the verified package as its
newest package. Look at the submission in Partner Center (or run store-status) before trying again.
"@
}
Write-Host "  ok    $($result.Reason) (live packages: $($result.Versions -join ', '))"
if ($ListingReport) { Write-ListingReport (ConvertFrom-CliJson $raw) $StoreListingRoot }
if ($env:GITHUB_OUTPUT) { "submissionId=$($result.Id)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
exit 0
