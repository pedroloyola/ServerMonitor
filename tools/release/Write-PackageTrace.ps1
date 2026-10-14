<#
.SYNOPSIS
    Records what a built MSIX is made of: commit, SDK, bundled .NET runtime, Windows App Runtime
    dependency and SHA-256, as a small JSON next to the package and (optionally) a job-summary table.

.DESCRIPTION
    REL.0D servicing traceability (ADR-017 amendment). A self-contained runtime is ours to service, so
    for every package we must be able to answer, without the build machine, "which .NET runtime is in
    it, built by which SDK, from which commit". The record is written next to the package
    ("<package>.build.json") and travels with the release artifact.

    Fails closed:
      * the SDK that ran must be exactly the one global.json pins (rollForward is "disable");
      * the runtime version comes from Assert-PackageRuntime.ps1 -PassThru, which re-proves the
        package (self-contained, floor, payload version) instead of trusting a value handed over;
      * every recorded value is validated against a strict shape, so no path, secret or free text
        can reach the record; an existing record is never overwritten.

    Nothing here touches a real install, the network, a secret or Partner Center.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackagePath,
    [string] $OutputPath,
    [string] $SummaryPath,
    [string] $SdkVersion,
    [string] $Commit,
    [string] $GlobalJsonPath = (Join-Path $PSScriptRoot '../../global.json'),
    [string] $RuntimeCheckScript = (Join-Path $PSScriptRoot 'Assert-PackageRuntime.ps1'),
    [string] $RuntimeFloorPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw 'Trace: package not found.' }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$packageName = Split-Path -Leaf $package
if ($packageName -cnotmatch '\A[A-Za-z0-9._-]+\.msix\z') { throw 'Trace: unsafe package file name.' }
if (-not $OutputPath) { $OutputPath = "$package.build.json" }
if (Test-Path -LiteralPath $OutputPath) { throw 'Trace: the build record already exists; refusing to overwrite it.' }

# ------------------------------------------------------------------------------------------- SDK
if (-not (Test-Path -LiteralPath $GlobalJsonPath -PathType Leaf)) { throw 'Trace: global.json is missing.' }
$pinned = [string] (Get-Content -LiteralPath $GlobalJsonPath -Raw | ConvertFrom-Json).sdk.version
if ($pinned -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]{3}\z') { throw 'Trace: global.json does not pin an exact SDK version.' }
if (-not $PSBoundParameters.ContainsKey('SdkVersion')) {
    # Run from the repository root so the muxer applies global.json, exactly as the build did.
    Push-Location (Split-Path -Parent (Resolve-Path -LiteralPath $GlobalJsonPath).Path)
    try { $SdkVersion = [string] (& dotnet --version) } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Trace: dotnet --version failed.' }
}
$SdkVersion = $SdkVersion.Trim()
if ($SdkVersion -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]{3}\z') { throw 'Trace: the SDK version is not an exact release version.' }
if ($SdkVersion -cne $pinned) { throw "Trace: the build used SDK $SdkVersion, but global.json pins $pinned." }

# ---------------------------------------------------------------------------------------- commit
if (-not $PSBoundParameters.ContainsKey('Commit')) {
    $Commit = [string] (git rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Trace: git rev-parse HEAD failed.' }
}
$Commit = $Commit.Trim()
if ($Commit -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Trace: the commit is not a full lowercase SHA-1.' }

# ------------------------------------------------------------------------- runtime (re-proven)
if (-not (Test-Path -LiteralPath $RuntimeCheckScript -PathType Leaf)) { throw 'Trace: the runtime check script is missing.' }
$checkArguments = @{ PackagePath = $package; PassThru = $true }
if ($RuntimeFloorPath) { $checkArguments.RuntimeFloorPath = $RuntimeFloorPath }
$runtime = @(& $RuntimeCheckScript @checkArguments 6> $null | Where-Object { $null -ne $_ -and $_.PSObject.Properties['RuntimeVersion'] })
if ($runtime.Count -ne 1) { throw 'Trace: the runtime check did not report exactly one runtime version.' }
$runtimeVersion = [string] $runtime[0].RuntimeVersion
$runtimeFloor = [string] $runtime[0].RuntimeFloor
foreach ($value in $runtimeVersion, $runtimeFloor) {
    if ($value -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'Trace: the runtime check reported a malformed version.' }
}

# ------------------------------------------------------- identity + Windows App Runtime dependency
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $manifestEntry = @($archive.Entries | Where-Object { $_.FullName -ieq 'AppxManifest.xml' })
    if ($manifestEntry.Count -ne 1) { throw 'Trace: AppxManifest.xml not found in the package.' }
    $reader = New-Object System.IO.StreamReader($manifestEntry[0].Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
}
finally { $archive.Dispose() }
$identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
if ($null -eq $identity) { throw 'Trace: the manifest has no Identity.' }
$packageVersion = $identity.GetAttribute('Version')
if ($packageVersion -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'Trace: the package version is malformed.' }
$appRuntime = @($manifest.SelectNodes("//*[local-name()='Dependencies']/*[local-name()='PackageDependency']") |
    Where-Object { $_.GetAttribute('Name') -like 'Microsoft.WindowsAppRuntime.*' })
if ($appRuntime.Count -ne 1) { throw 'Trace: expected exactly one Windows App Runtime package dependency.' }
$appRuntimeName = $appRuntime[0].GetAttribute('Name')
$appRuntimeMin = $appRuntime[0].GetAttribute('MinVersion')
if ($appRuntimeName -cnotmatch '\AMicrosoft\.WindowsAppRuntime\.[0-9.]+\z') { throw 'Trace: unexpected Windows App Runtime dependency name.' }
if ($appRuntimeMin -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'Trace: unexpected Windows App Runtime MinVersion.' }

$sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()

$record = [ordered] @{
    schema               = 1
    package              = $packageName
    packageVersion       = $packageVersion
    sha256               = $sha256
    commit               = $Commit
    sdk                  = $SdkVersion
    netRuntime           = $runtimeVersion
    netRuntimeFloor      = $runtimeFloor
    windowsAppRuntime    = $appRuntimeName
    windowsAppRuntimeMin = $appRuntimeMin
}
$record | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Host "Wrote $(Split-Path -Leaf $OutputPath)"

if ($SummaryPath) {
    @(
        ''
        '## Package build record'
        ''
        '| field | value |'
        '| --- | --- |'
        foreach ($key in $record.Keys) { if ($key -ne 'schema') { "| $key | ``$($record[$key])`` |" } }
    ) | Out-File -FilePath $SummaryPath -Append -Encoding utf8
}

return [pscustomobject] $record
