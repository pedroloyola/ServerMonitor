<# Synthetic XML fixtures and a scoped git stub: no real tags, commits or repositories are created. #>
[CmdletBinding()]
param([string] $CheckScript = (Join-Path $PSScriptRoot 'Assert-TagVersion.ps1'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $CheckScript -PathType Leaf)) { throw 'Guard script missing.' }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('tag-version-' + [guid]::NewGuid())
$app = Join-Path $temp 'src/ServerMonitor.App'
New-Item -ItemType Directory -Path $app -Force | Out-Null
$savedOutput = $env:GITHUB_OUTPUT
$env:GITHUB_OUTPUT = Join-Path $temp 'outputs.txt'
# Script scope shadows git only while this test script runs. The production guard is unchanged.
function git {
    if ($args.Count -ne 2 -or $args[0] -ne 'rev-parse' -or $args[1] -ne 'v1.1.7^{commit}') { throw 'Unexpected git invocation.' }
    $global:LASTEXITCODE = if ($missingTag) { 1 } else { 0 }
    return ('a' * 40)
}
$savedExit = Get-Variable LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue
$cases = @('baseline', 'invalid tag', 'missing tag', 'commit mismatch', 'Version', 'FileVersion', 'InformationalVersion', 'missing Version', 'missing project', 'invalid project XML', 'store manifest', 'developer manifest', 'missing manifest', 'missing identity', 'invalid manifest XML')
$failed = @()
try {
    foreach ($case in $cases) {
        $missingTag = $case -eq 'missing tag'
        $tag = if ($case -eq 'invalid tag') { '1.1.7' } else { 'v1.1.7' }
        $commit = if ($case -eq 'commit mismatch') { 'b' * 40 } else { 'a' * 40 }
        $xml = '<Project><PropertyGroup><Version>1.1.7</Version><FileVersion>1.1.7.0</FileVersion><InformationalVersion>1.1.7</InformationalVersion><AssemblyVersion>1.0.0.0</AssemblyVersion></PropertyGroup></Project>'
        if ($case -in @('Version', 'FileVersion', 'InformationalVersion')) { $xml = $xml -replace "<$case>[^<]+</$case>", "<$case>9.9.9.0</$case>" }
        if ($case -eq 'missing Version') { $xml = $xml.Replace('<Version>1.1.7</Version>', '') }
        if ($case -eq 'invalid project XML') { $xml = '<Project' }
        $project = Join-Path $app 'ServerMonitor.App.csproj'
        Set-Content -LiteralPath $project -Value $xml
        if ($case -eq 'missing project') { Remove-Item -LiteralPath $project }
        foreach ($name in @('Package.appxmanifest', 'Package.Dev.appxmanifest')) {
            $manifest = '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Synthetic" Version="1.1.7.0" /></Package>'
            if (($case -eq 'store manifest' -and $name -eq 'Package.appxmanifest') -or ($case -eq 'developer manifest' -and $name -eq 'Package.Dev.appxmanifest')) { $manifest = $manifest.Replace('1.1.7.0', '1.1.6.0') }
            if ($case -eq 'missing identity') { $manifest = '<Package />' }
            if ($case -eq 'invalid manifest XML') { $manifest = '<Package' }
            Set-Content -LiteralPath (Join-Path $app $name) -Value $manifest
        }
        if ($case -eq 'missing manifest') { Remove-Item -LiteralPath (Join-Path $app 'Package.Dev.appxmanifest') }
        Set-Content -LiteralPath $env:GITHUB_OUTPUT -Value ''
        $accepted = $true
        try { & $CheckScript -Tag $tag -ExpectedCommit $commit -RepositoryRoot $temp | Out-Null } catch { $accepted = $false }
        $output = Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw
        $expected = $case -eq 'baseline'
        $outputOk = if ($expected) { $output.Contains('version=1.1.7') -and $output.Contains('packageVersion=1.1.7.0') } else { [string]::IsNullOrWhiteSpace($output) }
        if ($accepted -ne $expected -or -not $outputOk) { $failed += $case; Write-Host "FAIL $case" }
        else { Write-Host "ok $case" }
    }
    if ($failed.Count) { throw "Tag cases failed: $($failed -join ', ')" }
    Write-Host "All $($cases.Count) tag/version cases passed (synthetic git resolution)."
} finally {
    $env:GITHUB_OUTPUT = $savedOutput
    $global:LASTEXITCODE = $savedExit
    $resolved = [IO.Path]::GetFullPath($temp)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
# Explicit success. A native command or a stub inside the suite may have left $LASTEXITCODE non-zero,
# and the GitHub Actions pwsh wrapper ends a step with `exit $LASTEXITCODE` (PR #53 B-1).
exit 0
