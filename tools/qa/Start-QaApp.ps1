<#
.SYNOPSIS
    THE way to launch ServerMonitor.App.exe for QA (UI.3 rule: no agent or script starts the app directly).

.DESCRIPTION
    Refuses BEFORE Start-Process unless the arguments select an isolated mode:
      - at least one EXACT data-harness flag (or the documented --qa-compact:<digits>), or an EXACT gallery flag
        (--qa-components / --qa-tokens; the gallery's own policy then owns its options), and
      - every other argument starting with --qa is an exact harness flag or a well-formed modifier
        ('flag value' or 'flag=value'), when not in gallery mode.
    A launch with no --qa argument at all (the production composition) is always refused, and so is any executable that
    is not a Debug x64 build (a Release build ignores every --qa flag and would start production).

    The lists below mirror QaStartupIsolation.HarnessFlags / ModifierFlags; QaLaunchRefusalTests fails if they drift.

    After the start it re-reads the PID's image path and command line; a mismatch stops exactly that PID (image path +
    start time re-checked) and throws. Returns PID, StartTime, the Process object and the verified command line.

    -ValidateOnly decides and prints ALLOWED without resolving or starting anything (used by the tests).

.EXAMPLE
    $app = & tools/qa/Start-QaApp.ps1 -Exe $exe -Arguments '--qa-health', '--qa-ui-language', 'pt-PT'
    ... ; Stop-QaApp-style: if ((Get-Process -Id $app.Pid).StartTime -eq $app.StartTime) { Stop-Process -Id $app.Pid -Force }
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [string[]]$Arguments = @(),
    [switch]$ValidateOnly,
    [int]$IdentityTimeoutSeconds = 3
)

$ErrorActionPreference = 'Stop'

# Mirrors QaStartupIsolation (tested against it).
$HarnessFlags = @('--qa-health', '--qa-discovery', '--qa-notifications', '--qa-compact', '--qa-history', '--qa-workloads', '--qa-store-screenshot', '--qa-proxyjump')
$ModifierFlags = @('--qa-ssh-config', '--qa-ui-language', '--qa-backup', '--qa-proxyjump-dir')
$GalleryFlags = @('--qa-components', '--qa-tokens')

function Test-IsHarness([string]$a) { ($HarnessFlags -ccontains $a) -or ($a -cmatch '^--qa-compact:\d{1,4}$') }
function Test-IsModifier([string]$a) {
    foreach ($flag in $ModifierFlags) {
        if ($a -ceq $flag -or ($a.StartsWith("$flag=", [StringComparison]::Ordinal) -and $a.Length -gt $flag.Length + 1)) { return $true }
    }
    $false
}

function Get-QaLaunchRefusal([string[]]$arguments) {
    $qa = @($arguments | Where-Object { $_.StartsWith('--qa', [StringComparison]::OrdinalIgnoreCase) })
    if ($qa.Count -eq 0) { return 'no --qa argument: that is the production composition (real data, Credential Manager, SSH)' }
    if (@($qa | Where-Object { $GalleryFlags -ccontains $_ }).Count -gt 0) { return $null }
    $unknown = $qa | Where-Object { -not (Test-IsHarness $_) -and -not (Test-IsModifier $_) } | Select-Object -First 1
    if ($unknown) { return "'$unknown' is not a recognised QA switch (exact, lower-case: $($HarnessFlags -join ', '); modifiers $($ModifierFlags -join ', '))" }
    if (@($qa | Where-Object { Test-IsHarness $_ }).Count -eq 0) { return "$($qa[0]) is not an isolated harness; combine it with one of: $($HarnessFlags -join ', ')" }
    $null
}

# Windows command-line quoting (CommandLineToArgvW rules), so every element reaches the app as exactly one argument.
function Join-CommandLine([string[]]$arguments) {
    ($arguments | ForEach-Object {
        if ($_ -ne '' -and $_ -notmatch '[\s"]') { $_ }
        else { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
    }) -join ' '
}

$refusal = Get-QaLaunchRefusal $Arguments
if ($refusal) { throw "QA launch refused: $refusal" }
if ($Exe -notmatch '\\bin\\x64\\Debug\\') { throw "QA launch refused: '$Exe' is not a Debug x64 build (Release ignores --qa flags and starts production)." }
if ($ValidateOnly) { 'ALLOWED'; return }

if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "QA launch: executable not found: $Exe" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path

$process = Start-Process -FilePath $Exe -ArgumentList (Join-CommandLine $Arguments) -PassThru
$started = $process.StartTime
$deadline = (Get-Date).AddSeconds($IdentityTimeoutSeconds)
do {
    $info = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)"
    if (-not $info -or $info.ExecutablePath) { break }
    Start-Sleep -Milliseconds 100
} while ((Get-Date) -lt $deadline)

$qaArguments = @($Arguments | Where-Object { $_.StartsWith('--qa', [StringComparison]::OrdinalIgnoreCase) })
$missing = $qaArguments | Where-Object { -not $info -or -not $info.CommandLine.Contains($_, [StringComparison]::Ordinal) }
if (-not $info -or $info.ExecutablePath -ne $Exe -or $missing) {
    $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    if ($current -and $current.Path -eq $Exe -and $current.StartTime -eq $started) { Stop-Process -Id $process.Id -Force }
    throw "QA launch: PID $($process.Id) is not the expected launch (image '$($info.ExecutablePath)', command line '$($info.CommandLine)')."
}

[pscustomobject]@{ Pid = $process.Id; StartTime = $started; Process = $process; Exe = $Exe; CommandLine = $info.CommandLine }
