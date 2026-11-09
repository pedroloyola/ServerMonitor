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
      * the package store-submit uploaded, in the shape a real uncommitted draft has (STORE-PIPE-1):
        EXACTLY ONE applicationPackages entry with fileStatus PendingUpload, whose fileName is, byte for
        byte, ServerAlyzer_<version>_x64_<tag commit first 7>.msix. That name binds the draft to the tag
        commit G4 proved (Vigil P-1 L-1). msstore publish writes it as
        new ApplicationPackage { FileStatus = PendingUpload, FileName = file.Name } WITHOUT a version:
        Partner Center fills in 'version' only once the commit has been processed. So a version on that
        entry is optional, but if one is there it must be the expected one;
      * every other package either PendingDelete (msstore publish marks the published package so) or a
        live package with a readable version OLDER than the expected one. A newer one would be the one
        customers get, and a second one of the same version is not the file this run vouches for.
        The SVA-9 STAGE failure (run 42478961083) was the old rule, which required 'version' on the
        live package before the commit and so read the real draft as [].

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
        return & $fail "the product has no uncommitted draft (status '$(ConvertTo-LogSafe $status)', expected 'PendingCommit')"
    }

    $id = [string] (Get-ScalarField $submission 'id')
    if ($id -cnotmatch '\A[0-9]{1,30}\z') { return & $fail "the draft has no numeric submission id ('$(ConvertTo-LogSafe $id)')" }
    if ($ExpectedSubmissionId -and -not [string]::Equals($id, $ExpectedSubmissionId, [StringComparison]::Ordinal)) {
        return & $fail "the pending draft is submission $id, not the staged submission $ExpectedSubmissionId"
    }

    $mode = Get-ScalarField $submission 'targetPublishMode'
    if (-not ($mode -is [string] -and [string]::Equals($mode, 'Manual', [StringComparison]::OrdinalIgnoreCase))) {
        return & $fail "targetPublishMode is '$(ConvertTo-LogSafe $mode)', not 'Manual': the publishing hold is not in force"
    }

    $visibility = Get-ScalarField $submission 'visibility'
    if (-not ($visibility -is [string] -and [string]::Equals($visibility, $ExpectedVisibility, [StringComparison]::OrdinalIgnoreCase))) {
        return & $fail "visibility is '$(ConvertTo-LogSafe $visibility)', expected '$ExpectedVisibility'"
    }

    # Read where it is, not through Get-Field: the pipeline would hide whether it is an array (PR #56 Relay I-1).
    $packagesProperty = $submission.PSObject.Properties | Where-Object { $_.Name -ieq 'applicationPackages' } | Select-Object -First 1
    if ($null -eq $packagesProperty -or $null -eq $packagesProperty.Value) { return & $fail 'the draft has no applicationPackages' }
    if ($packagesProperty.Value -isnot [array]) { return & $fail 'applicationPackages is not a JSON array' }
    $packages = $packagesProperty.Value
    $expected = [version] $ExpectedPackageVersion
    $versions = @()
    $pendingUploads = 0
    foreach ($package in $packages) {
        if ($package -isnot [pscustomobject]) { return & $fail 'a package entry is not a JSON object' }
        # PR #56 Relay L-4: a fileStatus that is present but not one string ("[PendingDelete]") makes the package
        # unreadable. Read as "not PendingDelete" it would count a package being removed as the live one.
        $fileStatusProperty = if ($package -is [pscustomobject]) { $package.PSObject.Properties | Where-Object { $_.Name -ieq 'fileStatus' } | Select-Object -First 1 }
        if ($null -ne $fileStatusProperty -and $null -ne $fileStatusProperty.Value -and $fileStatusProperty.Value -isnot [string]) {
            return & $fail 'a package has an unreadable fileStatus (not a single string)'
        }
        $fileStatus = [string] (Get-ScalarField $package 'fileStatus')
        if ([string]::Equals($fileStatus, 'PendingDelete', [StringComparison]::OrdinalIgnoreCase)) { continue }
        # A JSON string only (PR #56 Vigil N-3): a one-element array holding the right name is refused.
        $fileName = Get-ScalarField $package 'fileName'
        $versionProperty = $package.PSObject.Properties | Where-Object { $_.Name -ieq 'version' } | Select-Object -First 1
        $version = Get-ScalarField $package 'version'
        if ($null -ne $versionProperty -and $null -ne $versionProperty.Value -and $version -isnot [string]) { return & $fail 'a package has an unreadable version (not a single string)' }
        $text = if ($version -is [string]) { $version } else { '' }

        # STORE-PIPE-1: the upload msstore publish staged. Before the commit Partner Center has not filled in its
        # version (SVA-9 run 42478961083), so the file name is what binds it to the verified package.
        if ([string]::Equals($fileStatus, 'PendingUpload', [StringComparison]::OrdinalIgnoreCase)) {
            $pendingUploads++
            if ($pendingUploads -gt 1) { return & $fail 'the draft has more than one package waiting for upload (PendingUpload)' }
            if (-not ($fileName -is [string] -and [string]::Equals($fileName, $ExpectedPackageFileName, [StringComparison]::Ordinal))) {
                return & $fail "the draft's pending upload is '$(ConvertTo-LogSafe $fileName)', not '$ExpectedPackageFileName' uploaded from the tag commit"
            }
            if ($text -ne '' -and -not [string]::Equals($text, $ExpectedPackageVersion, [StringComparison]::Ordinal)) {
                return & $fail "the draft's pending upload carries version '$(ConvertTo-LogSafe $text)', not $ExpectedPackageVersion"
            }
            continue
        }

        # Any other live package (Uploaded, None, or no fileStatus): readable, and older than the expected one.
        if ($text -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z') { return & $fail "a live package has an unreadable version ('$(ConvertTo-LogSafe $text)')" }
        $versions += $text
        if (([version] $text) -gt $expected) {
            return & $fail "the draft carries package $text, newer than $ExpectedPackageVersion; customers would get a package this run did not verify"
        }
        if (([version] $text) -eq $expected) {
            return & $fail "the draft carries a second live package of version $text ('$(ConvertTo-LogSafe $fileName)') besides the pending upload; it is not the file this run vouches for"
        }
    }
    if ($pendingUploads -eq 0) {
        return & $fail "the draft has no package waiting for upload (no PendingUpload entry; live package version(s) [$($versions -join ', ')]): it is not the draft msstore publish staged"
    }

    return [pscustomobject] @{ Ok = $true; Reason = "draft $id is uncommitted, on the publishing hold, visibility $visibility, pending upload $ExpectedPackageFileName for $ExpectedPackageVersion"; Id = $id; Versions = $versions }
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
            Language        = ConvertTo-LogSafe $property.Name
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
    # STORE-PIPE-1: the packages of a REAL uncommitted draft, as msstore publish --noCommit leaves it (msstore-cli
    # v0.4.3 PrepareBundleAsync, SVA-9 run 42478961083): the published package marked PendingDelete, and the new
    # upload as { FileStatus = PendingUpload, FileName } WITHOUT a version. The previous fixture gave the
    # PendingUpload entry a version, a shape that does not exist before the commit, so the old rule passed here
    # and failed live. Replace it with an anonymised real capture once one exists (fileUploadUrl removed first).
    $file = 'ServerAlyzer_1.1.8.0_x64_744297c.msix'
    function Get-RealPackages { @(
        [ordered] @{ fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; fileStatus = 'PendingDelete'; id = '1152921504000000001'; version = '1.1.7.0'; architecture = 'X64' }
        [ordered] @{ fileName = 'ServerAlyzer_1.1.8.0_x64_744297c.msix'; fileStatus = 'PendingUpload' }
    ) }
    function New-Upload([hashtable] $Change = @{}) {
        $upload = [ordered] @{ fileName = 'ServerAlyzer_1.1.8.0_x64_744297c.msix'; fileStatus = 'PendingUpload' }
        foreach ($key in $Change.Keys) { $upload[$key] = $Change[$key] }
        return $upload
    }
    $old = [ordered] @{ fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; fileStatus = 'PendingDelete'; version = '1.1.7.0' }
    function New-Draft([hashtable] $Change = @{}, [string[]] $Remove = @(), [object[]] $Packages = $null, [switch] $Pascal) {
        $draft = [ordered] @{
            id                  = '1152921505701819999'
            status              = 'PendingCommit'
            targetPublishMode   = 'Manual'
            visibility          = 'Public'
            applicationPackages = (Get-RealPackages)
            fileUploadUrl       = 'https://example.invalid/upload?sig=REDACTED'
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
    $pascalPackages = @(
        [ordered] @{ FileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; FileStatus = 'PendingDelete'; Version = '1.1.7.0' }
        [ordered] @{ FileName = 'ServerAlyzer_1.1.8.0_x64_744297c.msix'; FileStatus = 'PendingUpload' }
    )
    $id = '1152921505701819999'
    $cases = @(
        # ---- STORE-PIPE-1: the real draft shape and the rule around it (report sva-store-stage-failure-relay.md section 2.3)
        @{ Name = 'real draft shape (no version on the upload)'; Raw = (New-Draft); Id = '';  Ok = $true }
        @{ Name = 'real draft shape, id bound (certify)';   Raw = (New-Draft); Id = $id; Ok = $true }
        @{ Name = 'real draft shape, PascalCase';            Raw = (New-Draft -Pascal -Packages $pascalPackages); Id = $id; Ok = $true }
        @{ Name = 'upload carries the expected version';    Raw = (New-Draft -Packages @($old, (New-Upload @{ version = '1.1.8.0' }))); Id = $id; Ok = $true }
        @{ Name = 'upload carries an empty version';        Raw = (New-Draft -Packages @($old, (New-Upload @{ version = '' }))); Id = $id; Ok = $true }
        @{ Name = 'upload carries another version';         Raw = (New-Draft -Packages @($old, (New-Upload @{ version = '1.1.9.0' }))); Id = $id; Ok = $false }
        @{ Name = 'upload version as a one-element array';  Raw = (New-Draft -Packages @($old, (New-Upload @{ version = @('1.1.8.0') }))); Id = $id; Ok = $false }
        @{ Name = 'upload of another commit (sha7)';        Raw = (New-Draft -Packages @($old, (New-Upload @{ fileName = 'ServerAlyzer_1.1.8.0_x64_deadbee.msix' }))); Id = $id; Ok = $false }
        @{ Name = 'upload of another version (name)';       Raw = (New-Draft -Packages @($old, (New-Upload @{ fileName = 'ServerAlyzer_1.1.7.0_x64_744297c.msix' }))); Id = $id; Ok = $false }
        @{ Name = 'two pending uploads';                    Raw = (New-Draft -Packages @($old, (New-Upload), (New-Upload))); Id = $id; Ok = $false }
        @{ Name = 'untouched clone: no pending upload';     Raw = (New-Draft -Packages @([ordered] @{ fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; fileStatus = 'Uploaded'; version = '1.1.7.0' })); Id = $id; Ok = $false }
        @{ Name = 'old package still Uploaded (older)';     Raw = (New-Draft -Packages @([ordered] @{ fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'; fileStatus = 'Uploaded'; version = '1.1.7.0' }, (New-Upload))); Id = $id; Ok = $true }
        @{ Name = 'newer live package in the draft';        Raw = (New-Draft -Packages @([ordered] @{ fileName = 'ServerAlyzer_1.1.9.0_x64_abcdef0.msix'; fileStatus = 'Uploaded'; version = '1.1.9.0' }, (New-Upload))); Id = $id; Ok = $false }
        @{ Name = 'newer package marked PendingDelete';     Raw = (New-Draft -Packages @((New-Upload), [ordered] @{ version = '1.1.9.0'; fileStatus = 'PendingDelete' })); Id = $id; Ok = $true }
        @{ Name = 'expected package only PendingDelete';    Raw = (New-Draft -Packages @((New-Upload @{ fileStatus = 'PendingDelete'; version = '1.1.8.0' }))); Id = $id; Ok = $false }
        @{ Name = 'second live package, same version';      Raw = (New-Draft -Packages @((New-Upload), [ordered] @{ version = '1.1.8.0'; fileName = 'manual-upload.msix'; fileStatus = 'Uploaded' })); Id = $id; Ok = $false }
        @{ Name = 'old package without a fileStatus';       Raw = (New-Draft -Packages @([ordered] @{ version = '1.1.7.0'; fileName = 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix' }, (New-Upload))); Id = $id; Ok = $true }
        @{ Name = 'unreadable package version';             Raw = (New-Draft -Packages @((New-Upload), [ordered] @{ version = 'v2'; fileStatus = 'Uploaded' })); Id = $id; Ok = $false }
        @{ Name = 'live package without a version';         Raw = (New-Draft -Packages @((New-Upload), [ordered] @{ fileName = 'x.msix'; fileStatus = 'Uploaded' })); Id = $id; Ok = $false }
        @{ Name = 'package file name missing';              Raw = (New-Draft -Packages @($old, [ordered] @{ fileStatus = 'PendingUpload' })); Id = $id; Ok = $false }
        @{ Name = 'package file name as a one-element array'; Raw = (New-Draft -Packages @($old, (New-Upload @{ fileName = @('ServerAlyzer_1.1.8.0_x64_744297c.msix') }))); Id = $id; Ok = $false }
        @{ Name = 'package file name in other case';        Raw = (New-Draft -Packages @($old, (New-Upload @{ fileName = 'ServerAlyzer_1.1.8.0_X64_744297C.msix' }))); Id = $id; Ok = $false }
        @{ Name = 'package fileStatus as a one-element array'; Raw = (New-Draft -Packages @((New-Upload), [ordered] @{ version = '1.1.7.0'; fileStatus = @('PendingDelete') })); Id = $id; Ok = $false }
        @{ Name = 'package fileStatus as a number';         Raw = (New-Draft -Packages @((New-Upload), [ordered] @{ version = '1.1.7.0'; fileStatus = 3 })); Id = $id; Ok = $false }
        @{ Name = 'pending upload status as an array';      Raw = (New-Draft -Packages @($old, (New-Upload @{ fileStatus = @('PendingUpload') }))); Id = $id; Ok = $false }
        @{ Name = 'package entry not an object';            Raw = (New-Draft -Packages @((New-Upload), 'ServerAlyzer_1.1.9.0_x64_abcdef0.msix')); Id = $id; Ok = $false }
        @{ Name = 'applicationPackages as a single object'; Raw = (New-Draft @{ applicationPackages = (New-Upload) }); Id = $id; Ok = $false }
        @{ Name = 'no packages field';                      Raw = (New-Draft -Remove 'applicationPackages'); Id = $id; Ok = $false }
        @{ Name = 'expected file name malformed';           Raw = (New-Draft); Id = $id; File = "..\$file"; Ok = $false }
        # ---- the draft itself
        @{ Name = 'JSON wrapped in CLI chatter';            Raw = "Retrieving Submission`n$(New-Draft)`nDone."; Id = $id; Ok = $true }
        @{ Name = 'manual (case) hold';                     Raw = (New-Draft @{ targetPublishMode = 'manual' }); Id = $id; Ok = $true }
        @{ Name = 'no draft: last published returned';      Raw = (New-Draft @{ status = 'Published' }); Id = $id; Ok = $false }
        @{ Name = 'already committed';                      Raw = (New-Draft @{ status = 'CommitStarted' }); Id = $id; Ok = $false }
        @{ Name = 'in certification';                       Raw = (New-Draft @{ status = 'Certification' }); Id = $id; Ok = $false }
        @{ Name = 'commit failed draft';                    Raw = (New-Draft @{ status = 'CommitFailed' }); Id = $id; Ok = $false }
        @{ Name = 'status in other case';                   Raw = (New-Draft @{ status = 'pendingcommit' }); Id = $id; Ok = $false }
        @{ Name = 'status missing';                         Raw = (New-Draft -Remove 'status'); Id = $id; Ok = $false }
        @{ Name = 'another draft (recreated)';              Raw = (New-Draft @{ id = '1152921505701810000' }); Id = $id; Ok = $false }
        @{ Name = 'id prefix of the staged id';             Raw = (New-Draft @{ id = '115292150570181999' }); Id = $id; Ok = $false }
        @{ Name = 'id missing';                             Raw = (New-Draft -Remove 'id'); Id = ''; Ok = $false }
        @{ Name = 'id not numeric';                         Raw = (New-Draft @{ id = 'abc' }); Id = ''; Ok = $false }
        @{ Name = 'expected id not numeric';                Raw = (New-Draft); Id = "$id "; Ok = $false }
        @{ Name = 'hold lost: Immediate';                   Raw = (New-Draft @{ targetPublishMode = 'Immediate' }); Id = $id; Ok = $false }
        @{ Name = 'hold lost: SpecificDate';                Raw = (New-Draft @{ targetPublishMode = 'SpecificDate' }); Id = $id; Ok = $false }
        @{ Name = 'hold missing';                           Raw = (New-Draft -Remove 'targetPublishMode'); Id = $id; Ok = $false }
        @{ Name = 'hold with trailing space';               Raw = (New-Draft @{ targetPublishMode = 'Manual ' }); Id = $id; Ok = $false }
        @{ Name = 'visibility changed';                     Raw = (New-Draft @{ visibility = 'Private' }); Id = $id; Ok = $false }
        @{ Name = 'visibility missing';                     Raw = (New-Draft -Remove 'visibility'); Id = $id; Ok = $false }
        @{ Name = 'hold as a one-element array';            Raw = (New-Draft @{ targetPublishMode = @('Manual') }); Id = $id; Ok = $false }
        @{ Name = 'visibility as a one-element array';      Raw = (New-Draft @{ visibility = @('Public') }); Id = $id; Ok = $false }
        @{ Name = 'status as a one-element array';          Raw = (New-Draft @{ status = @('PendingCommit') }); Id = $id; Ok = $false }
        @{ Name = 'id as a one-element array';              Raw = (New-Draft @{ id = @('1152921505701819999') }); Id = $id; Ok = $false }
        @{ Name = 'id as a JSON number';                    Raw = ((New-Draft) -replace '"id":\s*"1152921505701819999"', '"id": 1152921505701819999'); Id = $id; Ok = $true }
        @{ Name = 'empty output';                           Raw = ''; Id = $id; Ok = $false }
        @{ Name = 'not JSON';                               Raw = 'error: could not reach the service'; Id = $id; Ok = $false }
        @{ Name = 'malformed JSON';                         Raw = '{"status": '; Id = $id; Ok = $false }
    )
    $failedNames = @()
    foreach ($case in $cases) {
        $expectedFile = if ($case.ContainsKey('File')) { $case.File } else { $file }
        $result = Test-StagedSubmission -Raw $case.Raw -ExpectedPackageVersion '1.1.8.0' -ExpectedVisibility 'Public' -ExpectedSubmissionId $case.Id -ExpectedPackageFileName $expectedFile
        $ok = ($result.Ok -eq $case.Ok)
        if (-not $ok) { $failedNames += $case.Name }
        Write-Host ("  {0}  {1,-44} ok={2,-5}  {3}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $case.Name, $result.Ok, $result.Reason)
    }

    # Regression of the SVA-9 incident, kept as the reason for the change: the rule before STORE-PIPE-1 read the
    # version of every package that was not PendingDelete, so on the real draft it found only the version-less
    # upload and refused it as "a live package has an unreadable version ('')".
    $legacyVersions = @()
    $legacyRefused = $false
    foreach ($package in ((New-Draft) | ConvertFrom-Json).applicationPackages) {
        if ([string] $package.fileStatus -ieq 'PendingDelete') { continue }
        $legacyText = if ($package.PSObject.Properties['version'] -and $package.version -is [string]) { $package.version } else { '' }
        if ($legacyText -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z') { $legacyRefused = $true; break }
        $legacyVersions += $legacyText
    }
    if (-not $legacyRefused) { $failedNames += 'regression: the old rule no longer refuses the real draft shape (fixture drifted)' }
    else { Write-Host "  ok    regression: the old rule refuses the real draft shape (version '' on the upload), as in SVA-9" }

    # STORE-PIPE-1 round 2 (Vigil L-1, L-4): the MAIN path, as the workflows call it, with -ListingReport, the step
    # summary and the step output. The SAS upload URL never reaches any of them, and a Partner Center value holding
    # a newline and "::warning::" never starts a line (a workflow command).
    $injected = "`n::warning::injected"
    $mainCases = @(
        @{ Name = 'main path: passing draft, SAS and injected names'; Ok = $true; Raw = (New-Draft -Packages @(
            [ordered] @{ fileName = "old.msix$injected"; fileStatus = 'PendingDelete'; version = '1.1.7.0' }, (New-Upload)) `
            -Change @{ listings = [ordered] @{ 'en-us' = [ordered] @{ baseListing = [ordered] @{ description = 'd'; releaseNotes = 'n' } }; "zz$injected" = [ordered] @{ baseListing = [ordered] @{ releaseNotes = 'x' } } } }) }
        @{ Name = 'main path: refused draft, injected upload name'; Ok = $false; Raw = (New-Draft -Packages @($old, (New-Upload @{ fileName = "$file$injected" }))) }
        @{ Name = 'main path: refused draft, injected status'; Ok = $false; Raw = (New-Draft @{ status = "PendingCommit$injected" }) }
    )
    $mainDir = Join-Path ([IO.Path]::GetTempPath()) ('staged-main-' + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $mainDir | Out-Null
    $savedSummary = $env:GITHUB_STEP_SUMMARY; $savedOutput = $env:GITHUB_OUTPUT
    try {
        foreach ($case in $mainCases) {
            $draftPath = Join-Path $mainDir 'draft.json'
            Set-Content -LiteralPath $draftPath -Value $case.Raw -Encoding utf8
            $env:GITHUB_STEP_SUMMARY = Join-Path $mainDir 'summary.md'; $env:GITHUB_OUTPUT = Join-Path $mainDir 'output.txt'
            Set-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ''; Set-Content -LiteralPath $env:GITHUB_OUTPUT -Value ''
            $passed = $true; $text = ''
            try { $text = & $PSCommandPath -SubmissionPath $draftPath -ExpectedPackageVersion '1.1.8.0' -ExpectedVisibility 'Public' -ExpectedPackageFileName $file -ListingReport -StoreListingRoot $mainDir *>&1 | Out-String }
            catch { $passed = $false; $text += "`n" + $_.Exception.Message }
            $all = $text + "`n" + (Get-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Raw) + "`n" + (Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw)
            $command = @($all -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('::') })
            $problems = @()
            if ($passed -ne $case.Ok) { $problems += "ok=$passed" }
            if ($all.Contains('example.invalid') -or $all.Contains('sig=')) { $problems += 'the SAS upload URL was printed' }
            if ($command.Count) { $problems += "a line starts with '::' ($($command[0].Trim()))" }
            if ($case.Ok -and -not $text.Contains("$file / PendingUpload / -")) { $problems += 'the package report was not captured' }
            if ($case.Ok -and -not $text.Contains('<unprintable> / PendingDelete / 1.1.7.0')) { $problems += 'the injected old package name was not shown as <unprintable>' }
            if ($problems.Count) { $failedNames += "$($case.Name) ($($problems -join '; '))" }
            Write-Host ("  {0}  {1,-44} {2}" -f $(if ($problems.Count) { 'FAIL' } else { 'ok  ' }), $case.Name, $(if ($problems.Count) { $problems -join '; ' } else { 'no SAS, no workflow command' }))
        }
    }
    finally {
        $env:GITHUB_STEP_SUMMARY = $savedSummary; $env:GITHUB_OUTPUT = $savedOutput
        Remove-Item -LiteralPath $mainDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($failedNames.Count -gt 0) { throw "Staged submission cases failed: $($failedNames -join '; ')" }
    Write-Host "All $($cases.Count) staged-submission cases, $($mainCases.Count) main-path cases and the SVA-9 regression behaved as required."
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
one pending upload and nothing newer. Look at the submission in Partner Center (or run store-status) before trying again.
"@
}
Write-Host "  ok    $($result.Reason) (other live packages: $($result.Versions -join ', '))"
# STORE-PIPE-1: the draft's packages, by name, so a person can check the staged file in this log (the
# Partner Center UI shows the uncommitted draft as "no changes" with the old package). Only these three
# fields are printed: the submission also holds fileUploadUrl, a SAS URI with write access, which must
# never reach a log.
Write-Host '  packages in the draft (fileName / fileStatus / version):'
foreach ($package in @((ConvertFrom-CliJson $raw).PSObject.Properties | Where-Object { $_.Name -ieq 'applicationPackages' } | Select-Object -First 1).Value) {
    $shown = foreach ($field in @('fileName', 'fileStatus', 'version')) { $value = Get-ScalarField $package $field; if ($null -eq $value -or "$value" -eq '') { '-' } else { ConvertTo-LogSafe $value } }
    Write-Host "    $($shown -join ' / ')"
}
if ($ListingReport) { Write-ListingReport (ConvertFrom-CliJson $raw) $StoreListingRoot }
if ($env:GITHUB_OUTPUT) { "submissionId=$($result.Id)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
exit 0
