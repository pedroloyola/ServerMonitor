<#
.SYNOPSIS
    Proves, from the bytes inside a built MSIX, that every .NET executable in the package carries its
    own .NET runtime (self-contained) and that the Windows App Runtime is still a framework package.

.DESCRIPTION
    REL.0C. Up to 1.1.2.2 the production package was framework-dependent on .NET: both
    runtimeconfig.json files named the shared framework Microsoft.NETCore.App 10.0.0 and the package
    had no coreclr.dll. Windows does not ship .NET 10, and the Store has no .NET framework package
    an MSIX can depend on, so such a package only starts on a machine where somebody installed the
    .NET 10 runtime machine-wide. It worked on the development machine for exactly that reason.

    This check reads the package, never the source tree, and fails closed:

      * every expected executable X.exe has X.runtimeconfig.json and X.deps.json next to it;
      * each runtimeconfig.json declares runtimeOptions.includedFrameworks with Microsoft.NETCore.App
        and declares NO runtimeOptions.framework / frameworks (that is what makes the host run the
        app self-contained and never look for a machine-wide runtime);
      * each deps.json targets a runtime identifier (".../win-x64") and lists the
        runtimepack.Microsoft.NETCore.App.Runtime.<rid> library at the same version;
      * every executable reports the SAME Microsoft.NETCore.App version: the executables share ONE
        runtime payload at the package root, so two versions would mean one of them runs against
        files that are not the ones its deps.json describes;
      * the runtime files the host needs are present next to the executables (hostfxr, hostpolicy,
        coreclr, clrjit, System.Private.CoreLib, System.Runtime);
      * the Windows App Runtime is still a framework package (ADR-017): AppxManifest.xml declares a
        PackageDependency on Microsoft.WindowsAppRuntime.*, and the self-contained Windows App SDK
        payload (Microsoft.WindowsAppRuntime.dll) is NOT in the package.

    Nothing here touches a real install, the network or Partner Center.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackagePath,
    [string[]] $ExpectedExecutables = @('ServerMonitor.App.exe', 'ServerAlyzer.WidgetProvider.exe'),
    [string[]] $RequiredRuntimeFiles = @(
        'hostfxr.dll',
        'hostpolicy.dll',
        'coreclr.dll',
        'clrjit.dll',
        'System.Private.CoreLib.dll',
        'System.Runtime.dll'
    ),
    [string] $RuntimeIdentifier = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$failures = New-Object System.Collections.Generic.List[string]
function Add-Failure([string] $m) { $failures.Add($m) | Out-Null; Write-Host "  FAIL  $m" }
function Add-Pass([string] $m) { Write-Host "  ok    $m" }

function Read-EntryText($entry) {
    $reader = New-Object System.IO.StreamReader($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Get-Property($object, [string] $name) {
    if ($null -eq $object) { return $null }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

if (-not (Test-Path -LiteralPath $PackagePath)) { throw "Package not found: $PackagePath" }
$package = (Resolve-Path -LiteralPath $PackagePath).Path

Write-Host 'Package runtime check'
Write-Host "  package: $(Split-Path -Leaf $package)"

$runtimePackName = "runtimepack.Microsoft.NETCore.App.Runtime.$RuntimeIdentifier"
$versions = @{}

$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    # Zip entry names are case-preserving; the package file system is not case-sensitive.
    $entries = @{}
    foreach ($entry in $archive.Entries) {
        if (-not $entry.Name) { continue }
        $entries[$entry.FullName.Replace('\', '/').ToLowerInvariant()] = $entry
    }

    $executableDirectories = New-Object System.Collections.Generic.List[string]

    foreach ($exeName in $ExpectedExecutables) {
        $exe = @($archive.Entries | Where-Object { $_.Name -ieq $exeName })
        if ($exe.Count -ne 1) {
            Add-Failure "$exeName`: expected exactly one in the package, found $($exe.Count)"
            continue
        }
        $exePath = $exe[0].FullName.Replace('\', '/')
        $directory = if ($exePath.Contains('/')) { $exePath.Substring(0, $exePath.LastIndexOf('/') + 1) } else { '' }
        $executableDirectories.Add($directory.ToLowerInvariant()) | Out-Null
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($exeName)

        # ------------------------------------------------------------ runtimeconfig.json
        $configKey = ($directory + $stem + '.runtimeconfig.json').ToLowerInvariant()
        if (-not $entries.ContainsKey($configKey)) {
            Add-Failure "$exeName`: $stem.runtimeconfig.json is missing"
            continue
        }
        try { $config = Read-EntryText $entries[$configKey] | ConvertFrom-Json }
        catch { Add-Failure "$exeName`: $stem.runtimeconfig.json is not valid JSON"; continue }

        $options = Get-Property $config 'runtimeOptions'
        if ($null -eq $options) { Add-Failure "$exeName`: runtimeconfig.json has no runtimeOptions"; continue }

        $framework = Get-Property $options 'framework'
        $frameworks = Get-Property $options 'frameworks'
        if ($null -ne $framework -or $null -ne $frameworks) {
            Add-Failure "$exeName`: runtimeconfig.json references a shared framework (framework-dependent): the host would look for a machine-wide .NET runtime"
        }

        $included = @(Get-Property $options 'includedFrameworks')
        $netCore = @($included | Where-Object { $null -ne $_ -and (Get-Property $_ 'name') -eq 'Microsoft.NETCore.App' })
        if ($netCore.Count -ne 1) {
            Add-Failure "$exeName`: runtimeconfig.json does not declare includedFrameworks Microsoft.NETCore.App (not self-contained)"
            continue
        }
        $configVersion = [string] (Get-Property $netCore[0] 'version')
        if (-not $configVersion) { Add-Failure "$exeName`: includedFrameworks Microsoft.NETCore.App has no version"; continue }
        if ($null -eq $framework -and $null -eq $frameworks) {
            Add-Pass "$exeName`: self-contained, includedFrameworks Microsoft.NETCore.App $configVersion"
        }

        # ------------------------------------------------------------------ deps.json
        $depsKey = ($directory + $stem + '.deps.json').ToLowerInvariant()
        if (-not $entries.ContainsKey($depsKey)) { Add-Failure "$exeName`: $stem.deps.json is missing"; continue }
        try { $deps = Read-EntryText $entries[$depsKey] | ConvertFrom-Json }
        catch { Add-Failure "$exeName`: $stem.deps.json is not valid JSON"; continue }

        $targetName = [string] (Get-Property (Get-Property $deps 'runtimeTarget') 'name')
        if (-not $targetName.EndsWith("/$RuntimeIdentifier", [System.StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure "$exeName`: deps.json runtimeTarget is not RID-specific ($RuntimeIdentifier)"
            continue
        }
        $libraries = Get-Property $deps 'libraries'
        $runtimePacks = @()
        if ($null -ne $libraries) {
            $runtimePacks = @($libraries.PSObject.Properties | Where-Object { $_.Name -like "$runtimePackName/*" } | ForEach-Object { $_.Name })
        }
        if ($runtimePacks.Count -ne 1) {
            Add-Failure "$exeName`: deps.json lists $($runtimePacks.Count) $runtimePackName libraries, expected exactly one"
            continue
        }
        $depsVersion = $runtimePacks[0].Substring($runtimePackName.Length + 1)
        if ($depsVersion -ne $configVersion) {
            Add-Failure "$exeName`: deps.json runtime pack $depsVersion differs from runtimeconfig.json $configVersion"
            continue
        }
        Add-Pass "$exeName`: deps.json $runtimePackName/$depsVersion"
        $versions[$exeName] = $configVersion

        # In self-contained mode the host puts every runtime/native asset the deps.json names, resolved in
        # the executable's folder, on the trusted platform list WITHOUT checking that the file exists
        # (host trace: "Skipped file existence check"). A missing one therefore does not stop the start;
        # it surfaces later as a FileNotFoundException inside the running process (for the provider:
        # in the middle of a COM activation). The provider ships only its own four files and relies on
        # the app's, so this is where a provider dependency the app does not carry would surface.
        $target = Get-Property (Get-Property $deps 'targets') $targetName
        $assets = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        if ($null -ne $target) {
            foreach ($library in $target.PSObject.Properties) {
                foreach ($kind in 'runtime', 'native') {
                    $group = Get-Property $library.Value $kind
                    if ($null -eq $group) { continue }
                    foreach ($asset in $group.PSObject.Properties) { $assets.Add(($asset.Name -split '/')[-1]) | Out-Null }
                }
            }
        }
        if ($assets.Count -eq 0) { Add-Failure "$exeName`: deps.json names no runtime assets"; continue }
        $absent = @($assets | Where-Object { -not $entries.ContainsKey(($directory + $_).ToLowerInvariant()) } | Sort-Object)
        if ($absent.Count -gt 0) {
            $sample = @($absent | Select-Object -First 10) -join ', '
            Add-Failure "$exeName`: $($absent.Count) asset(s) named by deps.json are not in the package next to it: $sample"
        }
        else { Add-Pass "$exeName`: all $($assets.Count) deps.json runtime/native assets present" }
    }

    # ------------------------------------------------------- one shared runtime version
    $distinct = @($versions.Values | Sort-Object -Unique)
    if ($distinct.Count -gt 1) {
        $detail = ($versions.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
        Add-Failure "executables were built against different .NET runtimes ($detail); they share one runtime payload, so every one must match"
    }
    elseif ($distinct.Count -eq 1 -and $versions.Count -eq $ExpectedExecutables.Count) {
        Add-Pass "every executable uses Microsoft.NETCore.App $($distinct[0])"
    }

    # --------------------------------------------------------- runtime files present
    foreach ($directory in @($executableDirectories | Sort-Object -Unique)) {
        $missing = @($RequiredRuntimeFiles | Where-Object { -not $entries.ContainsKey(($directory + $_).ToLowerInvariant()) })
        $where = if ($directory) { $directory } else { 'the package root' }
        if ($missing.Count -gt 0) { Add-Failure "runtime file(s) missing next to the executables in ${where}: $($missing -join ', ')" }
        else { Add-Pass "runtime files present in ${where}: $($RequiredRuntimeFiles -join ', ')" }
    }

    # ------------------------------------- Windows App Runtime stays a framework package
    $manifestKey = 'appxmanifest.xml'
    if (-not $entries.ContainsKey($manifestKey)) { throw 'AppxManifest.xml not found inside the package.' }
    [xml] $manifest = Read-EntryText $entries[$manifestKey]
    $dependencies = @($manifest.SelectNodes("//*[local-name()='Dependencies']/*[local-name()='PackageDependency']") |
        ForEach-Object { $_.GetAttribute('Name') })
    $appRuntime = @($dependencies | Where-Object { $_ -like 'Microsoft.WindowsAppRuntime.*' })
    if ($appRuntime.Count -eq 0) {
        Add-Failure 'AppxManifest.xml declares no PackageDependency on Microsoft.WindowsAppRuntime.*: the Windows App Runtime must stay a framework package (ADR-017)'
    }
    else { Add-Pass "PackageDependency $($appRuntime -join ', ')" }

    $selfContainedAppSdk = @($archive.Entries | Where-Object { $_.Name -ieq 'Microsoft.WindowsAppRuntime.dll' })
    if ($selfContainedAppSdk.Count -gt 0) {
        Add-Failure 'Microsoft.WindowsAppRuntime.dll is in the payload: the Windows App SDK was packaged self-contained instead of as a framework package'
    }
    else { Add-Pass 'no self-contained Windows App SDK payload' }
}
finally { $archive.Dispose() }

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "PACKAGE RUNTIME CHECK FAILED ($($failures.Count) problem(s))"
    throw ($failures -join '; ')
}

Write-Host ''
Write-Host 'PACKAGE RUNTIME CHECK PASSED'
