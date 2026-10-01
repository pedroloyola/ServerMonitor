<#
.SYNOPSIS
    UI.2 R1 dialog keyboard probe (Cortex R-2, Beacon F-1): which button a Sa dialog focuses first, what a real Enter
    key press returns, and whether any pixel of the legacy accent family (#1846E1 ...) is on the dialog's buttons.

.DESCRIPTION
    For each (page x theme) it launches the EXACT Debug executable with
    --qa-components --qa-gallery-page <popup-dialog|popup-dialog-confirm> --qa-gallery-theme <theme>, verifies the
    started process' command line and image path, waits for the dialog (opened on load by QaPopupDialogPage), then:
      1. reads the page's "DialogInitialFocus" text by UI Automation (AutomationId),
      2. copies the window rectangle from the screen and counts, inside the dialog buttons' UIA bounds, pixels within
         +-6 per channel of the legacy accent family (-1 = buttons not found),
      3. brings the window to the foreground and sends ONE real Enter key press (keybd_event),
      4. reads "DialogResult" by UI Automation,
    and stops exactly that PID (image path + start time re-checked). Never stops a process by name.
    Expected: popup-dialog -> focus "Cancelar", Enter -> None; popup-dialog-confirm -> focus "Guardar", Enter -> Primary;
    popup-dialog-input (Kind unset, focused text box) -> DefaultButton Primary, Enter -> Primary (R2, Cortex C2-1).
    R2 (Cortex C2-2): one Tab and one Shift+Tab from the initial focus are recorded (focus moves between the dialog's
    own controls and comes back) before the Enter.

.EXAMPLE
    pwsh tools/qa/ui2-dialog-probe.ps1 -OutDir C:\path\.boss\evidence\ui2\dialog-probe
#>
param(
    [string]$Exe,
    [string[]]$Pages = @('popup-dialog', 'popup-dialog-confirm', 'popup-dialog-input'),
    [string[]]$Themes = @('dark', 'light', 'hc-sim'),
    [string]$OutDir,
    [int]$SettleSeconds = 4
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $Exe) { $Exe = Join-Path $repo 'src\ServerMonitor.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ServerMonitor.App.exe' }
if (-not (Test-Path $Exe)) { throw "Debug executable not found: $Exe (build ServerMonitor.slnx -c Debug first)" }
$Exe = (Resolve-Path $Exe).Path
if (-not $OutDir) { $OutDir = Join-Path $repo '.boss\evidence\ui2\dialog-probe' }
New-Item -ItemType Directory -Force $OutDir | Out-Null

Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaDialogNative {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  // Pixels within tol per channel of ANY of the colours (0xRRGGBB), inside the given rectangle.
  public static int CountNear(System.Drawing.Bitmap b, System.Drawing.Rectangle area, int[] colours, int tol) {
    var d = b.LockBits(new System.Drawing.Rectangle(0, 0, b.Width, b.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    var px = new byte[d.Stride * d.Height]; Marshal.Copy(d.Scan0, px, 0, px.Length); b.UnlockBits(d);
    area.Intersect(new System.Drawing.Rectangle(0, 0, d.Width, d.Height));
    int n = 0;
    for (int y = area.Top; y < area.Bottom; y++) for (int x = area.Left; x < area.Right; x++) {
      int i = y * d.Stride + x * 4;
      foreach (var c in colours) {
        if (Math.Abs(px[i + 2] - ((c >> 16) & 255)) <= tol && Math.Abs(px[i + 1] - ((c >> 8) & 255)) <= tol && Math.Abs(px[i] - (c & 255)) <= tol) { n++; break; }
      }
    }
    return n;
  }
}
"@ -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes

function Read-Text([IntPtr]$handle, [string]$automationId) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($element) { $element.Current.Name } else { "<$automationId not found>" }
}

# The legacy accent family of DesignTokens.xaml (SystemAccentColor + Light1-3 + Dark1-3 + BrandAccentPressed): the
# Fluent AccentButtonStyle paints #1846E1 in Light and #4D74F7 (Light2) in Dark. Scanned only inside the dialog's
# command buttons (UIA bounds, inflated 16 px), because the gallery's own chrome (theme radio buttons) is legacy-accented.
$legacyAccent = [int[]]@(0x1846E1, 0x2957F5, 0x4D74F7, 0x7E9CF9, 0x1338B5, 0x0F2C8E, 0x0A1E63, 0x1036B8)

function Measure-Accent([IntPtr]$handle, [string]$path, [string[]]$buttonNames) {
    $r = New-Object QaDialogNative+RECT
    [void][QaDialogNative]::GetWindowRect($handle, [ref]$r)
    $w = $r.R - $r.L; $h = $r.B - $r.T
    $bitmap = [System.Drawing.Bitmap]::new($w, $h)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($r.L, $r.T, 0, 0, [System.Drawing.Size]::new($w, $h)); $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) |
        Where-Object { $buttonNames -contains $_.Current.Name }
    if (-not $buttons) { $bitmap.Dispose(); return -1 }
    $left = ($buttons | ForEach-Object { $_.Current.BoundingRectangle.Left } | Measure-Object -Minimum).Minimum - $r.L - 16
    $top = ($buttons | ForEach-Object { $_.Current.BoundingRectangle.Top } | Measure-Object -Minimum).Minimum - $r.T - 16
    $right = ($buttons | ForEach-Object { $_.Current.BoundingRectangle.Right } | Measure-Object -Maximum).Maximum - $r.L + 16
    $bottom = ($buttons | ForEach-Object { $_.Current.BoundingRectangle.Bottom } | Measure-Object -Maximum).Maximum - $r.T + 16
    $area = [System.Drawing.Rectangle]::FromLTRB([int]$left, [int]$top, [int]$right, [int]$bottom)
    $hits = [QaDialogNative]::CountNear($bitmap, $area, $legacyAccent, 6)
    $bitmap.Dispose()
    $hits
}

$results = [System.Collections.Generic.List[object]]::new()
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
        $handle = $process.MainWindowHandle
        if ($process.HasExited -or $handle -eq 0) {
            $entry = [ordered]@{ page = $page; theme = $theme; pid = $process.Id; error = "no window (exit $($process.ExitCode))" }
            $results.Add($entry); ($entry | ConvertTo-Json -Compress); continue
        }
        [void][QaDialogNative]::SetForegroundWindow($handle)
        Start-Sleep -Milliseconds 500

        $focus = Read-Text $handle 'DialogInitialFocus'
        $accentPixels = Measure-Accent $handle (Join-Path $OutDir "$page-$theme.png") @('Cancelar', 'Guardar', 'Remover servidor')
        [void][QaDialogNative]::SetForegroundWindow($handle)
        Start-Sleep -Milliseconds 300
        $defaultButton = Read-Text $handle 'DialogDefaultButton'
        # Keys go to whatever window is in the foreground: refuse to inject unless it is this gallery (another desktop
        # session/agent can take the foreground). A dialog popup may be its own window, so compare the owning process.
        $foregroundOk = $false
        for ($attempt = 0; $attempt -lt 5 -and -not $foregroundOk; $attempt++) {
            [void][QaDialogNative]::SetForegroundWindow($handle)
            Start-Sleep -Milliseconds 300
            $owner = 0
            [void][QaDialogNative]::GetWindowThreadProcessId([QaDialogNative]::GetForegroundWindow(), [ref]$owner)
            $foregroundOk = $owner -eq $process.Id
        }
        if (-not $foregroundOk) {
            $entry = [ordered]@{ page = $page; theme = $theme; pid = $process.Id; initialFocus = $focus; defaultButton = $defaultButton; error = 'foreground owned by another process - keys not sent' }
            $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
            if ($current -and $current.Path -eq $Exe -and $current.StartTime -eq $started) { Stop-Process -Id $process.Id -Force }
            $results.Add($entry); ($entry | ConvertTo-Json -Compress); continue
        }
        $tabOrder = @()
        foreach ($shift in @($false, $true)) {
            if ($shift) { [QaDialogNative]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero) }
            [QaDialogNative]::keybd_event(0x09, 0, 0, [UIntPtr]::Zero)
            [QaDialogNative]::keybd_event(0x09, 0, 2, [UIntPtr]::Zero)
            if ($shift) { [QaDialogNative]::keybd_event(0x10, 0, 2, [UIntPtr]::Zero) }
            Start-Sleep -Milliseconds 400
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            $tabOrder += ('{0}:{1}' -f $(if ($shift) { 'Shift+Tab' } else { 'Tab' }), $focused.Current.Name)
        }
        [QaDialogNative]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero)
        [QaDialogNative]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 1500
        $result = Read-Text $handle 'DialogResult'

        $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($current -and $current.Path -eq $Exe -and $current.StartTime -eq $started) { Stop-Process -Id $process.Id -Force; $stopped = 'stopped' }
        elseif ($current) { $stopped = 'IDENTITY MISMATCH - not stopped' } else { $stopped = 'already exited' }
        $entry = [ordered]@{ page = $page; theme = $theme; pid = $process.Id; initialFocus = $focus; defaultButton = $defaultButton; tabOrder = ($tabOrder -join ' -> '); enterResult = $result; accentPixels = $accentPixels; process = $stopped }
        $results.Add($entry)
        ($entry | ConvertTo-Json -Compress)
    }
}
$results | ConvertTo-Json | Set-Content (Join-Path $OutDir 'dialog-probe.json') -Encoding utf8
