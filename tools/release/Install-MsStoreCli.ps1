<#
.SYNOPSIS
    Installs the ONE Microsoft Store Developer CLI the Store workflows may run, and fails closed on
    anything else.

.DESCRIPTION
    REL.0E (Vigil PR #56 Medium, Beacon REL.0E §5): microsoft-store-apppublisher installed whatever
    msstore-cli release was "latest", without a checksum or signature check, and that binary then received
    the Store client secret and decided what was deleted or committed. This script replaces the action.

    The pin (msstore-cli-pin.json, reviewed like code) names the release, the exact asset URL, its SHA-256
    and size, the product version stamped into msstore.exe, the source commit and the Authenticode signer.
    In order, any failure stops the run before the CLI is on PATH:
      1. the pin itself is well formed (exact GitHub release URL for the pinned tag, lowercase 64-hex
         hash, product version bound to the source commit, msstore.exe among the signed files);
      2. the downloaded archive has the pinned size and SHA-256 (checked BEFORE anything is extracted);
      3. it is extracted into a fresh folder (an existing one is refused, so nothing stale can mix in);
      4. every pinned file is there and carries a Valid Authenticode signature from the pinned signer;
      5. msstore.exe reports exactly the pinned product version (version + source commit), read from its
         version resource, without running it.
    Only then is the folder appended to GITHUB_PATH, so the next steps run this msstore.exe.

    -ArchivePath verifies a local copy instead of downloading (no network). -NoPath verifies without
    touching any PATH. -SelfTest runs offline counterproofs on synthetic archives: no network, no secret,
    no install.
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(ParameterSetName = 'Install')] [string] $PinPath = (Join-Path $PSScriptRoot 'msstore-cli-pin.json'),
    [Parameter(ParameterSetName = 'Install')] [string] $ArchivePath,
    [Parameter(ParameterSetName = 'Install')] [string] $Destination,
    [Parameter(ParameterSetName = 'Install')] [switch] $NoPath,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch] $SelfTest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$assetName = 'MSStoreCLI-win-x64.zip'
$pinFields = @('comment', 'version', 'url', 'sha256', 'size', 'productVersion', 'sourceCommit', 'signerSubject', 'signedFiles')

function Read-Pin([string] $Json) {
    $pin = $Json | ConvertFrom-Json
    $names = @($pin.PSObject.Properties.Name)
    foreach ($name in $names) { if ($pinFields -cnotcontains $name) { throw "Pin: unknown field '$name'." } }
    foreach ($name in $pinFields) { if ($names -cnotcontains $name) { throw "Pin: missing field '$name'." } }
    if ($pin.version -cnotmatch '\Av([0-9]+)\.([0-9]+)\.([0-9]+)\z') { throw "Pin: version '$($pin.version)' is not vMAJOR.MINOR.PATCH." }
    $numeric = $pin.version.Substring(1)
    $url = "https://github.com/microsoft/msstore-cli/releases/download/$($pin.version)/$assetName"
    if ($pin.url -cne $url) { throw "Pin: url must be exactly $url (a release asset of the pinned tag, never latest)." }
    if ($pin.sha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Pin: sha256 must be 64 lowercase hex characters.' }
    if ($pin.size -isnot [long] -and $pin.size -isnot [int] -or $pin.size -le 0) { throw 'Pin: size must be a positive integer.' }
    if ($pin.sourceCommit -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Pin: sourceCommit must be a full 40-character commit SHA.' }
    $product = '\A' + [regex]::Escape($numeric) + '\.[0-9]+\+' + [regex]::Escape($pin.sourceCommit.Substring(0, 10)) + '\z'
    if ($pin.productVersion -cnotmatch $product) { throw "Pin: productVersion '$($pin.productVersion)' must be $numeric.<build>+<first 10 of sourceCommit>." }
    if ([string]::IsNullOrWhiteSpace($pin.signerSubject) -or $pin.signerSubject -cnotmatch '\ACN=[^,]+, O=') { throw 'Pin: signerSubject must be a certificate subject (CN=..., O=...).' }
    $files = @($pin.signedFiles)
    if ($files -cnotcontains 'msstore.exe') { throw 'Pin: signedFiles must include msstore.exe.' }
    foreach ($file in $files) { if ($file -cnotmatch '\A[A-Za-z0-9_.-]+\.(exe|dll)\z') { throw "Pin: signed file '$file' must be a plain .exe/.dll name." } }
    return $pin
}

function Assert-Archive([string] $Path, $Pin) {
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne [long] $Pin.size) { throw "Archive size mismatch: $($item.Length) bytes, pinned $($Pin.size). Nothing was extracted." }
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $Pin.sha256) { throw "Archive SHA-256 mismatch: $hash, pinned $($Pin.sha256). Nothing was extracted." }
}

function Assert-Binaries([string] $Folder, $Pin) {
    foreach ($file in @($Pin.signedFiles)) {
        $path = Join-Path $Folder $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Signed file missing from the archive: $file." }
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode') { throw "$file is not validly Authenticode-signed ($($signature.Status), $($signature.SignatureType))." }
        if ($signature.SignerCertificate.Subject -cne $Pin.signerSubject) { throw "$file signer '$($signature.SignerCertificate.Subject)' is not the pinned signer." }
    }
    $version = (Get-Item -LiteralPath (Join-Path $Folder 'msstore.exe')).VersionInfo.ProductVersion
    if ($version -cne $Pin.productVersion) { throw "msstore.exe product version '$version' is not the pinned '$($Pin.productVersion)'." }
}

# Verifies, extracts and verifies again. Returns the folder holding the verified msstore.exe.
function Install-Verified($Pin, [string] $Archive, [string] $Target, [string] $PathFile) {
    Assert-Archive $Archive $Pin
    if (Test-Path -LiteralPath $Target) { throw "Destination already exists: $Target. Refusing to mix a verified CLI with older files." }
    Expand-Archive -LiteralPath $Archive -DestinationPath $Target
    Assert-Binaries $Target $Pin
    if ($PathFile) { Add-Content -LiteralPath $PathFile -Value $Target -Encoding utf8 }
    return $Target
}

if (-not $SelfTest) {
    $pin = Read-Pin (Get-Content -LiteralPath $PinPath -Raw)
    $work = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    if (-not $Destination) { $Destination = Join-Path $work "msstore-cli-$($pin.version)" }
    $pathFile = $null
    if (-not $NoPath) {
        if (-not $env:GITHUB_PATH) { throw 'GITHUB_PATH is not set: outside GitHub Actions, run with -NoPath (verification only).' }
        $pathFile = $env:GITHUB_PATH
    }
    if (-not $ArchivePath) {
        $download = Join-Path $work ('msstore-cli-download-' + [guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path $download | Out-Null
        $ArchivePath = Join-Path $download $assetName
        Write-Host "Downloading $($pin.url)"
        Invoke-WebRequest -Uri $pin.url -OutFile $ArchivePath -MaximumRetryCount 3 -RetryIntervalSec 5
    }
    $folder = Install-Verified $pin $ArchivePath $Destination $pathFile
    Write-Host "msstore-cli $($pin.version) verified: SHA-256 $($pin.sha256), $($pin.size) bytes, Authenticode '$($pin.signerSubject)', product version $($pin.productVersion)."
    if ($pathFile) { Write-Host "On PATH for the next steps: $folder" } else { Write-Host "Verified only (no PATH change): $folder" }
    exit 0
}

# ---- Self-test: offline counterproofs on synthetic archives -------------------------------------------
$root = Join-Path ([IO.Path]::GetTempPath()) ('msstore-pin-selftest-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $failed = 0
    function Test-Case([string] $Name, [scriptblock] $Body, [string] $Reason, [scriptblock] $After) {
        $failure = $null
        try { & $Body | Out-Null } catch { $failure = $_.Exception.Message }
        $ok = if ($Reason) { $null -ne $failure -and $failure.StartsWith($Reason) } else { $null -eq $failure }
        if ($ok -and $After) { $ok = [bool] (& $After) }
        if (-not $ok) { $script:failed++ }
        Write-Host ("  {0}  {1}{2}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $Name, $(if ($failure) { " -> $failure" } else { '' }))
    }

    # 1. The committed pin is well formed, and each malformed variant is refused for its own reason.
    $real = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'msstore-cli-pin.json') -Raw
    Test-Case 'committed pin is well formed' { Read-Pin $real } $null
    $pinMutations = @(
        @{ Name = 'url floats on latest'; Old = '/releases/download/v0.4.3/'; New = '/releases/latest/download/'; Reason = 'Pin: url must be exactly' }
        @{ Name = 'url from another repository'; Old = 'github.com/microsoft/msstore-cli/'; New = 'github.com/attacker/msstore-cli/'; Reason = 'Pin: url must be exactly' }
        @{ Name = 'url over http'; Old = '"https://github.com'; New = '"http://github.com'; Reason = 'Pin: url must be exactly' }
        @{ Name = 'url for another tag'; Old = '/download/v0.4.3/'; New = '/download/v0.4.2/'; Reason = 'Pin: url must be exactly' }
        @{ Name = 'version floats'; Old = '"version": "v0.4.3"'; New = '"version": "latest"'; Reason = 'Pin: version' }
        @{ Name = 'hash shortened'; Old = 'c5fb008b616f96b1b55f1d52e4e0a5dd8f6c156129b5f52f9b97ec0eed11bd57'; New = 'c5fb008b616f96b1'; Reason = 'Pin: sha256' }
        @{ Name = 'hash uppercase'; Old = 'c5fb008b616f96b1b55f1d52e4e0a5dd8f6c156129b5f52f9b97ec0eed11bd57'; New = 'C5FB008B616F96B1B55F1D52E4E0A5DD8F6C156129B5F52F9B97EC0EED11BD57'; Reason = 'Pin: sha256' }
        @{ Name = 'hash emptied'; Old = '"c5fb008b616f96b1b55f1d52e4e0a5dd8f6c156129b5f52f9b97ec0eed11bd57"'; New = '""'; Reason = 'Pin: sha256' }
        @{ Name = 'size zero'; Old = '"size": 57635931'; New = '"size": 0'; Reason = 'Pin: size' }
        @{ Name = 'product version from another commit'; Old = '"productVersion": "0.4.3.2+193cb57f2a"'; New = '"productVersion": "0.4.3.2+0000000000"'; Reason = 'Pin: productVersion' }
        @{ Name = 'product version of another release'; Old = '"productVersion": "0.4.3.2+193cb57f2a"'; New = '"productVersion": "0.4.2.1+193cb57f2a"'; Reason = 'Pin: productVersion' }
        @{ Name = 'short source commit'; Old = '"sourceCommit": "193cb57f2a58a50c9fd4d000fa12c6078852bb7a"'; New = '"sourceCommit": "193cb57f2a"'; Reason = 'Pin: sourceCommit' }
        @{ Name = 'msstore.exe not signature-checked'; Old = '"msstore.exe", '; New = ''; Reason = 'Pin: signedFiles must include msstore.exe' }
        @{ Name = 'signed file with a path'; Old = '"msalruntime.dll"'; New = '"..\\msalruntime.dll"'; Reason = 'Pin: signed file' }
        @{ Name = 'signer emptied'; Old = '"signerSubject": "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"'; New = '"signerSubject": ""'; Reason = 'Pin: signerSubject' }
        @{ Name = 'unknown field (e.g. a skip switch)'; Old = '"version": "v0.4.3",'; New = '"version": "v0.4.3", "skipVerification": true,'; Reason = 'Pin: unknown field' }
        @{ Name = 'field removed'; Old = '"size": 57635931,'; New = ''; Reason = 'Pin: missing field' }
    )
    foreach ($m in $pinMutations) {
        if (-not $real.Contains($m.Old)) { throw "Pin mutation target missing: $($m.Name)" }
        $text = $real.Replace($m.Old, $m.New)
        Test-Case "pin refused: $($m.Name)" { Read-Pin $text } $m.Reason
    }

    # 2. Archive and binary checks on synthetic archives. A Microsoft-signed binary that exists both here
    #    and on the runner (this pwsh) stands in for msstore.exe; nothing is ever executed from an archive.
    $pwsh = (Get-Process -Id $PID).Path
    $signer = (Get-AuthenticodeSignature -LiteralPath $pwsh).SignerCertificate.Subject
    $signedVersion = (Get-Item -LiteralPath $pwsh).VersionInfo.ProductVersion
    function New-Archive([string] $Name, [hashtable] $Files) {
        $stage = Join-Path $root "stage-$Name"
        New-Item -ItemType Directory -Path $stage | Out-Null
        foreach ($file in $Files.Keys) {
            if ($Files[$file] -eq '<signed>') { Copy-Item -LiteralPath $pwsh -Destination (Join-Path $stage $file) }
            else { Set-Content -LiteralPath (Join-Path $stage $file) -Value $Files[$file] }
        }
        $zip = Join-Path $root "$Name.zip"
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
        return $zip
    }
    function New-TestPin([string] $Zip, [hashtable] $Override = @{}) {
        $pin = [ordered] @{
            sha256 = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
            size = (Get-Item -LiteralPath $Zip).Length
            productVersion = $signedVersion
            signerSubject = $signer
            signedFiles = @('msstore.exe', 'helper.dll')
        }
        foreach ($key in $Override.Keys) { $pin[$key] = $Override[$key] }
        return [pscustomobject] $pin
    }
    $good = New-Archive 'good' @{ 'msstore.exe' = '<signed>'; 'helper.dll' = '<signed>'; 'readme.txt' = 'x' }
    $unsigned = New-Archive 'unsigned' @{ 'msstore.exe' = 'MZ not really a program'; 'helper.dll' = '<signed>' }
    $unsignedDll = New-Archive 'unsigned-dll' @{ 'msstore.exe' = '<signed>'; 'helper.dll' = 'not signed' }
    $missing = New-Archive 'missing' @{ 'msstore.exe' = '<signed>' }
    $n = 0
    function Next-Target { $script:n++; return (Join-Path $root "install-$script:n") }

    $t = Next-Target
    $wrongHash = (New-TestPin $good @{ sha256 = ('0' * 64) })
    Test-Case 'wrong SHA-256 refused before extraction' { Install-Verified $wrongHash $good $t $null } 'Archive SHA-256 mismatch' { -not (Test-Path -LiteralPath $t) }
    $t = Next-Target
    $flipped = (New-TestPin $good); $flipped.sha256 = $flipped.sha256.Substring(0, 63) + $(if ($flipped.sha256[63] -eq '0') { '1' } else { '0' })
    Test-Case 'one hex digit off refused before extraction' { Install-Verified $flipped $good $t $null } 'Archive SHA-256 mismatch' { -not (Test-Path -LiteralPath $t) }
    $t = Next-Target
    Test-Case 'the hash of another archive refused' { Install-Verified (New-TestPin $good @{ sha256 = (New-TestPin $unsigned).sha256 }) $good $t $null } 'Archive SHA-256 mismatch' { -not (Test-Path -LiteralPath $t) }
    $t = Next-Target
    Test-Case 'wrong size refused before extraction' { Install-Verified (New-TestPin $good @{ size = 1 }) $good $t $null } 'Archive size mismatch' { -not (Test-Path -LiteralPath $t) }
    $t = Next-Target
    Test-Case 'unsigned msstore.exe refused' { Install-Verified (New-TestPin $unsigned) $unsigned $t $null } 'msstore.exe is not validly Authenticode-signed'
    $t = Next-Target
    Test-Case 'unsigned pinned dll refused' { Install-Verified (New-TestPin $unsignedDll) $unsignedDll $t $null } 'helper.dll is not validly Authenticode-signed'
    $t = Next-Target
    Test-Case 'pinned file missing refused' { Install-Verified (New-TestPin $missing) $missing $t $null } 'Signed file missing'
    $t = Next-Target
    Test-Case 'signed by someone else refused' { Install-Verified (New-TestPin $good @{ signerSubject = 'CN=Contoso, O=Contoso' }) $good $t $null } 'msstore.exe signer'
    $t = Next-Target
    Test-Case 'other product version refused' { Install-Verified (New-TestPin $good @{ productVersion = '0.4.3.2+193cb57f2a' }) $good $t $null } "msstore.exe product version"
    $t = Next-Target
    New-Item -ItemType Directory -Path $t | Out-Null
    Test-Case 'existing destination refused' { Install-Verified (New-TestPin $good) $good $t $null } 'Destination already exists'
    $t = Next-Target
    $pathFile = Join-Path $root 'github_path.txt'
    Set-Content -LiteralPath $pathFile -Value 'C:\earlier' -Encoding utf8
    Test-Case 'pinned archive installs and lands on PATH' { Install-Verified (New-TestPin $good) $good $t $pathFile } $null {
        $lines = @(Get-Content -LiteralPath $pathFile)
        $lines.Count -eq 2 -and $lines[1] -ceq $t -and (Test-Path -LiteralPath (Join-Path $t 'msstore.exe'))
    }
    $t = Next-Target
    $pathFile2 = Join-Path $root 'github_path2.txt'
    Set-Content -LiteralPath $pathFile2 -Value '' -Encoding utf8
    Test-Case 'refused archive never reaches PATH' { Install-Verified (New-TestPin $unsigned) $unsigned $t $pathFile2 } 'msstore.exe is not validly' { @(Get-Content -LiteralPath $pathFile2 | Where-Object { $_ }).Count -eq 0 }

    # 3. Outside Actions, a real install needs GITHUB_PATH or an explicit -NoPath.
    $saved = $env:GITHUB_PATH
    try {
        $env:GITHUB_PATH = $null
        $result = & (Get-Process -Id $PID).Path -NoProfile -NonInteractive -File $PSCommandPath -ArchivePath $good -Destination (Next-Target) 2>&1 | Out-String
        $ok = $LASTEXITCODE -ne 0 -and $result -match 'GITHUB_PATH is not set'
        if (-not $ok) { $failed++ }
        Write-Host ("  {0}  install without GITHUB_PATH or -NoPath refused" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }))
    } finally { $env:GITHUB_PATH = $saved; $global:LASTEXITCODE = 0 }

    $total = 1 + $pinMutations.Count + 13
    if ($failed -gt 0) { throw "$failed of $total msstore pin cases are wrong." }
    Write-Host "All $total msstore pin cases behaved as required (pin shape, hash and size before extraction, signer, product version, PATH)."
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
