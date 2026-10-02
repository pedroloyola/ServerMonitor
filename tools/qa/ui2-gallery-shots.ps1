<#
.SYNOPSIS
    UI.2 gallery screenshot contract: one PNG per (page x theme) of the Debug-only component gallery.

.DESCRIPTION
    For every page and theme it launches the EXACT Debug executable with
    --qa-components --qa-gallery-page <page> --qa-gallery-theme <theme>, verifies that the started process' command
    line contains --qa-components and that its image path is that executable, waits for the window, captures ONLY that
    window (PrintWindow of its HWND; for popup-* pages a screen copy of the same window rectangle after bringing it to
    the foreground, because WinUI popups may be separate windows), and stops exactly that PID (image path + start time
    re-checked). It never stops a process by name and never launches without a --qa-* flag.

    FULL PAGE (R1, Prism nit): after the first viewport, a non-popup page is scrolled by UI Automation (ScrollPattern of
    the page's root ScrollViewer, AutomationId "PageScroll") one large step at a time, one numbered shot per step,
    until the end of the page.

    The gallery composes no services and touches no user data (see QaGalleryComposition). Output:
    <OutDir>\<page>-<theme>.png (first viewport), <page>-<theme>-01.png ... (next viewports) and a manifest.txt.

.EXAMPLE
    pwsh tools/qa/ui2-gallery-shots.ps1 -OutDir C:\path\.boss\evidence\ui2\gallery
    pwsh tools/qa/ui2-gallery-shots.ps1 -Pages buttons,forms -Themes dark,light
#>
param(
    [string]$Exe,
    [string[]]$Pages,
    [string[]]$Themes = @('dark', 'light', 'hc-sim'),
    [string]$OutDir,
    [int]$SettleSeconds = 3
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $Exe) { $Exe = Join-Path $repo 'src\ServerMonitor.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ServerMonitor.App.exe' }
if (-not (Test-Path $Exe)) { throw "Debug executable not found: $Exe (build ServerMonitor.slnx -c Debug first)" }
$Exe = (Resolve-Path $Exe).Path
if (-not $OutDir) { $OutDir = Join-Path $repo '.boss\evidence\ui2\gallery' }
New-Item -ItemType Directory -Force $OutDir | Out-Null

if (-not $Pages) {
    # The page ids are the policy's own list, so the helper never drifts from the gallery.
    $policy = Get-Content (Join-Path $repo 'src\ServerMonitor.App\Services\QaGalleryPolicy.cs') -Raw
    $list = [regex]::Match($policy, 'Pages \{ get; \} =\s*\[(?<ids>[^\]]+)\]').Groups['ids'].Value
    $Pages = [regex]::Matches($list, '"([a-z\-]+)"') | ForEach-Object { $_.Groups[1].Value }
}

Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaShotNative {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes

function Save-Window([IntPtr]$handle, [string]$path, [bool]$fromScreen) {
    $r = New-Object QaShotNative+RECT
    [void][QaShotNative]::GetWindowRect($handle, [ref]$r)
    $w = $r.R - $r.L; $h = $r.B - $r.T
    $bitmap = [System.Drawing.Bitmap]::new($w, $h)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    if ($fromScreen) {
        [void][QaShotNative]::SetForegroundWindow($handle)
        Start-Sleep -Milliseconds 400
        $graphics.CopyFromScreen($r.L, $r.T, 0, 0, [System.Drawing.Size]::new($w, $h))
    } else {
        $dc = $graphics.GetHdc(); [void][QaShotNative]::PrintWindow($handle, $dc, 2); $graphics.ReleaseHdc($dc)
    }
    $graphics.Dispose(); $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
    "${w}x${h}"
}

$log = [System.Collections.Generic.List[string]]::new()
foreach ($page in $Pages) {
    foreach ($theme in $Themes) {
        $arguments = @('--qa-components', '--qa-gallery-page', $page, '--qa-gallery-theme', $theme)
        # The only launch path (UI.3 rule): Start-QaApp verifies the PID's image + command line, or stops it and throws.
        $app = & (Join-Path $PSScriptRoot 'Start-QaApp.ps1') -Exe $Exe -Arguments $arguments
        $process = $app.Process
        $started = $app.StartTime

        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) { $process.Refresh(); if ($process.MainWindowHandle -ne 0 -or $process.HasExited) { break }; Start-Sleep -Milliseconds 250 }
        Start-Sleep -Seconds $SettleSeconds
        $process.Refresh()
        $file = Join-Path $OutDir "$page-$theme.png"
        $size = if (-not $process.HasExited -and $process.MainWindowHandle -ne 0) { Save-Window $process.MainWindowHandle $file ($page -like 'popup-*') } else { "NO WINDOW (exit $($process.ExitCode))" }
        $steps = 0
        if ($page -notlike 'popup-*' -and -not $process.HasExited -and $process.MainWindowHandle -ne 0) {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            $scroller = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PageScroll')))
            if ($scroller) {
                $pattern = $scroller.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
                while ($pattern.Current.VerticallyScrollable -and $pattern.Current.VerticalScrollPercent -lt 99.5 -and $steps -lt 20) {
                    $pattern.ScrollVertical([System.Windows.Automation.ScrollAmount]::LargeIncrement)
                    Start-Sleep -Milliseconds 600
                    $steps++
                    [void](Save-Window $process.MainWindowHandle (Join-Path $OutDir ("$page-$theme-{0:D2}.png" -f $steps)) $false)
                }
            }
        }

        $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($current -and $current.Path -eq $Exe -and $current.StartTime -eq $started) { Stop-Process -Id $process.Id -Force; $stopped = 'stopped' }
        elseif ($current) { $stopped = 'IDENTITY MISMATCH - not stopped' } else { $stopped = 'already exited' }
        $line = "$page-$theme PID=$($process.Id) $size +$steps scroll shots $stopped"
        $log.Add($line); $line
    }
}
# Appended, so a partial re-shoot never erases the record of an earlier full run.
Add-Content (Join-Path $OutDir 'manifest.txt') ("# run $(Get-Date -Format o)")
$log | Add-Content (Join-Path $OutDir 'manifest.txt')
