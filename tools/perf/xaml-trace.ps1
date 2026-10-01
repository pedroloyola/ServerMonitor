<#
.SYNOPSIS
    UI.3 gate 1A diagnostic: one isolated harness launch per executable under a user-mode ETW session with the
    Microsoft-Windows-XAML provider (plus the ServerMonitor-QaStartup markers), decoded to XML for attribution
    (which XAML files are parsed, and when, relative to the startup milestones).

.DESCRIPTION
    Same launch guards as startup-perf.ps1: only an isolated --qa-* harness flag, the started PID's command line and image
    are verified, and only that PID is stopped (image path + start time re-checked). No real-data path is touched; a
    metadata snapshot of %LOCALAPPDATA%\ServerMonitor is compared before/after.

.EXAMPLE
    pwsh tools/perf/xaml-trace.ps1 -Exe <a.exe>,<b.exe> -Labels base,cand -OutDir .boss\tmp\ui3\relay\xaml
#>
param(
    [Parameter(Mandatory)] [string[]]$Exe,
    [Parameter(Mandatory)] [string[]]$Labels,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$Harness = '--qa-health',
    [int]$Repeat = 1,
    [double]$SettleSeconds = 3
)

$ErrorActionPreference = 'Stop'
[System.Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
$HarnessFlags = @('--qa-health', '--qa-discovery', '--qa-notifications', '--qa-compact', '--qa-history', '--qa-workloads', '--qa-store-screenshot')
if ($HarnessFlags -notcontains $Harness) { throw "Refusing: '$Harness' is not an isolated harness flag." }
# pwsh -File passes an array as one string; accept 'a.exe,b.exe' too.
$Exe = @($Exe | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ })
$Labels = @($Labels | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Exe.Count -ne $Labels.Count) { throw 'One label per executable.' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$RealData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ServerMonitor'
function Get-Snapshot {
    if (-not (Test-Path -LiteralPath $RealData)) { return '' }
    (Get-ChildItem -LiteralPath $RealData -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        '{0}|{1}|{2}|{3}' -f $_.FullName, $_.Length, $_.LastWriteTimeUtc.ToString('o'), (Get-FileHash -LiteralPath $_.FullName).Hash
    }) -join "`n"
}
$before = Get-Snapshot

$session = 'ServerMonitorXamlTrace'
$providers = Join-Path $OutDir 'providers.txt'
@(
    '{531A35AB-63CE-4BCF-AA98-F88C7A89E455} 0xFFFFFFFFFFFFFFFF 5'
    '{6b0f3d52-8a1e-4c55-9d0e-3f7a2c41b9e1} 0xFFFFFFFFFFFFFFFF 5'
) | Set-Content $providers -Encoding ascii

$log = [System.Collections.Generic.List[string]]::new()
for ($r = 1; $r -le $Repeat; $r++) {
    for ($e = 0; $e -lt $Exe.Count; $e++) {
        $path = (Resolve-Path $Exe[$e]).Path
        $etl = Join-Path $OutDir ("{0}-{1}.etl" -f $Labels[$e], $r)
        & logman stop $session -ets 2>$null | Out-Null
        & logman create trace $session -pf $providers -o $etl -bs 1024 -nb 64 256 -ets | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "logman failed ($LASTEXITCODE)" }
        try {
            # The only launch path (UI.3 rule): refuses without an exact harness, verifies the PID's image + command line.
            $app = & (Join-Path $PSScriptRoot '..\qa\Start-QaApp.ps1') -Exe $path -Arguments @($Harness)
            $process = $app.Process
            $started = $app.StartTime
            $deadline = (Get-Date).AddSeconds(45)
            while ((Get-Date) -lt $deadline) { $process.Refresh(); if ($process.HasExited -or $process.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 100 }
            Start-Sleep -Milliseconds ([int]($SettleSeconds * 1000))
            $c = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
            if ($c -and $c.Path -eq $path -and $c.StartTime -eq $started) { Stop-Process -Id $process.Id -Force; $stop = 'stopped' } else { $stop = 'not stopped' }
        }
        finally { & logman stop $session -ets | Out-Null }
        $xml = [IO.Path]::ChangeExtension($etl, '.xml')
        & tracerpt $etl -o $xml -of XML -y | Out-Null
        $log.Add("$($Labels[$e]) run $r PID=$($process.Id) $stop -> $xml")
        Start-Sleep -Seconds 1.5
    }
}
$log | Set-Content (Join-Path $OutDir 'manifest.txt')
$log
if ((Get-Snapshot) -ne $before) { Write-Error 'REAL_DATA_CHANGED'; exit 2 }
'REAL_DATA_UNCHANGED'
