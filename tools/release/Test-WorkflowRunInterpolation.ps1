<# No third-party YAML dependency. Scan indented YAML run scalars (literal, folded, or inline).
   Unsupported flow-style run keys fail closed instead of being silently skipped. #>
[CmdletBinding()]
param([string] $RepositoryRoot = (Join-Path $PSScriptRoot '../..'), [switch] $SelfTest)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert-RunText([string] $Text) {
    $runIndent = -1
    $body = ''
    $runs = 0
    foreach ($line in ($Text -split '\r?\n')) {
        if ($runIndent -lt 0 -and $line -match '^\s*#') { continue }
        if ($runIndent -ge 0) {
            $indent = $line.Length - $line.TrimStart().Length
            if ([string]::IsNullOrWhiteSpace($line) -or $indent -gt $runIndent) { $body += "`n$line"; continue }
            $runIndent = -1
        }
        if ($line -match '^(?<space> *)(?:- +)?["'']?run["'']?\s*:\s*(?<value>.*)$') {
            $runs++
            $runIndent = $Matches.space.Length
            $body += "`n$($Matches.value)"
        }
        elseif ($line -match '(?:[,{]\s*)["'']?run["'']?\s*:') { throw 'Unsupported flow-style run key; use an indented run scalar.' }
    }
    if ($runs -eq 0) { throw 'No run blocks found; refusing an unverified workflow.' }
    # Check complete expressions, including bracket access and expressions spanning lines.
    foreach ($expression in [regex]::Matches($body, '(?s)\$\{\{.*?\}\}')) {
        # needs.* carries another job's outputs; github.event.inputs is the dispatch payload by another name.
        if ($expression.Value -match '\b(inputs|steps|needs)\s*[.\[]|\bgithub\s*\.\s*event\s*[.\[]') { throw 'Unsafe workflow interpolation in run block.' }
    }
}
$names = @('store-submit.yml', 'store-status.yml', 'release-build.yml')
foreach ($name in $names) {
    $text = Get-Content -LiteralPath (Join-Path $RepositoryRoot ".github/workflows/$name") -Raw
    Assert-RunText $text
    Write-Host "PASS run interpolation: $name"
    if ($SelfTest) {
        foreach ($expression in @('${{ inputs.attach_package }}', '${{ steps.verify.outputs.packagePath }}', '${{ needs.provenance.outputs.tagCommit }}', '${{ github.event.inputs.release_tag }}')) {
            # Reintroduce the defect in memory, into an actual run block of each workflow.
            $mutant = [regex]::new('(?m)^(\s*run:\s*\|)\r?$').Replace($text, ('$1' + "`n          Write-Host '" + $expression + "'"), 1)
            if ($mutant -eq $text) { throw 'Mutation insertion failed.' }
            $caught = $false
            try { Assert-RunText $mutant } catch {
                if ($_.Exception.Message -ne 'Unsafe workflow interpolation in run block.') { throw }
                $caught = $true
            }
            if (-not $caught) { throw 'Interpolation mutation survived.' }
            Write-Host "MUTATION DETECTED: $name / $expression"
        }
        Assert-RunText $text
    }
}
if ($SelfTest) {
    foreach ($text in @('run: echo "${{ inputs.tag }}"', "run: >`n  echo `${{ steps.x.outputs.y }}", 'run: echo "${{ inputs[''tag''] }}"')) {
        $caught = $false
        try { Assert-RunText $text } catch { if ($_.Exception.Message -ne 'Unsafe workflow interpolation in run block.') { throw }; $caught = $true }
        if (-not $caught) { throw 'Inline/folded/bracket regression survived.' }
    }
    Write-Host "All $(4 * $names.Count) workflow mutations and 3 scalar/access cases rejected; original workflows passed again."
}
# Explicit success. A native command or a stub inside the suite may have left $LASTEXITCODE non-zero,
# and the GitHub Actions pwsh wrapper ends a step with `exit $LASTEXITCODE` (PR #53 B-1).
exit 0
