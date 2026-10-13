<#
.SYNOPSIS
    Counterproof for Assert-DistributionPrivacy.ps1: crafts synthetic artifacts with a known path
    leak in each channel the check claims to cover, and proves the check rejects every one of them.

.DESCRIPTION
    A privacy check that only ever runs against a clean package proves nothing: a script that
    returns success unconditionally, or one that quietly skips what it cannot parse, would look
    identical. Every case here changes one thing in an otherwise clean synthetic package and asserts
    the outcome AND the reason, and every case also asserts that the leaked string itself never
    appears in the check's output.

    The fixtures are real formats, built in memory with System.Reflection.Metadata (part of the
    runtime PowerShell runs on): managed PE images with a CodeView entry, a #US string and an
    embedded portable PDB, standalone portable PDBs with documents and a Source Link record, and
    zip containers nested inside each other. Every leaked path is synthetic (fixture-user,
    fixture-share, ...). Nothing here reads a real package, a real profile or the network.

.PARAMETER CheckScript
    The check to exercise. Defaults to the one next to this file; the mutation runs point it at a
    deliberately broken copy to prove that each case actually guards something.
#>
[CmdletBinding()]
param(
    [string] $CheckScript = (Join-Path $PSScriptRoot 'Assert-DistributionPrivacy.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

if (-not (Test-Path -LiteralPath $CheckScript)) { throw "Cannot find $CheckScript" }

if (-not ('DistributionPrivacyFixture' -as [type])) {
    Add-Type -ReferencedAssemblies @('System.Reflection.Metadata', 'System.Collections.Immutable', 'System.Runtime', 'System.Collections') -TypeDefinition @'
using System;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

public static class DistributionPrivacyFixture
{
    private static readonly Guid SourceLinkKind = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    /// <summary>A standalone portable PDB with the given documents and, optionally, a Source Link record.</summary>
    public static byte[] PortablePdb(string[] documents, string sourceLinkJson)
    {
        var pdb = new MetadataBuilder();
        foreach (var document in documents ?? new string[0])
        {
            pdb.AddDocument(pdb.GetOrAddDocumentName(document), default(GuidHandle), default(BlobHandle), default(GuidHandle));
        }
        if (sourceLinkJson != null)
        {
            pdb.AddCustomDebugInformation(
                MetadataTokens.EntityHandle(TableIndex.Module, 1),
                pdb.GetOrAddGuid(SourceLinkKind),
                pdb.GetOrAddBlobUTF8(sourceLinkJson));
        }
        var rowCounts = ImmutableArray.Create(new int[MetadataTokens.TableCount]);
        var builder = new PortablePdbBuilder(pdb, rowCounts, default(MethodDefinitionHandle));
        var blob = new BlobBuilder();
        builder.Serialize(blob);
        return blob.ToArray();
    }

    /// <summary>A minimal managed library: optional CodeView PDB path, #US string and embedded portable PDB.</summary>
    public static byte[] Assembly(string name, string codeViewPath, string userString, string[] embeddedDocuments)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(new Guid("0d1f6a3e-2b7c-4c55-9a51-7f1c2a0b9e01")), default(GuidHandle), default(GuidHandle));
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default(StringHandle), default(BlobHandle), 0, AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(default(TypeAttributes), default(StringHandle), metadata.GetOrAddString("<Module>"), default(EntityHandle),
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.GetOrAddUserString("an ordinary literal");
        if (userString != null) metadata.GetOrAddUserString(userString);

        var debug = new DebugDirectoryBuilder();
        if (codeViewPath != null)
        {
            debug.AddCodeViewEntry(codeViewPath, new BlobContentId(new Guid("6f4e8f0a-8a1d-4f7e-b1a2-3c4d5e6f7a8b"), 0x12345678u), 0x0100);
        }
        if (embeddedDocuments != null)
        {
            var pdb = new BlobBuilder();
            pdb.WriteBytes(PortablePdb(embeddedDocuments, null));
            debug.AddEmbeddedPortablePdbEntry(pdb, 0x0100);
        }

        var pe = new ManagedPEBuilder(
            PEHeaderBuilder.CreateLibraryHeader(),
            new MetadataRootBuilder(metadata),
            new BlobBuilder(),
            debugDirectoryBuilder: debug);
        var output = new BlobBuilder();
        pe.Serialize(output);
        return output.ToArray();
    }
}
'@
}

# ------------------------------------------------------------------------------------- fixtures
# Synthetic leaks only. None of these is, or is derived from, a real path on any machine.
$Leak = @{
    CodeView     = 'C:\Users\fixture-user\src\ServerMonitor.Core\obj\Release\ServerMonitor.Core.pdb'
    UserString   = 'C:\Users\fixture-user\checkout\src\ServerMonitor.App\Services\Caller.cs'
    EmbeddedDoc  = 'E:\fixture-work\checkout\src\ServerMonitor.Core\Model.cs'
    PdbDoc       = 'F:\fixture-build\src\ServerMonitor.App\Views\Page.xaml'
    SourceLink   = 'G:\fixture-root\checkout\'
    JsonCiRoot   = 'D:\a\fixture-repo\fixture-repo\src\obj'
    Utf16Unc     = '\\fixture-share\builds\src\ServerMonitor.App'
    HomeDir      = '/home/fixture-user/checkout/src/Views/Page.xaml'
    MacUsers     = '/Users/fixture-user/checkout/src/Model.cs'
    EntryName    = 'a/C:/Users/fixture-user/secret-folder/readme.txt'
    DenyToken    = 'fixture-secret-machine-name'
    RepoRoot     = 'C:\fixture-root\fixture-checkout'
    ThirdParty   = 'C:\__w\1\s\fixture-vendor\obj\Vendor.pdb'
    ThirdParty2  = 'C:\__w\1\s\fixture-vendor\other\Vendor.Native.pdb'
    VendorFile   = '/home/fixture-user/vendor-secret/x.c'
}
$Clean = @{
    CodeView = '/_/src/ServerMonitor.Core/obj/Release/ServerMonitor.Core.pdb'
    Document = '/_/src/ServerMonitor.Core/Model.cs'
}

$RequiredNames = @('AppxManifest.xml', 'ServerMonitor.App.dll', 'ServerMonitor.Core.dll')

function New-Assembly([string] $name, [string] $codeView = $Clean.CodeView, [string] $userString = $null, [string[]] $embedded = $null) {
    return , [DistributionPrivacyFixture]::Assembly($name, $codeView, $userString, $embedded)
}

# Entries as an ordered list of (name, bytes) pairs, so a test can write the same name twice.
function New-ZipFromPairs([object[]] $pairs) {
    $buffer = New-Object System.IO.MemoryStream
    $archive = [System.IO.Compression.ZipArchive]::new($buffer, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($pair in $pairs) {
            $stream = $archive.CreateEntry($pair[0]).Open()
            try { $bytes = [byte[]] $pair[1]; $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    return , $buffer.ToArray()
}

function New-Zip([hashtable] $entries) {
    return , (New-ZipFromPairs @($entries.Keys | Sort-Object | ForEach-Object { , @($_, $entries[$_]) }))
}

function Get-Utf8([string] $text) { return , [System.Text.Encoding]::UTF8.GetBytes($text) }
function Get-Utf16([string] $text) { return , [System.Text.Encoding]::Unicode.GetBytes($text) }
function Get-Sha([byte[]] $bytes) { return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() }
function Get-PathToken([string] $value) {
    return (Get-Sha ([System.Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant()))).Substring(0, 12)
}

# Rewrites the uncompressed size an embedded PDB declares after its 'MPDB' signature.
function Set-EmbeddedPdbDeclaredSize([byte[]] $image, [int] $delta) {
    $copy = [byte[]] $image.Clone()
    for ($i = 0; $i -lt $copy.Length - 8; $i++) {
        if ($copy[$i] -eq 0x4D -and $copy[$i + 1] -eq 0x50 -and $copy[$i + 2] -eq 0x44 -and $copy[$i + 3] -eq 0x42) {
            $size = [System.BitConverter]::ToInt32($copy, $i + 4) + $delta
            [System.BitConverter]::GetBytes([int] $size).CopyTo($copy, $i + 4)
            return , $copy
        }
    }
    throw 'fixture error: no MPDB signature in the image'
}

# The clean package every case starts from: the required names, managed images with mapped
# CodeView paths, an embedded PDB with mapped documents, JSON and a manifest.
function Get-CleanEntries {
    return @{
        'AppxManifest.xml'                 = Get-Utf8 '<?xml version="1.0"?><Package><Identity Name="Fixture" /></Package>'
        'ServerMonitor.App.dll'            = New-Assembly 'ServerMonitor.App' -userString '/_/src/ServerMonitor.App/Services/Caller.cs' -embedded @($Clean.Document)
        'ServerMonitor.Core.dll'           = New-Assembly 'ServerMonitor.Core'
        'ServerMonitor.App.deps.json'      = Get-Utf8 '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"},"libraries":{"ServerMonitor.Core/1.0.0":{"path":"lib/net10.0"}}}'
        'resources.pri'                    = Get-Utf16 'mrm_pri2 ms-resource:///Files/Views/Page.xbf'
    }
}

# Nests the given entries inside $levels wrapper zips: the result, used as the input, puts the
# innermost container at depth $levels.
function New-NestedZip([hashtable] $innermost, [int] $levels) {
    $bytes = New-Zip $innermost
    for ($level = $levels; $level -ge 1; $level--) { $bytes = New-Zip @{ "level$level.zip" = $bytes } }
    return , $bytes
}

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ('distribution-privacy-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null
$results = New-Object System.Collections.Generic.List[object]

<#
    One case. -Mutate gets the clean entries and either edits them (the default input is a zip of
    them named -FileName) or returns the bytes of the single input itself. -Inputs instead receives
    (entries, caseDir) and returns the input paths, for multi-input cases. Every run writes a
    -ReportPath, and the redaction assertion covers the console AND that file.
#>
function Invoke-Case {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [ValidateSet('pass', 'fail')] [string] $Expected,
        [scriptblock] $Mutate = { param($e) },
        [scriptblock] $Inputs,
        [string[]] $Pattern = @(),                             # each must appear in the output
        [string[]] $MustNotPrint = @(),                        # never in the console or the report
        [string] $FileName = 'fixture.msix',
        [hashtable] $Arguments = @{},
        [switch] $LockInput
    )

    $entries = Get-CleanEntries
    $caseDir = Join-Path $workDir ([guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $caseDir | Out-Null
    if ($Inputs) {
        $paths = @(& $Inputs $entries $caseDir)
    }
    else {
        $bytes = & $Mutate $entries
        if ($null -eq $bytes) { $bytes = New-Zip $entries }
        $inputPath = Join-Path $caseDir $FileName
        [System.IO.File]::WriteAllBytes($inputPath, [byte[]] $bytes)
        $paths = @($inputPath)
    }
    $reportPath = Join-Path $caseDir 'report.json'

    $call = @{ Path = $paths; RequiredArtifacts = $RequiredNames; RepositoryRoot = $Leak.RepoRoot; ReportPath = $reportPath }
    foreach ($key in $Arguments.Keys) { $call[$key] = $Arguments[$key] }
    $lock = $null
    if ($LockInput) { $lock = [System.IO.File]::Open($paths[0], 'Open', 'Read', 'None') }
    $outcome = 'pass'
    # Collected line by line: when the check throws, whatever it printed before must still be here.
    $lines = New-Object System.Collections.Generic.List[string]
    try { & $CheckScript @call *>&1 | ForEach-Object { $lines.Add("$_") } }
    catch { $outcome = 'fail'; $lines.Add($_.Exception.Message) }
    finally { if ($lock) { $lock.Dispose() } }
    $output = $lines -join "`n"
    $report = if (Test-Path -LiteralPath $reportPath) { Get-Content -LiteralPath $reportPath -Raw } else { '' }

    $verdict = if ($outcome -eq $Expected) { 'OK' } else { 'TEST ERROR' }
    $detail = ''
    foreach ($expectedText in $Pattern) {
        if ($verdict -eq 'OK' -and $output -notmatch $expectedText) {
            $verdict = 'TEST ERROR'; $detail = "outcome right, reason wrong (expected /$expectedText/)"
        }
    }
    foreach ($secret in $MustNotPrint) {
        # The plain, slash, lower-case and JSON-escaped (doubled backslash) forms of the secret.
        $forms = @($secret, $secret.Replace('\', '/'), $secret.ToLowerInvariant(), $secret.Replace('\', '\\'), $secret.Replace('\', '\\').ToLowerInvariant())
        foreach ($form in $forms) {
            foreach ($channel in @(@('console', $output), @('report', $report))) {
                if ($channel[1].IndexOf($form, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $verdict = 'TEST ERROR'; $detail = "the leaked string appears in the $($channel[0]) (redaction broken)"
                }
            }
        }
    }
    if ($verdict -ne 'OK' -and -not $detail.Contains('redaction')) {
        # Synthetic fixtures only, so the failing lines are safe to show and say what went wrong.
        $detail += ' | ' + ((@($lines | Where-Object { $_ -match 'FAIL |PASSED' }) | Select-Object -First 3) -join ' / ')
    }
    $results.Add([pscustomobject] @{ Case = $Name; Expected = $Expected; Actual = $outcome; Verdict = $verdict; Detail = $detail }) | Out-Null
    Remove-Item -LiteralPath $caseDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Every case below is counted. A case that is silently skipped (an exception in a fixture, a
# refactoring that drops one) makes the suite fail instead of quietly shrinking.
$ExpectedCaseCount = 52

try {
    # ------------------------------------------------------------------------------ baseline
    Invoke-Case -Name 'baseline: clean package with /_/ mapped paths' -Expected 'pass'

    Invoke-Case -Name 'required artifacts match case-insensitively' -Expected 'pass' -Mutate {
        param($e)
        # PowerShell hashtables ignore case, so the renamed entry is written as an explicit pair.
        $core = $e['ServerMonitor.Core.dll']; $e.Remove('ServerMonitor.Core.dll')
        $pairs = @($e.Keys | Sort-Object | ForEach-Object { , @($_, $e[$_]) })
        New-ZipFromPairs ($pairs + @(, @('SERVERMONITOR.CORE.DLL', $core))) }

    # ------------------------------------------------------------------- one case per channel
    Invoke-Case -Name 'CodeView PDB path in the PE debug directory' -Expected 'fail' -MustNotPrint $Leak.CodeView `
        -Pattern 'ServerMonitor\.Core\.dll \[pe-codeview-pdb-path\] profile-dir' -Mutate {
            param($e) $e['ServerMonitor.Core.dll'] = New-Assembly 'ServerMonitor.Core' -codeView $Leak.CodeView }

    Invoke-Case -Name 'CallerFilePath-style string in the #US heap' -Expected 'fail' -MustNotPrint $Leak.UserString `
        -Pattern 'ServerMonitor\.App\.dll \[pe-user-string\] profile-dir' -Mutate {
            param($e) $e['ServerMonitor.App.dll'] = New-Assembly 'ServerMonitor.App' -userString $Leak.UserString }

    Invoke-Case -Name 'document path inside an embedded portable PDB' -Expected 'fail' -MustNotPrint $Leak.EmbeddedDoc `
        -Pattern 'ServerMonitor\.Core\.dll \[pe-embedded-pdb-document\] drive-path' -Mutate {
            param($e) $e['ServerMonitor.Core.dll'] = New-Assembly 'ServerMonitor.Core' -embedded @($Clean.Document, $Leak.EmbeddedDoc) }

    Invoke-Case -Name 'document path in a standalone PDB inside a symbols package' -Expected 'fail' -MustNotPrint $Leak.PdbDoc `
        -Pattern 'fixture\.msixupload!fixture\.appxsym!ServerMonitor\.App\.pdb \[pdb-document\] drive-path' -FileName 'fixture.msixupload' -Mutate {
            param($e)
            $symbols = New-Zip @{ 'ServerMonitor.App.pdb' = [DistributionPrivacyFixture]::PortablePdb(@($Clean.Document, $Leak.PdbDoc), $null) }
            New-Zip @{ 'fixture.msix' = (New-Zip $e); 'fixture.appxsym' = $symbols } }

    Invoke-Case -Name 'Source Link map keyed by a local path' -Expected 'fail' -MustNotPrint $Leak.SourceLink `
        -Pattern 'ServerMonitor\.App\.pdb \[pdb/bytes-utf8\] drive-path' -Mutate {
            param($e)
            $json = '{"documents":{"' + $Leak.SourceLink.Replace('\', '\\') + 'src\\*":"https://example.invalid/fixture/*"}}'
            $e['ServerMonitor.App.pdb'] = [DistributionPrivacyFixture]::PortablePdb(@($Clean.Document), $json) }

    Invoke-Case -Name 'CI runner root in a JSON file' -Expected 'fail' -MustNotPrint $Leak.JsonCiRoot `
        -Pattern 'ServerMonitor\.App\.deps\.json \[file/bytes-utf8\] ci-runner-root' -Mutate {
            param($e) $e['ServerMonitor.App.deps.json'] = Get-Utf8 ('{"probe":"' + $Leak.JsonCiRoot.Replace('\', '\\') + '"}') }

    Invoke-Case -Name 'UNC path stored as UTF-16 (resources.pri-like)' -Expected 'fail' -MustNotPrint $Leak.Utf16Unc `
        -Pattern 'resources\.pri \[file/bytes-utf16\] unc-path' -Mutate {
            param($e) $e['resources.pri'] = Get-Utf16 ("mrm_pri2 " + $Leak.Utf16Unc + "\Strings") }

    Invoke-Case -Name 'UTF-16 path at an odd byte offset' -Expected 'fail' -MustNotPrint $Leak.Utf16Unc `
        -Pattern 'resources\.pri \[file/bytes-utf16\] unc-path' -Mutate {
            param($e) $e['resources.pri'] = [byte[]] (@([byte] 7) + (Get-Utf16 ($Leak.Utf16Unc + '\x'))) }

    Invoke-Case -Name '/home/ profile path in a binary XAML file' -Expected 'fail' -MustNotPrint $Leak.HomeDir `
        -Pattern 'Page\.xbf \[file/bytes-utf8\] profile-dir' -Mutate {
            param($e) $e['Views/Page.xbf'] = [byte[]] (@([byte] 0x58, 0x42, 0x46, 0x00) + (Get-Utf8 $Leak.HomeDir)) }

    Invoke-Case -Name '/Users/ profile path nested two containers deep' -Expected 'fail' -MustNotPrint $Leak.MacUsers `
        -Pattern 'fixture\.msixupload!fixture\.msix!ServerMonitor\.Core\.dll \[pe-user-string\] profile-dir' -FileName 'fixture.msixupload' -Mutate {
            param($e)
            $e['ServerMonitor.Core.dll'] = New-Assembly 'ServerMonitor.Core' -userString $Leak.MacUsers
            New-Zip @{ 'fixture.msix' = (New-Zip $e) } }

    Invoke-Case -Name 'environment-derived: the repository root' -Expected 'fail' -MustNotPrint $Leak.RepoRoot `
        -Pattern 'notes\.txt \[file/bytes-utf8\] env-repo-root' -Mutate {
            param($e) $e['notes.txt'] = Get-Utf8 ('built in ' + $Leak.RepoRoot.Replace('\', '/') + '/src') }

    Invoke-Case -Name 'environment-derived: an explicit deny token without any path shape' -Expected 'fail' -MustNotPrint $Leak.DenyToken `
        -Pattern 'notes\.txt \[file/bytes-utf8\] deny-token' -Arguments @{ DenyToken = @($Leak.DenyToken) } -Mutate {
            param($e) $e['notes.txt'] = Get-Utf8 ('machine=' + $Leak.DenyToken) }

    # ------------------------------------------------------- artifact names are redacted too
    Invoke-Case -Name 'zip entry NAME that is a path: detected, and never printed or reported' -Expected 'fail' `
        -MustNotPrint @('fixture-user', $Leak.EntryName) `
        -Pattern '\[zip-entry-name\] profile-dir', 'fixture\.msix!a/C:<redacted:[0-9a-f]{12}> \[zip-entry-name\]' -Mutate {
            param($e) $e[$Leak.EntryName] = Get-Utf8 'harmless content' }

    Invoke-Case -Name 'input file NAME holding a deny token is redacted' -Expected 'fail' -MustNotPrint $Leak.DenyToken `
        -Pattern '<redacted:[0-9a-f]{12}>\.msix!notes\.txt \[file/bytes-utf8\] deny-token' -FileName "$($Leak.DenyToken).msix" `
        -Arguments @{ DenyToken = @($Leak.DenyToken) } -Mutate {
            param($e) $e['notes.txt'] = Get-Utf8 ('machine=' + $Leak.DenyToken) }

    # ------------------------------------------------------------------------- fail-closed
    Invoke-Case -Name 'required artifact missing' -Expected 'fail' -Pattern 'required artifact missing: ServerMonitor\.Core\.dll' -Mutate {
        param($e) $e.Remove('ServerMonitor.Core.dll') }

    Invoke-Case -Name 'corrupt PE (MZ header, garbage after it)' -Expected 'fail' `
        -Pattern 'unparseable: fixture\.msix!ServerMonitor\.Core\.dll \(BadImageFormatException\)' -Mutate {
            param($e) $e['ServerMonitor.Core.dll'] = [byte[]] (@([byte] 0x4D, 0x5A) + (Get-Utf8 ('x' * 200))) }

    Invoke-Case -Name '.dll that is not a PE at all' -Expected 'fail' -Pattern 'unparseable: fixture\.msix!Vendor\.dll \(BadImageFormatException\)' -Mutate {
        param($e) $e['Vendor.dll'] = Get-Utf8 'definitely not a portable executable' }

    Invoke-Case -Name 'truncated managed PE' -Expected 'fail' -Pattern 'unparseable: fixture\.msix!ServerMonitor\.Core\.dll \(BadImageFormatException\)' -Mutate {
        param($e) $full = [byte[]] $e['ServerMonitor.Core.dll']; $e['ServerMonitor.Core.dll'] = [byte[]] $full[0..([int]($full.Length / 3))] }

    Invoke-Case -Name 'embedded PDB whose MPDB header declares the wrong size' -Expected 'fail' `
        -Pattern 'unparseable: fixture\.msix!ServerMonitor\.Core\.dll \(embedded PDB size does not match its MPDB header\)' -Mutate {
            param($e) $e['ServerMonitor.Core.dll'] = Set-EmbeddedPdbDeclaredSize (New-Assembly 'ServerMonitor.Core' -embedded @($Clean.Document)) 1 }

    Invoke-Case -Name 'corrupt portable PDB' -Expected 'fail' -Pattern 'unparseable: fixture\.msix!ServerMonitor\.App\.pdb \(BadImageFormatException\)' -Mutate {
        param($e) $e['ServerMonitor.App.pdb'] = [byte[]] (@([byte] 0x42, 0x53, 0x4A, 0x42) + (Get-Utf8 ('y' * 64))) }

    Invoke-Case -Name 'Windows (MSF) PDB is refused, not byte-scanned' -Expected 'fail' `
        -Pattern 'unparseable: fixture\.msix!ServerMonitor\.App\.pdb \(not a portable PDB' -Mutate {
            param($e) $e['ServerMonitor.App.pdb'] = Get-Utf8 ("Microsoft C/C++ MSF 7.00`r`n" + ('z' * 64)) }

    Invoke-Case -Name 'unreadable container' -Expected 'fail' -Pattern 'unparseable: fixture\.msix \(InvalidDataException\)' -Mutate {
        param($e) Get-Utf8 'PK this is not a zip file at all' }

    Invoke-Case -Name 'input that does not exist' -Expected 'fail' -Pattern 'input not found: missing\.msix' -MustNotPrint $workDir `
        -Inputs { param($e, $dir) Join-Path $dir 'missing.msix' }

    Invoke-Case -Name 'no input at all' -Expected 'fail' -Pattern 'no input was given' -Inputs { param($e, $dir) , @() }

    Invoke-Case -Name 'empty input directory' -Expected 'fail' -Pattern 'input directory is empty: empty-dir' -MustNotPrint $workDir -Inputs {
        param($e, $dir) $d = Join-Path $dir 'empty-dir'; New-Item -ItemType Directory -Path $d | Out-Null; $d }

    Invoke-Case -Name 'input file locked by another process' -Expected 'fail' -Pattern 'unparseable: fixture\.msix \(IOException\)' `
        -MustNotPrint $workDir -LockInput

    Invoke-Case -Name 'nesting exactly at the depth limit is inspected' -Expected 'pass' -Mutate {
        param($e) $e['deep.zip'] = New-NestedZip @{ 'inner.txt' = (Get-Utf8 'nothing here') } 3 }

    Invoke-Case -Name 'nesting one level past the depth limit fails' -Expected 'fail' `
        -Pattern 'unparseable: fixture\.msix!deep\.zip!level1\.zip!level2\.zip!level3\.zip!level4\.zip \(containers nested deeper than 4\)' -Mutate {
            param($e) $e['deep.zip'] = New-NestedZip @{ 'inner.txt' = (Get-Utf8 'nothing here') } 4 }

    Invoke-Case -Name 'one entry over the per-entry cap (lowered for the test)' -Expected 'fail' `
        -Pattern 'unparseable: fixture\.msix \(per-entry size cap exceeded \(32768 bytes\)\)' -Arguments @{ MaxEntryBytes = 32768 } -Mutate {
            param($e) $e['bomb.bin'] = New-Object byte[] 65536 }

    Invoke-Case -Name 'total over the size cap (lowered for the test)' -Expected 'fail' `
        -Pattern '\(total size cap exceeded \(6000 bytes\)\)' -Arguments @{ MaxTotalBytes = 6000 } -Mutate {
            param($e) $e['filler.bin'] = New-Object byte[] 8000 }

    # ----------------------------------------------------------------- duplicates fail closed
    Invoke-Case -Name 'zip with two entries of the same name' -Expected 'fail' -Pattern 'duplicate artifact: fixture\.msix!vendor\.bin' -Mutate {
        param($e)
        $pairs = @($e.Keys | Sort-Object | ForEach-Object { , @($_, $e[$_]) })
        New-ZipFromPairs ($pairs + @(, @('vendor.bin', (Get-Utf8 'clean one'))) + @(, @('vendor.bin', (Get-Utf8 'clean two')))) }

    Invoke-Case -Name 'two directory inputs with the same name' -Expected 'fail' -Pattern 'duplicate input name: payload' -Inputs {
        param($e, $dir)
        foreach ($side in 'a', 'b') {
            $d = Join-Path $dir "$side\payload"; New-Item -ItemType Directory -Path $d -Force | Out-Null
            [System.IO.File]::WriteAllBytes((Join-Path $d "$side.txt"), (Get-Utf8 'nothing here'))
        }
        [System.IO.File]::WriteAllBytes((Join-Path $dir 'fixture.msix'), (New-Zip $e))
        @((Join-Path $dir 'fixture.msix'), (Join-Path $dir 'a\payload'), (Join-Path $dir 'b\payload')) }

    # ---------------------------------------------------------------------------- allowlist
    $vendor = New-Assembly 'Vendor' -codeView $Leak.ThirdParty
    $vendorHash = Get-Sha $vendor
    $allowFile = Join-Path $workDir 'allow.json'
    function Write-Allowlist($entries) { ConvertTo-Json -InputObject @($entries) -Depth 4 | Out-File -LiteralPath $allowFile -Encoding utf8 }

    Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = $vendorHash; categories = @('ci-runner-root'); reason = 'fixture vendor build agent' })
    Invoke-Case -Name 'allowlist: reviewed third-party file bound to its bytes' -Expected 'pass' -MustNotPrint $Leak.ThirdParty `
        -Pattern 'allow fixture\.msix!Vendor\.dll: 1 third-party finding' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
            param($e) $e['Vendor.dll'] = $vendor }

    Invoke-Case -Name 'allowlist: same name, different bytes is not excused' -Expected 'fail' -MustNotPrint $Leak.ThirdParty `
        -Pattern 'Vendor\.dll \[pe-codeview-pdb-path\] ci-runner-root' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
            param($e) $e['Vendor.dll'] = New-Assembly 'Vendor2' -codeView $Leak.ThirdParty }

    Invoke-Case -Name 'allowlist: the file name must match exactly, not as a substring' -Expected 'fail' -MustNotPrint $Leak.ThirdParty `
        -Pattern 'OtherVendor\.dll \[pe-codeview-pdb-path\] ci-runner-root' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
            param($e) $e['OtherVendor.dll'] = $vendor }

    # Vigil M-1: the leaky copy and the clean copy share a name; the binding must use the bytes that
    # produced the finding, not whichever file with that name was read last.
    $cleanVendorBin = Get-Utf8 'clean vendor payload'
    Write-Allowlist @(@{ file = 'vendor.bin'; sha256 = (Get-Sha $cleanVendorBin); categories = @('profile-dir'); reason = 'fixture: bound to the clean copy' })
    Invoke-Case -Name 'allowlist: two inputs with one name cannot lend each other a binding' -Expected 'fail' -MustNotPrint $Leak.VendorFile `
        -Pattern 'duplicate input name: vendor\.bin', 'vendor\.bin \[file/bytes-utf8\] profile-dir' -Arguments @{ AllowlistPath = $allowFile; RequiredArtifacts = @() } -Inputs {
            param($e, $dir)
            foreach ($side in 'dupA', 'dupB') { New-Item -ItemType Directory -Path (Join-Path $dir $side) | Out-Null }
            [System.IO.File]::WriteAllBytes((Join-Path $dir 'dupA\vendor.bin'), (Get-Utf8 ('src=' + $Leak.VendorFile)))
            [System.IO.File]::WriteAllBytes((Join-Path $dir 'dupB\vendor.bin'), $cleanVendorBin)
            @((Join-Path $dir 'dupA\vendor.bin'), (Join-Path $dir 'dupB\vendor.bin')) }

    Invoke-Case -Name 'allowlist: two zip entries with one name cannot lend each other a binding' -Expected 'fail' -MustNotPrint $Leak.VendorFile `
        -Pattern 'duplicate artifact: fixture\.msix!vendor\.bin', 'fixture\.msix!vendor\.bin \[file/bytes-utf8\] profile-dir' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
            param($e)
            $pairs = @($e.Keys | Sort-Object | ForEach-Object { , @($_, $e[$_]) })
            New-ZipFromPairs ($pairs + @(, @('vendor.bin', (Get-Utf8 ('src=' + $Leak.VendorFile)))) + @(, @('vendor.bin', $cleanVendorBin))) }

    Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = $vendorHash; categories = @('ci-runner-root'); reason = 'fixture vendor build agent' })
    Invoke-Case -Name 'allowlist: an excused file still fails on an environment match' -Expected 'fail' -MustNotPrint $Leak.DenyToken `
        -Pattern 'Vendor\.dll \[pe-user-string\] deny-token' -Arguments @{ AllowlistPath = $allowFile; DenyToken = @($Leak.DenyToken) } -Mutate {
            param($e)
            $tainted = New-Assembly 'Vendor' -codeView $Leak.ThirdParty -userString $Leak.DenyToken
            Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = (Get-Sha $tainted); categories = @('ci-runner-root'); reason = 'fixture vendor build agent' })
            $e['Vendor.dll'] = $tainted }

    Write-Allowlist @(@{ file = 'ServerMonitor.Core.dll'; sha256 = $vendorHash; categories = @('profile-dir'); reason = 'trying to excuse our own build' })
    Invoke-Case -Name 'allowlist: a file this repository builds can never be excused' -Expected 'fail' -Pattern 'fix the build instead' `
        -Arguments @{ AllowlistPath = $allowFile }

    Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = $vendorHash; categories = @('env-userprofile'); reason = 'trying to excuse the profile' })
    Invoke-Case -Name 'allowlist: environment categories can never be excused' -Expected 'fail' -Pattern 'can never be allowlisted' `
        -Arguments @{ AllowlistPath = $allowFile }

    Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = $vendorHash; categories = @('ci-runner-root'); reason = 'REVIEW' })
    Invoke-Case -Name 'allowlist: an unreviewed candidate excuses nothing' -Expected 'fail' -Pattern 'never reviewed' `
        -Arguments @{ AllowlistPath = $allowFile }

    Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = $vendorHash; categories = @('ci-runner-root'); reason = @('one', 'two') })
    Invoke-Case -Name 'allowlist: reason must be a single string' -Expected 'fail' -Pattern 'must be single strings' `
        -Arguments @{ AllowlistPath = $allowFile }

    Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = $vendorHash; tokens = @('0123456789ab'); categories = @('ci-runner-root'); reason = 'both bindings' })
    Invoke-Case -Name 'allowlist: exactly one binding (sha256 or tokens)' -Expected 'fail' -Pattern "exactly one of 'sha256' or 'tokens'" `
        -Arguments @{ AllowlistPath = $allowFile }

    Set-Content -LiteralPath $allowFile -Value '[{ "file": "Vendor.dll", ' -Encoding utf8
    Invoke-Case -Name 'allowlist: invalid JSON is an internal error reported by type only' -Expected 'fail' `
        -Pattern 'internal error \([A-Za-z]+Exception\)' -MustNotPrint @($allowFile, $workDir) -Arguments @{ AllowlistPath = $allowFile }

    Invoke-Case -Name 'allowlist: missing allowlist file' -Expected 'fail' -Pattern 'internal error \(allowlist file not found\)' `
        -MustNotPrint $workDir -Arguments @{ AllowlistPath = (Join-Path $workDir 'no-such-allowlist.json') }

    $vendorToken = Get-PathToken '\__w\1\s\fixture-vendor\obj\Vendor.pdb'
    Write-Allowlist @(@{ file = 'ServerMonitor.App.exe'; tokens = @($vendorToken); categories = @('ci-runner-root'); reason = 'apphost template' })
    Invoke-Case -Name 'allowlist: token-bound entry excuses exactly that path' -Expected 'pass' -MustNotPrint $Leak.ThirdParty `
        -Pattern 'allow fixture\.msix!ServerMonitor\.App\.exe: 1 third-party finding' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
            param($e) $e['ServerMonitor.App.exe'] = New-Assembly 'Apphost' -codeView $Leak.ThirdParty }

    Invoke-Case -Name 'allowlist: token-bound entry does not excuse another path of the SAME category' -Expected 'fail' `
        -MustNotPrint $Leak.ThirdParty2 -Pattern 'ServerMonitor\.App\.exe \[pe-user-string\] ci-runner-root' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
            param($e) $e['ServerMonitor.App.exe'] = New-Assembly 'Apphost' -codeView $Leak.ThirdParty -userString $Leak.ThirdParty2 }

    Invoke-Case -Name 'allowlist: token-bound entry does not excuse a path of another category' -Expected 'fail' -MustNotPrint $Leak.CodeView `
        -Pattern 'ServerMonitor\.App\.exe \[pe-codeview-pdb-path\] profile-dir' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
            param($e) $e['ServerMonitor.App.exe'] = New-Assembly 'Apphost' -codeView $Leak.CodeView }

    # --------------------------------------------------------------- candidates never print paths
    $candidatesFile = Join-Path $workDir 'candidates.json'
    Invoke-Case -Name 'allowlist candidates: written without paths, for review only' -Expected 'fail' -MustNotPrint $Leak.ThirdParty `
        -Pattern 'wrote 1 allowlist candidate' -Arguments @{ AllowlistCandidatesPath = $candidatesFile } -Mutate {
            param($e) $e['Vendor.dll'] = $vendor }
    $candidateText = if (Test-Path -LiteralPath $candidatesFile) { Get-Content -LiteralPath $candidatesFile -Raw } else { '' }
    $candidateLeak = $candidateText.IndexOf('fixture-vendor', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    $candidateShape = $candidateText -match '"reason":\s*"REVIEW"' -and $candidateText -match ('"tokens"[^\]]*' + $vendorToken)
    $results.Add([pscustomobject] @{
        Case = 'allowlist candidates: file content'; Expected = 'pass'; Actual = $(if (-not $candidateLeak -and $candidateShape) { 'pass' } else { 'fail' })
        Verdict = $(if (-not $candidateLeak -and $candidateShape) { 'OK' } else { 'TEST ERROR' }); Detail = "leak=$candidateLeak shape=$candidateShape" }) | Out-Null
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

$results | Format-Table -AutoSize Case, Expected, Actual, Verdict | Out-String -Width 220 | Write-Host

$broken = @($results | Where-Object { $_.Verdict -ne 'OK' })
if ($results.Count -ne $ExpectedCaseCount) {
    Write-Host "TEST ERROR  case count: expected $ExpectedCaseCount cases, ran $($results.Count)"
    throw "Distribution privacy counterproofs ran $($results.Count) case(s), expected $ExpectedCaseCount."
}
if ($broken.Count -gt 0) {
    foreach ($case in $broken) { Write-Host "TEST ERROR  $($case.Case): expected $($case.Expected), got $($case.Actual). $($case.Detail)" }
    throw "$($broken.Count) of $($results.Count) distribution privacy counterproof case(s) did not behave as required."
}

Write-Host "All $($results.Count) distribution privacy counterproof cases behaved as required."
