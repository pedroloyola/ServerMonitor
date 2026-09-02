# M13-QA-10 — medição do board (a ativação que começa no widget).
#
# READ-ONLY. Este script apenas OBSERVA janelas: não fecha o board, não fecha nem mata nenhum processo,
# não sintetiza teclas nem cliques (nada de Win+W, Esc ou Alt-Tab automáticos) e não mexe na z-order.
# Quem carrega nas teclas e clica és tu; ele só regista o que o Windows fez.
#
# Uso:
#   pwsh -File board-test.ps1 -Seconds 45 -Tag A
#
# Regista, com hora ao milissegundo: quem tinha o foreground antes e depois, se nasceu um segundo
# processo ServerMonitor.App, se o ServerAlyzer chegou a SER a janela de foreground, e se a janela do
# WidgetBoard continuou visível. No fim imprime um resumo com o veredicto de cada pergunta.
param(
    [int]$Seconds = 45,
    [string]$Tag = 'A',
    [string]$Out = "$PSScriptRoot\logs\board-$Tag.txt"
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class BoardProbe
{
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);

    public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
    public static string Txt(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
    public static bool Topmost(IntPtr h) { return (GetWindowLong(h, -20) & 0x8) != 0; }

    public static uint ForegroundPid()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) { return 0; }
        uint pid; GetWindowThreadProcessId(h, out pid); return pid;
    }

    public static string Foreground()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) { return "hwnd=0 pid=0 class=<nenhuma>"; }
        uint pid; GetWindowThreadProcessId(h, out pid);
        return string.Format("hwnd=0x{0:X8} pid={1} class={2} topmost={3} title='{4}'",
            (long)h, pid, Cls(h), Topmost(h), Txt(h));
    }

    // Uma linha por janela de topo do processo: handle[classe,visível/oculta,TOPMOST].
    public static string WindowsOf(uint pid)
    {
        var sb = new StringBuilder();
        EnumWindows((h, p) =>
        {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner == pid && GetWindow(h, 4) == IntPtr.Zero)
            {
                sb.AppendFormat("0x{0:X8}[{1}{2}{3}] ", (long)h, Cls(h),
                    IsWindowVisible(h) ? ",visivel" : ",oculta", Topmost(h) ? ",TOPMOST" : "");
            }
            return true;
        }, IntPtr.Zero);
        return sb.ToString().Trim();
    }
}
'@ -Language CSharp

function Pids([string]$name) {
    (Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) -join ','
}

$lines = New-Object System.Collections.Generic.List[string]
function Log([string]$text) {
    $line = "[$([DateTime]::Now.ToString('HH:mm:ss.fff'))] $text"
    $lines.Add($line)
    Write-Host $line
}

$appPidsSeen = New-Object System.Collections.Generic.HashSet[int]
$fgPidsSeen = New-Object System.Collections.Generic.List[uint32]
$appEverForeground = $false
$boardEverVisible = $false
$boardVisibleAtEnd = $false
$appPid0 = (Get-Process -Name ServerMonitor.App -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1)

# Evidencia do ponto 4 (o OnActionInvoked do provider disparou?). So o pacote da SPIKE escreve este
# ficheiro; com o pacote de producao instalado ele simplesmente nao existe e a seccao fica vazia.
$spikeLog = Join-Path $env:LOCALAPPDATA 'ServerMonitor\qa10-spike-actions.log'
$spikeLinesBefore = if (Test-Path $spikeLog) { (Get-Content $spikeLog).Count } else { 0 }

Log "=== M13-QA-10  tag=$Tag  duracao=${Seconds}s  (READ-ONLY: so observa) ==="
Log "FAZ AGORA A ACAO DO CASO (Win+W e clicar o widget). Nao toques em mais nada ate ao fim."

$lastFg = ''; $lastProcs = ''; $lastBoard = ''; $lastApp = ''
$start = [DateTime]::UtcNow

while (([DateTime]::UtcNow - $start).TotalSeconds -lt $Seconds) {
    $fg = [BoardProbe]::Foreground()
    if ($fg -ne $lastFg) {
        $lastFg = $fg
        Log "FG    $fg"
        $fgPid = [BoardProbe]::ForegroundPid()
        if ($fgPid -ne 0) {
            $fgPidsSeen.Add($fgPid)
            $n = try { (Get-Process -Id $fgPid -ErrorAction Stop).ProcessName } catch { '' }
            if ($n -eq 'ServerMonitor.App') { $appEverForeground = $true }
        }
    }

    $appPids = Pids 'ServerMonitor.App'
    $procs = "app=$appPids provider=$(Pids 'ServerAlyzer.WidgetProvider') board=$(Pids 'WidgetBoard')"
    if ($procs -ne $lastProcs) { $lastProcs = $procs; Log "PROC  $procs" }
    foreach ($id in ($appPids -split ',')) { if ($id) { [void]$appPidsSeen.Add([int]$id) } }

    $boardProc = Get-Process -Name WidgetBoard -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($boardProc) {
        $b = [BoardProbe]::WindowsOf([uint32]$boardProc.Id)
        if ($b -ne $lastBoard) { $lastBoard = $b; Log "BOARD $b" }
        $boardVisibleAtEnd = $b -match 'WindowsDashboard,visivel'
        if ($boardVisibleAtEnd) { $boardEverVisible = $true }
    }

    $appProc = Get-Process -Name ServerMonitor.App -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1
    if ($appProc) {
        $appProc.Refresh()
        $a = "MainWindowHandle=0x{0:X8} janelas={1}" -f [int64]$appProc.MainWindowHandle, [BoardProbe]::WindowsOf([uint32]$appProc.Id)
        if ($a -ne $lastApp) { $lastApp = $a; Log "APP   $a" }
    }

    Start-Sleep -Milliseconds 25
}

Log "=== fim ==="
Log "RESUMO 1 - o ServerAlyzer chegou a ser a janela de FOREGROUND?  $(if ($appEverForeground) { 'SIM' } else { 'NAO' })"
Log "RESUMO 2 - a janela do board (WindowsDashboard) ficou VISIVEL no fim? $(if ($boardVisibleAtEnd) { 'SIM' } elseif ($boardEverVisible) { 'NAO (abriu e fechou)' } else { 'nunca a vi aberta' })"
Log "RESUMO 3 - processos ServerMonitor.App vistos: $($appPidsSeen -join ', ') (o inicial era $(if ($appPid0) { $appPid0.Id } else { 'nenhum' }))"
Log "RESUMO 4 - PIDs que tiveram foreground, por ordem: $($fgPidsSeen -join ' -> ')"

$spikeAdded = @()
if (Test-Path $spikeLog) {
    $all = @(Get-Content $spikeLog)
    if ($all.Count -gt $spikeLinesBefore) { $spikeAdded = $all[$spikeLinesBefore..($all.Count - 1)] }
}
if ($spikeAdded.Count -gt 0) {
    Log "RESUMO 5 - o provider recebeu $($spikeAdded.Count) OnActionInvoked durante esta medicao:"
    foreach ($l in $spikeAdded) { Log "          $l" }
} else {
    Log "RESUMO 5 - o provider NAO recebeu nenhum OnActionInvoked (ou este pacote nao e o da spike)"
}

Log "NOTA - o veredicto de 'o board saiu da frente' e TEU, a olho. Os PIDs acima nao chegam para o decidir."

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
Set-Content -Path $Out -Value $lines -Encoding UTF8
Write-Host ""
Write-Host "Log guardado em: $Out"
