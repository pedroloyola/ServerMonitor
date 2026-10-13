<#
.SYNOPSIS
    Fails closed if a distributable artifact carries an absolute development path (PRIV-PDBPATH).

.DESCRIPTION
    A build bakes the machine it ran on into its output: the PDB path in the PE debug directory,
    the source document paths and Source Link map of a portable PDB, CallerFilePath constants,
    generated XAML #line and #pragma checksum paths. On a developer machine that is the user
    profile and whatever folder the repository lives in. None of it may reach a package that is
    distributed.

    This script inspects the bytes that would actually ship. Every input is opened and walked:

      * zip containers (.msix, .msixupload, .appxsym, .zip, ...) are opened, never grepped
        compressed, and every entry is inspected, recursively;
      * every PE image is parsed with System.Reflection.Metadata: the CodeView PDB path, any
        embedded portable PDB (decompressed, then parsed like a standalone one) and, for managed
        images, every string of the #US heap (where CallerFilePath constants live);
      * every portable PDB is parsed: each document name, then its bytes (Source Link JSON);
      * every file, PE or not, is then scanned byte-wise as ASCII/UTF-8 and as UTF-16LE (both
        alignments), which covers JSON, XML, .pri, .xbf, resources and native string tables.

    What counts as a leak:
      * an absolute Windows drive path with at least one directory (C:\x\...), a UNC path, a
        \Users\ or /home/ or /Users/ profile segment, a CI runner root (X:\a\..., /home/runner,
        /__w/, \_work\);
      * any value of the CURRENT environment that names this machine or this checkout: the user
        profile, the user name, the repository root, the CI workspace and temp roots, the NuGet
        package root. They are read at run time; nothing personal is written into this file.

    What is allowed:
      * deterministic, mapped paths such as /_/src/... contain none of the above;
      * a THIRD-PARTY file can be excused by -AllowlistPath, bound to its exact bytes (SHA-256) or
        to the exact matched paths (tokens), and to the structural categories it was reviewed for.
        A file this repository builds can never be excused, and an environment-derived match can
        never be excused for any file.

    Fail-closed, never skip:
      * a missing input, an unreadable container, a PE or portable PDB that cannot be parsed, a
        PDB in a format this script cannot parse structurally, or an empty scan is a FAILURE;
      * every name in -RequiredArtifacts must be found somewhere in the inputs, or it is a FAILURE.

    The output never contains the offending path. Each finding is reported as
    artifact + channel + category + a 12-hex SHA-256 token of the matched text, so two runs can be
    compared without the log becoming the leak.

.PARAMETER Path
    Packages (.msix and other zip containers), single files or directories to inspect.

.PARAMETER RequiredArtifacts
    File names that must be present in the inspected set (matched on the leaf name).

.PARAMETER DenyToken
    Extra literal strings that must not appear (used by the counterproofs; never personal data).

.PARAMETER AllowlistPath
    Optional JSON array of { "file": "<leaf name>", "categories": [ "<structural category>", ... ],
    "reason": "<why>" } entries bound EITHER to "sha256" (64 hex of the exact file bytes: the whole
    file is excused for those categories) OR to "tokens" (only those exact matched paths are excused,
    for a file whose bytes change every build, such as the apphost executables).

.PARAMETER ReportPath
    Optional path for a redacted JSON report of the findings.

.PARAMETER AllowlistCandidatesPath
    Maintenance aid: writes the blocking third-party files as allowlist entries with a reason of
    'REVIEW'. Nothing is excused until a person reviews each one, writes the real reason and
    commits it. Environment matches and files this repository builds are never written.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [string[]] $RequiredArtifacts = @(
        'AppxManifest.xml',
        'resources.pri',
        'ServerMonitor.App.exe',
        'ServerMonitor.App.dll',
        'ServerAlyzer.WidgetProvider.exe',
        'ServerAlyzer.WidgetProvider.dll',
        'ServerMonitor.Core.dll',
        'ServerMonitor.Infrastructure.dll',
        'ServerMonitor.Collectors.dll',
        'ServerMonitor.Features.dll',
        'ServerMonitor.WidgetContract.dll',
        'ServerMonitor.ActivationContract.dll'
    ),
    [string[]] $DenyToken = @(),
    [string] $AllowlistPath,
    [string] $ReportPath,
    [string] $AllowlistCandidatesPath,
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$RequiredArtifacts = @($RequiredArtifacts | Where-Object { $_ })
$DenyToken = @($DenyToken | Where-Object { $_ })

# ------------------------------------------------------------------------------------------ state
$script:findings = New-Object System.Collections.Generic.List[object]
$script:failures = New-Object System.Collections.Generic.List[string]
$script:seenNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$script:artifactHash = @{}
$script:counts = [ordered] @{ files = 0; containers = 0; pe = 0; managed = 0; pdb = 0 }
$script:latin1 = [System.Text.Encoding]::Latin1
$script:utf16 = [System.Text.Encoding]::Unicode
$MaxDepth = 4

function Add-Failure([string] $message) { $script:failures.Add($message) | Out-Null; Write-Host "  FAIL  $message" }

function Get-Sha256Hex([byte[]] $bytes) {
    return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-Token([string] $value) {
    return (Get-Sha256Hex ([System.Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant()))).Substring(0, 12)
}

function Get-Leaf([string] $artifact) { return ($artifact -split '[!/\\]')[-1] }

function Test-EnvironmentCategory([string] $category) { return $category -like 'env-*' -or $category -eq 'deny-token' }

# -------------------------------------------------------------------------------------- patterns
# ASCII-only character classes on purpose: random binary decoded as UTF-16 turns into CJK letters,
# and \w would happily call that a directory name. Regex fragments are single-quoted so PowerShell
# never touches their backslashes.
$seg = '[A-Za-z0-9 _.$~()@+#,''-]'
$sep = '[\\/]'
$tail = '(?:' + $sep + '+' + $seg + '+)*'   # the rest of the path: a token names the whole path
$options = [System.Text.RegularExpressions.RegexOptions]::Compiled -bor [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
$ignoreCase = $options -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
$structuralPatterns = @(
    # Order matters: a later pattern never re-reports a span an earlier one matched (Find-InText),
    # so the most specific name wins (a CI runner root and a profile path are drive paths too).
    @{ Category = 'ci-runner-root'; Regex = [regex]::new('(?<![A-Za-z0-9])[A-Za-z]:' + $sep + '+a' + $sep + '+' + $seg + '+' + $tail, $options) },
    @{ Category = 'ci-runner-root'; Regex = [regex]::new('(?:/home/runner|/Users/runner|' + $sep + '__w|' + $sep + '_work)' + $sep + $seg + '+' + $tail, $ignoreCase) },
    @{ Category = 'profile-dir';    Regex = [regex]::new($sep + '(?:Users|home|Documents and Settings)' + $sep + '+[A-Za-z0-9._-]{2,}' + $sep + $seg + '*' + $tail, $ignoreCase) },
    @{ Category = 'unc-path';       Regex = [regex]::new('(?<![\\/])\\\\[A-Za-z0-9][A-Za-z0-9._-]+\\' + $seg + '{2,}' + $tail, $options) },
    @{ Category = 'drive-path';     Regex = [regex]::new('(?<![A-Za-z0-9])[A-Za-z]:' + $sep + '+' + $seg + '{2,}' + $sep + '+' + $seg + '+' + $tail, $options) }
)
$structuralCategories = @($structuralPatterns | ForEach-Object { $_.Category } | Sort-Object -Unique)

# Environment-derived literals: what identifies THIS machine and THIS checkout. Computed here, at
# run time, so nothing personal is ever committed into this script.
function Get-EnvironmentTokens {
    $tokens = New-Object System.Collections.Generic.List[object]
    $add = {
        param([string] $category, [string] $value, [int] $minimum)
        if ([string]::IsNullOrWhiteSpace($value)) { return }
        $trimmed = $value.Trim().TrimEnd('\', '/')
        if ($trimmed.Length -lt $minimum) { return }
        foreach ($variant in @($trimmed, $trimmed.Replace('\', '/'), $trimmed.Replace('/', '\'))) {
            $tokens.Add([pscustomobject] @{ Category = $category; Value = $variant }) | Out-Null
        }
    }
    & $add 'env-userprofile' $env:USERPROFILE 4
    & $add 'env-userprofile' $env:HOME 4
    & $add 'env-username'    $env:USERNAME 3
    & $add 'env-username'    $env:USER 3
    & $add 'env-repo-root'   $RepositoryRoot 4
    & $add 'env-ci-root'     $env:GITHUB_WORKSPACE 4
    & $add 'env-ci-root'     $env:RUNNER_WORKSPACE 4
    & $add 'env-ci-root'     $env:RUNNER_TEMP 4
    & $add 'env-ci-root'     $env:AGENT_BUILDDIRECTORY 4
    & $add 'env-nuget-root'  $env:NUGET_PACKAGES 4
    foreach ($extra in $DenyToken) { & $add 'deny-token' $extra 3 }

    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    return @($tokens | Where-Object { $seen.Add($_.Value) })
}
$environmentTokens = @(Get-EnvironmentTokens)
$environmentPatterns = @($environmentTokens | ForEach-Object {
    # Also match the literal as its UTF-8 bytes read as Latin-1, so a non-ASCII user name is still
    # found by the byte scan.
    $asUtf8 = $script:latin1.GetString([System.Text.Encoding]::UTF8.GetBytes($_.Value))
    $alternatives = @([regex]::Escape($_.Value))
    if ($asUtf8 -ne $_.Value) { $alternatives += [regex]::Escape($asUtf8) }
    @{ Category = $_.Category; Regex = [regex]::new('(?:' + ($alternatives -join '|') + ')', $ignoreCase) }
})
$allPatterns = @($environmentPatterns) + $structuralPatterns

# --------------------------------------------------------------------------------------- allowlist
# Files whose bytes this repository's build produces. Their leaks are fixed in the build, never
# excused. ServerMonitor.App.exe and ServerAlyzer.WidgetProvider.exe are deliberately NOT here: they
# are the SDK's apphost template, whose debug directory names the .NET runtime's own build machine.
$FirstPartyNeverAllowlisted = '^(?:(?:ServerMonitor|ServerAlyzer)\..+\.(?:dll|pdb|json|pri)|resources\.pri|AppxManifest\.xml|AppxBlockMap\.xml|\[Content_Types\]\.xml)$'
$allowlist = @()
if ($AllowlistPath) {
    if (-not (Test-Path -LiteralPath $AllowlistPath)) { throw "Allowlist not found: $([System.IO.Path]::GetFileName($AllowlistPath))" }
    $allowlist = @(Get-Content -LiteralPath $AllowlistPath -Raw | ConvertFrom-Json)
    foreach ($entry in $allowlist) {
        $fields = @($entry.PSObject.Properties.Name)
        foreach ($field in 'file', 'categories', 'reason') {
            if ($fields -notcontains $field -or -not $entry.$field) { throw "Allowlist entry is missing '$field'." }
        }
        if (($fields -contains 'sha256') -eq ($fields -contains 'tokens')) {
            throw "Allowlist entry for '$($entry.file)' must be bound to exactly one of 'sha256' or 'tokens'."
        }
        if ($entry.file -match $FirstPartyNeverAllowlisted) {
            throw "Allowlist entry for '$($entry.file)' tries to excuse a file this repository builds; fix the build instead."
        }
        if ($fields -contains 'sha256' -and $entry.sha256 -notmatch '^[0-9a-f]{64}$') { throw "Allowlist entry for '$($entry.file)' needs the file's lowercase SHA-256." }
        if ($fields -contains 'tokens' -and (@($entry.tokens).Count -eq 0 -or @($entry.tokens | Where-Object { $_ -notmatch '^[0-9a-f]{12}$' }).Count -gt 0)) {
            throw "Allowlist entry for '$($entry.file)' has an empty or malformed token list."
        }
        if ($entry.reason -match '^\s*REVIEW\s*$') { throw "Allowlist entry for '$($entry.file)' was never reviewed (reason is still REVIEW)." }
        foreach ($category in @($entry.categories)) {
            if (Test-EnvironmentCategory $category) {
                throw "Allowlist entry for '$($entry.file)' tries to excuse '$category'; environment matches can never be allowlisted."
            }
            if ($structuralCategories -notcontains $category) { throw "Allowlist entry for '$($entry.file)' names unknown category '$category'." }
        }
    }
}

function Test-Allowed($finding) {
    $leaf = Get-Leaf $finding.Artifact
    $hash = $script:artifactHash[$finding.Artifact]
    foreach ($entry in $allowlist) {
        if ($entry.file -ine $leaf -or @($entry.categories) -notcontains $finding.Category) { continue }
        if ($entry.PSObject.Properties.Name -contains 'sha256') {
            if ($entry.sha256 -eq $hash) { return $true }
        }
        elseif (@($entry.tokens) -contains $finding.Token) { return $true }
    }
    return $false
}

# ---------------------------------------------------------------------------------------- matching
function Find-InText {
    param([string] $Text, [string] $Artifact, [string] $Channel, $Seen, [switch] $ByteScan)

    if ([string]::IsNullOrEmpty($Text)) { return }
    $claimed = New-Object System.Collections.Generic.List[object]
    foreach ($pattern in $allPatterns) {
        $structural = -not (Test-EnvironmentCategory $pattern.Category)
        foreach ($match in $pattern.Regex.Matches($Text)) {
            if ($structural) {
                $overlaps = $false
                foreach ($span in $claimed) {
                    if ($match.Index -lt $span[1] -and $span[0] -lt ($match.Index + $match.Length)) { $overlaps = $true; break }
                }
                if ($overlaps) { continue }
                $claimed.Add(@($match.Index, ($match.Index + $match.Length))) | Out-Null
            }
            $token = Get-Token $match.Value
            # Every structured channel reports what it finds, once per (channel, token). The byte scan
            # that follows only reports what no parser already explained for this artifact, so the
            # inventory names the real channel instead of "somewhere in the bytes".
            if ($null -ne $Seen) {
                if ($ByteScan) {
                    if (-not $Seen.Add($token)) { continue }
                }
                else {
                    $Seen.Add($token) | Out-Null
                    if (-not $Seen.Add("$Channel|$token")) { continue }
                }
            }
            $script:findings.Add([pscustomobject] @{
                Artifact = $Artifact
                Channel  = $Channel
                Category = $pattern.Category
                Token    = $token
                Allowed  = $false
            }) | Out-Null
        }
    }
}

function Find-InBytes {
    param([byte[]] $Bytes, [string] $Artifact, [string] $Channel, $Seen)

    if ($Bytes.Length -eq 0) { return }
    Find-InText -Text $script:latin1.GetString($Bytes) -Artifact $Artifact -Channel "$Channel/bytes-utf8" -Seen $Seen -ByteScan
    Find-InText -Text $script:utf16.GetString($Bytes, 0, $Bytes.Length - ($Bytes.Length % 2)) -Artifact $Artifact -Channel "$Channel/bytes-utf16" -Seen $Seen -ByteScan
    if ($Bytes.Length -gt 2) {
        $odd = $Bytes.Length - 1
        Find-InText -Text $script:utf16.GetString($Bytes, 1, $odd - ($odd % 2)) -Artifact $Artifact -Channel "$Channel/bytes-utf16" -Seen $Seen -ByteScan
    }
}

# ----------------------------------------------------------------------------------------- formats
function Test-Prefix([byte[]] $Bytes, [byte[]] $Prefix) {
    if ($Bytes.Length -lt $Prefix.Length) { return $false }
    for ($i = 0; $i -lt $Prefix.Length; $i++) { if ($Bytes[$i] -ne $Prefix[$i]) { return $false } }
    return $true
}
$MagicZip      = [byte[]] (0x50, 0x4B, 0x03, 0x04)
$MagicZipEmpty = [byte[]] (0x50, 0x4B, 0x05, 0x06)
$MagicPe       = [byte[]] (0x4D, 0x5A)
$MagicBsjb     = [byte[]] (0x42, 0x53, 0x4A, 0x42)
$MagicMsf      = [System.Text.Encoding]::ASCII.GetBytes('Microsoft C/C++ MSF 7.00')
$ZipExtensions = @('.msix', '.appx', '.msixbundle', '.appxbundle', '.msixupload', '.appxupload', '.appxsym', '.zip', '.nupkg', '.snupkg')
$PeExtensions  = @('.dll', '.exe', '.winmd', '.sys', '.ocx')

function Read-All([System.IO.Stream] $stream) {
    $buffer = New-Object System.IO.MemoryStream
    $stream.CopyTo($buffer)
    return ,$buffer.ToArray()
}

function Invoke-PortablePdb {
    param([byte[]] $Bytes, [string] $Artifact, [string] $ChannelPrefix, $Seen)

    $script:counts.pdb++
    $provider = [System.Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream(
        [System.IO.MemoryStream]::new($Bytes, $false), [System.Reflection.Metadata.MetadataStreamOptions]::PrefetchMetadata)
    try {
        $reader = $provider.GetMetadataReader()
        foreach ($handle in $reader.Documents) {
            $document = $reader.GetDocument($handle)
            Find-InText -Text $reader.GetString($document.Name) -Artifact $Artifact -Channel "$ChannelPrefix-document" -Seen $Seen
        }
    }
    finally { $provider.Dispose() }
    # Source Link JSON, compilation options and anything else the PDB stores uncompressed.
    Find-InBytes -Bytes $Bytes -Artifact $Artifact -Channel $ChannelPrefix -Seen $Seen
}

function Invoke-Pe {
    param([byte[]] $Bytes, [string] $Artifact)

    $script:counts.pe++
    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    $pe = [System.Reflection.PortableExecutable.PEReader]::new([System.IO.MemoryStream]::new($Bytes, $false))
    try {
        # Forces the headers to be parsed; a truncated or forged image throws here.
        if ($null -eq $pe.PEHeaders.PEHeader) { throw 'no PE optional header' }

        foreach ($entry in $pe.ReadDebugDirectory()) {
            if ($entry.Type -eq [System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) {
                $codeView = $pe.ReadCodeViewDebugDirectoryData($entry)
                Find-InText -Text $codeView.Path -Artifact $Artifact -Channel 'pe-codeview-pdb-path' -Seen $seen
            }
            elseif ($entry.Type -eq [System.Reflection.PortableExecutable.DebugDirectoryEntryType]::EmbeddedPortablePdb) {
                # 'MPDB' + uncompressed size + deflate stream (Portable PDB spec, embedded PDB entry).
                $blobReader = $pe.GetSectionData($entry.DataRelativeVirtualAddress).GetReader(0, $entry.DataSize)
                $raw = [byte[]] $blobReader.ReadBytes($entry.DataSize)
                if ($raw.Length -lt 8 -or [System.Text.Encoding]::ASCII.GetString($raw, 0, 4) -ne 'MPDB') { throw 'embedded PDB entry without MPDB signature' }
                $size = [System.BitConverter]::ToInt32($raw, 4)
                $deflate = [System.IO.Compression.DeflateStream]::new([System.IO.MemoryStream]::new($raw, 8, $raw.Length - 8), [System.IO.Compression.CompressionMode]::Decompress)
                try { $pdb = Read-All $deflate } finally { $deflate.Dispose() }
                if ($pdb.Length -ne $size) { throw "embedded PDB decompressed to $($pdb.Length) bytes, header says $size" }
                Invoke-PortablePdb -Bytes $pdb -Artifact $Artifact -ChannelPrefix 'pe-embedded-pdb' -Seen $seen
            }
        }

        if ($pe.HasMetadata) {
            $script:counts.managed++
            $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            # #US heap: every ldstr literal, which is where a CallerFilePath constant ends up.
            $handle = [System.Reflection.Metadata.Ecma335.MetadataTokens]::UserStringHandle(1)
            $heapSize = [System.Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetHeapSize($reader, [System.Reflection.Metadata.Ecma335.HeapIndex]::UserString)
            while (-not $handle.IsNil -and [System.Reflection.Metadata.Ecma335.MetadataTokens]::GetHeapOffset($handle) -lt $heapSize) {
                Find-InText -Text $reader.GetUserString($handle) -Artifact $Artifact -Channel 'pe-user-string' -Seen $seen
                $handle = [System.Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetNextHandle($reader, $handle)
            }
        }
    }
    finally { $pe.Dispose() }

    Find-InBytes -Bytes $Bytes -Artifact $Artifact -Channel 'pe-other' -Seen $seen
}

function Invoke-Zip {
    param([byte[]] $Bytes, [string] $Artifact, [int] $Depth)

    if ($Depth -gt $MaxDepth) { throw "containers nested deeper than $MaxDepth" }
    $script:counts.containers++
    $archive = [System.IO.Compression.ZipArchive]::new([System.IO.MemoryStream]::new($Bytes, $false), [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        foreach ($entry in $archive.Entries) {
            $entryArtifact = "$Artifact!$($entry.FullName)"
            Find-InText -Text $entry.FullName -Artifact $entryArtifact -Channel 'zip-entry-name' -Seen $null
            if (-not $entry.Name) { continue }   # directory entry
            $stream = $entry.Open()
            try { $content = Read-All $stream } finally { $stream.Dispose() }
            Invoke-Content -Bytes $content -Name $entry.Name -Artifact $entryArtifact -Depth ($Depth + 1)
        }
    }
    finally { $archive.Dispose() }
}

function Invoke-Content {
    param([byte[]] $Bytes, [string] $Name, [string] $Artifact, [int] $Depth)

    $script:counts.files++
    $script:seenNames.Add($Name) | Out-Null
    $script:artifactHash[$Artifact] = Get-Sha256Hex $Bytes
    $extension = [System.IO.Path]::GetExtension($Name).ToLowerInvariant()

    try {
        if ((Test-Prefix $Bytes $MagicZip) -or (Test-Prefix $Bytes $MagicZipEmpty) -or $ZipExtensions -contains $extension) {
            Invoke-Zip -Bytes $Bytes -Artifact $Artifact -Depth $Depth
        }
        elseif ((Test-Prefix $Bytes $MagicPe) -or $PeExtensions -contains $extension) {
            Invoke-Pe -Bytes $Bytes -Artifact $Artifact
        }
        elseif (Test-Prefix $Bytes $MagicBsjb) {
            Invoke-PortablePdb -Bytes $Bytes -Artifact $Artifact -ChannelPrefix 'pdb' -Seen (New-Object 'System.Collections.Generic.HashSet[string]')
        }
        elseif ($extension -eq '.pdb' -or (Test-Prefix $Bytes $MagicMsf)) {
            # A Windows (MSF) PDB cannot be parsed structurally here, and a byte scan alone would be a
            # silent downgrade, so it is a failure. A distributable package must not carry one.
            throw 'not a portable PDB (Windows/MSF or unknown PDB); cannot be inspected structurally'
        }
        else {
            Find-InBytes -Bytes $Bytes -Artifact $Artifact -Channel 'file' -Seen (New-Object 'System.Collections.Generic.HashSet[string]')
        }
    }
    catch {
        # The exception type only: a message could quote the very path this check must not print.
        Add-Failure "unparseable: $Artifact ($($_.Exception.GetType().Name))"
    }
}

# ---------------------------------------------------------------------------------------------- run
Write-Host 'Distribution privacy check'
Write-Host "  environment-derived literals: $($environmentTokens.Count) (values not printed)"
Write-Host "  allowlisted third-party files: $($allowlist.Count)"

foreach ($item in $Path) {
    if ([string]::IsNullOrWhiteSpace($item) -or -not (Test-Path -LiteralPath $item)) {
        Add-Failure "input not found: $([System.IO.Path]::GetFileName("$item"))"
        continue
    }
    $resolved = (Resolve-Path -LiteralPath $item).Path
    $label = [System.IO.Path]::GetFileName($resolved.TrimEnd('\', '/'))
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        $files = @(Get-ChildItem -LiteralPath $resolved -Recurse -File -Force)
        if ($files.Count -eq 0) { Add-Failure "input directory is empty: $label" }
        foreach ($file in $files) {
            $relative = [System.IO.Path]::GetRelativePath($resolved, $file.FullName).Replace('\', '/')
            Invoke-Content -Bytes ([System.IO.File]::ReadAllBytes($file.FullName)) -Name $file.Name -Artifact "$label/$relative" -Depth 0
        }
    }
    else {
        Invoke-Content -Bytes ([System.IO.File]::ReadAllBytes($resolved)) -Name ([System.IO.Path]::GetFileName($resolved)) -Artifact $label -Depth 0
    }
}

if ($script:counts.files -eq 0) { Add-Failure 'nothing was inspected' }

$missing = @($RequiredArtifacts | Where-Object { -not $script:seenNames.Contains($_) })
foreach ($name in $missing) { Add-Failure "required artifact missing: $name" }

Write-Host ("  inspected: {0} files, {1} containers, {2} PE images ({3} managed), {4} portable PDBs" -f `
    $script:counts.files, $script:counts.containers, $script:counts.pe, $script:counts.managed, $script:counts.pdb)
Write-Host "  required artifacts present: $($RequiredArtifacts.Count - $missing.Count)/$($RequiredArtifacts.Count)"

foreach ($finding in $script:findings) { $finding.Allowed = Test-Allowed $finding }
$blocking = @($script:findings | Where-Object { -not $_.Allowed })
$excused = @($script:findings | Where-Object { $_.Allowed })

foreach ($finding in $blocking) {
    Add-Failure ("path leak: {0} [{1}] {2} token={3}" -f $finding.Artifact, $finding.Channel, $finding.Category, $finding.Token)
}
foreach ($group in ($excused | Group-Object Artifact | Sort-Object Name)) {
    Write-Host ("  allow {0}: {1} third-party finding(s) ({2})" -f $group.Name, $group.Count, ((@($group.Group | ForEach-Object Category) | Sort-Object -Unique) -join ', '))
}

if ($ReportPath) {
    [pscustomobject] @{
        inspected       = $script:counts
        requiredMissing = $missing
        findings        = $script:findings
        failures        = $script:failures
    } | ConvertTo-Json -Depth 5 | Out-File -LiteralPath $ReportPath -Encoding utf8
}

if ($AllowlistCandidatesPath) {
    $candidates = @($blocking |
        Where-Object { -not (Test-EnvironmentCategory $_.Category) -and (Get-Leaf $_.Artifact) -notmatch $FirstPartyNeverAllowlisted } |
        Group-Object { "$(Get-Leaf $_.Artifact)|$($script:artifactHash[$_.Artifact])" } |
        ForEach-Object {
            $first = $_.Group[0]
            $candidate = [ordered] @{ file = Get-Leaf $first.Artifact }
            # Prefer the exact paths: they survive a byte change that does not move them (the apphost
            # executables are re-stamped with this app's name, icon and version on every build). A
            # native binary with hundreds of compiled-in source paths is bound to its bytes instead.
            $tokens = @($_.Group | ForEach-Object Token | Sort-Object -Unique)
            $apphost = $candidate.file -match '^(?:ServerMonitor|ServerAlyzer)\..+\.exe$'
            if ($apphost -or $tokens.Count -le 5) { $candidate.tokens = $tokens }
            else { $candidate.sha256 = $script:artifactHash[$first.Artifact] }
            $candidate.categories = @($_.Group | ForEach-Object Category | Sort-Object -Unique)
            $candidate.reason = 'REVIEW'
            [pscustomobject] $candidate
        } | Sort-Object file)
    ConvertTo-Json -InputObject $candidates -Depth 4 | Out-File -LiteralPath $AllowlistCandidatesPath -Encoding utf8
    Write-Host "  wrote $($candidates.Count) allowlist candidate(s) for review"
}

if ($script:failures.Count -gt 0) {
    Write-Host ''
    Write-Host "DISTRIBUTION PRIVACY CHECK FAILED ($($script:failures.Count) problem(s); $($blocking.Count) path leak(s))"
    throw "Distribution privacy check failed: $($script:failures.Count) problem(s), $($blocking.Count) path leak(s)."
}

Write-Host ''
Write-Host "DISTRIBUTION PRIVACY CHECK PASSED ($($excused.Count) reviewed third-party finding(s) allowlisted)"
