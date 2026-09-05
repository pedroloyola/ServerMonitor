<#
.SYNOPSIS
    Takes the submission JSON returned by 'msstore submission get', refuses to proceed
    unless the publishing hold is intact, injects the versioned release notes, and writes
    the JSON back out for 'msstore submission update'.

.DESCRIPTION
    Two things must be true before a submission is committed, and this script is the only
    place that decides them:

    1. targetPublishMode must be 'Manual'.
       'Manual' is the API spelling of "Don't publish this submission until I select
       Publish now". If the value is 'Immediate' or 'SpecificDate', committing would make
       a release go live without a human deciding. This script stops instead, and no
       switch is offered to override it: turning automatic publishing on is a product
       decision that belongs to a person, not to a workflow input.

    2. The listing changes must be exactly the release notes.
       Every other field of every listing is copied through untouched, and the script
       reports each field it wrote with its old and new length so a run can be audited.

    Visibility is asserted, never written: the product's audience is not this pipeline's
    business.

.PARAMETER SubmissionPath
    JSON produced by 'msstore submission get <productId>'.

.PARAMETER StoreListingRoot
    Directory holding one folder per language (for example release/store/pt-PT), each with
    a whats-new.txt.

.PARAMETER ExpectedPackageVersion
    The package version that must already be present in the submission, e.g. 1.1.2.0.
    Guards against updating metadata onto a draft that carries the wrong package.

.PARAMETER ExpectedVisibility
    Asserted, not written. Leave empty to skip the assertion.

.OUTPUTS
    The updated JSON at -OutputPath, and a human-readable change report on stdout.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SubmissionPath,
    [Parameter(Mandatory)] [string] $OutputPath,
    [string] $StoreListingRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path 'release/store'),
    [string] $ExpectedPackageVersion,
    [string] $ExpectedVisibility,
    [int] $MaxReleaseNotesLength = 1500
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$MANUAL_PUBLISH_MODE = 'Manual'

function Get-Member-CaseInsensitive {
    <# Partner Center JSON has been seen in both camelCase and PascalCase; match either. #>
    param([Parameter(Mandatory)] $Object, [Parameter(Mandatory)] [string] $Name)

    if ($null -eq $Object) { return $null }
    foreach ($property in $Object.PSObject.Properties) {
        if ($property.Name -ieq $Name) { return $property }
    }
    return $null
}

function Get-Value { param($Object, [string] $Name)
    $member = Get-Member-CaseInsensitive -Object $Object -Name $Name
    if ($null -eq $member) { return $null }
    return $member.Value
}

if (-not (Test-Path -LiteralPath $SubmissionPath)) { throw "Submission JSON not found: $SubmissionPath" }
$raw = Get-Content -LiteralPath $SubmissionPath -Raw
if ([string]::IsNullOrWhiteSpace($raw)) { throw "Submission JSON at $SubmissionPath is empty. Refusing to guess its contents." }

try { $submission = $raw | ConvertFrom-Json }
catch { throw "Submission JSON at $SubmissionPath could not be parsed: $($_.Exception.Message)" }

Write-Host 'Store submission update'
Write-Host "  submission json: $SubmissionPath"
Write-Host "  listing root:    $StoreListingRoot"

# ------------------------------------------------------- 1. the publishing hold
$publishModeMember = Get-Member-CaseInsensitive -Object $submission -Name 'targetPublishMode'
if ($null -eq $publishModeMember) {
    throw @'
STOP: the submission JSON has no targetPublishMode field.

Without it this workflow cannot prove that "Don't publish this submission until I select
Publish now" is still in force, and it will not commit a submission it cannot vouch for.
Inspect the submission in Partner Center and re-run once the field is present.
'@
}

$publishMode = $publishModeMember.Value
if ($publishMode -ne $MANUAL_PUBLISH_MODE) {
    throw @"
STOP: targetPublishMode is '$publishMode', not '$MANUAL_PUBLISH_MODE'.

'$MANUAL_PUBLISH_MODE' is the API spelling of "Don't publish this submission until I select
Publish now". With '$publishMode' the release would go live on its own once certification
passes, which no one has authorised.

This is deliberately not overridable from the workflow. If automatic publishing is really
wanted, a person must change it in Partner Center and say so.
"@
}
Write-Host "  ok    targetPublishMode = $publishMode (publishing hold intact)"

# ------------------------------------------------------------- 2. visibility
$visibility = Get-Value -Object $submission -Name 'visibility'
if ($ExpectedVisibility) {
    if ($visibility -ne $ExpectedVisibility) {
        throw "STOP: visibility is '$visibility' but '$ExpectedVisibility' was expected. This pipeline never changes the audience; a mismatch means something else did."
    }
    Write-Host "  ok    visibility = $visibility (unchanged, asserted only)"
}
else {
    Write-Host "  info  visibility = $visibility (not asserted)"
}

# --------------------------------------------------------- 3. package version
if ($ExpectedPackageVersion) {
    $packages = Get-Value -Object $submission -Name 'applicationPackages'
    if ($null -eq $packages) { throw 'STOP: the submission JSON has no applicationPackages array.' }

    $versions = @()
    foreach ($package in @($packages)) {
        $status = Get-Value -Object $package -Name 'fileStatus'
        if ($status -ieq 'PendingDelete') { continue }
        $version = Get-Value -Object $package -Name 'version'
        if ($version) { $versions += $version }
    }

    if ($versions -notcontains $ExpectedPackageVersion) {
        throw "STOP: the submission carries package version(s) [$($versions -join ', ')] but $ExpectedPackageVersion was expected. The wrong package would be certified."
    }
    Write-Host "  ok    submission carries package version $ExpectedPackageVersion (all: $($versions -join ', '))"
}

# ------------------------------------------------------------ 4. release notes
if (-not (Test-Path -LiteralPath $StoreListingRoot)) { throw "Store listing root not found: $StoreListingRoot" }

$listings = Get-Value -Object $submission -Name 'listings'
if ($null -eq $listings) { throw 'STOP: the submission JSON has no listings object.' }

$listingKeys = @($listings.PSObject.Properties.Name)
$languageDirs = @(Get-ChildItem -LiteralPath $StoreListingRoot -Directory | Sort-Object Name)
if ($languageDirs.Count -eq 0) { throw "No language folders found under $StoreListingRoot." }

$changes = New-Object System.Collections.Generic.List[object]
$touchedKeys = New-Object System.Collections.Generic.List[string]

foreach ($dir in $languageDirs) {
    $notesPath = Join-Path $dir.FullName 'whats-new.txt'
    if (-not (Test-Path -LiteralPath $notesPath)) { throw "STOP: $($dir.Name) has no whats-new.txt. Refusing to publish a language with no release notes." }

    $notes = (Get-Content -LiteralPath $notesPath -Raw) -replace "`r`n", "`n"
    $notes = $notes.TrimEnd()
    if ([string]::IsNullOrWhiteSpace($notes)) { throw "STOP: $notesPath is empty." }
    if ($notes.Length -gt $MaxReleaseNotesLength) {
        throw "STOP: $notesPath is $($notes.Length) characters; the Store limit is $MaxReleaseNotesLength. Partner Center would truncate or reject it."
    }

    $matchedKey = $listingKeys | Where-Object { $_ -ieq $dir.Name } | Select-Object -First 1
    if (-not $matchedKey) {
        throw "STOP: the submission has no listing for '$($dir.Name)'. Listings present: $($listingKeys -join ', '). Adding a new language is a product decision, not something this workflow does."
    }

    $listing = $listings.$matchedKey
    $baseListingMember = Get-Member-CaseInsensitive -Object $listing -Name 'baseListing'
    if ($null -eq $baseListingMember) { throw "STOP: listing '$matchedKey' has no baseListing object." }
    $baseListing = $baseListingMember.Value

    $releaseNotesMember = Get-Member-CaseInsensitive -Object $baseListing -Name 'releaseNotes'
    $previous = if ($null -eq $releaseNotesMember) { '' } else { [string] $releaseNotesMember.Value }

    if ($null -eq $releaseNotesMember) {
        $baseListing | Add-Member -NotePropertyName 'releaseNotes' -NotePropertyValue $notes -Force
    }
    else {
        $baseListing.($releaseNotesMember.Name) = $notes
    }

    $touchedKeys.Add($matchedKey) | Out-Null
    $changes.Add([pscustomobject] @{
        Language    = $matchedKey
        Source      = $notesPath
        OldLength   = $previous.Length
        NewLength   = $notes.Length
        Changed     = ($previous -ne $notes)
    }) | Out-Null
}

$untouched = @($listingKeys | Where-Object { $touchedKeys -notcontains $_ })

Write-Host ''
Write-Host 'Release notes written:'
$changes | Format-Table -AutoSize Language, OldLength, NewLength, Changed | Out-String -Width 200 | Write-Host
if ($untouched.Count -gt 0) { Write-Host "Listings left untouched: $($untouched -join ', ')" }

# ----------------------------------------------------------------- 5. write out
$json = $submission | ConvertTo-Json -Depth 100
Set-Content -LiteralPath $OutputPath -Value $json -Encoding UTF8
Write-Host ''
Write-Host "Updated submission written to $OutputPath ($((Get-Item -LiteralPath $OutputPath).Length) bytes)"

# Re-read what we just wrote and re-assert the hold, so the file that will actually be sent
# is the one that was checked.
$roundTrip = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
$roundTripMode = Get-Value -Object $roundTrip -Name 'targetPublishMode'
if ($roundTripMode -ne $MANUAL_PUBLISH_MODE) {
    throw "STOP: after serialisation targetPublishMode is '$roundTripMode'. The file that would be sent does not preserve the publishing hold."
}
Write-Host "  ok    re-read $OutputPath and targetPublishMode is still $roundTripMode"

if ($env:GITHUB_OUTPUT) {
    "targetPublishMode=$roundTripMode" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "languagesUpdated=$($touchedKeys -join ',')" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
