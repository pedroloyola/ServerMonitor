[CmdletBinding()]
param([string] $CheckScript = (Join-Path $PSScriptRoot 'Assert-PackageVersionFloor.ps1'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $CheckScript -PathType Leaf)) { throw 'Guard script missing.' }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('version-floor-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
$floorPath = Join-Path $temp 'floor.txt'
$cases = @(
    @{ Name='equal floor'; Value='1.1.7.0'; Pass=$true }
    @{ Name='higher patch'; Value='1.1.10.0'; Pass=$true }
    @{ Name='higher minor'; Value='1.2.0.0'; Pass=$true }
    @{ Name='maximum'; Value='65535.65535.65535.0'; Pass=$true }
    @{ Name='installed Developer'; Value='1.1.2.2'; Pass=$false }
    @{ Name='below floor'; Value='1.1.6.0'; Pass=$false }
    @{ Name='nonzero revision above floor'; Value='1.1.8.1'; Pass=$false }
    @{ Name='three parts'; Value='1.1.7'; Pass=$false }
    @{ Name='five parts'; Value='1.1.7.0.0'; Pass=$false }
    @{ Name='empty'; Value=''; Pass=$false }
    @{ Name='suffix'; Value='1.1.7.0-preview'; Pass=$false }
    @{ Name='newline'; Value="1.1.7.0`n"; Pass=$false }
    @{ Name='leading zero'; Value='1.01.7.0'; Pass=$false }
    @{ Name='unicode digits'; Value='١.1.7.0'; Pass=$false }
    @{ Name='zero major'; Value='0.9.9.0'; Pass=$false }
    @{ Name='overflow'; Value='65536.1.7.0'; Pass=$false }
    @{ Name='numeric overflow'; Value='999999999999.1.7.0'; Pass=$false }
)
foreach ($bad in @('', '# comment only', "1.1.7.0`n1.1.8.0", 'broken', '1.1.7', '1.1.7.1', '65536.1.7.0', ' 1.1.7.0')) {
    $cases += @{ Name="invalid floor [$bad]"; Value='1.2.0.0'; Floor=$bad; Pass=$false }
}
$cases += @{ Name='missing floor'; Value='1.2.0.0'; Missing=$true; Pass=$false }
$failed = @()
try {
    foreach ($case in $cases) {
        $floor = if ($case.ContainsKey('Floor')) { $case.Floor } else { "# synthetic floor`n1.1.7.0" }
        Set-Content -LiteralPath $floorPath -Value $floor
        $path = if ($case.ContainsKey('Missing')) { Join-Path $temp 'missing.txt' } else { $floorPath }
        $accepted = $true
        try { & $CheckScript -PackageVersion $case.Value -FloorPath $path | Out-Null } catch { $accepted = $false }
        if ($accepted -ne $case.Pass) { $failed += $case.Name; Write-Host "FAIL $($case.Name)" }
        else { Write-Host "ok $($case.Name)" }
    }
    if ($failed.Count) { throw "Floor cases failed: $($failed -join ', ')" }
    Write-Host "All $($cases.Count) package version floor cases passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
