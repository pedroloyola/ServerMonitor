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

    The gallery composes no services and touches no user data (see QaGalleryComposition). Output:
    <OutDir>\<page>-<theme>.png and a manifest.txt with the PIDs.

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
Add-Type -AssemblyName System.Drawing

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
        $arguments = "--qa-components --qa-gallery-page $page --qa-gallery-theme $theme"
        $process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru
        $started = $process.StartTime
        $info = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)"
        if ($info.CommandLine -notmatch '--qa-components' -or $info.ExecutablePath -ne $Exe) {
            Stop-Process -Id $process.Id -Force
            throw "PID $($process.Id) is not the expected gallery launch: $($info.CommandLine)"
        }

        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) { $process.Refresh(); if ($process.MainWindowHandle -ne 0 -or $process.HasExited) { break }; Start-Sleep -Milliseconds 250 }
        Start-Sleep -Seconds $SettleSeconds
        $process.Refresh()
        $file = Join-Path $OutDir "$page-$theme.png"
        $size = if (-not $process.HasExited -and $process.MainWindowHandle -ne 0) { Save-Window $process.MainWindowHandle $file ($page -like 'popup-*') } else { "NO WINDOW (exit $($process.ExitCode))" }

        $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($current -and $current.Path -eq $Exe -and $current.StartTime -eq $started) { Stop-Process -Id $process.Id -Force; $stopped = 'stopped' }
        elseif ($current) { $stopped = 'IDENTITY MISMATCH - not stopped' } else { $stopped = 'already exited' }
        $line = "$page-$theme PID=$($process.Id) $size $stopped"
        $log.Add($line); $line
    }
}
# Appended, so a partial re-shoot never erases the record of an earlier full run.
Add-Content (Join-Path $OutDir 'manifest.txt') ("# run $(Get-Date -Format o)")
$log | Add-Content (Join-Path $OutDir 'manifest.txt')
