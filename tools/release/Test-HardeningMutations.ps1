<# Mutate temporary copies only, require detection by the named case, then rerun the untouched guards.
   Covers the REL.0C hardening guards: release run provenance (G4) and the publishing hold (G6). #>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('hardening-mutations-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
$provenance = @{ Guard = 'Assert-ReleaseRunProvenance.ps1'; Suite = 'Test-ReleaseRunProvenance.ps1'; Marker = 'Provenance cases failed:' }
$publish = @{ Guard = 'Assert-ManualPublishMode.ps1'; Suite = 'Test-ManualPublishMode.ps1'; Marker = 'Publish mode cases failed:' }
$mutations = @(
    $provenance + @{ Name = 'head sha binding removed'; Old = "if ((Get-Field `$run 'head_sha') -cne `$tagCommit)"; New = 'if ($false)'; Case = 'head sha mismatch' }
    $provenance + @{ Name = 'workflow path check removed'; Old = "if ((Get-Field `$run 'path') -cne `$WorkflowPath)"; New = 'if ($false)'; Case = 'other workflow' }
    $provenance + @{ Name = 'push-only check removed'; Old = "if ((Get-Field `$run 'event') -cne 'push')"; New = 'if ($false)'; Case = 'dispatch run on the tag ref' }
    $provenance + @{ Name = 'tag push check removed'; Old = "if ((Get-Field `$run 'head_branch') -cne `$Tag)"; New = 'if ($false)'; Case = 'other tag push' }
    $provenance + @{ Name = 'conclusion check removed'; Old = "if ((Get-Field `$run 'conclusion') -cne 'success')"; New = 'if ($false)'; Case = 'run failed' }
    $provenance + @{ Name = 'fork check removed'; Old = "if (`$headRepositoryName -cne `$Repository)"; New = 'if ($false)'; Case = 'fork head repository' }
    $provenance + @{ Name = 'main containment removed'; Old = "if ((`$status -cnotin @('identical', 'ahead')) -or -not `$behindIsZero)"; New = 'if ($false)'; Case = 'tag not on main (diverged)' }
    $provenance + @{ Name = 'package/tag binding removed'; Old = 'if ($PackageVersion -cne $expectedPackage)'; New = 'if ($false)'; Case = 'package version mismatch' }
    $provenance + @{ Name = 'annotated tag name check removed'; Old = "if ((Get-Field `$tagObject 'tag') -cne `$Tag)"; New = 'if ($false)'; Case = 'annotated object names another tag' }
    $publish + @{ Name = 'publishing hold check removed'; Old = "-not [string]::Equals(`$PublishMode, 'Manual', [StringComparison]::OrdinalIgnoreCase)"; New = '$false'; Case = 'Immediate' }
)
try {
    foreach ($suite in @($provenance.Suite, $publish.Suite)) { & (Join-Path $PSScriptRoot $suite) | Out-Null }
    foreach ($mutation in $mutations) {
        $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot $mutation.Guard) -Raw
        if (-not $source.Contains($mutation.Old)) { throw "Mutation target missing: $($mutation.Name)" }
        $copy = Join-Path $temp $mutation.Guard
        Set-Content -LiteralPath $copy -Value $source.Replace($mutation.Old, $mutation.New)
        $detected = $false
        try { & (Join-Path $PSScriptRoot $mutation.Suite) -CheckScript $copy | Out-Null }
        catch {
            # A parser/setup error does not count as killing a mutation.
            if ($_.Exception.Message.Contains($mutation.Marker) -and $_.Exception.Message.Contains($mutation.Case)) { $detected = $true }
            else { throw }
        }
        if (-not $detected) { throw "SURVIVED: $($mutation.Name)" }
        Write-Host "MUTATION DETECTED: $($mutation.Name) [$($mutation.Case)]"
    }
    foreach ($suite in @($provenance.Suite, $publish.Suite)) { & (Join-Path $PSScriptRoot $suite) | Out-Null }
    Write-Host "All $($mutations.Count) hardening mutations detected; original guards passed again."
}
finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
