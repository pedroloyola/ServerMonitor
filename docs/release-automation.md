# Release automation

How a ServerAlyzer release goes from a tag to a Microsoft Store submission, what each
workflow is allowed to do, and what still needs a person.

The design principle throughout: **the pipeline can prepare and submit, but it can never
make the product public.** Publishing stays a human act. Since REL.0E (P-1) preparing and
submitting are also separate human decisions: `store-submit` only **stages** a draft, and
only `store-certify`, dispatched and approved separately, sends it to certification.

## The four workflows

| workflow | trigger | talks to the Store | commits to certification | can publish |
| --- | --- | --- | --- | --- |
| `release-build.yml` | tag `v*` builds and checks; a GitHub Release only by manual dispatch | no | no | no |
| `store-submit.yml` | manual dispatch only (`STAGE`); provenance job, then the `microsoft-store` environment | yes: uploads into a draft | **no** | no |
| `store-certify.yml` | manual dispatch only (`CERTIFY <version>`); provenance job, then the `microsoft-store` environment | yes: commits the staged draft | yes | no |
| `store-status.yml` | manual dispatch only, `microsoft-store-status` environment | reads only | no | no |

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
4. Installs exactly the SDK `global.json` pins (`global-json-file`; see *.NET runtime servicing*),
   then restore, non-incremental Release build, full test run, vulnerability scan.
5. Runs the release checks' own counterproof suites, so a green package check means
   something.
6. Builds the MSIX and refuses to continue if the build produced anything other than
   exactly one app package.
7. `Assert-PackageIdentity.ps1` — reads the package, not the source tree: identity name,
   publisher, version, architecture, app-level capabilities, payload hygiene, the exact set
   of executables, and that **every executable is PE subsystem 2 (Windows GUI)**.
8. `Assert-PackageRuntime.ps1` — also reads the package. Both executables are **self-contained
   for .NET**: each `runtimeconfig.json` has `includedFrameworks` and no shared-framework reference,
   and each `deps.json` is RID-specific and names the same runtime pack. Both are on the same
   runtime version. Every asset their `deps.json` names is present next to them, and so are the
   runtime files. The Windows App Runtime is still a **framework package**: the `PackageDependency`
   is declared, and `Microsoft.WindowsAppRuntime.dll` is not in the payload. Windows does not ship
   .NET 10 and the Store has no .NET framework package, so a framework-dependent package would not
   start on a clean machine (REL.0C). The bundled `Microsoft.NETCore.App` must also be at or above
   the **runtime floor** in `release/min-dotnet-runtime.txt`, and the runtime files themselves
   (`hostfxr`, `hostpolicy`, `System.Private.CoreLib`, `System.Runtime`) must report that same
   version in their version resource (see *.NET runtime servicing*).
   Then `Assert-PackageResources.ps1`: the package's default (fallback) language — the first manifest
   `<Resource Language>` and the `scoreAsDefault="1.0"` candidate of each string in `resources.pri` —
   is **en-US**, and every `ms-resource:` string of the manifest (`AppDescription` always) resolves,
   non-empty, in en-US, pt-PT and pt-BR. It dumps `resources.pri` with the build's own `makepri`
   (Microsoft.Windows.SDK.BuildTools, else the Windows SDK) and fails closed when there is none.
9. `Write-PackageTrace.ps1` — the **build record**: commit, SDK (must equal the `global.json` pin),
   bundled .NET runtime and floor (re-proven from the package), Windows App Runtime dependency and
   SHA-256, written as `<package>.build.json` next to the package and into the run summary.
10. `Assert-DistributionPrivacy.ps1` — opens everything about to be uploaded (the build record
   included) and fails closed if any file carries an absolute development path (see *Distribution
   privacy* below). It runs before the upload and before the GitHub Release, so a failure publishes
   nothing.
11. Writes the SHA-256 next to the package and uploads the package, the checksum and the build
   record as a workflow artifact.
12. Creates the GitHub Release **only if it does not already exist**, and by default
   **does not attach the MSIX**: the Microsoft Store is the distribution channel.

**The GitHub Release is never a side effect of a tag push (G2).** A push of `vX.Y.Z` runs every
step above up to the artifact upload, in a `build` job with **read-only** repository access. The
Release is created by a separate `release` job, which runs only on a **manual dispatch** with
`create_release: true` (default `false`), only after the `build` job succeeded, and is the only job
with `contents: write`. `attach_package` stays `false` by default. The MSIX name carries the commit
that was actually checked out (`git rev-parse HEAD`), not `github.sha`, which on a manual dispatch is
the commit of the branch the dispatch ran on.

### 2. `store-submit.yml` (stage only)

Takes the artifact from a `release-build` run and **stages** it in a Partner Center draft of the
existing product. It never commits: there is no commit step in the workflow, and
`Test-ReleaseWorkflowShape.ps1` fails CI if one is added (P-1).

It never builds. The package must arrive from a specific run id and its SHA-256 must match
what the operator types in, so the thing that gets certified is provably the thing that was
built and checked.

Inputs: the release tag (`vX.Y.Z`), the run id of that tag's `release-build` run, the package
version (`X.Y.Z.0`), the SHA-256 from that run, and `STAGE`.

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
1. Confirmation phrase `STAGE`, plus shape checks on the version, digest and the tag commit handed
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
5. **`Assert-NoPendingSubmission.ps1`.** This is the most important guard in the pipeline,
   because of what `msstore publish` does to a draft that already exists (see *What `msstore
   publish --noCommit` does to an existing draft* below): it **deletes** it. So the workflow
   refuses to run at all while anything is in flight (anything but `None`, `Published`,
   `Canceled`), and fails closed on unparseable output. A `PendingCommit` draft (typically one
   this workflow staged and a person is completing) gets an explicit refusal that says what to do
   instead: certify it with `store-certify`, or delete it by hand in Partner Center to stage again.
   The workflow calls the guard **exactly** as `Assert-NoPendingSubmission.ps1 -SubmissionPath
   before.json`, with its default allowed statuses; the shape guard refuses any other call (an extra
   `-AllowedStatuses`), and the guard's self-test runs through that default and pins it, so neither
   side can quietly let a `PendingCommit` draft through (PR #56 Relay M-1).
6. `msstore publish <msix> -id <productId> --noCommit` — uploads the package and leaves the
   submission in draft. This must come **before** metadata edits, because `publish`
   recreates the draft.
7. `msstore submission get`, then `Update-StoreSubmission.ps1`, which asserts
   `targetPublishMode == Manual`, asserts visibility, asserts the draft carries the
   expected package version, and writes the release notes from `release/store/`.
8. `msstore submission update` applies the metadata.
9. **Read back from the Store and prove the draft is staged, not committed**
   (`Assert-StagedSubmission.ps1`, on what the Store actually stored, not on the file that was
   sent): status exactly `PendingCommit`, `targetPublishMode` `Manual`, visibility unchanged, the
   expected package present **under the file name this run uploaded**
   (`ServerAlyzer_<version>_x64_<first 7 of the tag commit>.msix`, which `msstore publish` writes into
   `applicationPackages[].fileName`) and **no newer live package**. It also prints, per language, the
   lengths and counts of the listing fields and whether the release notes equal the repository's.
10. **Hand over.** The run summary reports the draft's **submission id**, the package and the next
    steps. Nothing has been sent to certification. The localized listing and screenshots are applied
    by a person, to this draft, only now (after the upload phase), and reviewed in Partner Center:
    see *Staged release: the human procedure*.

#### What `msstore publish --noCommit` does to an existing draft

Sources: the CLI documentation (DOC) and the CLI source, `microsoft/msstore-cli` `main` at
`5f15f06` (2026-10-06), `IStorePackagedAPIExtensions.PublishAsync` and the `submission` commands
(VERIFIED by reading the source; no Store call was made).

- `--noCommit` is **not a dry run**: it creates or reuses a draft, uploads the package and updates the
  draft; it only skips the final commit (DOC: "`--noCommit` uploads the package and updates the
  draft; it isn't a dry run").
- **Existing draft + a published submission** (our product): `publish` **deletes the pending draft**
  and creates a new one from the last published submission, discarding everything staged in it —
  listing text, screenshots, release notes, notes for certification (DOC and source:
  `if (pendingSubmissionId != null && lastPublishedSubmittion != null) DeleteSubmissionAsync(...)`).
  There is no prompt and no option to keep it. `--noCommit` changes nothing here.
- Existing draft and **no** published submission (first submission only): the draft is reused, or
  deleted and recreated if its upload URL expired or it is `CommitFailed`. Not our case.
- `msstore submission get` returns the pending draft if there is one, **otherwise the last published
  submission**; `msstore submission publish` commits whatever that lookup returns. It takes no
  submission id. Hence `Assert-StagedSubmission.ps1` proves `status == PendingCommit` and the id as
  the last read before any commit.
- `msstore submission update` replaces the **whole** submission with the JSON it is given (DOC), which
  is why `Update-StoreSubmission.ps1` starts from `submission get` and changes only the release notes.

Guarding against it: `Assert-NoPendingSubmission.ps1` before the upload (stage refuses while any
draft exists), the shared `store-submit` concurrency group (stage and certify never overlap), and no
workflow ever calls `msstore publish` except that one upload with `--noCommit`. A draft created in
Partner Center *between* the in-flight check and the upload (seconds) would still be deleted; the
procedure below says not to edit the product in Partner Center while a stage run is in progress.

**Gate on the first real dispatch (open, pre-existing Medium):** the Store CLI version is not pinned.
`microsoft-store-apppublisher` installs the **latest** `msstore`, and that CLI receives the Store
client secret in `reconfigure` and decides what is deleted or committed. Before the first real
`store-submit` run, a person chooses a reviewed `msstore-cli` release and either pins the action's
`version` input to it or adds a step between *Setup* and *Configure* that compares `msstore --version`
with an allowlist in the repository and fails closed (Vigil, PR #56). The behaviour described above is
the one documented today and read from `5f15f06`; re-read it whenever the pinned version changes.
This is a gate on the dispatch, not on merging the workflows.

### 2b. `store-certify.yml` (the certification gate)

Commits to certification the draft that `store-submit` staged, after a person completed and
reviewed it. A **second dispatch**, with its own inputs, its own confirmation phrase and its own
approval of the `microsoft-store` environment (each run is approved separately). It uploads
nothing, edits nothing and deletes nothing (`Test-ReleaseWorkflowShape.ps1` enforces this).

Inputs: the release tag, the run id of that tag's `release-build` run, the package version, the
**submission id** from the stage run's summary, and the phrase **`CERTIFY <version>`** (for example
`CERTIFY 1.1.7.0`, so a phrase typed for another release cannot certify this one).

0. The same provenance job (G4) as `store-submit`, before the environment is reached. It also refuses a
   malformed `submission_id` or a phrase other than `CERTIFY <version>` (and `store-submit`'s
   provenance job a phrase other than `STAGE`), so a doomed run fails **before** anyone is asked to
   approve the environment.
1. Confirmation phrase, version, submission id and tag commit checked again before any secret is used;
   the version floor is re-checked. `SUBMISSION_ID` comes **only** from the dispatch input: the shape
   guard refuses a second declaration, an assignment, a `GITHUB_ENV` write or an environment API call
   that could rebind it to the draft itself (PR #56 Relay W4).
2. `msstore reconfigure`, `msstore apps get` (identity), as in `store-submit`.
3. **`Assert-StagedSubmission.ps1 -ExpectedSubmissionId`**, on a fresh `msstore submission get`,
   immediately before the commit: the pending draft must be exactly the staged submission id, still
   `PendingCommit`, on the publishing hold (`Manual`), with the visibility unchanged, carrying the
   package version **as the file uploaded from the tag commit** that the provenance job proved
   (`TAG_COMMIT`, PR #56 Vigil L-1; another package of the same version under another name is refused)
   and no newer live package. Otherwise nothing is committed. The listing report is printed again
   (what is about to be certified).
4. `msstore submission publish` — the only commit in the pipeline. The publishing hold stays in
   force, so certification never means publication.
5. Report the resulting state.

No step of either Store job may be conditional (other than the final report) or `continue-on-error`,
so a later Store call can never run past a failed guard.

**Limits of the shape guard.** `Test-ReleaseWorkflowShape.ps1` reads the workflow **text**. It catches
regressions written the ordinary way: a commit step put back, `--noCommit` dropped, a guard removed,
reordered, made conditional or widened. It does not try to recognise a deliberately disguised command,
for example a verb held in a variable (`msstore submission $verb`) or a direct REST call to the
submission's `commit` endpoint (PR #56 Relay W1/W2). Against a hostile edit the controls are the pull
request review and the branch protection on `main`, which the Store environment's deployment rule
depends on. Chasing disguises with more patterns is deliberately not attempted.

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

**The run fails when a submission in flight is not on the publishing hold (G6)**, after printing
the report: `Assert-ManualPublishMode.ps1`.
- **In flight** (`PendingCommit`, `CommitStarted`, `PreProcessing`, `Certification`, `Release`,
  `PendingPublication`, `Publishing`), or any status it doesn't know, including an empty one:
  `targetPublishMode` must be `Manual`, or the run fails. Such a submission would publish itself once
  certification passes, and a mode changed in Partner Center during certification used to produce
  only a warning.
- **Idle** (`None`, `Published`, `Canceled` and the five `…Failed` states): a different mode on that
  last submission is reported, not failed. Nothing is waiting to be published, and the next
  submission's mode is set and verified by `store-submit` itself.

So `store-status` stays usable as the "product is idle" check before a submission.

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
`store-status` failing when a submission in flight is not on the publishing hold. Each rule is
proven by breaking it in memory.

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
proof that the allowlist holds for the runner's SDK before a release depends on it. That CI
package job also runs, on the same package, `Assert-PackageVersionFloor.ps1` and
`Assert-PackageIdentity.ps1` (with the version the repository declares: `Get-RepositoryPackageVersion.ps1`
reads the csproj `FileVersion` and both manifests and refuses any disagreement), `Assert-PackageRuntime.ps1`, `Assert-PackageResources.ps1`
and `Write-PackageTrace.ps1`, so every gate `release-build.yml` applies to the package is exercised on
every PR, not first on release day.

**Third-party binaries** carry their vendors' build paths (Microsoft build agents, NuGet
authors' machines). `tools/release/distribution-privacy-allowlist.json` excuses each one by exact
file name plus either the SHA-256 of the very bytes that produced the finding or the exact path
tokens, for the categories reviewed; `reason` is a single string. Files this repository builds, and
environment matches, can never be allowlisted. After a dependency update the gate fails closed;
regenerate the candidates with `-AllowlistCandidatesPath` (written redacted, reason `REVIEW`, which
the loader refuses), review each entry, write its real reason, and commit it. Never widen a
category to make a run pass.

The package carries the **.NET runtime** (REL.0C), so its native binaries (`coreclr.dll`,
`clrjit.dll`, `hostfxr.dll`, `mscordaccore*.dll`, `msquic.dll`, …) carry Microsoft build-agent paths.
Their allowlist entries are bound to path tokens, which stayed identical across runtime patches
10.0.11 to 10.0.13. Two things can change with a .NET patch: the file name
`mscordaccore_amd64_amd64_<file version>.dll`, and a token of `Microsoft.DiaSymReader.Native.amd64.dll`
(10.0.11 had its own; 10.0.12 and 10.0.13 share theirs). The SDK is pinned, so this happens only
in the pull request that moves `global.json` to a new SDK: its package job fails closed on those
files, and only on those. Review the candidates and add them in that same pull request. When the
runtime floor rises, **prune** the entries below it: `Test-RuntimeServicing.ps1` (S4) refuses a
versioned `mscordaccore` entry under the floor (the runtime patch is the file version's build
number divided by 100: `10.0.1326.x` is 10.0.13) and one whose `reason` does not name its runtime.
Such a package is refused by the runtime check anyway, so a stale entry could only widen the gate.

## .NET runtime servicing

Decision: ADR-017, amendment REL.0C (accepted 2026-10-14). The production MSIX carries its own .NET
runtime (self-contained); the Windows App Runtime stays a framework package that the Store keeps up
to date. **Windows Update and Microsoft Update do not patch a self-contained .NET runtime**: an
installed package keeps the runtime it was built with until we ship a new package. Servicing the
runtime is therefore a release obligation of this project.

**Controls.**

* **SDK pinned.** `global.json` pins one exact SDK (`10.0.402`, `rollForward: disable`,
  `allowPrerelease: false`). The SDK decides which runtime pack a self-contained build bundles
  (SDK 10.0.402 bundles `Microsoft.NETCore.App` 10.0.13; SDK 10.0.400 bundled 10.0.11), so a commit
  always builds with the same runtime. Every `actions/setup-dotnet` step in `ci.yml` and
  `release-build.yml` installs from `global-json-file: global.json`; none names a version of its own.
  `disable` rather than `latestPatch`: the runner image may already carry a newer SDK of the same
  band, and `latestPatch` would pick it and silently change the bundled runtime.
* **Runtime floor.** `release/min-dotnet-runtime.txt` holds the lowest runtime a package may bundle.
  `Assert-PackageRuntime.ps1` fails closed below it (CI package job, `release-build.yml`, and again in
  `store-submit.yml` with the **current** checkout's floor, so an older artifact that is still
  downloadable cannot be submitted once the floor has moved past it).
* **Build record.** Every packaged build writes what it is made of (see step 9 above), so "which
  runtime is in the package the Store has?" is answered from the artifact, not from memory.
* **The vulnerability scan does not cover the runtime.** `dotnet list package --vulnerable` reports
  NuGet package references; the runtime pack a self-contained build bundles is not one of them. The
  floor and the monthly review below are the control for the runtime.
* **Repository wiring** is itself checked on every PR: `Test-RuntimeServicing.ps1 -SelfTest` (SDK pin,
  `setup-dotnet` wiring, floor file, allowlist vs floor, build record placement).

**Process.**

* **Monthly:** .NET servicing releases ship monthly (normally on Patch Tuesday). Each month, read
  the .NET 10 release notes and decide whether to move. Target: a package with the new runtime
  submitted **within 7 days** of the regular release, when QA and distribution allow (the human
  decides; a Store submission stays a human action).
* **Critical CVE in the runtime:** evaluate **immediately**, not at the next monthly review.
* **Moving to a new runtime (one pull request):**
  1. Set `global.json` `version` to the SDK that bundles it (the release notes list SDK and runtime
     together).
  2. Raise `release/min-dotnet-runtime.txt` to that runtime, with a comment naming the release.
  3. Let the CI package job fail on the version-named runtime files; review the allowlist
     candidates, add the new `mscordaccore_amd64_amd64_<file version>.dll` entry (its `reason` names
     `Microsoft.NETCore.App <version>)`), prune the entries below the new floor.
  4. Green CI, review, merge, then the normal release path (version bump, tag, `release-build`,
     `store-submit`).
* **Local consequence:** with `rollForward: disable`, building this repository needs exactly the
  pinned SDK. On a development machine use the SDK installed machine-wide
  (`C:\Program Files\dotnet\dotnet.exe`, which has 10.0.402); an older user-scoped SDK (for example
  10.0.400 under `~/.dotnet`) now stops with "A compatible .NET SDK was not found". Install the new
  SDK on development machines in the same change that moves the pin.

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
   SHA-256 from the run summary, and keep the build record (SDK, bundled .NET runtime, commit) with
   the release notes. No GitHub Release is created; if one is wanted, dispatch
   `release-build.yml` for the tag with `create_release: true` (it rebuilds and re-checks first).
3. Run `store-status.yml` and confirm the product is idle and on the publishing hold.
4. **Stage:** dispatch `store-submit.yml` with the tag, the run id **of the tag-push run**, the
   package version, the SHA-256, and `STAGE`. The provenance job runs first; approve the
   `microsoft-store` environment only after it is green. Record the **submission id** from the run
   summary. Nothing has gone to certification.
5. **Complete and review the draft** by hand: *Staged release: the human procedure* below (backup,
   apply, verify).
6. **Certification GO:** dispatch `store-certify.yml` with the tag, the same run id, the package
   version, the submission id and `CERTIFY <version>`. Approve the environment after the
   provenance job is green.
7. Watch with `store-status.yml`.
8. When it reports `CERTIFIED (awaiting manual Publish now)`, a person decides whether to
   press **Publish now** in Partner Center (**publication GO**). No workflow does this.

Three separate human decisions, each with its own action: stage (`STAGE` + environment approval),
certify (`CERTIFY <version>` + a second environment approval), publish (Partner Center only).

## Staged release: the human procedure

For a release whose listing (text and/or screenshots) changes, or whenever the draft should be
reviewed before certification. The repository holds the content: text in
`release/store/<lang>/` (`description.md`, `short-description.txt`, `features.txt`,
`keywords.txt`, `whats-new.txt`, `screenshot-captions.txt`; `support.txt` is a fill-in sheet, and
its GATED privacy line is not filled until `PRIVACY.md` is rewritten), screenshots in
`release/store-screenshots/<lang>/NN-slug.png` with the caption of the matching `NN-slug` line.

**Rule while a draft exists:** do not run `store-submit` again (it refuses, because its upload
would delete the draft), and do not edit the product in Partner Center while a stage run is in
progress.

| step | who | action | idempotent? | evidence |
| --- | --- | --- | --- | --- |
| 1. Backup | person, read-only | **Before staging**, with the product idle: Partner Center → app → *Store listings* → **Export listings** (CSV of the published listing). Keep it in private evidence (`.boss/evidence/<release>/`), never in the public repository | yes (export only reads) | CSV + date |
| 2. Stage | workflow `store-submit` | Upload, release notes, read-back proof (see §2). The draft is `PendingCommit` | no: a second run **refuses** while the draft exists. To stage again: delete the draft (step R1), then rerun | run id, submission id |
| 3a. Text | person, in **this draft** | Per language, paste each field from `release/store/<lang>/` into the listing form (one feature per line, one keyword per line). Touch nothing else: not pricing/availability, properties, age ratings, packages or submission options | yes (setting the same value twice changes nothing) | — |
| 3b. Screenshots | person, in this draft | Per language: remove the old screenshots not on the list, upload `01…NN` **in order** with the caption of the matching `NN-slug` line. Count and compare names before uploading: Partner Center does not de-duplicate | only if you compare first | — |
| 4. Verify | person, read-only | *Submission options*: **Publish manually** (the hold) still selected; all three languages present; the package version on *Packages*; **Export listings** of the draft and diff it against the repository text; `store-status` reports `DRAFT (not submitted)` | yes | draft CSV + diff |
| 5. Certify | workflow `store-certify` (**certification GO**) | `CERTIFY <version>` + submission id; the run re-proves the draft (id, `PendingCommit`, hold, visibility, package) and commits it. Check that the listing report in its log matches what you reviewed | yes: once committed, the draft is no longer `PendingCommit`, so a second run refuses | run id, state `SUBMITTED` |
| 6. Publish | person (**publication GO**) | After certification: **Publish now** in Partner Center | — | — |

Alternative to 3a, **Import listings (CSV) into the draft**: start from the CSV exported for **this
draft** (so every column is explicit), replace only the columns that come from the repository, and
import with the PNG folder. HYPOTHESIS until confirmed in Partner Center on a draft: an import
overwrites the fields present and treats empty cells as "delete" (H-1), and screenshots referenced
in the CSV replace the language's set (H-2). Never import a CSV built from scratch.

### Rollback

| when | action (by a person) | effect |
| --- | --- | --- |
| R1. Before certification (steps 2–4) | Partner Center → the draft → **Delete** | Customers see no change. The next `store-submit` starts a new draft from the last published submission (old listing) |
| R2. The draft content is wrong, the package is fine | Fix it in the draft (or import the step-1 CSV into the draft), then repeat step 4 | Draft only |
| R3. `store-certify` refused | Nothing was committed. Read the reason (another draft id, status, hold, visibility, package); fix it in Partner Center or restage (R1 + step 2), then certify again | — |
| R4. Committed, in certification | **Cancel submission** in Partner Center | The published listing does not change |
| R5. Certified, waiting for Publish now | Do not publish; cancel it | Same, thanks to the publishing hold |
| R6. Already published | A listing-only submission that **imports the step-1 CSV** (plus the old screenshots if needed) | Restores the previous listing |

## Local checks

Everything the workflows rely on runs offline:

```powershell
./tools/release/Test-PackageVersionFloor.ps1   # synthetic floor and revision cases
./tools/release/Test-TagVersion.ps1            # synthetic tag/source coherence cases
./tools/release/Test-VersionGuardMutations.ps1 # remove checks; require suite failures; rerun originals
./tools/release/Test-ReleaseChecks.ps1          # package checks, 12 cases
./tools/release/Test-PackageRuntime.ps1         # .NET self-contained package runtime + runtime floor, 58 cases
./tools/release/Test-PackageTrace.ps1           # package build record, 17 cases
./tools/release/Test-RuntimeServicing.ps1 -SelfTest # SDK pin, setup-dotnet wiring, floor, allowlist vs floor, build record + CI identity/floor wiring, self-tests only via the runner and ending in `exit 0`, 29 mutations
./tools/release/Invoke-ReleaseSelfTests.ps1 -SelfTest # the runner catches a leaked exit code, a throw, a non-zero exit, 8 cases
./tools/release/Test-RepositoryPackageVersion.ps1 # csproj + both manifests declare one package version, 9 cases
./tools/release/Test-PackageResources.ps1       # en-US default + manifest strings in 3 languages (makepri stubbed), 23 cases
./tools/release/Test-DistributionPrivacy.ps1    # distribution privacy, 52 cases
./tools/release/Test-StoreSubmissionUpdate.ps1  # publishing hold, 12 cases
./tools/release/Get-SubmissionState.ps1 -SelfTest        # status mapping, 18 cases
./tools/release/Assert-NoPendingSubmission.ps1 -SelfTest # in-flight guard through its default allowed statuses (pinned), 20 cases
./tools/release/Assert-StagedSubmission.ps1 -SelfTest    # staged draft (P-1): PendingCommit, id, hold, visibility, package + tag-bound file name, 36 cases
./tools/release/Test-StoreStageCertify.ps1                # weaken each staged-draft / in-flight check in a copy; the named case must fail, 15 mutations
./tools/release/Test-ReleaseRunProvenance.ps1             # release run provenance (G4), 40 cases
./tools/release/Test-ManualPublishMode.ps1                # publishing hold in store-status (G6), 20 cases
./tools/release/Test-HardeningMutations.ps1               # weaken each G4/G6 check and the pin/wiring guards; require detection
./tools/release/Test-WorkflowRunInterpolation.ps1 -SelfTest # no inputs/steps/needs/event in run blocks
./tools/release/Test-WorkflowActionPins.ps1 -SelfTest     # actions pinned by full SHA (G8)
./tools/release/Test-ReleaseWorkflowShape.ps1 -SelfTest   # G2/G4/G6/runtime + P-1 stage/certify wiring, 41 mutations
```

CI runs the version, provenance, publishing-hold, submission-update, in-flight, staged-draft, interpolation,
pinning and wiring suites on every pull request (job *Release build + vulnerability scan*), not only on release day.

**A self-test passes only if it exits 0.** The workflows never call a `Test-*.ps1` directly: they hand
the list to `Invoke-ReleaseSelfTests.ps1`, which runs each one in its own `pwsh`, wrapped exactly like an
Actions step (`$ErrorActionPreference = 'stop'`, then `exit $LASTEXITCODE`), and fails on any exit code
other than 0. A suite that prints its success line but leaks `$LASTEXITCODE` from a stub or native
command (PR #53 B-1, `Test-PackageResources.ps1`) is named and fails the run, instead of failing a release
build on tag day. Every self-test also ends with an explicit `exit 0`. `Test-RuntimeServicing.ps1` (S7)
enforces both.

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
* [global.json overview (`version`, `rollForward`, `allowPrerelease`)](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)
* [actions/setup-dotnet — `global-json-file`](https://github.com/actions/setup-dotnet)
* [.NET release notes (SDK and runtime per servicing release)](https://github.com/dotnet/core/tree/main/release-notes) · [.NET support policy](https://dotnet.microsoft.com/platform/support/policy/dotnet-core)
* [.NET SDK MSBuild properties — `ContinuousIntegrationBuild`](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#continuousintegrationbuild)
* [C# compiler options — `Deterministic`, `PathMap`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/code-generation)
* [dotnet/sourcelink — deterministic builds and `ContinuousIntegrationBuild`](https://github.com/dotnet/sourcelink/blob/main/docs/README.md)
* [PE/COFF debug directory entries (CodeView, embedded portable PDB)](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Reflection.Metadata/specs/PE-COFF.md)
* [Portable PDB metadata (Document table, Source Link record)](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Reflection.Metadata/specs/PortablePdb-Metadata.md)
