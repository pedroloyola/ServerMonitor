<#
.SYNOPSIS
    Proves that the release-build run store-submit is about to consume is the one the tag claims (G4).

.DESCRIPTION
    store-submit downloads a package by run id. Before REL.0C nothing tied that run id to a release:
    any run of any workflow in this repository that uploaded an artifact with the right name, on any
    commit, would have been accepted, and the SHA-256 typed by the operator only proved that the
    bytes were the ones that run produced.

    This check reads JSON that the workflow fetched read-only from the GitHub REST API with the job's
    GITHUB_TOKEN (no Store secret is involved) and fails closed unless ALL of these hold:

      * the run is from this repository, not a fork (repository and head_repository);
      * the run is .github/workflows/release-build.yml, completed with conclusion success;
      * the run was started by the push of the requested tag (event push, head_branch == tag);
      * the tag ref exists, and dereferenced (annotated tags are objects) points at a commit;
      * the run built exactly that commit (head_sha == tag commit);
      * the tag commit is contained in main (compare tag...main: identical or ahead, behind_by 0);
      * the tag is vMAJOR.MINOR.PATCH and the package version is MAJOR.MINOR.PATCH.0.

    Runs started by workflow_dispatch are refused on purpose: their head_sha is the commit of the ref
    the dispatch ran on, not the tag they checked out, so the API cannot bind them to the tag.

    On success it prints the tag commit, and writes tagCommit=<sha> to GITHUB_OUTPUT when present.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RunPath,
    [Parameter(Mandatory)] [string] $TagRefPath,
    [string] $TagObjectPath,
    [Parameter(Mandatory)] [string] $ComparePath,
    [Parameter(Mandatory)] [AllowEmptyString()] [string] $Repository,
    [Parameter(Mandatory)] [AllowEmptyString()] [string] $Tag,
    [Parameter(Mandatory)] [AllowEmptyString()] [string] $PackageVersion,
    [string] $WorkflowPath = '.github/workflows/release-build.yml'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Json([string] $Path, [string] $What) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Provenance: $What is missing." }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { throw "Provenance: $What is not valid JSON." }
}

function Get-Field($Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

$sha = '\A[0-9a-f]{40}\z'

# ------------------------------------------------------------------ inputs
if ($Repository -cnotmatch '\A[A-Za-z0-9-]+/[A-Za-z0-9._-]+\z') { throw 'Provenance: invalid repository name.' }
if ($Tag -cnotmatch '\Av(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z') { throw 'Provenance: the tag is not vMAJOR.MINOR.PATCH.' }
$expectedPackage = $Tag.Substring(1) + '.0'
if ($PackageVersion -cne $expectedPackage) { throw "Provenance: package version $PackageVersion does not match tag $Tag (expected $expectedPackage)." }

# --------------------------------------------------------------- the tag
$tagRef = Read-Json $TagRefPath 'the tag ref'
if ((Get-Field $tagRef 'ref') -cne "refs/tags/$Tag") { throw "Provenance: the tag ref is not refs/tags/$Tag." }
$object = Get-Field $tagRef 'object'
$objectType = [string] (Get-Field $object 'type')
$objectSha = [string] (Get-Field $object 'sha')
if ($objectSha -cnotmatch $sha) { throw 'Provenance: the tag ref object sha is malformed.' }

if ($objectType -ceq 'commit') {
    $tagCommit = $objectSha
}
elseif ($objectType -ceq 'tag') {
    # Annotated tag: one more object to read. Nested tags (a tag of a tag) are refused.
    $tagObject = Read-Json $TagObjectPath 'the annotated tag object'
    if ((Get-Field $tagObject 'sha') -cne $objectSha) { throw 'Provenance: the annotated tag object is not the one the ref points at.' }
    if ((Get-Field $tagObject 'tag') -cne $Tag) { throw 'Provenance: the annotated tag object names a different tag.' }
    $target = Get-Field $tagObject 'object'
    if ((Get-Field $target 'type') -cne 'commit') { throw 'Provenance: the annotated tag does not point at a commit.' }
    $tagCommit = [string] (Get-Field $target 'sha')
    if ($tagCommit -cnotmatch $sha) { throw 'Provenance: the annotated tag target sha is malformed.' }
}
else { throw "Provenance: the tag ref points at an unsupported object type '$objectType'." }

# --------------------------------------------------------------- the run
$run = Read-Json $RunPath 'the workflow run'
$repositoryName = [string] (Get-Field (Get-Field $run 'repository') 'full_name')
$headRepositoryName = [string] (Get-Field (Get-Field $run 'head_repository') 'full_name')
if ($repositoryName -cne $Repository) { throw 'Provenance: the run belongs to another repository.' }
if ($headRepositoryName -cne $Repository) { throw 'Provenance: the run was built from another repository (fork).' }
if ((Get-Field $run 'path') -cne $WorkflowPath) { throw "Provenance: the run is not $WorkflowPath." }
if ((Get-Field $run 'status') -cne 'completed') { throw 'Provenance: the run has not completed.' }
if ((Get-Field $run 'conclusion') -cne 'success') { throw 'Provenance: the run did not succeed.' }
if ((Get-Field $run 'event') -cne 'push') { throw 'Provenance: only a run started by the tag push can be submitted (dispatch runs cannot be bound to the tag).' }
if ((Get-Field $run 'head_branch') -cne $Tag) { throw "Provenance: the run was not started by the push of $Tag." }
if ((Get-Field $run 'head_sha') -cne $tagCommit) { throw "Provenance: the run built a different commit than $Tag points at." }

# --------------------------------------------------- the tag is on main
$compare = Read-Json $ComparePath 'the tag...main comparison'
$status = [string] (Get-Field $compare 'status')
$behind = Get-Field $compare 'behind_by'
$behindIsZero = ($behind -is [int] -or $behind -is [long]) -and $behind -eq 0
if (($status -cnotin @('identical', 'ahead')) -or -not $behindIsZero) {
    throw 'Provenance: the tag commit is not contained in main.'
}

Write-Host "RELEASE RUN PROVENANCE PASSED: release-build push run for $Tag on main, commit $tagCommit, package $PackageVersion"
if ($env:GITHUB_OUTPUT) { "tagCommit=$tagCommit" | Out-File -FilePath $env:GITHUB_OUTPUT -Append }
