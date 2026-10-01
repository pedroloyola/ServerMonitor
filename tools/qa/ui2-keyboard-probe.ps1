<#
.SYNOPSIS
    UI.2 R1 keyboard probe (Beacon F-3): a segmented group and the sidebar nav group are ONE Tab stop each, entry lands
    on the selected item, arrows move focus (segmented: and select; nav: focus only), Tab leaves the group.

.DESCRIPTION
    Launches the EXACT Debug executable with --qa-components --qa-gallery-page <forms|navigation> --qa-gallery-theme dark,
    verifies the command line and image path, then drives REAL key presses (keybd_event, window in the foreground) and
    reads the focused element and each item's selection by UI Automation. Stops exactly that PID (image path + start time
    re-checked); never by name. Writes keyboard-probe.json.

    Segmented (Forms "1 h ... 30 d", selected "24 h"): UIA-focus the previous group's selected item, Tab -> "24 h";
    Right -> focus "7 d" AND selected; Tab -> focus outside the group; Shift+Tab -> back on the selected item "7 d".
    Navigation (sidebar sample "Visão geral ... Definições"): UIA-focus an UNSELECTED item, Down -> next item focused,
    selection unchanged; Tab -> outside the group; Shift+Tab -> back on the SELECTED item (entry redirect).

.EXAMPLE
    pwsh tools/qa/ui2-keyboard-probe.ps1 -OutDir C:\path\.boss\evidence\ui2\keyboard-probe
#>
param(
    [string]$Exe,
    [string]$OutDir,
    [int]$SettleSeconds = 4
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $Exe) { $Exe = Join-Path $repo 'src\ServerMonitor.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ServerMonitor.App.exe' }
if (-not (Test-Path $Exe)) { throw "Debug executable not found: $Exe (build ServerMonitor.slnx -c Debug first)" }
$Exe = (Resolve-Path $Exe).Path
if (-not $OutDir) { $OutDir = Join-Path $repo '.boss\evidence\ui2\keyboard-probe' }
New-Item -ItemType Directory -Force $OutDir | Out-Null

Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaKeyNative {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]

function Send-Key([byte]$vk, [switch]$Shift) {
    if ($Shift) { [QaKeyNative]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero) }
    # Arrow keys are extended keys (KEYEVENTF_EXTENDEDKEY = 1); without it they arrive as the numeric keypad.
    $extended = if ($vk -ge 0x25 -and $vk -le 0x28) { 1 } else { 0 }
    [QaKeyNative]::keybd_event($vk, 0, $extended, [UIntPtr]::Zero)
    [QaKeyNative]::keybd_event($vk, 0, $extended -bor 2, [UIntPtr]::Zero)
    if ($Shift) { [QaKeyNative]::keybd_event(0x10, 0, 2, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 400
}

# Panels have no UI Automation peer, so a group is identified by its items: the RadioButtons of the page in tree
# order, sliced by the first item's name and the group size.
function Get-Group($root, [string]$firstName, [int]$count) {
    $all = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton))))
    $start = [Array]::FindIndex($all, [Predicate[object]]{ param($e) $e.Current.Name -eq $firstName })
    if ($start -lt 0) { throw "group starting at '$firstName' not found" }
    $all[$start..($start + $count - 1)]
}

function Get-State($items) {
    $focused = $AE::FocusedElement
    $selected = ($items | Where-Object { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } | ForEach-Object { $_.Current.Name }) -join ','
    $inside = [bool]($items | Where-Object { [System.Windows.Automation.Automation]::Compare($_, $focused) })
    [ordered]@{ focused = $focused.Current.Name; focusInGroup = $inside; selected = $selected }
}

function Invoke-Page([string]$page, [scriptblock]$steps) {
    # The only launch path (UI.3 rule): Start-QaApp verifies the PID's image + command line, or stops it and throws.
    $app = & (Join-Path $PSScriptRoot 'Start-QaApp.ps1') -Exe $Exe -Arguments @('--qa-components', '--qa-gallery-page', $page, '--qa-gallery-theme', 'dark')
    $process = $app.Process
    $started = $app.StartTime
    try {
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) { $process.Refresh(); if ($process.MainWindowHandle -ne 0 -or $process.HasExited) { break }; Start-Sleep -Milliseconds 250 }
        Start-Sleep -Seconds $SettleSeconds
        $process.Refresh()
        [void][QaKeyNative]::SetForegroundWindow($process.MainWindowHandle)
        Start-Sleep -Milliseconds 400
        $root = $AE::FromHandle($process.MainWindowHandle)
        $result = & $steps $root
        $result['pid'] = $process.Id
        $result
    }
    finally {
        $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($current -and $current.Path -eq $Exe -and $current.StartTime -eq $started) { Stop-Process -Id $process.Id -Force }
    }
}

$segmented = Invoke-Page 'forms' {
    param($root)
    $group = Get-Group $root '1 h' 5
    $log = [ordered]@{ page = 'forms'; group = ($group | ForEach-Object { $_.Current.Name }) -join ','; initial = Get-State $group }
    # Start point: the previous group's selected item ("Todos · 12"), focused programmatically by UIA.
    (Get-Group $root 'Todos · 12' 1)[0].SetFocus(); Start-Sleep -Milliseconds 300
    Send-Key 0x09;                 $log['afterTab'] = Get-State $group
    Send-Key 0x27;                 $log['afterRight'] = Get-State $group
    Send-Key 0x09;                 $log['afterTabOut'] = Get-State $group
    Send-Key 0x09 -Shift;          $log['afterShiftTabBack'] = Get-State $group
    $log
}
$segmented | ConvertTo-Json -Depth 4

$navigation = Invoke-Page 'navigation' {
    param($root)
    $group = Get-Group $root 'Visão geral' 4
    $log = [ordered]@{ page = 'navigation'; group = ($group | ForEach-Object { $_.Current.Name }) -join ','; initial = Get-State $group }
    $unselected = $group | Where-Object { -not $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } | Select-Object -First 1
    $unselected.SetFocus(); Start-Sleep -Milliseconds 300
    $log['afterFocusUnselected'] = Get-State $group
    Send-Key 0x28;                 $log['afterDown'] = Get-State $group
    Send-Key 0x09;                 $log['afterTabOut'] = Get-State $group
    Send-Key 0x09 -Shift;          $log['afterShiftTabBack'] = Get-State $group
    $log
}
$navigation | ConvertTo-Json -Depth 4

@($segmented, $navigation) | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'keyboard-probe.json') -Encoding utf8
