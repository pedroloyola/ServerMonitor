<# Mutate temporary copies only, require detection, then rerun the untouched production guards. #>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('version-mutations-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
$mutations = @(
    @{ Name='revision check removed'; Guard='Assert-PackageVersionFloor.ps1'; Suite='Test-PackageVersionFloor.ps1'; Old='if ($parsed.Revision -ne 0)'; New='if ($false)'; Case='nonzero revision above floor' }
    @{ Name='floor comparison removed'; Guard='Assert-PackageVersionFloor.ps1'; Suite='Test-PackageVersionFloor.ps1'; Old='if ($candidate -lt $floor)'; New='if ($false)'; Case='below floor' }
    @{ Name='component bounds removed'; Guard='Assert-PackageVersionFloor.ps1'; Suite='Test-PackageVersionFloor.ps1'; Old='if ($parsed.Major -eq 0 -or @($parsed.Major, $parsed.Minor, $parsed.Build, $parsed.Revision).Where({ $_ -gt 65535 }).Count -gt 0)'; New='if ($false)'; Case='overflow' }
    @{ Name='commit comparison removed'; Guard='Assert-TagVersion.ps1'; Suite='Test-TagVersion.ps1'; Old='if ($resolved -ne $ExpectedCommit)'; New='if ($false)'; Case='commit mismatch' }
    @{ Name='project comparison removed'; Guard='Assert-TagVersion.ps1'; Suite='Test-TagVersion.ps1'; Old='elseif ($actual -ne $expectations[$name])'; New='elseif ($false)'; Case='FileVersion' }
    @{ Name='manifest comparison removed'; Guard='Assert-TagVersion.ps1'; Suite='Test-TagVersion.ps1'; Old='if ($actual -ne $packageVersion)'; New='if ($false)'; Case='developer manifest' }
)
try {
    foreach ($suite in @('Test-PackageVersionFloor.ps1', 'Test-TagVersion.ps1')) { & (Join-Path $PSScriptRoot $suite) }
    foreach ($mutation in $mutations) {
        $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot $mutation.Guard) -Raw
        if (-not $source.Contains($mutation.Old)) { throw "Mutation target missing: $($mutation.Name)" }
        $copy = Join-Path $temp $mutation.Guard
        Set-Content -LiteralPath $copy -Value $source.Replace($mutation.Old, $mutation.New)
        $detected = $false
        try { & (Join-Path $PSScriptRoot $mutation.Suite) -CheckScript $copy }
        catch {
            # A parser/setup error does not count as killing a mutation.
            if ($_.Exception.Message.Contains('cases failed:') -and $_.Exception.Message.Contains($mutation.Case)) { $detected = $true }
            else { throw }
        }
        if (-not $detected) { throw "SURVIVED: $($mutation.Name)" }
        Write-Host "MUTATION DETECTED: $($mutation.Name) [$($mutation.Case)]"
    }
    foreach ($suite in @('Test-PackageVersionFloor.ps1', 'Test-TagVersion.ps1')) { & (Join-Path $PSScriptRoot $suite) }
    Write-Host "All $($mutations.Count) mutations detected; original guards passed again."
} finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
