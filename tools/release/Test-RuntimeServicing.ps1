<# REL.0D servicing controls for the self-contained .NET runtime (ADR-017 amendment), checked on the
   repository text (offline, no SDK, no network):
   S1  global.json pins ONE exact SDK: version MAJOR.MINOR.PATCH (feature band + patch), rollForward
       "disable", allowPrerelease false. The SDK decides which runtime is bundled, so the same commit
       must always build with the same SDK.
   S2  every actions/setup-dotnet step in ci.yml and release-build.yml installs from global.json
       (global-json-file: global.json) and no workflow names a dotnet-version of its own.
   S3  release/min-dotnet-runtime.txt holds exactly one canonical MAJOR.MINOR.PATCH.
   S4  the privacy allowlist keeps no versioned runtime file (mscordaccore_amd64_amd64_<file version>.dll)
       below the floor: such a package is refused by the runtime check anyway, so the entry can only
       widen the gate. The runtime patch is the file version's build number divided by 100 (10.0.1326.x is
       Microsoft.NETCore.App 10.0.13), and the entry's reason must name that same runtime.
   S5  release-build.yml writes the build record after the runtime check and before the privacy gate and
       the upload; ci.yml's package job writes it after its runtime check.
   S6  ci.yml's package job checks the package identity (Assert-PackageIdentity.ps1) with the version the
       repository declares (Get-RepositoryPackageVersion.ps1), never a literal, and the default Store identity.
   Unsupported shapes fail closed. -SelfTest reintroduces each defect in memory and requires a failure
   for the right reason. #>
[CmdletBinding()]
param([string] $RepositoryRoot = (Join-Path $PSScriptRoot '../..'), [switch] $SelfTest)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Text([string] $Relative) {
    $path = Join-Path $RepositoryRoot $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Servicing: $Relative is missing." }
    return (Get-Content -LiteralPath $path -Raw) -replace "`r`n", "`n"
}

function ConvertTo-RuntimeVersion([string] $Value) {
    if ($Value -cnotmatch '\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z') { return $null }
    return [version] $Value
}

function Assert-GlobalJson([string] $Text) {
    try { $json = $Text | ConvertFrom-Json } catch { throw 'Servicing S1: global.json is not valid JSON.' }
    $sdk = $json.PSObject.Properties['sdk']
    if ($null -eq $sdk -or $null -eq $sdk.Value) { throw 'Servicing S1: global.json has no sdk section.' }
    $names = @($sdk.Value.PSObject.Properties.Name | Sort-Object)
    if (($names -join ',') -cne 'allowPrerelease,rollForward,version') { throw "Servicing S1: global.json sdk must have exactly version, rollForward and allowPrerelease (found: $($names -join ', '))." }
    if ($sdk.Value.version -isnot [string] -or $sdk.Value.version -cnotmatch '\A[0-9]+\.[0-9]+\.[1-9][0-9]{2}\z') { throw 'Servicing S1: global.json must pin an exact SDK version (MAJOR.MINOR.FEATUREPATCH).' }
    if ($sdk.Value.rollForward -cne 'disable') { throw 'Servicing S1: global.json rollForward must be "disable" (the SDK decides the bundled runtime).' }
    if ($sdk.Value.allowPrerelease -isnot [bool] -or $sdk.Value.allowPrerelease) { throw 'Servicing S1: global.json allowPrerelease must be false.' }
}

function Assert-SetupDotnet([string] $Name, [string] $Text) {
    $lines = $Text -split '\n'
    $steps = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*#') { continue }
        if ($lines[$i] -notmatch '^(\s*)(?:-\s+)?uses:\s*actions/setup-dotnet@') { continue }
        $steps++
        # The step's own lines: until the next step ("- ") or a shallower key.
        $block = New-Object System.Collections.Generic.List[string]
        for ($j = $i + 1; $j -lt $lines.Count; $j++) {
            if ($lines[$j] -match '^\s*-\s' -or $lines[$j] -match '^\s{0,5}\S') { break }
            $block.Add($lines[$j]) | Out-Null
        }
        $with = @($block | Where-Object { $_ -notmatch '^\s*#' })
        if (@($with | Where-Object { $_ -cmatch '^\s+global-json-file:\s*global\.json\s*$' }).Count -ne 1) {
            throw "Servicing S2: $Name has a setup-dotnet step that does not install from global-json-file: global.json."
        }
    }
    if ($steps -eq 0) { throw "Servicing S2: $Name has no actions/setup-dotnet step; review this guard." }
    if (@($lines | Where-Object { $_ -notmatch '^\s*#' -and $_ -match 'dotnet-version\s*:' }).Count -gt 0) {
        throw "Servicing S2: $Name names a dotnet-version; the SDK comes only from global.json."
    }
}

function Get-Floor([string] $Text) {
    $values = @(($Text -split '\n') | Where-Object { $_ -notmatch '^\s*(#.*)?$' })
    if ($values.Count -ne 1) { throw 'Servicing S3: release/min-dotnet-runtime.txt must contain exactly one version.' }
    $floor = ConvertTo-RuntimeVersion $values[0]
    if ($null -eq $floor) { throw 'Servicing S3: release/min-dotnet-runtime.txt is not a canonical MAJOR.MINOR.PATCH version.' }
    return $floor
}

function Assert-Allowlist([string] $Text, [version] $Floor) {
    try { $entries = @($Text | ConvertFrom-Json) } catch { throw 'Servicing S4: the allowlist is not valid JSON.' }
    $versioned = 0
    foreach ($entry in $entries) {
        $file = [string] $entry.file
        if ($file -notmatch '^mscordaccore_') { continue }
        if ($file -ceq 'mscordaccore.dll') { continue }
        if ($file -cnotmatch '\Amscordaccore_amd64_amd64_([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)\.dll\z') {
            throw "Servicing S4: unrecognised versioned runtime file '$file'; review this guard."
        }
        $runtime = [version]::new([int] $Matches[1], [int] $Matches[2], [int] ([math]::Floor([int] $Matches[3] / 100)))
        if ($runtime -lt $Floor) { throw "Servicing S4: allowlist entry '$file' (Microsoft.NETCore.App $runtime) is below the runtime floor $Floor; prune it." }
        if ([string] $entry.reason -notmatch "Microsoft\.NETCore\.App $([regex]::Escape($runtime.ToString()))\)") {
            throw "Servicing S4: allowlist entry '$file' must name the runtime it belongs to (Microsoft.NETCore.App $runtime)."
        }
        $versioned++
    }
    if ($versioned -eq 0) { throw 'Servicing S4: no versioned mscordaccore entry in the allowlist; the packaged runtime ships one. Review this guard.' }
}

function Get-StepIndex([string] $Text, [string] $Marker, [string] $What, [string] $Rule = 'S5') {
    $at = $Text.IndexOf($Marker, [StringComparison]::Ordinal)
    if ($at -lt 0) { throw "Servicing ${Rule}: $What is missing." }
    return $at
}

function Assert-ReleaseTrace([string] $Text) {
    $body = (($Text -split '\n') | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    $runtime = Get-StepIndex $body './tools/release/Assert-PackageRuntime.ps1 `' 'release-build: the package runtime check'
    $trace = Get-StepIndex $body './tools/release/Write-PackageTrace.ps1 `' 'release-build: the package build record'
    $privacy = Get-StepIndex $body "Assert-DistributionPrivacy.ps1 ```n            -Path 'artifacts/release' ``" 'release-build: the privacy gate on artifacts/release'
    $upload = Get-StepIndex $body 'uses: actions/upload-artifact@' 'release-build: the artifact upload'
    if (-not ($runtime -lt $trace -and $trace -lt $privacy -and $privacy -lt $upload)) {
        throw 'Servicing S5: release-build must run runtime check, then build record, then privacy gate, then upload.'
    }
    if ($body -notmatch '-SummaryPath \$env:GITHUB_STEP_SUMMARY') { throw 'Servicing S5: release-build must put the build record in the job summary.' }
}

function Assert-CiTrace([string] $Text) {
    $body = (($Text -split '\n') | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    $runtime = Get-StepIndex $body './tools/release/Assert-PackageRuntime.ps1 -PackagePath' 'ci: the package runtime check'
    $trace = Get-StepIndex $body './tools/release/Write-PackageTrace.ps1 -PackagePath' 'ci: the package build record'
    if ($trace -lt $runtime) { throw 'Servicing S5: ci must write the build record after the package runtime check.' }
    if ($body -notmatch 'Write-PackageTrace\.ps1 [^\n]*-SummaryPath \$env:GITHUB_STEP_SUMMARY') { throw 'Servicing S5: ci must put the build record in the job summary.' }
}

# S6 (Vigil PR #51 L-1): the CI package job proves the package identity, with the version the repository
# declares (never a literal), after building it.
function Assert-CiIdentity([string] $Text) {
    $body = (($Text -split '\n') | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    $build = Get-StepIndex $body '-c Release -p:Packaged=true' 'ci: the packaged build' 'S6'
    $version = Get-StepIndex $body '$version = ./tools/release/Get-RepositoryPackageVersion.ps1' 'ci: the repository package version' 'S6'
    $floorCheck = Get-StepIndex $body './tools/release/Assert-PackageVersionFloor.ps1 -PackageVersion $version' 'ci: the package version floor on the repository version' 'S6'
    $identity = Get-StepIndex $body './tools/release/Assert-PackageIdentity.ps1 -PackagePath $candidates[0].FullName -ExpectedVersion $version' 'ci: the package identity check' 'S6'
    if (-not ($build -lt $version -and $version -lt $floorCheck -and $floorCheck -lt $identity)) {
        throw 'Servicing S6: ci must build the package, read the repository version, check it against the floor, then check the identity.'
    }
    # Exact line: the script's own defaults are the Store identity; any extra argument overrides them.
    $calls = @([regex]::Matches($body, '(?m)^.*Assert-PackageIdentity\.ps1.*$'))
    if ($calls.Count -ne 1 -or $calls[0].Value -cnotmatch '\A\s+\./tools/release/Assert-PackageIdentity\.ps1 -PackagePath \$candidates\[0\]\.FullName -ExpectedVersion \$version\s*\z') {
        throw 'Servicing S6: ci must run the identity check exactly once, with the repository version and the default (Store) identity.'
    }
}

# S7 (PR #53 B-1): a self-test may print its success line and still fail the Actions step through a leaked
# $LASTEXITCODE. So (a) the workflows never call a tools/release/Test-*.ps1 directly: every self-test goes
# through Invoke-ReleaseSelfTests.ps1, which runs it in its own pwsh wrapped like a step and requires exit 0,
# and whose own counterproofs run in both workflows; (b) every self-test script ends with an explicit `exit 0`.
function Assert-SelfTestExits([string] $Name, [string] $Text) {
    $body = (($Text -split '\n') | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    $direct = @([regex]::Matches($body, '(?m)^.*\./tools/release/Test-[A-Za-z-]+\.ps1.*$'))
    if ($direct.Count -gt 0) { throw "Servicing S7: $Name calls a self-test directly ($($direct[0].Value.Trim())); run it through Invoke-ReleaseSelfTests.ps1." }
    if ($body -notmatch '(?m)^\s+\./tools/release/Invoke-ReleaseSelfTests\.ps1 -SelfTest\s*$') { throw "Servicing S7: $Name does not run the self-test runner's own counterproofs." }
}

function Assert-ExplicitExit([string] $Endings) {
    foreach ($line in ($Endings -split '\n' | Where-Object { $_ })) {
        $name, $last = $line -split '\|', 2
        if ($last -cne 'exit 0') { throw "Servicing S7: $name does not end with an explicit 'exit 0'." }
    }
}

$selfTestScripts = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'tools/release') -Filter 'Test-*.ps1' -File) +
    @(Get-Item -LiteralPath (Join-Path $RepositoryRoot 'tools/release/Invoke-ReleaseSelfTests.ps1'))
if ($selfTestScripts.Count -lt 2) { throw 'Servicing S7: no self-test scripts found; review this guard.' }
$endings = ($selfTestScripts | Sort-Object Name | ForEach-Object {
        $last = @((Get-Content -LiteralPath $_.FullName) | Where-Object { $_.Trim() }) | Select-Object -Last 1
        "$($_.Name)|$($last.Trim())"
    }) -join "`n"

$inputs = [ordered] @{
    'global.json'                          = Read-Text 'global.json'
    '.github/workflows/ci.yml'             = Read-Text '.github/workflows/ci.yml'
    '.github/workflows/release-build.yml'  = Read-Text '.github/workflows/release-build.yml'
    'release/min-dotnet-runtime.txt'       = Read-Text 'release/min-dotnet-runtime.txt'
    'tools/release/distribution-privacy-allowlist.json' = Read-Text 'tools/release/distribution-privacy-allowlist.json'
    'self-test endings'                    = $endings
}

function Invoke-Checks($i) {
    Assert-GlobalJson $i['global.json']
    Assert-SetupDotnet 'ci.yml' $i['.github/workflows/ci.yml']
    Assert-SetupDotnet 'release-build.yml' $i['.github/workflows/release-build.yml']
    $floor = Get-Floor $i['release/min-dotnet-runtime.txt']
    Assert-Allowlist $i['tools/release/distribution-privacy-allowlist.json'] $floor
    Assert-ReleaseTrace $i['.github/workflows/release-build.yml']
    Assert-CiTrace $i['.github/workflows/ci.yml']
    Assert-CiIdentity $i['.github/workflows/ci.yml']
    Assert-SelfTestExits 'ci.yml' $i['.github/workflows/ci.yml']
    Assert-SelfTestExits 'release-build.yml' $i['.github/workflows/release-build.yml']
    Assert-ExplicitExit $i['self-test endings']
    return $floor
}

$floor = Invoke-Checks $inputs
Write-Host "PASS runtime servicing controls (SDK pinned by global.json, runtime floor $floor)"

if ($SelfTest) {
    $versionedEntry = '"file": "mscordaccore_amd64_amd64_10.0.1326.50504.dll",'
    $mutations = @(
        @{ File = 'global.json'; Old = '"rollForward": "disable"'; New = '"rollForward": "latestFeature"'; Reason = 'S1: .*rollForward'; Name = 'SDK may roll forward' }
        @{ File = 'global.json'; Old = '"rollForward": "disable"'; New = '"rollForward": "latestPatch"'; Reason = 'S1: .*rollForward'; Name = 'SDK may take a newer patch' }
        @{ File = 'global.json'; Old = '"allowPrerelease": false'; New = '"allowPrerelease": true'; Reason = 'S1: .*allowPrerelease'; Name = 'prerelease SDK allowed' }
        @{ File = 'global.json'; Old = '"version": "10.0.402"'; New = '"version": "10.0.x"'; Reason = 'S1: .*exact SDK'; Name = 'floating SDK version' }
        @{ File = 'global.json'; Old = '"version": "10.0.402"'; New = '"version": "10.0.402-rc.1"'; Reason = 'S1: .*exact SDK'; Name = 'prerelease SDK version' }
        @{ File = 'global.json'; Old = ",`n    `"allowPrerelease`": false"; New = ''; Reason = 'S1: .*exactly version'; Name = 'allowPrerelease removed' }
        @{ File = '.github/workflows/ci.yml'; Old = 'global-json-file: global.json'; New = "dotnet-version: '10.0.x'"; Reason = 'S2: ci\.yml'; Name = 'ci: one job back on 10.0.x' }
        @{ File = '.github/workflows/ci.yml'; Old = "global-json-file: global.json`n"; New = "global-json-file: global.json`n          dotnet-version: '10.0.x'`n"; Reason = 'S2: ci\.yml names a dotnet-version'; Name = 'ci: dotnet-version next to global-json-file' }
        @{ File = '.github/workflows/release-build.yml'; Old = 'global-json-file: global.json'; New = "dotnet-version: '10.0.x'"; Reason = 'S2: release-build\.yml'; Name = 'release-build back on 10.0.x' }
        @{ File = '.github/workflows/release-build.yml'; Old = 'global-json-file: global.json'; New = '# global-json-file: global.json'; Reason = 'S2: release-build\.yml has a setup-dotnet step'; Name = 'release-build: global-json-file commented out' }
        @{ File = 'release/min-dotnet-runtime.txt'; Old = "`n10.0.13`n"; New = "`n10.0`n"; Reason = 'S3: '; Name = 'floor not canonical' }
        @{ File = 'release/min-dotnet-runtime.txt'; Old = "`n10.0.13`n"; New = "`n10.0.13`n10.0.14`n"; Reason = 'S3: .*exactly one'; Name = 'two floors' }
        @{ File = 'release/min-dotnet-runtime.txt'; Old = "`n10.0.13`n"; New = "`n10.0.14`n"; Reason = 'S4: .*10\.0\.13\) is below the runtime floor 10\.0\.14'; Name = 'floor raised without pruning the allowlist' }
        @{ File = 'tools/release/distribution-privacy-allowlist.json'; Old = $versionedEntry; New = '"file": "mscordaccore_amd64_amd64_10.0.1226.42308.dll",'; Reason = 'S4: .*10\.0\.12\) is below'; Name = 'stale 10.0.12 entry kept' }
        @{ File = 'tools/release/distribution-privacy-allowlist.json'; Old = $versionedEntry; New = '"file": "mscordaccore_amd64_amd64_10.0.1426.1.dll",'; Reason = 'S4: .*must name the runtime it belongs to \(Microsoft\.NETCore\.App 10\.0\.14\)'; Name = 'entry renamed without its reason' }
        @{ File = 'tools/release/distribution-privacy-allowlist.json'; Old = $versionedEntry; New = '"file": "mscordaccore_amd64_amd64_10.0.13.dll",'; Reason = 'S4: unrecognised'; Name = 'unrecognised versioned name' }
        @{ File = '.github/workflows/release-build.yml'; Old = './tools/release/Write-PackageTrace.ps1 `'; New = 'Write-Host `'; Reason = 'S5: release-build: the package build record is missing'; Name = 'release-build without the build record' }
        @{ File = '.github/workflows/release-build.yml'; Old = "-Path 'artifacts/release' ``"; New = "-Path 'artifacts/release/x.msix' ``"; Reason = 'S5: release-build: the privacy gate'; Name = 'privacy gate no longer covers the record' }
        @{ File = '.github/workflows/ci.yml'; Old = './tools/release/Write-PackageTrace.ps1 -PackagePath'; New = 'Write-Host -Object'; Reason = 'S5: ci: the package build record is missing'; Name = 'ci without the build record' }
        @{ File = '.github/workflows/ci.yml'; Old = './tools/release/Assert-PackageIdentity.ps1 -PackagePath'; New = 'Write-Host -Object'; Reason = 'S6: ci: the package identity check is missing'; Name = 'ci without the identity check' }
        @{ File = '.github/workflows/ci.yml'; Old = '-ExpectedVersion $version'; New = "-ExpectedVersion '1.1.1.0'"; Reason = 'S6: ci: the package identity check is missing'; Name = 'ci identity against a literal version' }
        @{ File = '.github/workflows/ci.yml'; Old = '$version = ./tools/release/Get-RepositoryPackageVersion.ps1'; New = "`$version = '1.1.1.0'"; Reason = 'S6: ci: the repository package version is missing'; Name = 'ci version hard-coded' }
        @{ File = '.github/workflows/ci.yml'; Old = '-ExpectedVersion $version'; New = '-ExpectedVersion $version -ExpectedIdentityName Other'; Reason = 'S6: .*exact'; Name = 'ci identity with an overridden identity name' }
        @{ File = '.github/workflows/ci.yml'; Old = './tools/release/Assert-PackageVersionFloor.ps1 -PackageVersion $version'; New = 'Write-Host -Object $version'; Reason = 'S6: ci: the package version floor on the repository version is missing'; Name = 'ci without the package version floor' }
        @{ File = '.github/workflows/ci.yml'; Old = "./tools/release/Invoke-ReleaseSelfTests.ps1 -Test 'Test-PackageRuntime.ps1'"; New = './tools/release/Test-PackageRuntime.ps1'; Reason = 'S7: ci\.yml calls a self-test directly'; Name = 'ci: a self-test called directly (B-1 shape)' }
        @{ File = '.github/workflows/release-build.yml'; Old = "./tools/release/Invoke-ReleaseSelfTests.ps1 -Test @("; New = "./tools/release/Test-PackageResources.ps1`n          ./tools/release/Invoke-ReleaseSelfTests.ps1 -Test @("; Reason = 'S7: release-build\.yml calls a self-test directly'; Name = 'release-build: a self-test called directly' }
        @{ File = '.github/workflows/release-build.yml'; Old = './tools/release/Invoke-ReleaseSelfTests.ps1 -SelfTest'; New = 'Write-Host runner'; Reason = 'S7: release-build\.yml does not run the self-test runner'; Name = 'release-build: runner counterproofs dropped' }
        @{ File = 'self-test endings'; Old = 'Test-PackageResources.ps1|exit 0'; New = 'Test-PackageResources.ps1|Write-Host "All 23 package resources cases behaved as required."'; Reason = 'S7: Test-PackageResources\.ps1 does not end with an explicit'; Name = 'self-test without the explicit exit 0' }
    )
    $count = 0
    foreach ($mutation in $mutations) {
        $text = $inputs[$mutation.File]
        $hits = ([regex]::Matches($text, [regex]::Escape($mutation.Old))).Count
        if ($hits -lt 1) { throw "Mutation target missing: $($mutation.Name)" }
        $copy = [ordered] @{}
        foreach ($key in $inputs.Keys) { $copy[$key] = $inputs[$key] }
        # First occurrence only: one defect at a time.
        $at = $text.IndexOf($mutation.Old, [StringComparison]::Ordinal)
        $copy[$mutation.File] = $text.Substring(0, $at) + $mutation.New + $text.Substring($at + $mutation.Old.Length)
        $caught = $null
        try { Invoke-Checks $copy | Out-Null } catch { $caught = $_.Exception.Message }
        if ($null -eq $caught) { throw "SURVIVED: $($mutation.Name)" }
        if ($caught -notmatch "^Servicing $($mutation.Reason)") { throw "Mutation '$($mutation.Name)' was rejected for the wrong reason: $caught" }
        Write-Host "MUTATION DETECTED: $($mutation.Name)"
        $count++
    }

    # The ordering in S5, which a removal cannot show: the record moved after the upload.
    $release = $inputs['.github/workflows/release-build.yml']
    $traceStep = [regex]::Match($release, '(?s)\n      # REL\.0D servicing traceability.*?\n(?=\n      # PRIV-PDBPATH)')
    if (-not $traceStep.Success) { throw 'Mutation target missing: build record step' }
    $moved = $release.Remove($traceStep.Index, $traceStep.Length)
    $moved = $moved -replace '(?s)(\n          retention-days: 90\n)', ('$1' + $traceStep.Value.Replace('$', '$$'))
    $copy = [ordered] @{}; foreach ($key in $inputs.Keys) { $copy[$key] = $inputs[$key] }
    $copy['.github/workflows/release-build.yml'] = $moved
    $caught = $null
    try { Invoke-Checks $copy | Out-Null } catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or $caught -notmatch '^Servicing S5: release-build must run runtime check, then build record') { throw "SURVIVED or wrong reason: build record after the upload ($caught)" }
    Write-Host 'MUTATION DETECTED: build record after the upload'
    $count++

    Invoke-Checks $inputs | Out-Null
    Write-Host "All $count runtime servicing mutations detected; the repository passed again."
}
# Explicit success. A native command or a stub inside the suite may have left $LASTEXITCODE non-zero,
# and the GitHub Actions pwsh wrapper ends a step with `exit $LASTEXITCODE` (PR #53 B-1).
exit 0
