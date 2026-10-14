<#
.SYNOPSIS
    Counterproof for Assert-PackageRuntime.ps1: builds synthetic MSIX packages and proves the check
    rejects every way the package can fall back to a machine-wide .NET runtime.

.DESCRIPTION
    Each case mutates exactly one property of an otherwise valid synthetic self-contained package;
    the baseline case asserts the unmutated one is accepted. coreclr and clrjit are placeholders (the
    check reads only their presence). The four runtime files whose version the check reads (hostfxr,
    hostpolicy, System.Private.CoreLib, System.Runtime) are real, tiny PE files compiled here with a
    Win32 version resource whose ProductVersion has the shape the real runtime files carry
    ("10.0.13 @Commit: <sha>", "10.0.13-servicing.<build>+<sha>", "10.0.13+<sha>").

    Every case uses its own synthetic runtime floor file (default 10.0.13), never the tracked one.

    Nothing here touches a real package, a real install, the network or Partner Center.
#>
[CmdletBinding()]
param([string] $CheckScript = (Join-Path $PSScriptRoot 'Assert-PackageRuntime.ps1'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Roslyn ships with PowerShell 7 (Add-Type uses it); loaded explicitly to emit the version resources.
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')

if (-not (Test-Path -LiteralPath $CheckScript -PathType Leaf)) { throw "Cannot find $CheckScript" }
$checkScript = $CheckScript

$Version = '10.0.13'
$Floor = '10.0.13'
$RuntimeFiles = @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'clrjit.dll', 'System.Private.CoreLib.dll', 'System.Runtime.dll')
$VersionedRuntimeFiles = @('hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'System.Runtime.dll')
$Executables = @('ServerMonitor.App', 'ServerAlyzer.WidgetProvider')

# A minimal PE (an empty class library) with a Win32 version resource: FileVersionInfo reads its
# ProductVersion exactly as it reads the real runtime files'. Cached per version string.
$script:peCache = @{}
function New-VersionedPe([Parameter(Mandatory)] [string] $ProductVersion) {
    if ($script:peCache.ContainsKey($ProductVersion)) { return ,$script:peCache[$ProductVersion] }
    $escaped = $ProductVersion.Replace('\', '\\').Replace('"', '\"')
    $source = "[assembly: System.Reflection.AssemblyInformationalVersion(`"$escaped`")] [assembly: System.Reflection.AssemblyFileVersion(`"10.0.1.1`")]"
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)
    $reference = [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile([object].Assembly.Location)
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
        'SyntheticRuntimeFile', [Microsoft.CodeAnalysis.SyntaxTree[]] @($tree), [Microsoft.CodeAnalysis.MetadataReference[]] @($reference), $options)
    $resources = $compilation.CreateDefaultWin32Resources($true, $true, $null, $null)
    $stream = New-Object System.IO.MemoryStream
    $result = $compilation.Emit($stream, $null, $null, $resources)
    if (-not $result.Success) { throw "Could not compile the synthetic runtime file for '$ProductVersion'." }
    $script:peCache[$ProductVersion] = $stream.ToArray()
    return ,$script:peCache[$ProductVersion]
}

# The ProductVersion shapes the real .NET 10.0.13 runtime files carry (VERIFIED on the runtime files).
function Get-RuntimeFileVersionString([string] $File, [string] $Version) {
    switch ($File) {
        'System.Private.CoreLib.dll' { return "$Version-servicing.26504.104+4099f83977f4d29604a9aeb971fb0a7bc1d5f699" }
        'System.Runtime.dll' { return "$Version+4099f83977f4d29604a9aeb971fb0a7bc1d5f699" }
        default { return "$Version @Commit: 4099f83977f4d29604a9aeb971fb0a7bc1d5f699" }
    }
}

function Set-RuntimeFileVersion($Entries, [string] $Version, [string] $Prefix = '') {
    foreach ($file in $VersionedRuntimeFiles) { $Entries["$Prefix$file"] = New-VersionedPe (Get-RuntimeFileVersionString $file $Version) }
}

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
    Set-RuntimeFileVersion $entries $Version
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
                $bytes = if ($Entries[$name] -is [byte[]]) { $Entries[$name] } else { [System.Text.Encoding]::UTF8.GetBytes([string] $Entries[$name]) }
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
        [string] $ExpectedMessagePattern,
        # Content of this case's runtime floor file.
        [AllowEmptyString()] [string] $FloorContent = "# synthetic floor`n$script:Floor",
        [switch] $NoFloorFile,
        # For a 'pass' case: the runtime version -PassThru must report.
        [string] $ExpectedRuntime = $script:Version
    )

    $entries = Get-DefaultEntries
    & $Mutate $entries
    $packagePath = Join-Path $workDir ("$([guid]::NewGuid().ToString('n')).msix")
    New-SyntheticPackage -Path $packagePath -Entries $entries
    $floorPath = Join-Path $workDir ("$([guid]::NewGuid().ToString('n')).floor.txt")
    if (-not $NoFloorFile) { [System.IO.File]::WriteAllText($floorPath, $FloorContent) }

    $outcome = 'pass'; $message = ''; $reported = $null
    try { $reported = & $checkScript -PackagePath $packagePath -RuntimeFloorPath $floorPath -PassThru 6> $null }
    catch { $outcome = 'fail'; $message = $_.Exception.Message }

    $verdict = if ($outcome -eq $Expected) { 'OK' } else { 'TEST ERROR' }
    if ($verdict -eq 'OK' -and $Expected -eq 'fail') {
        # A rejection only counts for the reason the case is about.
        if (-not $ExpectedMessagePattern) { $verdict = 'TEST ERROR'; $message = "case has no expected reason: $message" }
        elseif ($message -notmatch $ExpectedMessagePattern) { $verdict = 'TEST ERROR'; $message = "rejected, but for the wrong reason: $message" }
    }
    if ($verdict -eq 'OK' -and $Expected -eq 'pass') {
        $runtime = @($reported | Where-Object { $null -ne $_ -and $_.PSObject.Properties['RuntimeVersion'] })
        if ($runtime.Count -ne 1 -or $runtime[0].RuntimeVersion -cne $ExpectedRuntime) {
            $verdict = 'TEST ERROR'; $message = "accepted, but -PassThru did not report runtime $ExpectedRuntime"
        }
    }
    $results.Add([pscustomobject] @{ Case = $Name; Expected = $Expected; Actual = $outcome; Verdict = $verdict; Detail = $message }) | Out-Null
    Remove-Item -LiteralPath $packagePath, $floorPath -Force -ErrorAction SilentlyContinue
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
        param($e) $e['ServerMonitor.App.deps.json'] = (New-Deps -Version '10.0.14')
    }

    Invoke-Case -Name 'app and provider built against different runtimes' -Expected 'fail' -ExpectedMessagePattern 'different \.NET runtimes' -Mutate {
        param($e)
        $e['ServerAlyzer.WidgetProvider.runtimeconfig.json'] = (New-RuntimeConfig -Version '10.0.14')
        $e['ServerAlyzer.WidgetProvider.deps.json'] = (New-Deps -Exe 'ServerAlyzer.WidgetProvider' -Version '10.0.14')
    }

    # ------------------------------------------------------------- servicing floor (REL.0D)
    function Set-WholeRuntime($e, [string] $v) {
        foreach ($exe in $Executables) {
            $e["$exe.runtimeconfig.json"] = (New-RuntimeConfig -Version $v)
            $e["$exe.deps.json"] = (New-Deps -Exe $exe -Version $v)
        }
        Set-RuntimeFileVersion $e $v
    }

    Invoke-Case -Name 'floor: newer patch accepted' -Expected 'pass' -ExpectedRuntime '10.0.14' -Mutate { param($e) Set-WholeRuntime $e '10.0.14' }
    Invoke-Case -Name 'floor: newer minor/major accepted' -Expected 'pass' -ExpectedRuntime '11.0.0' -Mutate { param($e) Set-WholeRuntime $e '11.0.0' }
    Invoke-Case -Name 'floor: numeric, not text (10.0.13 >= 10.0.9)' -Expected 'pass' -FloorContent '10.0.9' -Mutate { param($e) }

    Invoke-Case -Name 'floor: whole runtime one patch below (10.0.12)' -Expected 'fail' -ExpectedMessagePattern 'ServerMonitor\.App\.exe: bundled Microsoft\.NETCore\.App 10\.0\.12 is below the tracked runtime floor 10\.0\.13' -Mutate {
        param($e) Set-WholeRuntime $e '10.0.12'
    }
    Invoke-Case -Name 'floor: numeric, not text (10.0.2 < 10.0.13)' -Expected 'fail' -ExpectedMessagePattern 'bundled Microsoft\.NETCore\.App 10\.0\.2 is below the tracked runtime floor 10\.0\.13' -Mutate {
        param($e) Set-WholeRuntime $e '10.0.2'
    }
    Invoke-Case -Name 'floor: older major above the floor patch (9.0.20)' -Expected 'fail' -ExpectedMessagePattern 'bundled Microsoft\.NETCore\.App 9\.0\.20 is below the tracked runtime floor' -Mutate {
        param($e) Set-WholeRuntime $e '9.0.20'
    }
    Invoke-Case -Name 'floor: only the widget provider below' -Expected 'fail' -ExpectedMessagePattern 'ServerAlyzer\.WidgetProvider\.exe: bundled Microsoft\.NETCore\.App 10\.0\.12 is below' -Mutate {
        param($e)
        $e['ServerAlyzer.WidgetProvider.runtimeconfig.json'] = (New-RuntimeConfig -Version '10.0.12')
        $e['ServerAlyzer.WidgetProvider.deps.json'] = (New-Deps -Exe 'ServerAlyzer.WidgetProvider' -Version '10.0.12')
    }

    foreach ($bad in @('10.0.13-rc.1', '10.0', '10.0.13.0', ' 10.0.13', '10.0.013', 'v10.0.13')) {
        Invoke-Case -Name "runtime version not canonical [$bad]" -Expected 'fail' -ExpectedMessagePattern 'version is not a canonical MAJOR\.MINOR\.PATCH release' -Mutate (
            [scriptblock]::Create("param(`$e) `$e['ServerMonitor.App.runtimeconfig.json'] = (New-RuntimeConfig -Version '$bad')"))
    }

    foreach ($badFloor in @('', '# comment only', "10.0.13`n10.0.14", 'broken', '10.0', '10.0.13.0', ' 10.0.13', '10.0.013', '10.0.13-rc.1')) {
        Invoke-Case -Name "invalid floor [$($badFloor -replace "`n", '\n')]" -Expected 'fail' -FloorContent $badFloor -ExpectedMessagePattern '^Runtime floor: ' -Mutate { param($e) }
    }
    Invoke-Case -Name 'missing floor file' -Expected 'fail' -NoFloorFile -ExpectedMessagePattern '^Runtime floor: .*missing' -Mutate { param($e) }

    # ------------------------------------------------- the payload is the runtime the JSON names
    foreach ($file in $VersionedRuntimeFiles) {
        Invoke-Case -Name "runtime file older than the JSON: $file" -Expected 'fail' -ExpectedMessagePattern ("runtime file $([regex]::Escape($file)) in the package root reports runtime 10\.0\.12, but the executables declare Microsoft\.NETCore\.App 10\.0\.13") -Mutate (
            [scriptblock]::Create("param(`$e) `$e['$file'] = New-VersionedPe (Get-RuntimeFileVersionString '$file' '10.0.12')"))
        Invoke-Case -Name "runtime file without a version resource: $file" -Expected 'fail' -ExpectedMessagePattern ("runtime file $([regex]::Escape($file)) in the package root reports no runtime version") -Mutate (
            [scriptblock]::Create("param(`$e) `$e['$file'] = 'MZ'"))
    }
    Invoke-Case -Name 'runtime file version only shares a prefix (10.0.130)' -Expected 'fail' -ExpectedMessagePattern 'System\.Runtime\.dll in the package root reports runtime 10\.0\.130' -Mutate {
        param($e) $e['System.Runtime.dll'] = New-VersionedPe '10.0.130+4099f83977f4d29604a9aeb971fb0a7bc1d5f699'
    }
    Invoke-Case -Name 'runtime file version with a fourth part (10.0.13.5)' -Expected 'fail' -ExpectedMessagePattern 'System\.Runtime\.dll in the package root reports no runtime version' -Mutate {
        param($e) $e['System.Runtime.dll'] = New-VersionedPe '10.0.13.5'
    }
    Invoke-Case -Name 'JSON rewritten to a newer runtime over an old payload' -Expected 'fail' -ExpectedMessagePattern 'reports runtime 10\.0\.12, but the executables declare Microsoft\.NETCore\.App 10\.0\.13' -Mutate {
        param($e) Set-RuntimeFileVersion $e '10.0.12'
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
# Explicit success. A native command or a stub inside the suite may have left $LASTEXITCODE non-zero,
# and the GitHub Actions pwsh wrapper ends a step with `exit $LASTEXITCODE` (PR #53 B-1).
exit 0
