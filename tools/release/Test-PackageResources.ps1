<#
.SYNOPSIS
    Counterproof for Assert-PackageResources.ps1: synthetic packages and synthetic makepri dumps prove the
    check refuses a non-en-US default, a missing language, a missing or empty ms-resource string, and a
    missing makepri.

.DESCRIPTION
    makepri is replaced by a stub script that writes a prepared dump in the shape "makepri dump /dt
    Detailed" produces (NamedResource uri=".../Resources/<Name>", Candidate type="String",
    QualifierSet/Qualifier name="Language" value="EN-US" scoreAsDefault="1.0", Value). The real makepri
    is exercised by the CI package job on the real package. Every rejection must carry its reason.
#>
[CmdletBinding()]
param([string] $CheckScript = (Join-Path $PSScriptRoot 'Assert-PackageResources.ps1'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not (Test-Path -LiteralPath $CheckScript -PathType Leaf)) { throw 'Check script missing.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('package-resources-test-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $work | Out-Null
$results = New-Object System.Collections.Generic.List[object]

function New-Manifest([string[]] $Languages = @('en-US', 'pt-PT', 'pt-BR'), [string] $Description = 'ms-resource:AppDescription', [string] $Extra = '') {
    $resources = ($Languages | ForEach-Object { "<Resource Language=`"$_`" />" }) -join ''
    return "<?xml version=`"1.0`" encoding=`"utf-8`"?><Package xmlns=`"http://schemas.microsoft.com/appx/manifest/foundation/windows10`" xmlns:uap=`"http://schemas.microsoft.com/appx/manifest/uap/windows10`"><Identity Name=`"PedroLoy.ServerAlyzer`" Publisher=`"CN=X`" Version=`"1.1.7.0`" /><Resources>$resources</Resources><Applications><Application Id=`"App`"><uap:VisualElements DisplayName=`"ServerAlyzer`" Description=`"$Description`" />$Extra</Application></Applications></Package>"
}

# Candidates: @{ Lang = 'EN-US' (or $null = neutral); Score = '1.0'; Value = '...' }
function New-Resource([string] $Name, [object[]] $Candidates) {
    $body = foreach ($c in $Candidates) {
        $qualifier = if ($c.Lang) { "<QualifierSet index=`"1`"><Qualifier name=`"Language`" value=`"$($c.Lang)`" priority=`"700`" scoreAsDefault=`"$($c.Score)`" index=`"1`"/></QualifierSet>" } else { '' }
        "<Candidate type=`"String`">$qualifier<Value>$([Security.SecurityElement]::Escape($c.Value))</Value></Candidate>"
    }
    return "<NamedResource name=`"$Name`" index=`"1`" uri=`"ms-resource://PedroLoy.ServerAlyzer/Resources/$Name`">$($body -join '')</NamedResource>"
}

function Get-GoodCandidates([string] $Name) {
    return @(
        @{ Lang = 'EN-US'; Score = '1.0'; Value = "$Name (en)" }
        @{ Lang = 'PT-PT'; Score = '0.0'; Value = "$Name (pt-PT)" }
        @{ Lang = 'PT-BR'; Score = '0.0'; Value = "$Name (pt-BR)" }
    )
}

function New-Dump([string[]] $Resources) {
    return "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><PriInfo><ResourceMap name=`"PedroLoy.ServerAlyzer`"><ResourceMapSubtree name=`"Resources`">$($Resources -join '')</ResourceMapSubtree></ResourceMap></PriInfo>"
}

function Invoke-Case {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [string] $ExpectedReason,             # empty = must pass
        [string] $Manifest = (New-Manifest),
        [string[]] $Resources = @((New-Resource 'AppDescription' (Get-GoodCandidates 'AppDescription'))),
        [switch] $NoPri,
        [switch] $NoMakePri,
        [switch] $MakePriFails,
        # makepri reports failure but leaves a (complete-looking) dump behind.
        [switch] $MakePriFailsAfterWriting
    )
    $dir = Join-Path $work ([guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $package = Join-Path $dir 'ServerAlyzer_1.1.7.0_x64_0123456.msix'
    $archive = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = [ordered] @{ 'AppxManifest.xml' = $Manifest }
        if (-not $NoPri) { $entries['resources.pri'] = 'mrm_pri2 synthetic' }
        foreach ($key in $entries.Keys) {
            $writer = New-Object IO.StreamWriter($archive.CreateEntry($key).Open())
            try { $writer.Write($entries[$key]) } finally { $writer.Dispose() }
        }
    }
    finally { $archive.Dispose() }

    $dumpSource = Join-Path $dir 'prepared-dump.xml'
    Set-Content -LiteralPath $dumpSource -Value (New-Dump $Resources) -Encoding utf8
    $stub = Join-Path $dir 'makepri-stub.ps1'
    $exit = if ($MakePriFails -or $MakePriFailsAfterWriting) { 1 } else { 0 }
    $write = if ($MakePriFails) { 0 } else { 1 }
    Set-Content -LiteralPath $stub -Value @"
`$of = `$args[[array]::IndexOf(`$args, '/of') + 1]
if (-not (Test-Path -LiteralPath `$args[[array]::IndexOf(`$args, '/if') + 1])) { exit 2 }
if ($write -eq 1) { Copy-Item -LiteralPath '$dumpSource' -Destination `$of }
exit $exit
"@
    $arguments = @{ PackagePath = $package }
    if ($NoMakePri) { $arguments.MakePriSearchRoots = @((Join-Path $dir 'no-sdk-here')) }
    else { $arguments.MakePriPath = $stub }

    $failure = $null
    try { & $CheckScript @arguments 6> $null } catch { $failure = $_.Exception.Message }
    $verdict = 'OK'; $detail = ''
    if ($ExpectedReason) {
        if ($null -eq $failure) { $verdict = 'TEST ERROR'; $detail = 'accepted' }
        elseif ($failure -notmatch $ExpectedReason) { $verdict = 'TEST ERROR'; $detail = "wrong reason: $failure" }
    }
    elseif ($null -ne $failure) { $verdict = 'TEST ERROR'; $detail = "rejected: $failure" }
    $results.Add([pscustomobject] @{ Case = $Name; Verdict = $verdict; Detail = $detail }) | Out-Null
}

$widget = "<Extensions><Extension><Definition Description=`"ms-resource:WidgetDescription`" /></Extension></Extensions>"
$allGood = @((New-Resource 'AppDescription' (Get-GoodCandidates 'AppDescription')), (New-Resource 'WidgetDescription' (Get-GoodCandidates 'WidgetDescription')))

try {
    Invoke-Case -Name 'baseline'
    Invoke-Case -Name 'baseline: every manifest ms-resource resolves' -Manifest (New-Manifest -Extra $widget) -Resources $allGood
    Invoke-Case -Name 'baseline: language-neutral candidate equal to en-US' -Resources @((New-Resource 'AppDescription' ((Get-GoodCandidates 'AppDescription') + @(@{ Lang = $null; Value = 'AppDescription (en)' }))))
    Invoke-Case -Name 'baseline: long ms-resource form' -Manifest (New-Manifest -Description 'ms-resource:///Resources/AppDescription')

    Invoke-Case -Name 'manifest default pt-BR (the 1.1.x defect)' -Manifest (New-Manifest -Languages @('pt-BR', 'pt-PT', 'en-US')) -ExpectedReason 'first <Resource Language>\) is pt-BR, not en-US'
    Invoke-Case -Name 'manifest without pt-PT' -Manifest (New-Manifest -Languages @('en-US', 'pt-BR')) -ExpectedReason 'does not declare <Resource Language="pt-PT">'
    Invoke-Case -Name 'manifest without languages' -Manifest (New-Manifest -Languages @()) -ExpectedReason 'declares no <Resource Language>'
    Invoke-Case -Name 'AppDescription missing from the PRI' -Resources @() -ExpectedReason 'ms-resource:AppDescription: expected one named resource'
    Invoke-Case -Name 'AppDescription missing even when the manifest no longer uses it' -Manifest (New-Manifest -Description 'ServerAlyzer') -Resources @() -ExpectedReason 'ms-resource:AppDescription: expected one named resource'
    foreach ($language in 'EN-US', 'PT-PT', 'PT-BR') {
        $candidates = @(Get-GoodCandidates 'AppDescription' | Where-Object { $_.Lang -ne $language })
        if ($language -eq 'EN-US') { $candidates[0].Score = '1.0' }
        Invoke-Case -Name "AppDescription without $language" -Resources @((New-Resource 'AppDescription' $candidates)) -ExpectedReason "ms-resource:AppDescription does not resolve in: $([regex]::Escape(@{ 'EN-US' = 'en-US'; 'PT-PT' = 'pt-PT'; 'PT-BR' = 'pt-BR' }[$language]))"
    }
    $empty = @(Get-GoodCandidates 'AppDescription'); $empty[2].Value = ' '
    Invoke-Case -Name 'AppDescription empty in pt-BR' -Resources @((New-Resource 'AppDescription' $empty)) -ExpectedReason 'does not resolve in: pt-BR'
    $ptDefault = @(Get-GoodCandidates 'AppDescription'); $ptDefault[0].Score = '0.0'; $ptDefault[2].Score = '1.0'
    Invoke-Case -Name 'PRI falls back to pt-BR' -Resources @((New-Resource 'AppDescription' $ptDefault)) -ExpectedReason 'default \(fallback\) candidate is PT-BR, not en-US'
    $noDefault = @(Get-GoodCandidates 'AppDescription'); $noDefault[0].Score = '0.0'
    Invoke-Case -Name 'PRI without a default candidate' -Resources @((New-Resource 'AppDescription' $noDefault)) -ExpectedReason 'default \(fallback\) candidate is none'
    $twoDefaults = @(Get-GoodCandidates 'AppDescription'); $twoDefaults[1].Score = '1.0'
    Invoke-Case -Name 'PRI with two defaults' -Resources @((New-Resource 'AppDescription' $twoDefaults)) -ExpectedReason 'default \(fallback\) candidate is EN-US, PT-PT'
    Invoke-Case -Name 'language-neutral candidate in Portuguese' -Resources @((New-Resource 'AppDescription' ((Get-GoodCandidates 'AppDescription') + @(@{ Lang = $null; Value = 'Painel' })))) -ExpectedReason 'language-neutral candidate is not the en-US text'
    Invoke-Case -Name 'another manifest ms-resource missing' -Manifest (New-Manifest -Extra $widget) -ExpectedReason 'ms-resource:WidgetDescription: expected one named resource'
    Invoke-Case -Name 'unsupported ms-resource form' -Manifest (New-Manifest -Extra "<X Description=`"ms-resource:Other/Thing`" />") -ExpectedReason 'unsupported ms-resource reference form'
    Invoke-Case -Name 'no resources.pri in the package' -NoPri -ExpectedReason 'resources\.pri not found'
    Invoke-Case -Name 'makepri missing (fails closed)' -NoMakePri -ExpectedReason '^Package resources: makepri was not found'
    Invoke-Case -Name 'makepri fails' -MakePriFails -ExpectedReason '^Package resources: makepri could not dump'
    Invoke-Case -Name 'makepri fails after writing a dump' -MakePriFailsAfterWriting -ExpectedReason '^Package resources: makepri could not dump resources\.pri \(exit 1\)'
}
finally {
    $resolved = [IO.Path]::GetFullPath($work)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}

$results | Format-Table -AutoSize Case, Verdict, Detail | Out-String -Width 220 | Write-Host
$broken = @($results | Where-Object Verdict -ne 'OK')
if ($broken.Count -gt 0) { throw "$($broken.Count) of $($results.Count) package resources case(s) did not behave as required." }
Write-Host "All $($results.Count) package resources cases behaved as required."
