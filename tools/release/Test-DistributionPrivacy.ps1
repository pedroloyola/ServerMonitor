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
    DenyToken    = 'fixture-secret-machine-name'
    RepoRoot     = 'C:\fixture-root\fixture-checkout'
    ThirdParty   = 'C:\__w\1\s\fixture-vendor\obj\Vendor.pdb'
}
$Clean = @{
    CodeView = '/_/src/ServerMonitor.Core/obj/Release/ServerMonitor.Core.pdb'
    Document = '/_/src/ServerMonitor.Core/Model.cs'
}

$RequiredNames = @('AppxManifest.xml', 'ServerMonitor.App.dll', 'ServerMonitor.Core.dll')

function New-Assembly([string] $name, [string] $codeView = $Clean.CodeView, [string] $userString = $null, [string[]] $embedded = $null) {
    return , [DistributionPrivacyFixture]::Assembly($name, $codeView, $userString, $embedded)
}

function New-Zip([hashtable] $entries) {
    $buffer = New-Object System.IO.MemoryStream
    $archive = [System.IO.Compression.ZipArchive]::new($buffer, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($name in ($entries.Keys | Sort-Object)) {
            $stream = $archive.CreateEntry($name).Open()
            try { $bytes = [byte[]] $entries[$name]; $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    return , $buffer.ToArray()
}

function Get-Utf8([string] $text) { return , [System.Text.Encoding]::UTF8.GetBytes($text) }
function Get-Utf16([string] $text) { return , [System.Text.Encoding]::Unicode.GetBytes($text) }

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

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ('distribution-privacy-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null
$results = New-Object System.Collections.Generic.List[object]

function Invoke-Case {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [ValidateSet('pass', 'fail')] [string] $Expected,
        [Parameter(Mandatory)] [scriptblock] $Mutate,          # receives the entries hashtable
        [string] $Pattern,                                     # must appear in the output on 'fail'
        [string[]] $MustNotPrint = @(),                        # leak strings that must never be printed
        [string] $FileName = 'fixture.msix',
        [hashtable] $Arguments = @{}
    )

    $entries = Get-CleanEntries
    $bytes = & $Mutate $entries
    if ($null -eq $bytes) { $bytes = New-Zip $entries }
    $caseDir = Join-Path $workDir ([guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $caseDir | Out-Null
    $inputPath = Join-Path $caseDir $FileName
    [System.IO.File]::WriteAllBytes($inputPath, [byte[]] $bytes)

    $call = @{ Path = @($inputPath); RequiredArtifacts = $RequiredNames; RepositoryRoot = $Leak.RepoRoot }
    foreach ($key in $Arguments.Keys) { $call[$key] = $Arguments[$key] }
    $outcome = 'pass'
    # Collected line by line: when the check throws, whatever it printed before must still be here.
    $lines = New-Object System.Collections.Generic.List[string]
    try { & $CheckScript @call *>&1 | ForEach-Object { $lines.Add("$_") } }
    catch { $outcome = 'fail'; $lines.Add($_.Exception.Message) }
    $output = $lines -join "`n"

    $verdict = if ($outcome -eq $Expected) { 'OK' } else { 'TEST ERROR' }
    $detail = ''
    if ($verdict -eq 'OK' -and $Pattern -and $output -notmatch $Pattern) {
        $verdict = 'TEST ERROR'; $detail = "outcome right, reason wrong (expected /$Pattern/)"
    }
    foreach ($secret in $MustNotPrint) {
        foreach ($form in @($secret, $secret.Replace('\', '/'), $secret.ToLowerInvariant())) {
            if ($output.IndexOf($form, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $verdict = 'TEST ERROR'; $detail = 'the leaked string appears in the output (redaction broken)'
            }
        }
    }
    if ($verdict -ne 'OK' -and -not $detail.Contains('redaction')) {
        # Synthetic fixtures only, so the tail of the output is safe to show and says what went wrong.
        $detail += ' | ' + ((@($lines | Where-Object { $_ -match 'FAIL ' }) | Select-Object -First 3) -join ' / ')
    }
    $results.Add([pscustomobject] @{ Case = $Name; Expected = $Expected; Actual = $outcome; Verdict = $verdict; Detail = $detail }) | Out-Null
    Remove-Item -LiteralPath $caseDir -Recurse -Force -ErrorAction SilentlyContinue
}

try {
    # ------------------------------------------------------------------------------ baseline
    Invoke-Case -Name 'baseline: clean package with /_/ mapped paths' -Expected 'pass' -Mutate { param($e) }

    # ------------------------------------------------------------------- one case per channel
    Invoke-Case -Name 'CodeView PDB path in the PE debug directory' -Expected 'fail' -MustNotPrint $Leak.CodeView `
        -Pattern 'ServerMonitor\.Core\.dll \[pe-codeview-pdb-path\] profile-dir' -Mutate {
            param($e) $e['ServerMonitor.Core.dll'] = New-Assembly 'ServerMonitor.Core' -codeView $Leak.CodeView }

    Invoke-Case -Name 'CallerFilePath-style string in the #US heap' -Expected 'fail' -MustNotPrint $Leak.UserString `
        -Pattern 'ServerMonitor\.App\.dll \[pe-user-string\] profile-dir' -Mutate {
            param($e) $e['ServerMonitor.App.dll'] = New-Assembly 'ServerMonitor.App' -userString $Leak.UserString }

    Invoke-Case -Name 'document path inside an embedded portable PDB' -Expected 'fail' -MustNotPrint $Leak.EmbeddedDoc `
        -Pattern '\[pe-embedded-pdb-document\] drive-path' -Mutate {
            param($e) $e['ServerMonitor.Core.dll'] = New-Assembly 'ServerMonitor.Core' -embedded @($Clean.Document, $Leak.EmbeddedDoc) }

    Invoke-Case -Name 'document path in a standalone PDB inside a symbols package' -Expected 'fail' -MustNotPrint $Leak.PdbDoc `
        -Pattern 'fixture\.msixupload!fixture\.appxsym!ServerMonitor\.App\.pdb \[pdb-document\] drive-path' -FileName 'fixture.msixupload' -Mutate {
            param($e)
            $symbols = New-Zip @{ 'ServerMonitor.App.pdb' = [DistributionPrivacyFixture]::PortablePdb(@($Clean.Document, $Leak.PdbDoc), $null) }
            New-Zip @{ 'fixture.msix' = (New-Zip $e); 'fixture.appxsym' = $symbols } }

    Invoke-Case -Name 'Source Link map keyed by a local path' -Expected 'fail' -MustNotPrint $Leak.SourceLink `
        -Pattern 'ServerMonitor\.App\.pdb \[pdb/bytes-utf8\]' -Mutate {
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
        -Pattern '\[file/bytes-utf8\] env-repo-root' -Mutate {
            param($e) $e['notes.txt'] = Get-Utf8 ('built in ' + $Leak.RepoRoot.Replace('\', '/') + '/src') }

    Invoke-Case -Name 'environment-derived: an explicit deny token without any path shape' -Expected 'fail' -MustNotPrint $Leak.DenyToken `
        -Pattern '\[file/bytes-utf8\] deny-token' -Arguments @{ DenyToken = @($Leak.DenyToken) } -Mutate {
            param($e) $e['notes.txt'] = Get-Utf8 ('machine=' + $Leak.DenyToken) }

    # ------------------------------------------------------------------------- fail-closed
    Invoke-Case -Name 'required artifact missing' -Expected 'fail' -Pattern 'required artifact missing: ServerMonitor\.Core\.dll' -Mutate {
        param($e) $e.Remove('ServerMonitor.Core.dll') }

    Invoke-Case -Name 'corrupt PE (MZ header, garbage after it)' -Expected 'fail' -Pattern 'unparseable: fixture\.msix!ServerMonitor\.Core\.dll' -Mutate {
        param($e) $e['ServerMonitor.Core.dll'] = [byte[]] (@([byte] 0x4D, 0x5A) + (Get-Utf8 ('x' * 200))) }

    Invoke-Case -Name '.dll that is not a PE at all' -Expected 'fail' -Pattern 'unparseable: fixture\.msix!Vendor\.dll' -Mutate {
        param($e) $e['Vendor.dll'] = Get-Utf8 'definitely not a portable executable' }

    Invoke-Case -Name 'truncated managed PE' -Expected 'fail' -Pattern 'unparseable: fixture\.msix!ServerMonitor\.Core\.dll' -Mutate {
        param($e) $full = [byte[]] $e['ServerMonitor.Core.dll']; $e['ServerMonitor.Core.dll'] = [byte[]] $full[0..([int]($full.Length / 3))] }

    Invoke-Case -Name 'corrupt portable PDB' -Expected 'fail' -Pattern 'unparseable: fixture\.msix!ServerMonitor\.App\.pdb' -Mutate {
        param($e) $e['ServerMonitor.App.pdb'] = [byte[]] (@([byte] 0x42, 0x53, 0x4A, 0x42) + (Get-Utf8 ('y' * 64))) }

    Invoke-Case -Name 'Windows (MSF) PDB is refused, not byte-scanned' -Expected 'fail' -Pattern 'unparseable: fixture\.msix!ServerMonitor\.App\.pdb' -Mutate {
        param($e) $e['ServerMonitor.App.pdb'] = Get-Utf8 ("Microsoft C/C++ MSF 7.00`r`n" + ('z' * 64)) }

    Invoke-Case -Name 'unreadable container' -Expected 'fail' -Pattern 'unparseable: fixture\.msix' -Mutate {
        param($e) Get-Utf8 'PK this is not a zip file at all' }

    Invoke-Case -Name 'input that does not exist' -Expected 'fail' -Pattern 'input not found' -Arguments @{ Path = @((Join-Path $workDir 'missing.msix')) } -Mutate { param($e) }

    # --------------------------------------------------------------------------- allowlist
    $vendor = New-Assembly 'Vendor' -codeView $Leak.ThirdParty
    $vendorHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([byte[]] $vendor)).ToLowerInvariant()
    $allowFile = Join-Path $workDir 'allow.json'
    function Write-Allowlist($entries) { ConvertTo-Json -InputObject @($entries) -Depth 4 | Out-File -LiteralPath $allowFile -Encoding utf8 }

    Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = $vendorHash; categories = @('ci-runner-root'); reason = 'fixture vendor build agent' })
    Invoke-Case -Name 'allowlist: reviewed third-party file bound to its bytes' -Expected 'pass' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
        param($e) $e['Vendor.dll'] = $vendor }

    Invoke-Case -Name 'allowlist: same name, different bytes is not excused' -Expected 'fail' -MustNotPrint $Leak.ThirdParty `
        -Pattern 'Vendor\.dll \[pe-codeview-pdb-path\] ci-runner-root' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
            param($e) $e['Vendor.dll'] = New-Assembly 'Vendor2' -codeView $Leak.ThirdParty }

    Invoke-Case -Name 'allowlist: an excused file still fails on an environment match' -Expected 'fail' -MustNotPrint $Leak.DenyToken `
        -Pattern 'Vendor\.dll \[pe-user-string\] deny-token' -Arguments @{ AllowlistPath = $allowFile; DenyToken = @($Leak.DenyToken) } -Mutate {
            param($e)
            $tainted = New-Assembly 'Vendor' -codeView $Leak.ThirdParty -userString $Leak.DenyToken
            $script:taintedHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([byte[]] $tainted)).ToLowerInvariant()
            Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = $script:taintedHash; categories = @('ci-runner-root'); reason = 'fixture vendor build agent' })
            $e['Vendor.dll'] = $tainted }

    Write-Allowlist @(@{ file = 'ServerMonitor.Core.dll'; sha256 = $vendorHash; categories = @('profile-dir'); reason = 'trying to excuse our own build' })
    Invoke-Case -Name 'allowlist: a file this repository builds can never be excused' -Expected 'fail' -Pattern 'fix the build instead' `
        -Arguments @{ AllowlistPath = $allowFile } -Mutate { param($e) }

    Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = $vendorHash; categories = @('env-userprofile'); reason = 'trying to excuse the profile' })
    Invoke-Case -Name 'allowlist: environment categories can never be excused' -Expected 'fail' -Pattern 'can never be allowlisted' `
        -Arguments @{ AllowlistPath = $allowFile } -Mutate { param($e) }

    Write-Allowlist @(@{ file = 'Vendor.dll'; sha256 = $vendorHash; categories = @('ci-runner-root'); reason = 'REVIEW' })
    Invoke-Case -Name 'allowlist: an unreviewed candidate excuses nothing' -Expected 'fail' -Pattern 'never reviewed' `
        -Arguments @{ AllowlistPath = $allowFile } -Mutate { param($e) }

    $vendorToken = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes(('\__w\1\s\fixture-vendor\obj\Vendor.pdb').ToLowerInvariant()))).Substring(0, 12).ToLowerInvariant()
    Write-Allowlist @(@{ file = 'ServerMonitor.App.exe'; tokens = @($vendorToken); categories = @('ci-runner-root'); reason = 'apphost template' })
    Invoke-Case -Name 'allowlist: token-bound entry excuses exactly that path' -Expected 'pass' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
        param($e) $e['ServerMonitor.App.exe'] = New-Assembly 'Apphost' -codeView $Leak.ThirdParty }

    Invoke-Case -Name 'allowlist: token-bound entry does not excuse another path' -Expected 'fail' -MustNotPrint $Leak.CodeView `
        -Pattern 'ServerMonitor\.App\.exe \[pe-codeview-pdb-path\] profile-dir' -Arguments @{ AllowlistPath = $allowFile } -Mutate {
            param($e) $e['ServerMonitor.App.exe'] = New-Assembly 'Apphost' -codeView $Leak.CodeView }
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

$results | Format-Table -AutoSize Case, Expected, Actual, Verdict | Out-String -Width 220 | Write-Host

$broken = @($results | Where-Object { $_.Verdict -ne 'OK' })
if ($broken.Count -gt 0) {
    foreach ($case in $broken) { Write-Host "TEST ERROR  $($case.Case): expected $($case.Expected), got $($case.Actual). $($case.Detail)" }
    throw "$($broken.Count) of $($results.Count) distribution privacy counterproof case(s) did not behave as required."
}

Write-Host "All $($results.Count) distribution privacy counterproof cases behaved as required."
