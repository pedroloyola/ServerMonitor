<#
.SYNOPSIS
    Runs release self-tests the way a GitHub Actions pwsh step runs them, one child process each, and
    requires every one to EXIT 0, not only to print its success line.

.DESCRIPTION
    PR #53 B-1: Test-PackageResources.ps1 printed "All 23 ... as required." and still failed the CI step,
    because its makepri stub left $LASTEXITCODE = 1 and the Actions pwsh wrapper ends a step with
    `exit $LASTEXITCODE`. Run in-process, one after another, a later native command can hide such a leak
    (or a leak can fail an unrelated later line). Here each self-test runs in its own pwsh, wrapped
    exactly like an Actions step (dot-sourced, $ErrorActionPreference = 'stop', then
    `if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }`), and anything but
    exit code 0 fails the run, naming the self-test.

    -Test takes "Name.ps1" or "Name.ps1 -Switch" entries, resolved next to this script only.
    -SelfTest proves the runner itself catches a leaked exit code, a throw and a non-zero exit, and
    passes a clean script, on synthetic scripts in a temporary folder.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')] [string[]] $Test,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch] $SelfTest,
    [Parameter(ParameterSetName = 'Run')] [string] $Root = $PSScriptRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pwsh = (Get-Process -Id $PID).Path

function Invoke-AsActionsStep([string] $ScriptPath, [string[]] $Switches) {
    $call = "& '$($ScriptPath.Replace("'", "''"))'"
    foreach ($switch in $Switches) { $call += " $switch" }
    # The template GitHub Actions uses for `shell: pwsh` (the step body is dot-sourced).
    $wrapper = "`$ErrorActionPreference = 'stop'`n$call`nif ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit `$LASTEXITCODE }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(". { $wrapper }"))
    & $pwsh -NoProfile -NonInteractive -EncodedCommand $encoded | Out-Host
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    return $code
}

function Invoke-All([string] $Directory, [string[]] $Entries) {
    if ($Entries.Count -eq 0) { throw 'Release self-tests: no self-test to run.' }
    $results = foreach ($entry in $Entries) {
        if ($entry -cnotmatch '\A([A-Za-z][A-Za-z0-9-]*\.ps1)((?: -[A-Za-z]+)*)\z') { throw "Release self-tests: unsupported entry '$entry'." }
        $path = Join-Path $Directory $Matches[1]
        $switches = @($Matches[2] -split ' ' | Where-Object { $_ })
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release self-tests: $($Matches[1]) is missing." }
        Write-Host "::group::$entry"
        $code = Invoke-AsActionsStep $path $switches
        Write-Host '::endgroup::'
        [pscustomobject] @{ SelfTest = $entry; ExitCode = $code }
    }
    $results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    $bad = @($results | Where-Object ExitCode -ne 0)
    if ($bad.Count -gt 0) {
        throw "Release self-tests: $($bad.Count) of $($results.Count) did not exit 0 as an Actions step: $(($bad | ForEach-Object { "$($_.SelfTest) (exit $($_.ExitCode))" }) -join ', ')."
    }
    Write-Host "All $($results.Count) release self-tests exited 0 as Actions steps."
}

if ($SelfTest) {
    $work = Join-Path ([IO.Path]::GetTempPath()) ('release-selftests-' + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $scripts = [ordered] @{
            'Clean.ps1'        = 'param([switch] $SelfTest) Write-Host "All 3 cases behaved as required."'
            # The B-1 shape: a stub's exit code leaks, then the success line is printed.
            'Leaks.ps1'        = "Set-Content -LiteralPath (Join-Path `$PSScriptRoot 'stub.ps1') -Value 'exit 1'; & (Join-Path `$PSScriptRoot 'stub.ps1'); Write-Host 'All 23 cases behaved as required.'"
            'LeaksNative.ps1'  = "& '$($pwsh.Replace("'", "''"))' -NoProfile -Command 'exit 1'; Write-Host 'All cases behaved as required.'"
            'LeakReset.ps1'    = "& (Join-Path `$PSScriptRoot 'stub.ps1'); `$global:LASTEXITCODE = 0; Write-Host 'All cases behaved as required.'"
            'Throws.ps1'       = "throw 'a case failed'"
            'ExitsThree.ps1'   = 'exit 3'
        }
        foreach ($name in $scripts.Keys) { Set-Content -LiteralPath (Join-Path $work $name) -Value $scripts[$name] }
        Set-Content -LiteralPath (Join-Path $work 'stub.ps1') -Value 'exit 1'

        $cases = @(
            @{ Entries = @('Clean.ps1', 'Clean.ps1 -SelfTest', 'LeakReset.ps1'); Reason = $null }
            @{ Entries = @('Clean.ps1', 'Leaks.ps1'); Reason = 'did not exit 0 as an Actions step: Leaks\.ps1 \(exit 1\)' }
            @{ Entries = @('LeaksNative.ps1'); Reason = 'LeaksNative\.ps1 \(exit 1\)' }
            @{ Entries = @('Throws.ps1', 'Clean.ps1'); Reason = 'Throws\.ps1 \(exit 1\)' }
            @{ Entries = @('ExitsThree.ps1'); Reason = 'ExitsThree\.ps1 \(exit 3\)' }
            @{ Entries = @('Missing.ps1'); Reason = 'Missing\.ps1 is missing' }
            @{ Entries = @('..\Clean.ps1'); Reason = "unsupported entry" }
            @{ Entries = @('Clean.ps1; exit 0'); Reason = "unsupported entry" }
        )
        $failed = 0
        foreach ($case in $cases) {
            $failure = $null
            try { Invoke-All $work $case.Entries 6> $null *> $null } catch { $failure = $_.Exception.Message }
            $ok = if ($case.Reason) { $null -ne $failure -and $failure -match $case.Reason } else { $null -eq $failure }
            if (-not $ok) { $failed++ }
            Write-Host ("  {0}  [{1}] {2}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), ($case.Entries -join ', '), $failure)
        }
        if ($failed -gt 0) { throw "$failed of $($cases.Count) release self-test runner cases are wrong." }
        Write-Host "All $($cases.Count) release self-test runner cases behaved as required."
    }
    finally {
        $resolved = [IO.Path]::GetFullPath($work)
        if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
    exit 0
}

Invoke-All $Root $Test
exit 0
