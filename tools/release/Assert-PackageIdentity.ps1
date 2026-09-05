<#
.SYNOPSIS
    Validates a built MSIX against the Store product it claims to be, reading everything
    from inside the package rather than from the source tree.

.DESCRIPTION
    The source tree is what we intended to ship; the package is what we would actually
    ship. Every check here therefore opens the .msix and reads the real bytes:

      * Identity Name / Publisher / Version / ProcessorArchitecture from AppxManifest.xml
      * the application-level <Capabilities>, so a stray broadFileSystemAccess cannot slip in
      * every .exe in the payload is a GUI-subsystem PE (subsystem byte 2), which is what
        stops the widget provider and the app from flashing a console window
      * no test assemblies, PDBs or QA scripts in the payload
      * the SHA-256 of the package, printed and optionally written next to it

    Widget size declarations also use the element name <Capability>, but nested inside the
    widget <Definition>. Those are not app capabilities. This script only reads the children
    of the package-level <Capabilities> element, which is the distinction a naive grep of
    the manifest gets wrong.

.OUTPUTS
    Writes 'sha256' and 'packageVersion' to $env:GITHUB_OUTPUT when running in Actions.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackagePath,
    [Parameter(Mandatory)] [string] $ExpectedVersion,
    [string] $ExpectedIdentityName = 'PedroLoy.ServerAlyzer',
    [string] $ExpectedPublisher    = 'CN=32C0A056-FD57-422E-A59C-A8C26434951D',
    [string] $ExpectedArchitecture = 'x64',
    [string[]] $ExpectedExecutables = @('ServerMonitor.App.exe', 'ServerAlyzer.WidgetProvider.exe'),
    [string[]] $AllowedCapabilities = @('runFullTrust'),
    [string] $ExpectedSha256,
    [switch] $WriteChecksumFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$failures = New-Object System.Collections.Generic.List[string]
function Add-Failure([string] $m) { $failures.Add($m) | Out-Null; Write-Host "  FAIL  $m" }
function Add-Pass([string] $m) { Write-Host "  ok    $m" }

<#
    Reads the Subsystem field of a PE image straight out of the zip entry.

    Layout: offset 0x3C holds e_lfanew, which points at the "PE\0\0" signature. The COFF
    header is 20 bytes, then the optional header begins; Subsystem sits at offset 68 of the
    optional header in both PE32 and PE32+. 2 = IMAGE_SUBSYSTEM_WINDOWS_GUI, 3 = console.
#>
function Get-PeSubsystem {
    param([Parameter(Mandatory)] $Entry)

    $stream = $Entry.Open()
    try {
        $buffer = New-Object byte[] 1024
        $read = 0
        while ($read -lt $buffer.Length) {
            $chunk = $stream.Read($buffer, $read, $buffer.Length - $read)
            if ($chunk -le 0) { break }
            $read += $chunk
        }
        if ($read -lt 0x40) { return $null }

        if ($buffer[0] -ne 0x4D -or $buffer[1] -ne 0x5A) { return $null }   # 'MZ'

        $peOffset = [System.BitConverter]::ToInt32($buffer, 0x3C)
        $subsystemOffset = $peOffset + 4 + 20 + 68
        if ($peOffset -le 0 -or ($subsystemOffset + 2) -gt $read) { return $null }

        if ($buffer[$peOffset] -ne 0x50 -or $buffer[$peOffset + 1] -ne 0x45) { return $null }   # 'PE'

        return [System.BitConverter]::ToUInt16($buffer, $subsystemOffset)
    }
    finally { $stream.Dispose() }
}

if (-not (Test-Path -LiteralPath $PackagePath)) { throw "Package not found: $PackagePath" }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$packageInfo = Get-Item -LiteralPath $package

Write-Host 'Package identity check'
Write-Host "  package: $package"
Write-Host "  size:    $($packageInfo.Length) bytes"

$sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "  sha256:  $sha256"
if ($ExpectedSha256) {
    if ($sha256 -ne $ExpectedSha256.ToLowerInvariant()) {
        Add-Failure "SHA-256 is $sha256 but $ExpectedSha256 was expected"
    }
    else { Add-Pass 'SHA-256 matches the expected value' }
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $manifestEntry = $archive.Entries | Where-Object { $_.FullName -eq 'AppxManifest.xml' } | Select-Object -First 1
    if (-not $manifestEntry) { throw 'AppxManifest.xml not found inside the package.' }

    $reader = New-Object System.IO.StreamReader($manifestEntry.Open())
    try { $manifestXml = $reader.ReadToEnd() } finally { $reader.Dispose() }
    [xml] $manifest = $manifestXml

    # ------------------------------------------------------------------ identity
    $identity = $manifest.SelectSingleNode("//*[local-name()='Identity']")
    if ($null -eq $identity) { throw 'AppxManifest.xml has no <Identity> element.' }

    $actualName         = $identity.GetAttribute('Name')
    $actualPublisher    = $identity.GetAttribute('Publisher')
    $actualVersion      = $identity.GetAttribute('Version')
    $actualArchitecture = $identity.GetAttribute('ProcessorArchitecture')

    if ($actualName -ne $ExpectedIdentityName) { Add-Failure "Identity/@Name is '$actualName', expected '$ExpectedIdentityName'" }
    else { Add-Pass "Identity/@Name = $actualName" }

    if ($actualPublisher -ne $ExpectedPublisher) { Add-Failure "Identity/@Publisher is '$actualPublisher', expected '$ExpectedPublisher'" }
    else { Add-Pass "Identity/@Publisher = $actualPublisher" }

    if ($actualVersion -ne $ExpectedVersion) { Add-Failure "Identity/@Version is '$actualVersion', expected '$ExpectedVersion'" }
    else { Add-Pass "Identity/@Version = $actualVersion" }

    if ($actualArchitecture -ne $ExpectedArchitecture) { Add-Failure "Identity/@ProcessorArchitecture is '$actualArchitecture', expected '$ExpectedArchitecture'" }
    else { Add-Pass "Identity/@ProcessorArchitecture = $actualArchitecture" }

    # -------------------------------------------------- package-level capabilities
    $containers = $manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Capabilities']")
    $declared = New-Object System.Collections.Generic.List[string]
    foreach ($container in $containers) {
        foreach ($child in $container.ChildNodes) {
            if ($child.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
            $capabilityName = $child.GetAttribute('Name')
            if ($capabilityName) { $declared.Add($capabilityName) | Out-Null }
        }
    }
    $declaredUnique = @($declared | Sort-Object -Unique)
    $unexpected = @($declaredUnique | Where-Object { $AllowedCapabilities -notcontains $_ })
    if ($unexpected.Count -gt 0) { Add-Failure "unexpected app capability/capabilities: $($unexpected -join ', ')" }
    else { Add-Pass "app capabilities = $($declaredUnique -join ', ')" }

    # ------------------------------------------------------------ payload hygiene
    $payload = @($archive.Entries | Where-Object { $_.Name })
    $forbidden = @($payload | Where-Object {
        $_.Name -like '*.pdb' -or
        $_.Name -like '*.ps1' -or
        $_.Name -like '*Tests.dll' -or
        $_.Name -like '*Tests.exe' -or
        $_.FullName -like '.boss/*'
    })
    if ($forbidden.Count -gt 0) {
        $sample = @($forbidden | ForEach-Object { $_.FullName } | Select-Object -First 10) -join ', '
        Add-Failure "payload contains files that must not ship: $sample"
    }
    else { Add-Pass 'payload has no PDBs, scripts or test assemblies' }

    # -------------------------------------------------- executables and subsystem
    $executables = @($payload | Where-Object { $_.Name -like '*.exe' })
    $names = @($executables | ForEach-Object { $_.Name } | Sort-Object -Unique)
    $missing = @($ExpectedExecutables | Where-Object { $names -notcontains $_ })
    $extra   = @($names | Where-Object { $ExpectedExecutables -notcontains $_ })

    if ($missing.Count -gt 0) { Add-Failure "expected executable(s) missing from the package: $($missing -join ', ')" }
    if ($extra.Count -gt 0)   { Add-Failure "unexpected executable(s) in the package: $($extra -join ', ')" }
    if ($missing.Count -eq 0 -and $extra.Count -eq 0) { Add-Pass "executables = $($names -join ', ')" }

    foreach ($entry in $executables) {
        $subsystem = Get-PeSubsystem -Entry $entry
        if ($null -eq $subsystem) {
            Add-Failure "$($entry.FullName): could not read the PE subsystem"
            continue
        }
        if ($subsystem -ne 2) {
            Add-Failure "$($entry.FullName): PE subsystem is $subsystem, expected 2 (Windows GUI); a console-subsystem executable shows a console window"
        }
        else { Add-Pass "$($entry.FullName): PE subsystem 2 (Windows GUI)" }
    }
}
finally { $archive.Dispose() }

if ($WriteChecksumFile) {
    $checksumPath = "$package.sha256"
    "$sha256  $($packageInfo.Name)" | Out-File -LiteralPath $checksumPath -Encoding ascii
    Write-Host "  wrote $checksumPath"
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "PACKAGE IDENTITY CHECK FAILED ($($failures.Count) problem(s))"
    throw ($failures -join '; ')
}

Write-Host ''
Write-Host 'PACKAGE IDENTITY CHECK PASSED'

if ($env:GITHUB_OUTPUT) {
    "sha256=$sha256"                  | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "packageVersion=$ExpectedVersion" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
