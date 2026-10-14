<#
.SYNOPSIS
    Proves, from the bytes inside a built MSIX, that the package's default (fallback) language is en-US
    and that every ms-resource: string the manifest uses (AppDescription first) resolves in en-US, pt-PT
    and pt-BR.

.DESCRIPTION
    REL.0D (Cortex N-3 gate after PR #46). The Store listing, Start and the Widgets board read the
    manifest strings through resources.pri, not through the app. Up to 1.1.x the first manifest
    language was pt-BR, so every language the package does not carry fell back to Portuguese. A green
    source tree does not prove what makepri wrote, so this reads the package:

      * AppxManifest.xml: the FIRST <Resource Language> (the package default) is en-US, and en-US,
        pt-PT and pt-BR are all declared;
      * resources.pri, dumped with makepri ("dump /dt Detailed"): for every ms-resource:<Name> the
        manifest references, plus AppDescription even if the manifest stopped referencing it, the
        named resource Resources/<Name> exists, has a non-empty String candidate for each of the three
        languages, and its default candidate (the language qualifier with scoreAsDefault 1.0, which is
        what the resource manager falls back to) is en-US. A language-neutral candidate, if any, must
        carry the en-US text.

    makepri is the one the build used when it can be found (the Microsoft.Windows.SDK.BuildTools NuGet
    package), else the Windows SDK's. No makepri: the check fails closed, it never skips.

    Nothing here touches a real install, the network or Partner Center.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackagePath,
    [string] $MakePriPath,
    [string[]] $MakePriSearchRoots = @(
        $(if ($env:NUGET_PACKAGES) { Join-Path $env:NUGET_PACKAGES 'microsoft.windows.sdk.buildtools' } else { Join-Path $HOME '.nuget/packages/microsoft.windows.sdk.buildtools' }),
        $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin' })
    ),
    [string] $DefaultLanguage = 'en-US',
    [string[]] $RequiredLanguages = @('en-US', 'pt-PT', 'pt-BR'),
    [string[]] $RequiredResources = @('AppDescription')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$failures = New-Object System.Collections.Generic.List[string]
function Add-Failure([string] $m) { $failures.Add($m) | Out-Null; Write-Host "  FAIL  $m" }
function Add-Pass([string] $m) { Write-Host "  ok    $m" }

# ------------------------------------------------------------------------------------- makepri
function Find-MakePri {
    $found = New-Object System.Collections.Generic.List[object]
    foreach ($root in @($MakePriSearchRoots | Where-Object { $_ })) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($exe in @(Get-ChildItem -LiteralPath $root -Recurse -Filter 'makepri.exe' -File -ErrorAction SilentlyContinue)) {
            if ($exe.Directory.Name -ne 'x64') { continue }
            $version = $null
            foreach ($part in ($exe.FullName.Substring($root.Length) -split '[\\/]')) {
                $candidate = $null
                if ([version]::TryParse($part, [ref] $candidate) -and ($null -eq $version -or $candidate -gt $version)) { $version = $candidate }
            }
            if ($null -ne $version) { $found.Add([pscustomobject] @{ Path = $exe.FullName; Version = $version }) | Out-Null }
        }
        # The first root that has one wins: the build's own makepri before the machine's.
        if ($found.Count -gt 0) { break }
    }
    if ($found.Count -eq 0) { return $null }
    return ($found | Sort-Object Version -Descending | Select-Object -First 1).Path
}

if (-not $MakePriPath) { $MakePriPath = Find-MakePri }
if (-not $MakePriPath -or -not (Test-Path -LiteralPath $MakePriPath -PathType Leaf)) {
    throw 'Package resources: makepri was not found, so resources.pri cannot be checked. Install the Windows SDK build tools; this check never skips.'
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw 'Package resources: package not found.' }
$package = (Resolve-Path -LiteralPath $PackagePath).Path

Write-Host 'Package resources check'
Write-Host "  package: $(Split-Path -Leaf $package)"
Write-Host "  makepri: $(Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $MakePriPath)))"

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('package-resources-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    # ---------------------------------------------------------------------------------- manifest
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package)
    try {
        $manifestEntry = @($archive.Entries | Where-Object { $_.FullName -ieq 'AppxManifest.xml' })
        $priEntry = @($archive.Entries | Where-Object { $_.FullName -ieq 'resources.pri' })
        if ($manifestEntry.Count -ne 1) { throw 'Package resources: AppxManifest.xml not found in the package.' }
        if ($priEntry.Count -ne 1) { throw 'Package resources: resources.pri not found at the package root.' }
        $reader = New-Object System.IO.StreamReader($manifestEntry[0].Open())
        try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $priPath = Join-Path $work 'resources.pri'
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($priEntry[0], $priPath)
    }
    finally { $archive.Dispose() }

    $languages = @($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Resources']/*[local-name()='Resource']") |
        ForEach-Object { $_.GetAttribute('Language') } | Where-Object { $_ })
    if ($languages.Count -eq 0) { Add-Failure 'AppxManifest.xml declares no <Resource Language>' }
    elseif ($languages[0] -ine $DefaultLanguage) { Add-Failure "the package default language (first <Resource Language>) is $($languages[0]), not $DefaultLanguage" }
    else { Add-Pass "package default language $($languages[0])" }
    foreach ($language in $RequiredLanguages) {
        if (@($languages | Where-Object { $_ -ieq $language }).Count -eq 0) { Add-Failure "AppxManifest.xml does not declare <Resource Language=`"$language`">" }
    }

    # Every ms-resource: the manifest uses, in any attribute or element text.
    $names = New-Object 'System.Collections.Generic.SortedSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $RequiredResources) { $names.Add($name) | Out-Null }
    foreach ($node in @($manifest.SelectNodes('//@*') + $manifest.SelectNodes('//text()'))) {
        $value = [string] $node.Value
        if ($value -match '\Ams-resource:(?:///?Resources/)?([A-Za-z0-9_.]+)\z') { $names.Add($Matches[1]) | Out-Null }
        elseif ($value -match '\Ams-resource:') { Add-Failure "unsupported ms-resource reference form in the manifest: $value" }
    }

    # ------------------------------------------------------------------------- resources.pri dump
    $dumpPath = Join-Path $work 'resources.dump.xml'
    $makePriOutput = & $MakePriPath dump /if $priPath /of $dumpPath /dt Detailed /o 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dumpPath -PathType Leaf)) {
        throw "Package resources: makepri could not dump resources.pri (exit $LASTEXITCODE)."
    }
    try { [xml] $dump = Get-Content -LiteralPath $dumpPath -Raw -Encoding utf8 }
    catch { throw 'Package resources: the makepri dump is not valid XML.' }

    function Get-Language($candidate) {
        $qualifiers = @($candidate.SelectNodes("./*[local-name()='QualifierSet']/*[local-name()='Qualifier'][@name='Language']"))
        if ($qualifiers.Count -eq 0) { return $null }
        if ($qualifiers.Count -gt 1) { return '<several>' }
        return $qualifiers[0]
    }

    foreach ($name in $names) {
        $resources = @($dump.SelectNodes("//*[local-name()='NamedResource']") | Where-Object {
                $_.GetAttribute('uri') -match "/Resources/$([regex]::Escape($name))\z" })
        if ($resources.Count -ne 1) { Add-Failure "ms-resource:$name`: expected one named resource Resources/$name in resources.pri, found $($resources.Count)"; continue }

        $byLanguage = @{}
        $neutral = $null
        $default = New-Object System.Collections.Generic.List[string]
        foreach ($candidate in @($resources[0].SelectNodes("./*[local-name()='Candidate']"))) {
            if ($candidate.GetAttribute('type') -ne 'String') { continue }
            $valueNode = $candidate.SelectSingleNode("./*[local-name()='Value']")
            $text = if ($null -eq $valueNode) { '' } else { [string] $valueNode.InnerText }
            $qualifier = Get-Language $candidate
            if ($null -eq $qualifier) { $neutral = $text; continue }
            if ($qualifier -is [string]) { Add-Failure "ms-resource:$name`: a candidate has several language qualifiers"; continue }
            $language = $qualifier.GetAttribute('value')
            $byLanguage[$language.ToUpperInvariant()] = $text
            if ($qualifier.GetAttribute('scoreAsDefault') -eq '1.0') { $default.Add($language) | Out-Null }
        }

        $missing = @($RequiredLanguages | Where-Object { -not $byLanguage.ContainsKey($_.ToUpperInvariant()) -or [string]::IsNullOrWhiteSpace($byLanguage[$_.ToUpperInvariant()]) })
        if ($missing.Count -gt 0) { Add-Failure "ms-resource:$name does not resolve in: $($missing -join ', ')" }
        else { Add-Pass "ms-resource:$name resolves in $($RequiredLanguages -join ', ')" }

        if ($default.Count -ne 1 -or $default[0] -ine $DefaultLanguage) {
            $shown = if ($default.Count -eq 0) { 'none' } else { $default -join ', ' }
            Add-Failure "ms-resource:$name`: the default (fallback) candidate is $shown, not $DefaultLanguage"
        }
        else { Add-Pass "ms-resource:$name falls back to $DefaultLanguage" }

        if ($null -ne $neutral -and $byLanguage.ContainsKey($DefaultLanguage.ToUpperInvariant()) -and $neutral -cne $byLanguage[$DefaultLanguage.ToUpperInvariant()]) {
            Add-Failure "ms-resource:$name`: the language-neutral candidate is not the $DefaultLanguage text"
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "PACKAGE RESOURCES CHECK FAILED ($($failures.Count) problem(s))"
    throw ('Package resources: ' + ($failures -join '; '))
}
Write-Host ''
Write-Host "PACKAGE RESOURCES CHECK PASSED (default $DefaultLanguage; $($names.Count) manifest string(s) in $($RequiredLanguages -join ', '))"
