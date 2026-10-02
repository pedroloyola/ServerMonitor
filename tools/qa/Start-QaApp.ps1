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
    is not a Debug x64 build (a Release build ignores every --qa flag and would start production). The executable is
    judged on its CANONICAL, OS-resolved path (no '.'/'..' segment; junctions/symlinks resolved; never under Program Files
    or WindowsApps; must be <worktree with ServerMonitor.slnx>\src\ServerMonitor.App\bin\x64\Debug\<tfm>\win-x64\), and
    before the start the binary itself is read as metadata: ServerMonitor.App.dll must be AssemblyConfiguration 'Debug'
    and contain ServerMonitor.App.Qa.QaStartupIsolation.

    The lists below mirror QaStartupIsolation.HarnessFlags / ModifierFlags; QaLaunchRefusalTests fails if they drift.

    After the start it re-reads the PID's image path and command line; a mismatch stops exactly that PID (image path +
    start time re-checked) and throws. Returns PID, StartTime, the Process object and the verified command line.

    -ValidateOnly runs every check (the binary one too when the executable exists) and prints ALLOWED, but never starts
    anything (used by the tests).

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
$HarnessFlags = @('--qa-health', '--qa-discovery', '--qa-notifications', '--qa-compact', '--qa-history', '--qa-workloads', '--qa-store-screenshot', '--qa-proxyjump', '--qa-overview')
$ModifierFlags = @('--qa-ssh-config', '--qa-ui-language', '--qa-backup', '--qa-proxyjump-dir', '--qa-overview-scenario')
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

if (-not ('QaLaunch.Native' -as [type])) {
Add-Type -Namespace QaLaunch -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, System.Text.StringBuilder path, uint size, uint flags);
'@
}

# The REAL location of a path (Vigil M-1A-R1): every junction/symlink on the way resolved by the OS. A path that does not
# exist yet resolves through its deepest existing ancestor; the missing tail is appended unchanged.
function Get-FinalPath([string]$fullPath) {
    $existing = $fullPath; $tail = @()
    while ($existing -and -not (Test-Path -LiteralPath $existing)) {
        $tail = @([IO.Path]::GetFileName($existing)) + $tail
        $existing = [IO.Path]::GetDirectoryName($existing)
    }
    if (-not $existing) { return $fullPath }
    # access 0 = attributes only (no data, no OneDrive hydration); BACKUP_SEMANTICS opens directories too.
    $handle = [QaLaunch.Native]::CreateFileW($existing, 0, 7, [IntPtr]::Zero, 3, 0x02000000, [IntPtr]::Zero)
    if ($handle.IsInvalid) { throw "QA launch refused: cannot resolve the real location of '$existing'." }
    try {
        $buffer = [System.Text.StringBuilder]::new(32768)
        if ([QaLaunch.Native]::GetFinalPathNameByHandleW($handle, $buffer, 32768, 0) -eq 0) { throw "QA launch refused: cannot resolve the real location of '$existing'." }
        $final = $buffer.ToString() -replace '^\\\\\?\\UNC\\', '\\' -replace '^\\\\\?\\', ''
    }
    finally { $handle.Dispose() }
    if ($tail.Count -gt 0) { $final = [IO.Path]::Combine([string[]](@($final) + $tail)) }
    $final
}

# Canonical-first (Vigil M-1A-R1): no '.'/'..' segment, then the OS-resolved real path must be a Debug x64 build output of
# a ServerMonitor worktree - never an installation (Program Files / WindowsApps).
function Get-QaExecutableRefusal([string]$exe) {
    if (@($exe -split '[\\/]' | Where-Object { $_ -eq '..' -or $_ -eq '.' }).Count -gt 0) { return "'$exe' contains a '.' or '..' segment" }
    if (-not [IO.Path]::IsPathFullyQualified($exe)) { return "'$exe' is not an absolute path" }
    $installRoots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') + '\' }
    $isInstall = { param($p) $p -match '\\WindowsApps\\' -or @($installRoots | Where-Object { $p.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0 }
    $full = [IO.Path]::GetFullPath($exe)
    if (& $isInstall $full) { return "'$exe' is in an installation folder ($full)" }
    $final = Get-FinalPath $full
    if (& $isInstall $final) { return "'$exe' resolves to an installation folder ($final)" }
    $match = [regex]::Match($final, '^(?<root>.+?)\\src\\ServerMonitor\.App\\bin\\x64\\Debug\\[^\\]+\\win-x64\\ServerMonitor\.App\.exe$', 'IgnoreCase')
    if (-not $match.Success) { return "'$exe' resolves to '$final', not <worktree>\src\ServerMonitor.App\bin\x64\Debug\<tfm>\win-x64\ServerMonitor.App.exe (a Release build ignores --qa flags and starts production)" }
    if (-not (Test-Path -LiteralPath (Join-Path $match.Groups['root'].Value 'ServerMonitor.slnx') -PathType Leaf)) { return "'$final' is not inside a ServerMonitor worktree (no ServerMonitor.slnx at $($match.Groups['root'].Value))" }
    $null
}

# The binary itself, read as metadata only (never loaded or run): ServerMonitor.App.dll next to the exe must be a Debug
# build that carries the gate 1A isolation (ServerMonitor.App.Qa.QaStartupIsolation).
function Get-QaBinaryRefusal([string]$exe) {
    $dll = [IO.Path]::ChangeExtension($exe, '.dll')
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { return "no ServerMonitor.App.dll next to '$exe'" }
    $stream = [IO.File]::OpenRead($dll)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $pe.HasMetadata) { return "'$dll' is not a .NET assembly" }
        $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $configuration = $null
        foreach ($handle in $md.GetAssemblyDefinition().GetCustomAttributes()) {
            $attribute = $md.GetCustomAttribute($handle)
            $typeName = switch ($attribute.Constructor.Kind) {
                ([System.Reflection.Metadata.HandleKind]::MemberReference) {
                    $parent = $md.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor).Parent
                    if ($parent.Kind -eq [System.Reflection.Metadata.HandleKind]::TypeReference) { $md.GetString($md.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$parent).Name) }
                }
                ([System.Reflection.Metadata.HandleKind]::MethodDefinition) {
                    # The attribute type is defined in this very assembly (e.g. System.Private.CoreLib).
                    $md.GetString($md.GetTypeDefinition($md.GetMethodDefinition([System.Reflection.Metadata.MethodDefinitionHandle]$attribute.Constructor).GetDeclaringType()).Name)
                }
            }
            if ($typeName -ne 'AssemblyConfigurationAttribute') { continue }
            $blob = $md.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -eq 1) { $configuration = $blob.ReadSerializedString() }
        }
        if ($configuration -cne 'Debug') { return "'$dll' is a '$configuration' build, not Debug" }
        $isolation = $false
        foreach ($handle in $md.TypeDefinitions) {
            $type = $md.GetTypeDefinition($handle)
            if ($md.GetString($type.Name) -ceq 'QaStartupIsolation' -and $md.GetString($type.Namespace) -ceq 'ServerMonitor.App.Qa') { $isolation = $true; break }
        }
        if (-not $isolation) { return "'$dll' lacks ServerMonitor.App.Qa.QaStartupIsolation (pre-1A or non-QA build)" }
    }
    finally { $stream.Dispose() }
    $null
}

$refusal = Get-QaLaunchRefusal $Arguments
if ($refusal) { throw "QA launch refused: $refusal" }
$exeRefusal = Get-QaExecutableRefusal $Exe
if ($exeRefusal) { throw "QA launch refused: $exeRefusal" }
$exists = Test-Path -LiteralPath $Exe -PathType Leaf
if ($exists) {
    # Also under -ValidateOnly, so the binary rule is provable without ever reaching Start-Process.
    $Exe = Get-FinalPath ([IO.Path]::GetFullPath($Exe))
    $binaryRefusal = Get-QaBinaryRefusal $Exe
    if ($binaryRefusal) { throw "QA launch refused: $binaryRefusal" }
}
if ($ValidateOnly) { 'ALLOWED'; return }
if (-not $exists) { throw "QA launch: executable not found: $Exe" }

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
