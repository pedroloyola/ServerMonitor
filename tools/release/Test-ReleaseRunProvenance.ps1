<# Synthetic API JSON only: no network, no real run, tag or repository is read. #>
[CmdletBinding()]
param([string] $CheckScript = (Join-Path $PSScriptRoot 'Assert-ReleaseRunProvenance.ps1'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $CheckScript -PathType Leaf)) { throw 'Guard script missing.' }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('run-provenance-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
$savedOutput = $env:GITHUB_OUTPUT
$env:GITHUB_OUTPUT = Join-Path $temp 'outputs.txt'

$repo = 'owner/ServerMonitor'
$commit = 'a' * 40
$tagSha = 'b' * 40

function New-Fixture {
    @{
        Run = [ordered]@{
            id = 1; path = '.github/workflows/release-build.yml'; event = 'push'; head_branch = 'v1.1.7'
            head_sha = $commit; status = 'completed'; conclusion = 'success'
            repository = @{ full_name = $repo }; head_repository = @{ full_name = $repo }
        }
        TagRef = [ordered]@{ ref = 'refs/tags/v1.1.7'; object = [ordered]@{ type = 'tag'; sha = $tagSha } }
        TagObject = [ordered]@{ sha = $tagSha; tag = 'v1.1.7'; object = [ordered]@{ type = 'commit'; sha = $commit } }
        Compare = [ordered]@{ status = 'ahead'; behind_by = 0; ahead_by = 3 }
        Tag = 'v1.1.7'; Version = '1.1.7.0'; Repository = $repo
    }
}

# Each case mutates a fresh valid fixture; Pass says whether the guard must accept it.
$cases = @(
    @{ Name = 'baseline annotated tag'; Pass = $true; Edit = {} }
    @{ Name = 'lightweight tag'; Pass = $true; Edit = { param($f) $f.TagRef.object = [ordered]@{ type = 'commit'; sha = $commit }; $f.Remove('TagObject') } }
    @{ Name = 'main identical to tag'; Pass = $true; Edit = { param($f) $f.Compare.status = 'identical' } }
    @{ Name = 'other workflow'; Pass = $false; Edit = { param($f) $f.Run.path = '.github/workflows/ci.yml' } }
    @{ Name = 'workflow path with ref suffix'; Pass = $false; Edit = { param($f) $f.Run.path = '.github/workflows/release-build.yml@refs/heads/x' } }
    @{ Name = 'run failed'; Pass = $false; Edit = { param($f) $f.Run.conclusion = 'failure' } }
    @{ Name = 'run cancelled'; Pass = $false; Edit = { param($f) $f.Run.conclusion = 'cancelled' } }
    @{ Name = 'run in progress'; Pass = $false; Edit = { param($f) $f.Run.status = 'in_progress'; $f.Run.conclusion = $null } }
    @{ Name = 'dispatch run'; Pass = $false; Edit = { param($f) $f.Run.event = 'workflow_dispatch'; $f.Run.head_branch = 'main' } }
    @{ Name = 'dispatch run on the tag ref'; Pass = $false; Edit = { param($f) $f.Run.event = 'workflow_dispatch' } }
    @{ Name = 'pull request run'; Pass = $false; Edit = { param($f) $f.Run.event = 'pull_request' } }
    @{ Name = 'other tag push'; Pass = $false; Edit = { param($f) $f.Run.head_branch = 'v1.1.8' } }
    @{ Name = 'head sha mismatch'; Pass = $false; Edit = { param($f) $f.Run.head_sha = 'c' * 40 } }
    @{ Name = 'head sha is the tag object'; Pass = $false; Edit = { param($f) $f.Run.head_sha = $tagSha } }
    @{ Name = 'head sha empty'; Pass = $false; Edit = { param($f) $f.Run.head_sha = '' } }
    @{ Name = 'head sha short prefix'; Pass = $false; Edit = { param($f) $f.Run.head_sha = 'aaaaaaa' } }
    @{ Name = 'head sha uppercase'; Pass = $false; Edit = { param($f) $f.Run.head_sha = 'A' * 40 } }
    @{ Name = 'completed status missing (success reported)'; Pass = $false; Edit = { param($f) $f.Run.status = 'in_progress' } }
    @{ Name = 'tag with trailing newline (consistent everywhere)'; Pass = $false; Edit = {
        param($f)
        $t = "v1.1.7`n"
        $f.Tag = $t; $f.Version = "1.1.7`n.0"; $f.Run.head_branch = $t; $f.TagRef.ref = "refs/tags/$t"; $f.TagObject.tag = $t
    } }
    @{ Name = 'fork head repository'; Pass = $false; Edit = { param($f) $f.Run.head_repository = @{ full_name = 'evil/ServerMonitor' } } }
    @{ Name = 'other repository'; Pass = $false; Edit = { param($f) $f.Run.repository = @{ full_name = 'evil/ServerMonitor' } } }
    @{ Name = 'missing head repository'; Pass = $false; Edit = { param($f) $f.Run.Remove('head_repository') } }
    @{ Name = 'tag ref for another tag'; Pass = $false; Edit = { param($f) $f.TagRef.ref = 'refs/tags/v1.1.8' } }
    @{ Name = 'annotated object missing'; Pass = $false; Edit = { param($f) $f.Remove('TagObject') } }
    @{ Name = 'annotated object names another tag'; Pass = $false; Edit = { param($f) $f.TagObject.tag = 'v9.9.9' } }
    @{ Name = 'annotated object is another object'; Pass = $false; Edit = { param($f) $f.TagObject.sha = 'd' * 40 } }
    @{ Name = 'nested tag'; Pass = $false; Edit = { param($f) $f.TagObject.object.type = 'tag' } }
    @{ Name = 'tag of a tree'; Pass = $false; Edit = { param($f) $f.TagRef.object.type = 'tree' } }
    @{ Name = 'malformed tag sha'; Pass = $false; Edit = { param($f) $f.TagRef.object.sha = 'xyz' } }
    @{ Name = 'tag not on main (diverged)'; Pass = $false; Edit = { param($f) $f.Compare.status = 'diverged'; $f.Compare.behind_by = 2 } }
    @{ Name = 'tag not on main (behind)'; Pass = $false; Edit = { param($f) $f.Compare.status = 'behind'; $f.Compare.behind_by = 1 } }
    @{ Name = 'compare says ahead but behind_by > 0'; Pass = $false; Edit = { param($f) $f.Compare.behind_by = 1 } }
    @{ Name = 'compare without behind_by'; Pass = $false; Edit = { param($f) $f.Compare.Remove('behind_by') } }
    @{ Name = 'package version mismatch'; Pass = $false; Edit = { param($f) $f.Version = '1.1.8.0' } }
    @{ Name = 'package revision not zero'; Pass = $false; Edit = { param($f) $f.Version = '1.1.7.1' } }
    @{ Name = 'tag not a release tag'; Pass = $false; Edit = { param($f) $f.Tag = 'v1.1.7-rc1' } }
    @{ Name = 'tag with leading zero'; Pass = $false; Edit = { param($f) $f.Tag = 'v1.01.7' } }
    @{ Name = 'empty repository'; Pass = $false; Edit = { param($f) $f.Repository = '' } }
    @{ Name = 'invalid run JSON'; Pass = $false; Edit = { param($f) $f.RunRaw = '{ not json' } }
    @{ Name = 'missing run file'; Pass = $false; Edit = { param($f) $f.RunMissing = $true } }
)

$failed = @()
try {
    $index = 0
    foreach ($case in $cases) {
        $index++
        $fixture = New-Fixture
        & $case.Edit $fixture
        $dir = Join-Path $temp "case$index"
        New-Item -ItemType Directory -Path $dir | Out-Null
        $runPath = Join-Path $dir 'run.json'
        if ($fixture.ContainsKey('RunRaw')) { Set-Content -LiteralPath $runPath -Value $fixture.RunRaw }
        elseif (-not $fixture.ContainsKey('RunMissing')) { $fixture.Run | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $runPath }
        $fixture.TagRef | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dir 'tagref.json')
        $tagObjectPath = Join-Path $dir 'tagobject.json'
        if ($fixture.ContainsKey('TagObject')) { $fixture.TagObject | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $tagObjectPath }
        $fixture.Compare | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dir 'compare.json')

        $accepted = $true
        try {
            & $CheckScript -RunPath $runPath -TagRefPath (Join-Path $dir 'tagref.json') -TagObjectPath $tagObjectPath `
                -ComparePath (Join-Path $dir 'compare.json') -Repository $fixture.Repository -Tag $fixture.Tag `
                -PackageVersion $fixture.Version | Out-Null
        }
        catch {
            # Only the guard's own refusal counts; a parameter, parser or fixture error is a broken test.
            if (-not $_.Exception.Message.StartsWith('Provenance:')) { throw "Case '$($case.Name)' failed for a reason that is not the guard's: $($_.Exception.Message)" }
            $accepted = $false
        }
        if ($accepted -ne $case.Pass) { $failed += $case.Name; Write-Host "FAIL $($case.Name)" }
        else { Write-Host "ok $($case.Name)" }
    }
    $written = Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw
    if ($written -notmatch "tagCommit=$commit") { $failed += 'tag commit output' }
    if ($failed.Count) { throw "Provenance cases failed: $($failed -join ', ')" }
    Write-Host "All $($cases.Count) release run provenance cases passed."
}
finally {
    $env:GITHUB_OUTPUT = $savedOutput
    $resolved = [IO.Path]::GetFullPath($temp)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
# Explicit success. A native command or a stub inside the suite may have left $LASTEXITCODE non-zero,
# and the GitHub Actions pwsh wrapper ends a step with `exit $LASTEXITCODE` (PR #53 B-1).
exit 0
