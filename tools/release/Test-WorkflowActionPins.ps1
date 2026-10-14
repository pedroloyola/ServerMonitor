<# G8. Every action used by a workflow that holds Store secrets or writes GitHub Releases must be pinned
   to a full 40-character commit SHA, with the release it corresponds to in a trailing comment. A tag
   (@v4) or a branch is a moving reference: whoever controls that repository decides what runs next to
   the credentials. No third-party YAML dependency; unsupported shapes fail closed. #>
[CmdletBinding()]
param([string] $RepositoryRoot = (Join-Path $PSScriptRoot '../..'), [switch] $SelfTest)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ActionPins([string] $Text) {
    $count = 0
    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -match '^\s*#') { continue }
        if ($line -notmatch '^\s*(?:-\s+)?["'']?uses["'']?\s*:') {
            if ($line -match '[,{]\s*["'']?uses["'']?\s*:') { throw 'Unsupported flow-style uses key.' }
            continue
        }
        $count++
        if ($line -cnotmatch '^\s*(?:-\s+)?uses:\s+[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_./-]+)?@[0-9a-f]{40}\s+#\s*v[0-9]+(?:\.[0-9]+){0,2}\s*$') {
            throw "Action not pinned to a full commit SHA with its release in a comment: $($line.Trim())"
        }
    }
    if ($count -eq 0) { throw 'No uses lines found; refusing an unverified workflow.' }
    return $count
}

$names = @('release-build.yml', 'store-submit.yml', 'store-status.yml')
foreach ($name in $names) {
    $text = Get-Content -LiteralPath (Join-Path $RepositoryRoot ".github/workflows/$name") -Raw
    $count = Assert-ActionPins $text
    Write-Host "PASS action pins: $name ($count uses)"
    if ($SelfTest) {
        # Reintroduce the defect in memory: re-point the first pinned action at a moving tag, a short SHA,
        # and a SHA without its release comment.
        $mutants = @(
            [regex]::new('@[0-9a-f]{40}\s+#\s*(v[0-9.]+)').Replace($text, '@$1', 1),
            [regex]::new('@([0-9a-f]{7})[0-9a-f]{33}').Replace($text, '@$1', 1),
            [regex]::new('(@[0-9a-f]{40})\s+#\s*v[0-9.]+').Replace($text, '$1', 1),
            [regex]::new('@[0-9a-f]{40}').Replace($text, '@main', 1)
        )
        foreach ($mutant in $mutants) {
            if ($mutant -eq $text) { throw 'Mutation insertion failed.' }
            $caught = $false
            try { Assert-ActionPins $mutant | Out-Null } catch {
                if (-not $_.Exception.Message.StartsWith('Action not pinned')) { throw }
                $caught = $true
            }
            if (-not $caught) { throw "Pin mutation survived in $name." }
        }
        Write-Host "MUTATIONS DETECTED: $name (tag, short SHA, missing release comment, branch)"
        Assert-ActionPins $text | Out-Null
    }
}
if ($SelfTest) {
    foreach ($text in @("steps:`n  - uses: actions/checkout@v4", "steps:`n  - { uses: actions/checkout@v4 }", "steps:`n  - run: echo")) {
        $caught = $false
        try { Assert-ActionPins $text | Out-Null } catch { $caught = $true }
        if (-not $caught) { throw 'Synthetic pin case survived.' }
    }
    Write-Host 'All 12 workflow pin mutations and 3 synthetic shapes rejected; original workflows passed again.'
}
