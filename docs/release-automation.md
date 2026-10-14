# Release automation

How a ServerAlyzer release goes from a tag to a Microsoft Store submission, what each
workflow is allowed to do, and what still needs a person.

The design principle throughout: **the pipeline can prepare and submit, but it can never
make the product public.** Publishing stays a human act.

## The three workflows

| workflow | trigger | talks to the Store | can publish |
| --- | --- | --- | --- |
| `release-build.yml` | tag `v*` builds and checks; a GitHub Release only by manual dispatch | no | no |
| `store-submit.yml` | manual dispatch only; provenance job, then the `microsoft-store` environment | yes | no |
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
   Then `Assert-PackageVersionFloor.ps1` on that version (see *Package version floor*), before
   anything is built.
4. Restore, non-incremental Release build, full test run, vulnerability scan.
5. Runs the release checks' own counterproof suites, so a green package check means
   something.
6. Builds the MSIX and refuses to continue if the build produced anything other than
   exactly one app package.
7. `Assert-PackageIdentity.ps1` — reads the package, not the source tree: identity name,
   publisher, version, architecture, app-level capabilities, payload hygiene, the exact set
   of executables, and that **every executable is PE subsystem 2 (Windows GUI)**.
8. `Assert-DistributionPrivacy.ps1` — opens everything about to be uploaded and fails closed
   if any file carries an absolute development path (see *Distribution privacy* below).
   It runs before the upload and before the GitHub Release, so a failure publishes nothing.
9. Writes the SHA-256 next to the package and uploads both as a workflow artifact.
10. Creates the GitHub Release **only if it does not already exist**, and by default
   **does not attach the MSIX**: the Microsoft Store is the distribution channel.

**The GitHub Release is never a side effect of a tag push (G2).** A push of `vX.Y.Z` runs every
step above up to the artifact upload, in a `build` job with **read-only** repository access. The
Release is created by a separate `release` job, which runs only on a **manual dispatch** with
`create_release: true` (default `false`), only after the `build` job succeeded, and is the only job
with `contents: write`. `attach_package` stays `false` by default. The MSIX name carries the commit
that was actually checked out (`git rev-parse HEAD`), not `github.sha`, which on a manual dispatch is
the commit of the branch the dispatch ran on.

### 2. `store-submit.yml`

Takes the artifact from a `release-build` run and submits it to the existing product.

It never builds. The package must arrive from a specific run id and its SHA-256 must match
what the operator types in, so the thing that gets certified is provably the thing that was
built and checked.

Inputs: the release tag (`vX.Y.Z`), the run id of that tag's `release-build` run, the package
version (`X.Y.Z.0`), the SHA-256 from that run, and `SUBMIT`.

Order of operations, and why:

0. **Provenance job (G4)**, with no environment, no reviewer prompt and no Store secret, only the
   job's `GITHUB_TOKEN` with `actions: read`. It reads the run, the tag ref (and the annotated tag
   object behind it) and a `tag...main` comparison from the GitHub API, and
   `Assert-ReleaseRunProvenance.ps1` fails closed unless the run is
   `.github/workflows/release-build.yml` of this repository (not a fork), `completed` with
   `success`, started by the **push of that tag** (`event push`, `head_branch` = tag), on exactly the
   commit the tag points at, that commit is contained in `main`, and the package version is the
   tag's version with Revision 0. Runs started by a manual dispatch are refused: the API records the
   dispatching branch's commit for them, not the tag they built, so they cannot be bound to the tag.
   The submit job `needs` this job, so a failure here stops everything before anyone is asked to
   approve the Store environment.
1. Confirmation phrase `SUBMIT`, plus shape checks on the version, digest and the tag commit handed
   over by the provenance job.
2. Download the artifact from the named run. Before any secret is used, with this checkout's gates:
   the version floor; the file name must be `ServerAlyzer_<version>_x64_<first 7 of the tag
   commit>.msix`; the full package identity check against the typed SHA-256; distribution privacy;
   and the **package runtime** check (`Assert-PackageRuntime.ps1`, REL.0C). The runtime check ships
   with the self-contained runtime change; on a branch where the script does not exist yet the step
   **fails** on purpose instead of skipping, so a framework-dependent package can never be submitted.
3. `msstore reconfigure` from environment secrets, passed to the step as environment values rather
   than written into the generated script.
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

**The run fails when `targetPublishMode` is not `Manual` (G6)**, after printing the report:
`Assert-ManualPublishMode.ps1`. A submission that is not on the publishing hold would publish
itself once certification passes; a mode changed in Partner Center while a submission sits in
certification used to produce only a warning. If the last *published* submission was made by hand
with another mode, the run stays red until the next pipeline submission (which is always `Manual`);
read the message, it names the submission status.

Artifact paths are also validated: exactly one regular `.msix`, directly inside `incoming`,
with a file name matching `\A[A-Za-z0-9._-]+\.msix\z`; reparse points and nested packages
are rejected. The upload step receives the path through `env`, revalidates it and passes
it as a quoted argument. Inputs, step outputs, other jobs' outputs (`needs.*`) and the event
payload (`github.event.*`) must not be interpolated into `run` scripts;
`Test-WorkflowRunInterpolation.ps1 -SelfTest` checks all three release workflows and proves
detection by reintroducing each kind of interpolation in memory.

**Actions are pinned to full commit SHAs (G8)** in the three release workflows, with the release
in a trailing comment (`uses: actions/checkout@<40 hex> # v4.4.0`). A tag or branch is a moving
reference, and these jobs hold Store secrets or write Releases. `Test-WorkflowActionPins.ps1
-SelfTest` fails on any other form. Dependabot keeps proposing updates for SHA-pinned actions;
review each one as a supply-chain change (the release notes and the diff of the action), and keep
the comment in step with the SHA. `ci.yml` (no secrets, read-only token) still uses major tags.

`Test-ReleaseWorkflowShape.ps1 -SelfTest` checks the wiring itself: the Release only from the
dispatch-only job, the provenance job before the Store environment and without secrets, every gate
(floor, name binding, identity, privacy, runtime) before the Store CLI is configured, and
`store-status` failing on a publish mode other than `Manual`. Each rule is proven by breaking it in
memory.

## Package version floor

`release/min-package-version.txt` tracks the minimum acceptable package version, currently
**1.1.7.0**. It exceeds the installed Developer version 1.1.2.2 and the local QA versions
1.1.3.0 through 1.1.6.0, preserving the upgrade path. This is the provisional REL.0
candidate, not a reservation for a commercial release; a future release may use 1.2.0.0.

`Assert-PackageVersionFloor.ps1` requires exactly four canonical decimal components,
MSIX component bounds and **Revision = 0** (a nonzero revision was rejected by Partner
Center). Missing, malformed or ambiguous floor files stop the run. Equality is allowed
for a retry of the same candidate; this tracked floor is not a live Store history query.

The release workflow checks the floor after tag/source coherence and before building or
uploading. The submission workflow checks it again with the current checkout's floor,
before any Store CLI or secret use, and verifies that the actual package identity matches
that checked version. An older artifact cannot bypass the current submission gate.

To raise the floor, review all previously used Developer, QA and Store versions, select a
strictly higher four-part version ending in `.0`, update the single version line in the
floor file and explain the reason in its comments. Never lower it to accept an old artifact.
Run the floor, tag and mutation suites below. The RC preparation updates the project and
both manifests together and creates the matching `vMAJOR.MINOR.PATCH` tag separately;
changing the floor alone does not bump those files or authorize a release.

## Distribution privacy

A build records where it ran. Without care, every Release DLL carried the PDB path of the
developer's checkout (the PE CodeView entry), and `ServerMonitor.App.dll` a `CallerFilePath`
constant: a user profile and folder layout, shipped inside the MSIX (`PRIV-PDBPATH`).

**The fix** is `src/Directory.Build.props`: Release builds of every project under `src/` set
`ContinuousIntegrationBuild`, which turns on the SDK's deterministic source paths. The git
checkout is mapped to `/_/` in the PE, the PDB and `CallerFilePath`, and Source Link is written
against the mapped paths, so the PDB still resolves to the exact commit on GitHub. Debug is not
affected: breakpoints and source stepping keep the real local paths — so a **Debug package must
never leave the machine**, and any package that is shared goes through the gate first. The file
chains to a repository-root `Directory.Build.props` if one is ever added (and only that one).

**The gate** is `tools/release/Assert-DistributionPrivacy.ps1`.

* *Channels:* zip containers are opened recursively and entry names are checked; every PE is
  parsed with System.Reflection.Metadata (CodeView PDB path, embedded portable PDB — decompressed
  and checked against its declared size — and every `#US` string, where `CallerFilePath` lands);
  every portable PDB is parsed (documents, then Source Link and the rest of its bytes); every file
  is then byte-scanned as UTF-8 and UTF-16 (both alignments).
* *Detectors:* absolute drive, UNC, profile (`\Users\`, `/home/`, `/Users/`) and CI runner paths,
  plus this machine's own profile, user name, computer/domain name, checkout and CI workspace, read
  from the environment at run time. A path made only of non-ASCII folder names is caught only by
  those environment literals, on the machine it came from.
* *Fail-closed:* no input, a missing input, an empty directory, anything it cannot parse (reported
  with its exception type or a fixed reason), a Windows/MSF PDB, nesting deeper than 4, a file or
  zip entry over 1 GiB or a run over 2 GiB decompressed (`-MaxEntryBytes`, `-MaxTotalBytes`), a
  missing required file, an internal error, and **two artifacts or two inputs with the same name**
  (so nothing keyed by a name — the allowlist, the report — can confuse them).
* *Redaction:* the output names the artifact, the channel, the category and a 12-hex SHA-256 token
  of the matched text. Any part of an artifact name (input, file or zip entry) that itself matches
  a detector is printed as `<redacted:token>`, in the console, in `-ReportPath` JSON and in the
  allowlist candidates. Errors never carry an exception message.

It runs in `release-build.yml` (before the upload), in `store-submit.yml` (before the Store), and
in CI on every PR twice: on the first-party Release binaries, and on a **packaged** Release build
made exactly as `release-build.yml` makes it (unsigned, nothing uploaded), with the allowlist — the
proof that the allowlist holds for the runner's SDK before a release depends on it.

**Third-party binaries** carry their vendors' build paths (Microsoft build agents, NuGet
authors' machines). `tools/release/distribution-privacy-allowlist.json` excuses each one by exact
file name plus either the SHA-256 of the very bytes that produced the finding or the exact path
tokens, for the categories reviewed; `reason` is a single string. Files this repository builds, and
environment matches, can never be allowlisted. After a dependency update the gate fails closed;
regenerate the candidates with `-AllowlistCandidatesPath` (written redacted, reason `REVIEW`, which
the loader refuses), review each entry, write its real reason, and commit it. Never widen a
category to make a run pass.

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
>
> The settings that make these restrictions hold (protecting `main`, a reviewer on
> `microsoft-store-status`, CODEOWNERS enforcement) are human steps, described with read-only
> verification commands in [`operations/release-security-setup.md`](operations/release-security-setup.md).

| secret | what it is |
| --- | --- |
| `AZURE_AD_TENANT_ID` | the Microsoft Entra tenant associated with the Partner Center account |
| `AZURE_AD_APPLICATION_CLIENT_ID` | Application (client) ID of the Entra app registration |
| `AZURE_AD_APPLICATION_SECRET` | client secret for that registration |
| `SELLER_ID` | the publisher/seller ID from Partner Center account settings |

These four names are the ones Microsoft's own documentation uses.

### The identity behind them

| | |
| --- | --- |
| Entra tenant | `ServerAlyzer.onmicrosoft.com` — dedicated to this project, nothing else lives in it |
| Tenant ID | `483ae5df-a88a-4256-9c93-5b419c9e312f` |
| Application | **ServerAlyzer Store CI** |
| Client ID | `3539a75c-3fca-42be-af76-c472706d5e8c` |
| Partner Center role | Manager (Windows), and only that role |
| Reply URL | `https://pedroloyola.com` — mandatory in the Partner Center creation form; unused, because the runtime authenticates with client credentials and never performs an interactive redirect |

`SELLER_ID` comes from Partner Center → Account settings → **Legal info**.

## Rotating the client secret

Entra caps client secrets at 24 months and Microsoft recommends under 12; this one is
issued for **6 months**. Rotate it **before** it expires — an expired secret fails every
Store workflow at the `msstore reconfigure` step, and the error does not say "expired" in
an obvious way.

Overlap the old and new secret. Never revoke first:

1. **Create the new secret** in Entra → App registrations → ServerAlyzer Store CI →
   Certificates & secrets → New client secret. Description `GitHub Store CI <yyyy-mm>`,
   expiry 6 months. Copy the **Value**, not the Secret ID — they are different, and only
   the Value works. It is shown once.
2. **Update `AZURE_AD_APPLICATION_SECRET`** in *both* environments, `microsoft-store` and
   `microsoft-store-status`. Missing one leaves a workflow that fails later, in isolation,
   for no visible reason.
3. **Run `store-status.yml`** and confirm it authenticates and reports the real state. This
   is the proof the new secret works, and it writes nothing.
4. **Only then delete the old secret** in Entra.

Record the description and the expiry date. Never record the value — not in this
repository, not in an issue, not in a run log.

| description | created | expires |
| --- | --- | --- |
| `GitHub Store CI` | 2026-09-05 | **2027-03-04** |

## Running a release

1. Land the version bump on `main` and tag that commit (`vX.Y.Z`).
2. `release-build.yml` runs on the tag push. Record the **run id**, the package name and the
   SHA-256 from the run summary. No GitHub Release is created; if one is wanted, dispatch
   `release-build.yml` for the tag with `create_release: true` (it rebuilds and re-checks first).
3. Run `store-status.yml` and confirm the product is idle and on the publishing hold.
4. Dispatch `store-submit.yml` with the tag, the run id **of the tag-push run**, the package
   version, the SHA-256, and `SUBMIT`. The provenance job runs first; approve the
   `microsoft-store` environment only after it is green.
5. Watch with `store-status.yml`.
6. When it reports `CERTIFIED (awaiting manual Publish now)`, a person decides whether to
   press **Publish now** in Partner Center. No workflow does this.

## Local checks

Everything the workflows rely on runs offline:

```powershell
./tools/release/Test-PackageVersionFloor.ps1   # synthetic floor and revision cases
./tools/release/Test-TagVersion.ps1            # synthetic tag/source coherence cases
./tools/release/Test-VersionGuardMutations.ps1 # remove checks; require suite failures; rerun originals
./tools/release/Test-ReleaseChecks.ps1          # package checks, 12 cases
./tools/release/Test-DistributionPrivacy.ps1    # distribution privacy, 52 cases
./tools/release/Test-StoreSubmissionUpdate.ps1  # publishing hold, 12 cases
./tools/release/Get-SubmissionState.ps1 -SelfTest        # status mapping, 18 cases
./tools/release/Assert-NoPendingSubmission.ps1 -SelfTest # in-flight guard, 19 cases
./tools/release/Test-ReleaseRunProvenance.ps1             # release run provenance (G4), 35 cases
./tools/release/Test-ManualPublishMode.ps1                # publishing hold in store-status (G6), 9 cases
./tools/release/Test-HardeningMutations.ps1               # remove each G4/G6 check; require its case to fail
./tools/release/Test-WorkflowRunInterpolation.ps1 -SelfTest # no inputs/steps/needs/event in run blocks
./tools/release/Test-WorkflowActionPins.ps1 -SelfTest     # actions pinned by full SHA (G8)
./tools/release/Test-ReleaseWorkflowShape.ps1 -SelfTest   # G2/G4/G6/runtime wiring of the workflows
```

CI runs the version, provenance, publishing-hold, interpolation, pinning and wiring suites on every
pull request (job *Release build + vulnerability scan*), not only on release day.

## Sources

* [Microsoft Store Developer CLI (MSIX) — overview](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/overview)
* [Microsoft Store Developer CLI — commands](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands)
* [Publish app updates to Microsoft Store with GitHub Actions](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/github-actions)
* [Manage app submissions (submission resource, `targetPublishMode`, status values)](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions)
* [Add and manage app credentials in Microsoft Entra ID](https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-credentials)
* [microsoft/microsoft-store-apppublisher](https://github.com/microsoft/microsoft-store-apppublisher)
* [GitHub REST — workflow runs (`path`, `event`, `head_branch`, `head_sha`, `conclusion`)](https://docs.github.com/en/rest/actions/workflow-runs#get-a-workflow-run)
* [GitHub REST — git references and tag objects](https://docs.github.com/en/rest/git/refs) · [tags](https://docs.github.com/en/rest/git/tags)
* [GitHub — security hardening for GitHub Actions (pinning to a full-length commit SHA, untrusted input)](https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions)
* [.NET SDK MSBuild properties — `ContinuousIntegrationBuild`](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#continuousintegrationbuild)
* [C# compiler options — `Deterministic`, `PathMap`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/code-generation)
* [dotnet/sourcelink — deterministic builds and `ContinuousIntegrationBuild`](https://github.com/dotnet/sourcelink/blob/main/docs/README.md)
* [PE/COFF debug directory entries (CodeView, embedded portable PDB)](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Reflection.Metadata/specs/PE-COFF.md)
* [Portable PDB metadata (Document table, Source Link record)](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Reflection.Metadata/specs/PortablePdb-Metadata.md)
