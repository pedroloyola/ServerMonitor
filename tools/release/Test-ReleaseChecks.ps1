<#
.SYNOPSIS
    Counterproof for Assert-PackageIdentity.ps1: builds synthetic MSIX packages and proves
    the check fails on each defect it is supposed to catch.

.DESCRIPTION
    A validation script that only ever runs against a good package proves nothing, because
    a script that returns success unconditionally would look identical. Each case here
    mutates exactly one property of an otherwise valid synthetic package and asserts that
    the check rejects it; the baseline case asserts it accepts the unmutated one.

    Nothing here touches a real package, the network, or Partner Center.

.NOTES
    The synthetic executables are header-only PE stubs. Assert-PackageIdentity reads only
    the DOS/PE headers to find the Subsystem field, so a 1 KB stub exercises the real code
    path without shipping a binary into the repository.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$checkScript = Join-Path $PSScriptRoot 'Assert-PackageIdentity.ps1'
if (-not (Test-Path -LiteralPath $checkScript)) { throw "Cannot find $checkScript" }

$IdentityName = 'PedroLoy.ServerAlyzer'
$Publisher    = 'CN=32C0A056-FD57-422E-A59C-A8C26434951D'

# Subsystem constants from winnt.h.
$SUBSYSTEM_GUI     = 2
$SUBSYSTEM_CONSOLE = 3

function New-PeStub {
    <# Builds a minimal but structurally valid PE image with the requested subsystem. #>
    param([Parameter(Mandatory)] [int] $Subsystem)

    $bytes = New-Object byte[] 1024
    $bytes[0] = 0x4D; $bytes[1] = 0x5A                                   # 'MZ'

    $peOffset = 0x80
    [System.BitConverter]::GetBytes([int] $peOffset).CopyTo($bytes, 0x3C)

    $bytes[$peOffset]     = 0x50                                          # 'P'
    $bytes[$peOffset + 1] = 0x45                                          # 'E'
    $bytes[$peOffset + 2] = 0x00
    $bytes[$peOffset + 3] = 0x00

    # PE32+ optional header magic, immediately after the 20-byte COFF header.
    [System.BitConverter]::GetBytes([uint16] 0x020B).CopyTo($bytes, $peOffset + 4 + 20)

    # Subsystem lives at offset 68 of the optional header.
    [System.BitConverter]::GetBytes([uint16] $Subsystem).CopyTo($bytes, $peOffset + 4 + 20 + 68)

    return $bytes
}

function New-Manifest {
    param(
        [string] $Name         = $IdentityName,
        [string] $PublisherId  = $Publisher,
        [string] $Version      = '1.1.1.0',
        [string] $Architecture = 'x64',
        [string[]] $Capabilities = @('runFullTrust')
    )

    $capabilityXml = ($Capabilities | ForEach-Object { "    <rescap:Capability Name=`"$_`" />" }) -join "`n"

    # The nested <Capability> elements inside the widget Definition are the trap: they are
    # widget sizes, not app capabilities, and must not be counted.
    return @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3">
  <Identity Name="$Name" Publisher="$PublisherId" Version="$Version" ProcessorArchitecture="$Architecture" />
  <Applications>
    <Application Id="App">
      <Extensions>
        <uap3:Extension Category="windows.appExtension">
          <uap3:AppExtension Name="com.microsoft.windows.widgets" Id="ServerAlyzer" DisplayName="ServerAlyzer">
            <uap3:Properties>
              <Definition Id="ServerAlyzerWidget">
                <Capabilities>
                  <Capability>
                    <Size Name="small" />
                  </Capability>
                  <Capability>
                    <Size Name="medium" />
                  </Capability>
                </Capabilities>
              </Definition>
            </uap3:Properties>
          </uap3:AppExtension>
        </uap3:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
$capabilityXml
  </Capabilities>
</Package>
"@
}

function New-SyntheticPackage {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $ManifestXml = (New-Manifest),
        [hashtable] $Executables = @{ 'ServerMonitor.App.exe' = 2; 'ServerAlyzer.WidgetProvider.exe' = 2 },
        [string[]] $ExtraFiles = @()
    )

    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry('AppxManifest.xml')
        $stream = $entry.Open()
        try {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($ManifestXml)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally { $stream.Dispose() }

        foreach ($name in $Executables.Keys) {
            $entry = $archive.CreateEntry($name)
            $stream = $entry.Open()
            try {
                $bytes = New-PeStub -Subsystem $Executables[$name]
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }

        foreach ($name in $ExtraFiles) {
            $entry = $archive.CreateEntry($name)
            $stream = $entry.Open()
            try {
                $bytes = [System.Text.Encoding]::UTF8.GetBytes('placeholder')
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("release-checks-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

$results = New-Object System.Collections.Generic.List[object]

function Invoke-Case {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Build,
        [Parameter(Mandatory)] [ValidateSet('pass', 'fail')] [string] $Expected,
        [string] $ExpectedMessagePattern
    )

    $packagePath = Join-Path $workDir ("$([guid]::NewGuid().ToString('n')).msix")
    & $Build $packagePath

    $outcome = 'pass'
    $message = ''
    try {
        & $checkScript -PackagePath $packagePath -ExpectedVersion '1.1.1.0' `
            -ExpectedIdentityName $IdentityName -ExpectedPublisher $Publisher *> $null
    }
    catch {
        $outcome = 'fail'
        $message = $_.Exception.Message
    }

    $verdict = if ($outcome -eq $Expected) { 'OK' } else { 'TEST ERROR' }
    if ($verdict -eq 'OK' -and $Expected -eq 'fail' -and $ExpectedMessagePattern) {
        if ($message -notmatch $ExpectedMessagePattern) {
            $verdict = 'TEST ERROR'
            $message = "rejected, but for the wrong reason: $message"
        }
    }

    $results.Add([pscustomobject] @{
        Case     = $Name
        Expected = $Expected
        Actual   = $outcome
        Verdict  = $verdict
        Detail   = $message
    }) | Out-Null

    Remove-Item -LiteralPath $packagePath -Force -ErrorAction SilentlyContinue
}

try {
    Invoke-Case -Name 'baseline: valid package' -Expected 'pass' -Build {
        param($p) New-SyntheticPackage -Path $p
    }

    Invoke-Case -Name 'console subsystem on the widget provider' -Expected 'fail' -ExpectedMessagePattern 'PE subsystem is 3' -Build {
        param($p) New-SyntheticPackage -Path $p -Executables @{
            'ServerMonitor.App.exe' = $SUBSYSTEM_GUI
            'ServerAlyzer.WidgetProvider.exe' = $SUBSYSTEM_CONSOLE
        }
    }

    Invoke-Case -Name 'console subsystem on the main app' -Expected 'fail' -ExpectedMessagePattern 'PE subsystem is 3' -Build {
        param($p) New-SyntheticPackage -Path $p -Executables @{
            'ServerMonitor.App.exe' = $SUBSYSTEM_CONSOLE
            'ServerAlyzer.WidgetProvider.exe' = $SUBSYSTEM_GUI
        }
    }

    Invoke-Case -Name 'wrong package version' -Expected 'fail' -ExpectedMessagePattern 'Identity/@Version' -Build {
        param($p) New-SyntheticPackage -Path $p -ManifestXml (New-Manifest -Version '1.1.2.0')
    }

    Invoke-Case -Name 'wrong identity name' -Expected 'fail' -ExpectedMessagePattern 'Identity/@Name' -Build {
        param($p) New-SyntheticPackage -Path $p -ManifestXml (New-Manifest -Name 'PedroLoy.ServerAlyzer.Dev')
    }

    Invoke-Case -Name 'wrong publisher' -Expected 'fail' -ExpectedMessagePattern 'Identity/@Publisher' -Build {
        param($p) New-SyntheticPackage -Path $p -ManifestXml (New-Manifest -PublisherId 'CN=Somebody Else')
    }

    Invoke-Case -Name 'wrong architecture' -Expected 'fail' -ExpectedMessagePattern 'ProcessorArchitecture' -Build {
        param($p) New-SyntheticPackage -Path $p -ManifestXml (New-Manifest -Architecture 'arm64')
    }

    Invoke-Case -Name 'broadFileSystemAccess sneaked into capabilities' -Expected 'fail' -ExpectedMessagePattern 'unexpected app capability' -Build {
        param($p) New-SyntheticPackage -Path $p -ManifestXml (New-Manifest -Capabilities @('runFullTrust', 'broadFileSystemAccess'))
    }

    Invoke-Case -Name 'test assembly in the payload' -Expected 'fail' -ExpectedMessagePattern 'must not ship' -Build {
        param($p) New-SyntheticPackage -Path $p -ExtraFiles @('ServerMonitor.App.Tests.dll')
    }

    Invoke-Case -Name 'QA script in the payload' -Expected 'fail' -ExpectedMessagePattern 'must not ship' -Build {
        param($p) New-SyntheticPackage -Path $p -ExtraFiles @('QaKill.ps1')
    }

    Invoke-Case -Name 'unexpected extra executable' -Expected 'fail' -ExpectedMessagePattern 'unexpected executable' -Build {
        param($p) New-SyntheticPackage -Path $p -Executables @{
            'ServerMonitor.App.exe' = $SUBSYSTEM_GUI
            'ServerAlyzer.WidgetProvider.exe' = $SUBSYSTEM_GUI
            'ServerAlyzer.Debug.exe' = $SUBSYSTEM_GUI
        }
    }

    Invoke-Case -Name 'expected executable missing' -Expected 'fail' -ExpectedMessagePattern 'missing from the package' -Build {
        param($p) New-SyntheticPackage -Path $p -Executables @{ 'ServerMonitor.App.exe' = $SUBSYSTEM_GUI }
    }
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

$results | Format-Table -AutoSize Case, Expected, Actual, Verdict | Out-String -Width 200 | Write-Host

$broken = @($results | Where-Object { $_.Verdict -ne 'OK' })
if ($broken.Count -gt 0) {
    foreach ($case in $broken) { Write-Host "TEST ERROR  $($case.Case): expected $($case.Expected), got $($case.Actual). $($case.Detail)" }
    throw "$($broken.Count) of $($results.Count) counterproof case(s) did not behave as required."
}

Write-Host "All $($results.Count) counterproof cases behaved as required."
