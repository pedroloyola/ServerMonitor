<#
.SYNOPSIS
    Counterproof for Write-PackageTrace.ps1: the build record is written only for the pinned SDK, a
    re-proven runtime, a full commit and a well-formed package, and never carries anything else.

.DESCRIPTION
    Synthetic only: a tiny zip with an AppxManifest.xml stands in for the package, and a stub stands
    in for Assert-PackageRuntime.ps1 (its own counterproofs are in Test-PackageRuntime.ps1). Every
    rejection must carry the expected reason; an unrelated exception is a test error.
#>
[CmdletBinding()]
param([string] $TraceScript = (Join-Path $PSScriptRoot 'Write-PackageTrace.ps1'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not (Test-Path -LiteralPath $TraceScript -PathType Leaf)) { throw 'Trace script missing.' }

$commit = '0123456789abcdef0123456789abcdef01234567'
$work = Join-Path ([IO.Path]::GetTempPath()) ('package-trace-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $work | Out-Null

function New-Manifest([switch] $NoAppRuntime, [string] $Version = '1.1.7.0') {
    $dependency = if ($NoAppRuntime) { '' } else { '<PackageDependency Name="Microsoft.WindowsAppRuntime.2" MinVersion="2.3.1.0" Publisher="CN=Microsoft Corporation" />' }
    "<?xml version=`"1.0`" encoding=`"utf-8`"?><Package xmlns=`"http://schemas.microsoft.com/appx/manifest/foundation/windows10`"><Identity Name=`"PedroLoy.ServerAlyzer`" Publisher=`"CN=X`" Version=`"$Version`" ProcessorArchitecture=`"x64`" /><Dependencies>$dependency</Dependencies></Package>"
}

function New-Package([string] $Directory, [string] $Name, [string] $Manifest) {
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $path = Join-Path $Directory $Name
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = New-Object IO.StreamWriter($archive.CreateEntry('AppxManifest.xml').Open())
        try { $writer.Write($Manifest) } finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
    return $path
}

function New-Stub([string] $Directory, [string] $Body) {
    $path = Join-Path $Directory 'stub-runtime-check.ps1'
    Set-Content -LiteralPath $path -Value "param([string] `$PackagePath, [switch] `$PassThru, [string] `$RuntimeFloorPath)`n$Body"
    return $path
}

$okRuntime = "[pscustomobject] @{ RuntimeVersion = '10.0.13'; RuntimeFloor = '10.0.13' }"
$pinnedJson = '{ "sdk": { "version": "10.0.402", "rollForward": "disable", "allowPrerelease": false } }'
$results = New-Object System.Collections.Generic.List[object]

function Invoke-Case {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [string] $ExpectedReason,                     # $null/empty = must pass
        [string] $Sdk = '10.0.402',
        [string] $CommitValue = $commit,
        [string] $GlobalJson = $pinnedJson,
        [switch] $NoGlobalJson,
        [string] $StubBody = $okRuntime,
        [string] $PackageName = 'ServerAlyzer_1.1.7.0_x64_0123456.msix',
        [string] $Manifest = (New-Manifest),
        [switch] $ExistingRecord
    )
    $dir = Join-Path $work ([guid]::NewGuid().ToString('n'))
    $package = New-Package $dir $PackageName $Manifest
    $globalPath = Join-Path $dir 'global.json'
    if (-not $NoGlobalJson) { Set-Content -LiteralPath $globalPath -Value $GlobalJson }
    $stub = New-Stub $dir $StubBody
    $summary = Join-Path $dir 'summary.md'
    $record = "$package.build.json"
    if ($ExistingRecord) { Set-Content -LiteralPath $record -Value '{}' }

    $failure = $null
    try {
        & $TraceScript -PackagePath $package -SummaryPath $summary -SdkVersion $Sdk -Commit $CommitValue `
            -GlobalJsonPath $globalPath -RuntimeCheckScript $stub 6> $null | Out-Null
    }
    catch { $failure = $_.Exception.Message }

    $verdict = 'OK'; $detail = ''
    if ($ExpectedReason) {
        if ($null -eq $failure) { $verdict = 'TEST ERROR'; $detail = 'accepted' }
        elseif ($failure -notmatch $ExpectedReason) { $verdict = 'TEST ERROR'; $detail = "rejected for the wrong reason: $failure" }
        elseif ($ExistingRecord -and (Get-Content -LiteralPath $record -Raw).Trim() -ne '{}') { $verdict = 'TEST ERROR'; $detail = 'existing record was overwritten' }
    }
    elseif ($null -ne $failure) { $verdict = 'TEST ERROR'; $detail = "rejected: $failure" }
    else {
        $json = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
        $expected = [ordered] @{
            schema = 1; package = $PackageName; packageVersion = '1.1.7.0'
            sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
            commit = $CommitValue; sdk = $Sdk; netRuntime = '10.0.13'; netRuntimeFloor = '10.0.13'
            windowsAppRuntime = 'Microsoft.WindowsAppRuntime.2'; windowsAppRuntimeMin = '2.3.1.0'
        }
        $names = @($json.PSObject.Properties.Name)
        if (($names -join ',') -cne (@($expected.Keys) -join ',')) { $verdict = 'TEST ERROR'; $detail = "unexpected fields: $($names -join ',')" }
        else {
            foreach ($key in $expected.Keys) {
                if ([string] $json.$key -cne [string] $expected[$key]) { $verdict = 'TEST ERROR'; $detail += "$key=$($json.$key) " }
            }
        }
        # No path of this machine may reach the record.
        if ((Get-Content -LiteralPath $record -Raw) -match '[\\/]|[A-Za-z]:') { $verdict = 'TEST ERROR'; $detail += 'record contains a path ' }
        if (-not (Test-Path -LiteralPath $summary) -or (Get-Content -LiteralPath $summary -Raw) -notmatch [regex]::Escape($expected.sha256)) {
            $verdict = 'TEST ERROR'; $detail += 'summary misses the sha-256 '
        }
    }
    $results.Add([pscustomobject] @{ Case = $Name; Verdict = $verdict; Detail = $detail }) | Out-Null
}

try {
    Invoke-Case -Name 'baseline'
    Invoke-Case -Name 'SDK other than the pinned one' -Sdk '10.0.403' -ExpectedReason '^Trace: the build used SDK 10\.0\.403, but global\.json pins 10\.0\.402'
    Invoke-Case -Name 'SDK older than the pinned one' -Sdk '10.0.400' -ExpectedReason '^Trace: the build used SDK'
    Invoke-Case -Name 'SDK preview' -Sdk '10.0.402-preview.1' -ExpectedReason '^Trace: the SDK version is not an exact release'
    Invoke-Case -Name 'SDK empty' -Sdk '' -ExpectedReason '^Trace: the SDK version is not an exact release'
    Invoke-Case -Name 'global.json with a floating version' -GlobalJson '{ "sdk": { "version": "10.0.x" } }' -ExpectedReason '^Trace: global\.json does not pin an exact SDK'
    Invoke-Case -Name 'global.json missing' -NoGlobalJson -ExpectedReason '^Trace: global\.json is missing'
    Invoke-Case -Name 'commit short' -CommitValue '0123456' -ExpectedReason '^Trace: the commit is not'
    Invoke-Case -Name 'commit uppercase' -CommitValue $commit.ToUpperInvariant() -ExpectedReason '^Trace: the commit is not'
    Invoke-Case -Name 'commit empty' -CommitValue '' -ExpectedReason '^Trace: the commit is not'
    Invoke-Case -Name 'runtime check rejects the package' -StubBody "throw 'STUB: below the tracked runtime floor'" -ExpectedReason '^STUB: below the tracked runtime floor'
    Invoke-Case -Name 'runtime check reports nothing' -StubBody '' -ExpectedReason '^Trace: the runtime check did not report exactly one'
    Invoke-Case -Name 'runtime check reports a preview' -StubBody "[pscustomobject] @{ RuntimeVersion = '10.0.13-rc.1'; RuntimeFloor = '10.0.13' }" -ExpectedReason '^Trace: the runtime check reported a malformed version'
    Invoke-Case -Name 'unsafe package name' -PackageName 'Server Alyzer.msix' -ExpectedReason '^Trace: unsafe package file name'
    Invoke-Case -Name 'no Windows App Runtime dependency' -Manifest (New-Manifest -NoAppRuntime) -ExpectedReason '^Trace: expected exactly one Windows App Runtime'
    Invoke-Case -Name 'package version malformed' -Manifest (New-Manifest -Version '1.1.7') -ExpectedReason '^Trace: the package version is malformed'
    Invoke-Case -Name 'record already exists' -ExistingRecord -ExpectedReason '^Trace: the build record already exists'
}
finally {
    $resolved = [IO.Path]::GetFullPath($work)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}

$results | Format-Table -AutoSize Case, Verdict, Detail | Out-String -Width 200 | Write-Host
$broken = @($results | Where-Object Verdict -ne 'OK')
if ($broken.Count -gt 0) { throw "$($broken.Count) of $($results.Count) package trace case(s) did not behave as required." }
Write-Host "All $($results.Count) package trace cases behaved as required."
