# Release automation

How a ServerAlyzer release goes from a tag to a Microsoft Store submission, what each
workflow is allowed to do, and what still needs a person.

The design principle throughout: **the pipeline can prepare and submit, but it can never
make the product public.** Publishing stays a human act.

## The three workflows

| workflow | trigger | talks to the Store | can publish |
| --- | --- | --- | --- |
| `release-build.yml` | tag `v*`, or manual dispatch | no | no |
| `store-submit.yml` | manual dispatch only, `microsoft-store` environment | yes | no |
| `store-status.yml` | manual dispatch only, `microsoft-store-status` environment | reads only | no |

`release-build.yml` holds no Store secrets at all: it is not attached to the environment
that carries them.

### 1. `release-build.yml`

Builds the shippable MSIX from an exact tag and proves it is what the tag says.

1. Refuses any tag that is not `vMAJOR.MINOR.PATCH`.
2. Checks out that exact tag with full history.
3. `Assert-TagVersion.ps1` — the tag must resolve to the checked-out commit, and the
   csproj `Version` / `FileVersion` / `InformationalVersion` plus both appxmanifests must
   all agree with it. `AssemblyVersion` is deliberately excluded; it is pinned at `1.0.0.0`.
4. Restore, non-incremental Release build, full test run, vulnerability scan.
5. Runs the release checks' own counterproof suites, so a green package check means
   something.
6. Builds the MSIX and refuses to continue if the build produced anything other than
   exactly one app package.
7. `Assert-PackageIdentity.ps1` — reads the package, not the source tree: identity name,
   publisher, version, architecture, app-level capabilities, payload hygiene, the exact set
   of executables, and that **every executable is PE subsystem 2 (Windows GUI)**.
8. Writes the SHA-256 next to the package and uploads both as a workflow artifact.
9. Creates the GitHub Release **only if it does not already exist**, and by default
   **does not attach the MSIX**: the Microsoft Store is the distribution channel.

### 2. `store-submit.yml`

Takes the artifact from a `release-build` run and submits it to the existing product.

It never builds. The package must arrive from a specific run id and its SHA-256 must match
what the operator types in, so the thing that gets certified is provably the thing that was
built and checked.

Order of operations, and why:

1. Confirmation phrase `SUBMIT`, plus shape checks on the version and digest.
2. Download the artifact from the named run; re-run the full package identity check on it.
3. `msstore reconfigure` from environment secrets.
4. `msstore apps get 9N6ZBSBN1TD2` — the product must report identity
   `PedroLoy.ServerAlyzer`, or the run stops.
5. **`Assert-NoPendingSubmission.ps1`.** This is the most important guard in the pipeline.
   The CLI documentation states that `msstore publish` *deletes* a pending draft and
   recreates it from the last published submission, discarding staged changes. So the
   workflow refuses to run at all while anything is in flight, and fails closed on
   unparseable output.
6. `msstore publish <msix> -id <productId> --noCommit` — uploads the package and leaves the
   submission in draft. This must come **before** metadata edits, because `publish`
   recreates the draft.
7. `msstore submission get`, then `Update-StoreSubmission.ps1`, which asserts
   `targetPublishMode == Manual`, asserts visibility, asserts the draft carries the
   expected package version, and writes the release notes from `release/store/`.
8. `msstore submission update` applies the metadata.
9. **Read back from the Store and re-check the hold on what the Store actually stored**,
   not on the file that was sent. If `targetPublishMode` is not `Manual`, the run stops
   here with the draft uncommitted.
10. `msstore submission publish` commits the submission to certification.

### 3. `store-status.yml`

Read-only. Reports one of:

| reported state | underlying API status |
| --- | --- |
| `NO SUBMISSION` | `None` |
| `DRAFT (not submitted)` | `PendingCommit` |
| `SUBMITTED` | `CommitStarted` |
| `CERTIFICATION IN PROGRESS` | `PreProcessing`, `Certification`, `Release` |
| `CERTIFIED (awaiting manual Publish now)` | `PendingPublication` |
| `PUBLISHING` | `Publishing` |
| `PUBLIC` | `Published` **and** visibility `Public` |
| `PUBLISHED (audience: X, not public)` | `Published` with any other visibility |
| `FAILED (…)` | any `*Failed` |

`Published` is not the same as public. A product whose visibility is `Private` is published
to its audience and to nobody else, and the report says so rather than claiming the product
is live.

## The publishing hold

`targetPublishMode` in the submission resource takes `Immediate`, `Manual` or
`SpecificDate`. `Manual` is the API spelling of *"Don't publish this submission until I
select Publish now"*.

Two independent places assert it is `Manual`: once on the JSON before it is sent, and once
on the JSON read back from the Store immediately before committing. Neither is overridable
from a workflow input. Turning automatic publishing on is a product decision and belongs to
a person changing it in Partner Center deliberately.

## Store metadata

```
release/store/
  en-US/whats-new.txt
  pt-BR/whats-new.txt
  pt-PT/whats-new.txt
```

One folder per Store listing language, matched case-insensitively against the listing keys
in the submission JSON. Rules the pipeline enforces:

* a folder with no `whats-new.txt` stops the run — no language ships with empty notes;
* notes longer than 1500 characters stop the run, rather than being silently truncated;
* a folder whose language has no listing in the submission stops the run — adding a
  language is a product decision;
* listings present in the submission but absent from `release/store/` are left untouched
  and reported.

Nothing else in any listing is written. Descriptions, features, screenshots, pricing,
markets, age ratings and visibility are never modified by these workflows.

**No credentials belong in these files.** Certification test credentials live in Partner
Center under *Additional Testing Info*, and stay there.

## Secrets

Stored as GitHub **environment** secrets on `microsoft-store` and
`microsoft-store-status`. Never in the repository, never printed.

> **`microsoft-store-status` is not a privilege boundary.** Both environments carry the same
> Entra application, and that application holds the Manager role in Partner Center — it can
> write. Only the *commands in `store-status.yml`* are read-only. Anyone who can edit that
> workflow, or add another workflow that joins the environment, holds a credential that
> could submit or publish. What actually constrains the credential is the branch/ref
> restriction on each environment and the required reviewer on `microsoft-store`.
>
> A genuinely read-only credential would need a second Entra application with a Partner
> Center role that cannot submit. Microsoft documents Manager as the role for this flow and
> does not document a weaker one that works, so that option is not available today.

| secret | what it is |
| --- | --- |
| `AZURE_AD_TENANT_ID` | the Microsoft Entra tenant associated with the Partner Center account |
| `AZURE_AD_APPLICATION_CLIENT_ID` | Application (client) ID of the Entra app registration |
| `AZURE_AD_APPLICATION_SECRET` | client secret for that registration |
| `SELLER_ID` | the publisher/seller ID from Partner Center account settings |

These four names are the ones Microsoft's own documentation uses.

## Running a release

1. Land the version bump and tag the commit (`vX.Y.Z`).
2. `release-build.yml` runs on the tag push. Record the package name and SHA-256 from the
   run summary.
3. Run `store-status.yml` and confirm the product is idle.
4. Dispatch `store-submit.yml` with the run id, package version, the SHA-256, and `SUBMIT`.
   Approve the `microsoft-store` environment when GitHub asks.
5. Watch with `store-status.yml`.
6. When it reports `CERTIFIED (awaiting manual Publish now)`, a person decides whether to
   press **Publish now** in Partner Center. No workflow does this.

## Local checks

Everything the workflows rely on runs offline:

```powershell
./tools/release/Test-ReleaseChecks.ps1          # package checks, 12 cases
./tools/release/Test-StoreSubmissionUpdate.ps1  # publishing hold, 12 cases
./tools/release/Get-SubmissionState.ps1 -SelfTest        # status mapping, 18 cases
./tools/release/Assert-NoPendingSubmission.ps1 -SelfTest # in-flight guard, 19 cases
```

## Sources

* [Microsoft Store Developer CLI (MSIX) — overview](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/overview)
* [Microsoft Store Developer CLI — commands](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands)
* [Publish app updates to Microsoft Store with GitHub Actions](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/github-actions)
* [Manage app submissions (submission resource, `targetPublishMode`, status values)](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions)
* [Add and manage app credentials in Microsoft Entra ID](https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-credentials)
* [microsoft/microsoft-store-apppublisher](https://github.com/microsoft/microsoft-store-apppublisher)
