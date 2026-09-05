# `release.yml` → `release-build.yml` migration review

Scope: **only** whether every intentional responsibility of the removed
`.github/workflows/release.yml` is preserved, explicitly replaced, or deliberately dropped
with a reason. Nothing else about the product is reviewed here.

Baseline compared: `release.yml` at `main` = `336091b`, against `release-build.yml` on
`chore/release-automation`.

## Verdict

**PASS.** Fifteen responsibilities: eleven preserved (four of them strengthened), three
replaced, one deliberately dropped. The dropped one is called out in full below because it
is a real reduction in capability, not a wash.

## Responsibility by responsibility

| # | old responsibility | disposition | detail |
|---|---|---|---|
| 1 | trigger on `push` tags `v*` | **preserved** | identical |
| 2 | `workflow_dispatch` input `ref`: "Tag **or commit SHA** to package" | **DROPPED** | see below |
| 3 | `permissions: contents: read` | **replaced** | top-level stays `contents: read`; the job alone raises to `contents: write`, only to create the Release |
| 4 | checkout the exact ref | **preserved, strengthened** | plus `fetch-depth: 0` and `fetch-tags: true`, without which a tag cannot be resolved to a commit |
| 5 | `actions/setup-dotnet@v4`, `10.0.x` | **preserved** | identical |
| 6 | `dotnet restore ServerMonitor.slnx` | **preserved** | identical |
| 7 | `dotnet build … -c Release --no-incremental --disable-build-servers` | **preserved** | identical, same P-008/P-013 reasoning |
| 8 | `dotnet test … -c Release --no-build --disable-build-servers` | **preserved** | identical |
| 9 | `dotnet build …csproj -c Release -p:Packaged=true --no-incremental --disable-build-servers` | **preserved** | identical |
| 10 | find the MSIX under `AppPackages`, excluding `Dependencies` | **preserved, strengthened** | old took `Select-Object -First 1` and shipped whichever came first if the build produced several; new **fails** unless there is exactly one |
| 11 | fail if no MSIX was produced | **preserved** | `Write-Error`/`exit 1` became `throw`; both fail the step |
| 12 | copy the MSIX to `artifacts/`, keeping the build's filename | **replaced** | copied to `artifacts/release/` as `ServerAlyzer_<packageVersion>_x64_<sha7>.msix`, so provenance is in the filename — the convention already used for the 1.1.1 artifact |
| 13 | write `<name>.sha256` and print the digest | **preserved** | now produced by `Assert-PackageIdentity.ps1 -WriteChecksumFile`; digest printed and also surfaced in the run summary and as a step output |
| 14 | upload artifact `ServerMonitor-msix-x64`, `if-no-files-found: error` | **replaced** | name is now `serveralyzer-msix-<packageVersion>`, because `store-submit.yml` resolves the artifact by version. `if-no-files-found: error` kept; `retention-days: 90` added. Nothing in the repository referenced the old name (verified by search) |
| 15 | **no** GitHub Release creation, **no** Store upload, **no** Store secrets | **preserved in spirit, extended deliberately** | Release creation was added because R1 asked for it, and it refuses to modify a Release that already exists. Still no Store contact and no Store secrets: `release-build.yml` does not join the `microsoft-store` environment |

## The one deliberate removal

**Old:** `workflow_dispatch` accepted `ref` described as *"Tag or commit SHA to package"*, so
any commit could be packaged on demand.

**New:** `workflow_dispatch` accepts `tag`, and refuses anything that is not
`vMAJOR.MINOR.PATCH`.

**Why.** The point of `release-build.yml` is that the package provably matches the tag: the
tag must resolve to the checked-out commit, and the csproj and both appxmanifests must carry
that same version. A bare commit SHA has no version to agree with, so the check either could
not run or would have to be skipped — and a skippable check on the release path is not a
check. The artifact name and the `store-submit.yml` handshake are both keyed on the package
version too.

**What is lost, honestly.** You can no longer produce an MSIX from an arbitrary commit
through this workflow.

**What covers it.** `ci.yml` still builds and tests every push and pull request. For a
throwaway package from an untagged commit, build locally with the documented command, or
create a temporary tag. If on-demand packaging of arbitrary commits turns out to be needed
often, the right fix is a separate explicitly-named workflow, not weakening the version
check on the release path.

## Things that did not exist before and are additive

* tag ↔ commit ↔ csproj ↔ manifest version coherence (`Assert-TagVersion.ps1`)
* package identity, capability, payload and GUI-subsystem verification
  (`Assert-PackageIdentity.ps1`)
* the release checks' own counterproof suites, run before packaging
* vulnerability scan, carried over from `ci.yml`
* `concurrency` group with `cancel-in-progress: false`, so two tag pushes cannot race
* a run summary carrying tag, commit, versions, package name and SHA-256

## Signing

The old workflow performed no signing, and neither does the new one.
`AppxPackageSigningEnabled=false` remains in `ServerMonitor.App.csproj` per ADR-017: the
Store signs the production package. Nothing was lost here.

## Release notes

The old workflow had no notion of release notes. The new one uses
`docs/release-notes-<version>.md` when it exists, and otherwise creates the Release with an
explicit placeholder rather than inventing notes. Store listing notes are a separate thing
and live under `release/store/`.

## Failure behaviour

Old: any failing step failed the run; the collect step used `Write-Error` plus `exit 1`.

New: same, with more places that stop early — a malformed tag, a tag/commit mismatch, a
version disagreement, more than one MSIX, or any package identity problem all fail before
an artifact is produced. No new step swallows an error, and no step uses
`continue-on-error`. The only `if:` conditions are `inputs.create_release != false` on the
Release step and `always()` on the final report in `store-submit.yml`.
