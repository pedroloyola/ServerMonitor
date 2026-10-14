<#
.SYNOPSIS
    Counterproof for Assert-PackageRuntime.ps1: builds synthetic MSIX packages and proves the check
    rejects every way the package can fall back to a machine-wide .NET runtime.

.DESCRIPTION
    Each case mutates exactly one property of an otherwise valid synthetic self-contained package;
    the baseline case asserts the unmutated one is accepted. The synthetic runtime files are
    placeholders: the check reads their presence and the two JSON files, never their contents.

    Nothing here touches a real package, a real install, the network or Partner Center.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$checkScript = Join-Path $PSScriptRoot 'Assert-PackageRuntime.ps1'
if (-not (Test-Path -LiteralPath $checkScript)) { throw "Cannot find $checkScript" }

$Version = '10.0.11'
$RuntimeFiles = @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'clrjit.dll', 'System.Private.CoreLib.dll', 'System.Runtime.dll')
$Executables = @('ServerMonitor.App', 'ServerAlyzer.WidgetProvider')

function New-RuntimeConfig {
    param([string] $Version = $script:Version, [switch] $FrameworkDependent, [switch] $Both)
    $included = "`"includedFrameworks`": [ { `"name`": `"Microsoft.NETCore.App`", `"version`": `"$Version`" } ]"
    $shared = "`"framework`": { `"name`": `"Microsoft.NETCore.App`", `"version`": `"10.0.0`" }"
    $body = if ($Both) { "$included, $shared" } elseif ($FrameworkDependent) { $shared } else { $included }
    return "{ `"runtimeOptions`": { `"tfm`": `"net10.0`", $body } }"
}

function New-Deps {
    param(
        [string] $Exe = 'ServerMonitor.App',
        [string] $Version = $script:Version,
        [string] $Target = '.NETCoreApp,Version=v10.0/win-x64',
        [switch] $NoRuntimePack,
        [string] $ExtraAsset
    )
    $extra = if ($ExtraAsset) { ", `"lib/net8.0/$ExtraAsset`": {}" } else { '' }
    $packTarget = if ($NoRuntimePack) { '' } else { ", `"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/$Version`": { `"runtime`": { `"System.Private.CoreLib.dll`": {}, `"System.Runtime.dll`": {} }, `"native`": { `"coreclr.dll`": {}, `"clrjit.dll`": {} } }" }
    $packLibrary = if ($NoRuntimePack) { '' } else { ", `"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/$Version`": { `"type`": `"runtimepack`", `"serviceable`": false, `"sha512`": `"`" }" }
    return "{ `"runtimeTarget`": { `"name`": `"$Target`", `"signature`": `"`" }, `"targets`": { `"$Target`": { `"$Exe/1.0.0`": { `"runtime`": { `"$Exe.dll`": {}$extra } }$packTarget } }, `"libraries`": { `"$Exe/1.0.0`": { `"type`": `"project`" }$packLibrary } }"
}

function New-Manifest {
    param([switch] $NoAppRuntimeDependency)
    $dependency = if ($NoAppRuntimeDependency) { '' } else { '<PackageDependency Name="Microsoft.WindowsAppRuntime.2" MinVersion="2.3.1.0" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />' }
    return @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="PedroLoy.ServerAlyzer" Publisher="CN=32C0A056-FD57-422E-A59C-A8C26434951D" Version="1.1.1.0" ProcessorArchitecture="x64" />
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26100.0" />
    $dependency
  </Dependencies>
</Package>
"@
}

# Default content of a valid package; each case overrides or removes single entries.
function Get-DefaultEntries {
    $entries = [ordered] @{ 'AppxManifest.xml' = (New-Manifest) }
    foreach ($exe in $Executables) {
        $entries["$exe.exe"] = 'MZ'
        $entries["$exe.dll"] = 'MZ'
        $entries["$exe.runtimeconfig.json"] = (New-RuntimeConfig)
        $entries["$exe.deps.json"] = (New-Deps -Exe $exe)
    }
    foreach ($file in $RuntimeFiles) { $entries[$file] = 'MZ' }
    return $entries
}

function New-SyntheticPackage {
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [System.Collections.IDictionary] $Entries)
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            if ($null -eq $Entries[$name]) { continue }
            $stream = $archive.CreateEntry($name).Open()
            try {
                $bytes = [System.Text.Encoding]::UTF8.GetBytes([string] $Entries[$name])
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("package-runtime-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null
$results = New-Object System.Collections.Generic.List[object]

function Invoke-Case {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Mutate,
        [Parameter(Mandatory)] [ValidateSet('pass', 'fail')] [string] $Expected,
        [string] $ExpectedMessagePattern
    )

    $entries = Get-DefaultEntries
    & $Mutate $entries
    $packagePath = Join-Path $workDir ("$([guid]::NewGuid().ToString('n')).msix")
    New-SyntheticPackage -Path $packagePath -Entries $entries

    $outcome = 'pass'; $message = ''
    try { & $checkScript -PackagePath $packagePath *> $null }
    catch { $outcome = 'fail'; $message = $_.Exception.Message }

    $verdict = if ($outcome -eq $Expected) { 'OK' } else { 'TEST ERROR' }
    if ($verdict -eq 'OK' -and $Expected -eq 'fail' -and $ExpectedMessagePattern -and $message -notmatch $ExpectedMessagePattern) {
        $verdict = 'TEST ERROR'
        $message = "rejected, but for the wrong reason: $message"
    }
    $results.Add([pscustomobject] @{ Case = $Name; Expected = $Expected; Actual = $outcome; Verdict = $verdict; Detail = $message }) | Out-Null
    Remove-Item -LiteralPath $packagePath -Force -ErrorAction SilentlyContinue
}

try {
    Invoke-Case -Name 'baseline: self-contained package' -Expected 'pass' -Mutate { param($e) }

    Invoke-Case -Name 'baseline: runtime in a subfolder next to the executables' -Expected 'pass' -Mutate {
        param($e)
        foreach ($key in @($e.Keys)) { if ($key -ne 'AppxManifest.xml') { $e["App/$key"] = $e[$key]; $e.Remove($key) } }
    }

    Invoke-Case -Name 'app framework-dependent (the 1.1.2.2 defect)' -Expected 'fail' -ExpectedMessagePattern 'ServerMonitor\.App\.exe: runtimeconfig\.json references a shared framework' -Mutate {
        param($e) $e['ServerMonitor.App.runtimeconfig.json'] = (New-RuntimeConfig -FrameworkDependent)
    }

    Invoke-Case -Name 'widget provider framework-dependent' -Expected 'fail' -ExpectedMessagePattern 'ServerAlyzer\.WidgetProvider\.exe: runtimeconfig\.json references a shared framework' -Mutate {
        param($e) $e['ServerAlyzer.WidgetProvider.runtimeconfig.json'] = (New-RuntimeConfig -FrameworkDependent)
    }

    Invoke-Case -Name 'includedFrameworks AND a shared framework reference' -Expected 'fail' -ExpectedMessagePattern 'references a shared framework' -Mutate {
        param($e) $e['ServerMonitor.App.runtimeconfig.json'] = (New-RuntimeConfig -Both)
    }

    Invoke-Case -Name 'runtimeconfig.json missing' -Expected 'fail' -ExpectedMessagePattern 'runtimeconfig\.json is missing' -Mutate {
        param($e) $e.Remove('ServerAlyzer.WidgetProvider.runtimeconfig.json')
    }

    Invoke-Case -Name 'runtimeconfig.json not JSON' -Expected 'fail' -ExpectedMessagePattern 'not valid JSON' -Mutate {
        param($e) $e['ServerMonitor.App.runtimeconfig.json'] = '{ runtimeOptions'
    }

    Invoke-Case -Name 'deps.json missing' -Expected 'fail' -ExpectedMessagePattern 'deps\.json is missing' -Mutate {
        param($e) $e.Remove('ServerMonitor.App.deps.json')
    }

    Invoke-Case -Name 'deps.json not RID-specific' -Expected 'fail' -ExpectedMessagePattern 'not RID-specific' -Mutate {
        param($e) $e['ServerAlyzer.WidgetProvider.deps.json'] = (New-Deps -Exe 'ServerAlyzer.WidgetProvider' -Target '.NETCoreApp,Version=v10.0')
    }

    Invoke-Case -Name 'deps.json without the runtime pack' -Expected 'fail' -ExpectedMessagePattern 'expected exactly one' -Mutate {
        param($e) $e['ServerAlyzer.WidgetProvider.deps.json'] = (New-Deps -Exe 'ServerAlyzer.WidgetProvider' -NoRuntimePack)
    }

    Invoke-Case -Name 'deps.json and runtimeconfig.json disagree' -Expected 'fail' -ExpectedMessagePattern 'differs from runtimeconfig' -Mutate {
        param($e) $e['ServerMonitor.App.deps.json'] = (New-Deps -Version '10.0.12')
    }

    Invoke-Case -Name 'app and provider built against different runtimes' -Expected 'fail' -ExpectedMessagePattern 'different \.NET runtimes' -Mutate {
        param($e)
        $e['ServerAlyzer.WidgetProvider.runtimeconfig.json'] = (New-RuntimeConfig -Version '10.0.12')
        $e['ServerAlyzer.WidgetProvider.deps.json'] = (New-Deps -Exe 'ServerAlyzer.WidgetProvider' -Version '10.0.12')
    }

    foreach ($file in $RuntimeFiles) {
        Invoke-Case -Name "runtime file missing: $file" -Expected 'fail' -ExpectedMessagePattern ([regex]::Escape($file)) -Mutate ([scriptblock]::Create("param(`$e) `$e.Remove('$file')"))
    }

    Invoke-Case -Name 'runtime files not next to the executables' -Expected 'fail' -ExpectedMessagePattern 'runtime file\(s\) missing' -Mutate {
        param($e) foreach ($file in $RuntimeFiles) { $e["runtime/$file"] = $e[$file]; $e.Remove($file) }
    }

    Invoke-Case -Name 'expected executable missing' -Expected 'fail' -ExpectedMessagePattern 'expected exactly one in the package, found 0' -Mutate {
        param($e) $e.Remove('ServerAlyzer.WidgetProvider.exe')
    }

    Invoke-Case -Name 'provider depends on an assembly the app does not ship' -Expected 'fail' -ExpectedMessagePattern 'WidgetProvider\.exe: 1 asset\(s\) named by deps\.json are not in the package' -Mutate {
        param($e) $e['ServerAlyzer.WidgetProvider.deps.json'] = (New-Deps -Exe 'ServerAlyzer.WidgetProvider' -ExtraAsset 'Only.In.Provider.dll')
    }

    Invoke-Case -Name 'provider dependency present at the root' -Expected 'pass' -Mutate {
        param($e)
        $e['ServerAlyzer.WidgetProvider.deps.json'] = (New-Deps -Exe 'ServerAlyzer.WidgetProvider' -ExtraAsset 'Shared.Dependency.dll')
        $e['Shared.Dependency.dll'] = 'MZ'
    }

    Invoke-Case -Name 'Windows App Runtime dependency removed' -Expected 'fail' -ExpectedMessagePattern 'no PackageDependency on Microsoft\.WindowsAppRuntime' -Mutate {
        param($e) $e['AppxManifest.xml'] = (New-Manifest -NoAppRuntimeDependency)
    }

    Invoke-Case -Name 'Windows App SDK packaged self-contained' -Expected 'fail' -ExpectedMessagePattern 'Microsoft\.WindowsAppRuntime\.dll is in the payload' -Mutate {
        param($e) $e['Microsoft.WindowsAppRuntime.dll'] = 'MZ'
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
