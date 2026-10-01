<#
.SYNOPSIS
    UI.3 gate 1A (PERF-UI2-DICT): A/B startup measurement of two Debug builds through an isolated --qa-* harness, with
    user-mode ETW milestones (QaStartupMarker) and a bootstrap confidence interval of the difference.

.DESCRIPTION
    Launches <ExeA> and <ExeB> ALTERNATELY (A B A B ...) with exactly one harness flag (default --qa-health), after
    WarmupPairs discarded pairs. For every launch it checks that the started PID's command line contains the flag and
    that its image is the expected executable, waits for the main window plus SettleSeconds, and stops exactly that PID
    (image path + start time re-checked). It never launches without the flag and never stops a process by name.

    Milestones come from the ServerMonitor-QaStartup EventSource (Debug-only, see Qa/QaStartupMarker.cs) captured in a
    user-mode ETW session (logman, no elevation): ResourcesLoaded (App.InitializeComponent done; payload also carries the
    duration of that call), ShellActivated, ContentLoaded and FirstFrame, each as ms since process creation.

    Real-data guard: a metadata-only snapshot (relative name, size, LastWriteTimeUtc, SHA256) of
    %LOCALAPPDATA%\ServerMonitor is taken before and after the series; any difference is reported as REAL_DATA_CHANGED and
    the script exits non-zero. Nothing under that folder is opened for writing.

    Both builds must carry the QA isolation patch (QaStartupIsolation: a harness launch whose composition still reaches
    real data aborts before the host starts).

.EXAMPLE
    pwsh tools/perf/startup-perf.ps1 -ExeA <base>\ServerMonitor.App.exe -ExeB <cand>\ServerMonitor.App.exe -Runs 25 -OutDir .boss\tmp\ui3\relay\run1
#>
param(
    [Parameter(Mandatory)] [string]$ExeA,
    [Parameter(Mandatory)] [string]$ExeB,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$Harness = '--qa-health',
    [int]$Runs = 20,
    [int]$WarmupPairs = 2,
    [double]$SettleSeconds = 2.5,
    [double]$CooldownSeconds = 1.5,
    [int]$TimeoutSeconds = 45,
    [int]$BootstrapSamples = 10000,
    # Alternate the order inside each pair (AB, BA, AB, ...) so neither build always runs right after the other.
    [switch]$Counterbalance
)

$ErrorActionPreference = 'Stop'
# Invariant numbers in every CSV/JSON (a pt-PT host would otherwise write decimal commas).
[System.Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
$ProviderGuid = '{6b0f3d52-8a1e-4c55-9d0e-3f7a2c41b9e1}'
$SessionName = 'ServerMonitorStartupPerf'
$HarnessFlags = @('--qa-health', '--qa-discovery', '--qa-notifications', '--qa-compact', '--qa-history', '--qa-workloads', '--qa-store-screenshot')
if ($HarnessFlags -notcontains $Harness) { throw "Refusing: '$Harness' is not an isolated harness flag ($($HarnessFlags -join ', '))." }

$ExeA = (Resolve-Path $ExeA).Path
$ExeB = (Resolve-Path $ExeB).Path
foreach ($exe in $ExeA, $ExeB) {
    if ($exe -notmatch '\\bin\\x64\\Debug\\') { throw "Refusing: $exe is not a Debug x64 build (the harnesses exist only in Debug)." }
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# ---- real-data metadata snapshot ---------------------------------------------------------------------------------
$RealData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ServerMonitor'
function Get-RealDataSnapshot {
    if (-not (Test-Path -LiteralPath $RealData)) { return @() }
    Get-ChildItem -LiteralPath $RealData -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            Name   = $_.FullName.Substring($RealData.Length)
            Length = $_.Length
            Utc    = $_.LastWriteTimeUtc.ToString('o')
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
}

# ---- machine state -----------------------------------------------------------------------------------------------
function Get-MachineState([string]$label) {
    # CIM rather than Get-Counter: counter paths are localized on non-English Windows.
    $cpu = 1..3 | ForEach-Object {
        Start-Sleep -Seconds 1
        (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
    } | Measure-Object -Average
    $battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
    $others = Get-CimInstance Win32_Process -Filter "Name='ServerMonitor.App.exe'" | ForEach-Object { "$($_.ProcessId) $($_.ExecutablePath) :: $($_.CommandLine)" }
    [pscustomobject]@{
        Label            = $label
        Time             = (Get-Date).ToString('o')
        CpuBusyPct3s     = [math]::Round($cpu.Average, 1)
        PowerScheme      = (powercfg /getactivescheme) -join ' '
        OnBattery        = if ($battery) { $battery.BatteryStatus -eq 1 } else { 'no battery' }
        FreeMemoryMB     = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1024)
        OtherAppProcs    = @($others)
    }
}

# ---- launch one ----------------------------------------------------------------------------------------------------
$PlacementFile = Join-Path ([IO.Path]::GetTempPath()) ("ServerMonitor-QA\{0}\window-placement.json" -f ($Harness -replace '^--qa-', '' -replace '^store-screenshot$', 'screenshot'))
function Invoke-Launch([string]$exe, [string]$condition, [int]$index) {
    # Only UI.2+ deletes a stale QA placement at startup; a leftover file would give the two builds different windows.
    $placementExisted = Test-Path -LiteralPath $PlacementFile
    $process = Start-Process -FilePath $exe -ArgumentList $Harness -PassThru
    $started = $process.StartTime
    # ExecutablePath can still be empty right after creation; re-query briefly (still fail-closed below).
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $info = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)"
        if (-not $info -or $info.ExecutablePath) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $info -or $info.CommandLine -notmatch [regex]::Escape($Harness) -or $info.ExecutablePath -ne $exe) {
        $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($current -and $current.Path -eq $exe -and $current.StartTime -eq $started) { Stop-Process -Id $process.Id -Force }
        throw "PID $($process.Id) is not the expected harness launch: $($info.CommandLine)"
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited -or $process.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    }
    $exited = $process.HasExited
    $exitCode = if ($exited) { $process.ExitCode } else { $null }
    if (-not $exited) { Start-Sleep -Milliseconds ([int]($SettleSeconds * 1000)) }

    $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    if ($current -and $current.Path -eq $exe -and $current.StartTime -eq $started) { Stop-Process -Id $process.Id -Force; $stop = 'stopped' }
    elseif ($current) { $stop = 'IDENTITY MISMATCH - not stopped' } else { $stop = "exited ($exitCode)" }
    Start-Sleep -Milliseconds ([int]($CooldownSeconds * 1000))
    [pscustomobject]@{ Index = $index; Condition = $condition; Pid = $process.Id; Start = $started.ToString('o'); Stop = $stop; PlacementExisted = $placementExisted }
}

# ---- statistics ----------------------------------------------------------------------------------------------------
function Get-Quantile([double[]]$sorted, [double]$q) {
    if ($sorted.Count -eq 0) { return [double]::NaN }
    $pos = ($sorted.Count - 1) * $q; $lo = [math]::Floor($pos); $hi = [math]::Ceiling($pos)
    $sorted[$lo] + ($sorted[$hi] - $sorted[$lo]) * ($pos - $lo)
}
function Get-Median([double[]]$values) { Get-Quantile ([double[]]($values | Sort-Object)) 0.5 }
function Get-BootstrapDiff([double[]]$a, [double[]]$b, [int]$n) {
    $rng = [System.Random]::new(20261001)
    $diffs = [double[]]::new($n)
    for ($i = 0; $i -lt $n; $i++) {
        $ra = [double[]]::new($a.Count); for ($j = 0; $j -lt $a.Count; $j++) { $ra[$j] = $a[$rng.Next($a.Count)] }
        $rb = [double[]]::new($b.Count); for ($j = 0; $j -lt $b.Count; $j++) { $rb[$j] = $b[$rng.Next($b.Count)] }
        [array]::Sort($ra); [array]::Sort($rb)
        $diffs[$i] = (Get-Quantile $rb 0.5) - (Get-Quantile $ra 0.5)
    }
    [array]::Sort($diffs)
    [pscustomobject]@{ Low = (Get-Quantile $diffs 0.025); High = (Get-Quantile $diffs 0.975) }
}
# Paired: each B launch minus the A launch right before it, so slow drift in machine load cancels.
function Get-PairedBootstrap([double[]]$d, [int]$n) {
    $rng = [System.Random]::new(20261002)
    $medians = [double[]]::new($n)
    for ($i = 0; $i -lt $n; $i++) {
        $r = [double[]]::new($d.Count); for ($j = 0; $j -lt $d.Count; $j++) { $r[$j] = $d[$rng.Next($d.Count)] }
        [array]::Sort($r); $medians[$i] = Get-Quantile $r 0.5
    }
    [array]::Sort($medians)
    [pscustomobject]@{ Median = (Get-Median $d); Low = (Get-Quantile $medians 0.025); High = (Get-Quantile $medians 0.975) }
}

# ---- run -----------------------------------------------------------------------------------------------------------
$before = @(Get-RealDataSnapshot)
$before | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutDir 'realdata-before.json')
$stateBefore = Get-MachineState 'before'

$etl = Join-Path $OutDir 'startup.etl'
& logman stop $SessionName -ets 2>$null | Out-Null
& logman create trace $SessionName -p $ProviderGuid 0xFFFFFFFFFFFFFFFF 5 -o $etl -ets | Out-Null
if ($LASTEXITCODE -ne 0) { throw "logman could not start the ETW session ($LASTEXITCODE)." }

$launches = [System.Collections.Generic.List[object]]::new()
try {
    for ($w = 1; $w -le $WarmupPairs; $w++) {
        $launches.Add((Invoke-Launch $ExeA 'A-warmup' (-$w)))
        $launches.Add((Invoke-Launch $ExeB 'B-warmup' (-$w)))
    }
    for ($i = 1; $i -le $Runs; $i++) {
        if ($Counterbalance -and $i % 2 -eq 0) {
            $launches.Add((Invoke-Launch $ExeB 'B' $i))
            $launches.Add((Invoke-Launch $ExeA 'A' $i))
        } else {
            $launches.Add((Invoke-Launch $ExeA 'A' $i))
            $launches.Add((Invoke-Launch $ExeB 'B' $i))
        }
        Write-Host ("pair {0}/{1} done" -f $i, $Runs)
    }
}
finally {
    & logman stop $SessionName -ets | Out-Null
}
$launches | Export-Csv (Join-Path $OutDir 'launches.csv') -NoTypeInformation

$stateAfter = Get-MachineState 'after'
$after = @(Get-RealDataSnapshot)
$after | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutDir 'realdata-after.json')
$realDataSame = (($before | ConvertTo-Json -Depth 3) -eq ($after | ConvertTo-Json -Depth 3))

# ---- decode ----------------------------------------------------------------------------------------------------------
$xml = Join-Path $OutDir 'startup.xml'
& tracerpt $etl -o $xml -of XML -y | Out-Null
[xml]$doc = Get-Content $xml -Raw
$names = @{ 1 = 'ResourcesLoaded'; 2 = 'ShellActivated'; 3 = 'ContentLoaded'; 4 = 'FirstFrame' }
$byPid = @{}
foreach ($event in $doc.Events.Event) {
    $id = [int]$event.System.EventID
    $pidValue = [int]$event.System.Execution.ProcessID
    $data = @{}
    foreach ($d in @($event.EventData.Data)) { if ($d.Name) { $data[$d.Name] = $d.'#text' } }
    $task = $event.RenderingInfo.Task
    $name = if ($task -and $names.Values -contains $task) { $task } elseif ($names.ContainsKey($id)) { $names[$id] } else { $null }
    if (-not $name -or -not $data.ContainsKey('msSinceProcessStart')) { continue }
    if (-not $byPid.ContainsKey($pidValue)) { $byPid[$pidValue] = @{} }
    $byPid[$pidValue][$name] = [double]::Parse($data['msSinceProcessStart'], [Globalization.CultureInfo]::InvariantCulture)
    if ($name -eq 'ResourcesLoaded') {
        $byPid[$pidValue]['InitializeComponentMs'] = [double]::Parse($data['initializeComponentMs'], [Globalization.CultureInfo]::InvariantCulture)
    }
}

$metrics = 'InitializeComponentMs', 'ResourcesLoaded', 'ShellActivated', 'ContentLoaded', 'FirstFrame'
$rows = foreach ($l in $launches) {
    $m = $byPid[[int]$l.Pid]
    $row = [ordered]@{ Index = $l.Index; Condition = $l.Condition; Pid = $l.Pid; Stop = $l.Stop; PlacementExisted = $l.PlacementExisted }
    foreach ($k in $metrics) { $row[$k] = if ($m -and $m.ContainsKey($k)) { $m[$k] } else { $null } }
    [pscustomobject]$row
}
$rows | Export-Csv (Join-Path $OutDir 'samples.csv') -NoTypeInformation

$summary = foreach ($k in $metrics) {
    $a = [double[]]@($rows | Where-Object { $_.Condition -eq 'A' -and $null -ne $_.$k } | ForEach-Object { $_.$k })
    $b = [double[]]@($rows | Where-Object { $_.Condition -eq 'B' -and $null -ne $_.$k } | ForEach-Object { $_.$k })
    if ($a.Count -lt 2 -or $b.Count -lt 2) { [pscustomobject]@{ Metric = $k; nA = $a.Count; nB = $b.Count; Note = 'insufficient samples' }; continue }
    $sa = [double[]]($a | Sort-Object); $sb = [double[]]($b | Sort-Object)
    $medA = Get-Quantile $sa 0.5; $medB = Get-Quantile $sb 0.5
    $ci = Get-BootstrapDiff $a $b $BootstrapSamples
    $pairs = [double[]]@(foreach ($i in 1..$Runs) {
        $ra = $rows | Where-Object { $_.Condition -eq 'A' -and $_.Index -eq $i -and $null -ne $_.$k }
        $rb = $rows | Where-Object { $_.Condition -eq 'B' -and $_.Index -eq $i -and $null -ne $_.$k }
        if ($ra -and $rb) { $rb.$k - $ra.$k }
    })
    $paired = Get-PairedBootstrap $pairs $BootstrapSamples
    [pscustomobject]@{
        Metric        = $k
        nA            = $a.Count; nB = $b.Count
        MedianA       = [math]::Round($medA, 1); IqrA = '{0:N1}-{1:N1}' -f (Get-Quantile $sa 0.25), (Get-Quantile $sa 0.75)
        MedianB       = [math]::Round($medB, 1); IqrB = '{0:N1}-{1:N1}' -f (Get-Quantile $sb 0.25), (Get-Quantile $sb 0.75)
        DiffMs        = [math]::Round($medB - $medA, 1)
        DiffPct       = [math]::Round(100 * ($medB - $medA) / $medA, 2)
        Ci95Ms        = '[{0:N1}, {1:N1}]' -f $ci.Low, $ci.High
        Ci95Pct       = '[{0:N2}%, {1:N2}%]' -f (100 * $ci.Low / $medA), (100 * $ci.High / $medA)
        Significant   = ($ci.Low -gt 0 -or $ci.High -lt 0)
        PairedMedMs   = [math]::Round($paired.Median, 1)
        PairedCi95Pct = '[{0:N2}%, {1:N2}%]' -f (100 * $paired.Low / $medA), (100 * $paired.High / $medA)
        PairedSignif  = ($paired.Low -gt 0 -or $paired.High -lt 0)
    }
}

$result = [pscustomobject]@{
    Harness = $Harness; Runs = $Runs; Counterbalance = [bool]$Counterbalance; WarmupPairs = $WarmupPairs; SettleSeconds = $SettleSeconds
    ExeA = $ExeA; ExeB = $ExeB
    ExeASha256 = (Get-FileHash $ExeA).Hash; ExeBSha256 = (Get-FileHash $ExeB).Hash
    RealDataFiles = $before.Count; RealDataUnchanged = $realDataSame
    MachineBefore = $stateBefore; MachineAfter = $stateAfter
    Summary = $summary
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'summary.json')
$summary | Format-Table -AutoSize | Out-String -Width 220 | Tee-Object (Join-Path $OutDir 'summary.txt')
if (-not $realDataSame) { Write-Error "REAL_DATA_CHANGED: %LOCALAPPDATA%\ServerMonitor metadata differs before/after (see realdata-*.json)."; exit 2 }
"REAL_DATA_UNCHANGED ($($before.Count) files)"
