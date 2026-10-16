# Release 1.1.7 preparation (REL.0E, proposal)

Proposal only. Nothing here has been applied: no ruleset, no tag, no Store or Partner Center
operation. Labels: **VERIFIED** (checked read-only on 2026-10-16), **HYPOTHESIS** (not proven),
**NOT_RUN**, **NEEDS HUMAN**. The release follows the *portal-only release* in
`docs/release-automation.md` (F-1 accepted).

## 1. Tag ruleset for `v*` (F-4)

Today the repository has one ruleset, `main` (branch, `~DEFAULT_BRANCH`: deletion,
non_fast_forward, pull_request, required_status_checks, no bypass). **No rule covers tags**
(VERIFIED, `GET /repos/{repo}/rulesets`). Anyone with write access, or a workflow token with
`contents: write`, could move or delete a release tag, and a tag-push `release-build` run would then
build whatever the tag points at.

Two rulesets, because the bypass of one ruleset applies to all of its rules:

| file | rules | bypass | effect |
| --- | --- | --- | --- |
| `release/github/tag-ruleset-v-immutable.json` | `update`, `deletion`, `non_fast_forward` | **none** | No one, the admin included, can move, force-push or delete a `v*` tag while the ruleset is active |
| `release/github/tag-ruleset-v-create.json` | `creation` | repository role **admin** (`actor_id` 5), `always` | Only an admin can create a `v*` tag |

Undoing a published tag then needs a deliberate, visible step (disable the ruleset), never a
`git push --force`. The existing tags `v1.0.0`, `v1.1.0`, `v1.1.1` become immutable too.

HYPOTHESIS: `actor_id` 5 is the built-in *Repository admin* role. The REST reference documents
`RepositoryRole` as an actor type but not the ids. After applying, confirm in the web UI that the
bypass list of `release-tags-create-admin-only` reads *Repository admin*; if not, fix it there.

**Apply (person with admin rights, not an agent):**

```powershell
$R = 'pedroloyola/ServerMonitor'
gh api --method POST "repos/$R/rulesets" --input release/github/tag-ruleset-v-immutable.json
gh api --method POST "repos/$R/rulesets" --input release/github/tag-ruleset-v-create.json
```

**Verify (read-only):**

```powershell
gh api "repos/$R/rulesets" --jq '.[] | {id, name, target, enforcement}'
# expected: main (branch), release-tags-immutable (tag, active), release-tags-create-admin-only (tag, active)
gh api "repos/$R/rulesets/<id>" --jq '{target, enforcement, conditions, bypass_actors, rules: [.rules[].type]}'
# immutable: include ["refs/tags/v*"], bypass_actors [], rules [update, deletion, non_fast_forward]
# create:    include ["refs/tags/v*"], bypass_actors [{RepositoryRole, always}], rules [creation]
```

Functional check after the release tag exists (it changes nothing when refused):
`git push origin :refs/tags/v1.1.7` must be **refused** by `release-tags-immutable`. Do not run it
before the ruleset is confirmed active.

## 2. Which commit the tag `v1.1.7` points at

**Decision: `v1.1.7` → `1ff0fe3744e5c4f615af076a6ae674226a6a2665`, not the current `main`.**

Reasons:

1. 1ff0fe3 is the commit the 1.1.7.0 RC was built, tested and QA'd from (RC SHA-256
   `7c9915396ee3666aa506e40b77aa5c958eb32c4468fcb15e0b6eeffd9a81b909`). Tagging it keeps one
   commit for "what was tested" and "what ships", and the package name
   `ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix` names it.
2. `main` after it changes no product code (VERIFIED: `git diff --name-only 1ff0fe3 1f13cc8` lists
   only `docs/`, `release/store/`, `tests/`, `.github/` and `tools/release/`; nothing under `src/`).
   No `.csproj`, `.props`, `.targets` or manifest reads `release/store/` (VERIFIED), so the listing
   text is not in the package. A tag on `main` would build the same product under another name and
   make the QA evidence point at a commit that was never QA'd.
3. The provenance gate accepts it: the tag commit must be *contained in* `main` (compare
   `tag...main`: `identical` or `ahead`, `behind_by` 0). VERIFIED today:
   `1ff0fe3...1f13cc8` → `status ahead, ahead_by 15, behind_by 0`.

**The floating `msstore` action does not matter for this tag.** A tag push runs the workflow file
*of the tagged commit*. `release-build.yml` at 1ff0fe3 never installs or calls `msstore`
(VERIFIED: no `msstore` reference in that file); it only *reads* the old `store-*.yml` in its
`Test-ReleaseWorkflowShape` self-test, which checks the shape of those files at 1ff0fe3 and runs
nothing. Every workflow that runs `msstore` is dispatched from `main` and uses `main`'s file, so the
pinned installer from #66. On the portal-only path only `store-status` (read-only) runs.

What the 1ff0fe3 run does **not** get from `main`: the `Install-MsStoreCli.ps1 -SelfTest` line (not
needed for build-only) and the test-only flake fixes of #68. If `Test (Release)` fails on one of
those known flakes, use **Re-run failed jobs** on the same run: the run id, `event push`,
`head_branch` and `head_sha` stay the same, and provenance reads the latest attempt's conclusion
(GitHub behaviour, HYPOTHESIS here: not exercised). If it fails again, STOP: do not move the tag.

### P1. Tag and build (person, after the GO)

```powershell
git fetch origin --tags
git rev-parse 1ff0fe3744e5c4f615af076a6ae674226a6a2665^{commit}   # 1ff0fe3744e5c4f615af076a6ae674226a6a2665
git merge-base --is-ancestor 1ff0fe3744e5c4f615af076a6ae674226a6a2665 origin/main; $LASTEXITCODE   # 0
git ls-remote --tags origin v1.1.7                                    # empty: the tag does not exist yet
git show 1ff0fe3:src/ServerMonitor.App/Package.appxmanifest | Select-String 'Version="1.1.7.0"'
git show 1ff0fe3:src/ServerMonitor.App/ServerMonitor.App.csproj | Select-String '<Version>1.1.7</Version>'
git tag -a v1.1.7 1ff0fe3744e5c4f615af076a6ae674226a6a2665 -m "ServerAlyzer 1.1.7"
git push origin refs/tags/v1.1.7
gh run list --workflow release-build.yml --branch v1.1.7 --json databaseId,event,headSha,status,conclusion
```

Expected: one run, `event push`, `headSha` 1ff0fe3744e5…, then `conclusion success`. In its
summary: package `ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix` and a SHA-256. That SHA-256 is **not**
`7c991539…` (another runner, and the known CsWinRT non-determinism REL0-MSIX-REPRO); P2b bridges
the two. Record run id, package name, SHA-256 and the build record.

### P2a. Run provenance (read-only)

Run from a `main` checkout (the gate script is unchanged since 1ff0fe3), `R` = repository,
`TAG = 'v1.1.7'`, `RUN` = the run id:

```powershell
gh run view $RUN --json workflowName,event,headBranch,headSha,status,conclusion
gh api "repos/$R/actions/runs/$RUN" > run.json
gh api "repos/$R/git/ref/tags/$TAG" > tagref.json
$tagObj = (gh api "repos/$R/git/ref/tags/$TAG" --jq '.object.sha').Trim()
gh api "repos/$R/git/tags/$tagObj" > tagobject.json                 # annotated tag (P1 uses -a)
$tagCommit = (git rev-parse "$TAG^{commit}").Trim()
$main = (gh api "repos/$R/git/ref/heads/main" --jq '.object.sha').Trim()
gh api "repos/$R/compare/$tagCommit...$main" --jq '{status: .status, ahead_by: .ahead_by, behind_by: .behind_by}' > compare.json
./tools/release/Assert-ReleaseRunProvenance.ps1 -RunPath run.json -TagRefPath tagref.json -TagObjectPath tagobject.json `
  -ComparePath compare.json -Repository $R -Tag $TAG -PackageVersion 1.1.7.0
```

Expected:

* `gh run view`: `workflowName "Release build"`, `event "push"`, `headBranch "v1.1.7"`,
  `headSha "1ff0fe3744e5c4f615af076a6ae674226a6a2665"`, `status "completed"`, `conclusion "success"`;
* `tagref.json`: `ref refs/tags/v1.1.7`, `object.type tag`; `tagobject.json`: `tag v1.1.7`,
  `object.type commit`, `object.sha` 1ff0fe3744e5…;
* `compare.json`: `status "ahead"`, `behind_by 0` (`ahead_by` ≥ 15, it grows with `main`);
* last line: `RELEASE RUN PROVENANCE PASSED: release-build push run for v1.1.7 on main, commit
  1ff0fe3744e5c4f615af076a6ae674226a6a2665, package 1.1.7.0`.

Any other output: STOP, do not download.

### P2b. Verify the download (person, local)

```powershell
$d = '<new empty folder>'
gh run download $RUN -n serveralyzer-msix-1.1.7.0 -D $d
$p = Join-Path $d 'ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix'
(Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant()          # = the run summary
Get-Content "$p.sha256"                                              # "<same sha>  ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix"
(Get-Content "$p.build.json" -Raw | ConvertFrom-Json)                # commit = 1ff0fe3744e5…
./tools/release/Assert-PackageIdentity.ps1 -PackagePath $p -ExpectedVersion 1.1.7.0 -ExpectedSha256 <run summary sha>
./tools/release/Assert-PackageRuntime.ps1 -PackagePath $p
./tools/release/Assert-PackageResources.ps1 -PackagePath $p
./tools/release/Assert-DistributionPrivacy.ps1 -Path $d -AllowlistPath tools/release/distribution-privacy-allowlist.json
python -I <compare-msix.py> <the QA RC .msix> $p
```

Expected: every gate passes; `compare-msix.py` ends with `RESULT: ONLY KNOWN NON-DETERMINISM`
(only the two managed entry assemblies and the footprint files differ). Anything else: STOP.
`compare-msix.py` is the REL.0E runbook helper (internal evidence, not in this repository).

### P2c. Freeze the file

`Set-ItemProperty $p -Name IsReadOnly -Value $true`; record path and SHA-256 in the private
evidence **before** P4. Upload that exact path, from a folder with no other package.

## 3. F-2: package `Version` filled in only at commit

Status: **HYPOTHESIS, NOT_RUN**. The question is whether Partner Center fills in
`applicationPackages[].version` when it receives the upload or only when it processes the package at
commit. The submission API reference documents the field but not when it is set.

What can be checked read-only:

| check | how | answers |
| --- | --- | --- |
| The published 1.1.1.0 submission shows a `Version` | `store-status` with `include_metadata: true` before P4 (person dispatches; read-only `GET`) | Only that the field exists after commit, not when it is set |
| Partner Center shows the version after the upload | P4: the draft's *Packages* page after the upload is validated | What the **portal** checked; it is not the API field |
| The API field on a portal-created draft before submit | P6: `store-status` with `include_metadata: true` | **Resolves F-2**: `Version` `1.1.7.0` present → the field is set at upload; empty/null → set at commit. Record the result either way |

No agent may run these now (`store-*` dispatch is forbidden under REL.0E).

Effect on the portal-only path: **none on the outcome, a little on the evidence.** Nothing there
depends on the API field: P4 relies on what Partner Center shows, and P6 already accepts an empty
`Version` ("if already filled in"). The binding of the live package to the P2c file rests on the file
name `ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix` and the version, architecture and identity Partner Center
shows, because Partner Center never shows the hash. F-2 matters only for the API path (`store-submit`
read-back requires the version and would stop, fail-closed, with a draft left behind).

## 4. Portal-only staging checklist (one page)

Preconditions (all **NEEDS HUMAN**): `PRIVACY.md` rewritten and #65 merged (the 9 privacy
NEEDS HUMAN answers first); tag ruleset applied (§1) or explicitly waived; release GO for
`v1.1.7` → 1ff0fe3; Partner Center account with MFA, and no other user who can submit or publish.

| # | step | check (stop on any failure) | evidence |
| --- | --- | --- | --- |
| 0 | Partner Center audit, read-only (table below, PC-1…PC-28) | Every row matches or has a written decision; PC-12 privacy URL only after #65 | audit sheet |
| 1 | P1 tag + build | §2 P1 outputs | run id, SHA-256, build record |
| 2 | P2a provenance | `RELEASE RUN PROVENANCE PASSED … 1ff0fe3… 1.1.7.0` | command output |
| 3 | P2b verify | gates pass; SHA = summary = `.sha256`; `.build.json` commit 1ff0fe3; compare-msix known only | gate logs |
| 4 | P2c freeze | file read-only, SHA recorded | path + SHA |
| 5 | P3 backup | product idle (PC-7 no draft), hold `Manual` (PC-9); **Export listings** CSV | CSV + date |
| 6 | P4 draft | *Update*; upload the P2c file only; Packages page: 1.1.7.0, x64, `PedroLoy.ServerAlyzer`, only one new package, old package replaced | screenshot |
| 7 | P5 listing | text from `release/store/<lang>/` at `main` (PC-17…PC-25); 8 screenshots in order with captions, old ones removed (PC-23/24); nothing else touched | — |
| 8 | P5 submission fields | privacy URL (PC-12), cert notes (PC-27), runFullTrust reason (PC-28), age rating (PC-16) | screenshot |
| 9 | P6 verify | hold still *Publish manually*; 3 languages; draft CSV diff vs repo = no difference; `store-status include_metadata` (or Packages page) FileName `ServerAlyzer_1.1.7.0_x64_1ff0fe3.msix`; record `Version` (F-2) | CSV diff, run id |
| 10 | P7 certification GO | *Submit to the Store* with *Publish manually* | date, submission id |
| 11 | P8 publication GO | after certification, **Publish now**; tear down any cert test endpoint (PC-27) | date |

Rollback: R1 (delete the draft) before step 10; R4/R5 after; R6 after publish
(`docs/release-automation.md`).

### Partner Center audit, read-only (Beacon PC-1…PC-28, merged)

Sign in, look, write down; change nothing. "Repo" is the expected value.

| id | where in Partner Center | expected (repo) |
| --- | --- | --- |
| PC-1 | Product identity: Package/Identity/Name | `PedroLoy.ServerAlyzer` |
| PC-2 | Package/Identity/Publisher | `CN=32C0A056-FD57-422E-A59C-A8C26434951D` |
| PC-3 | Publisher display name | `Pedro Loy` |
| PC-4 | PFN / Store ID | `PedroLoy.ServerAlyzer_htb92ajfwgw1e` / `9N6ZBSBN1TD2` |
| PC-5 | App name reservations | `ServerAlyzer` reserved; DisplayName `ServerAlyzer` |
| PC-6 | Packages: published version | lower than 1.1.7.0 (CHANGELOG: 1.1.1.0); no other pending package |
| PC-7 | Submissions | no draft or submission in progress (else decide before P4) |
| PC-8 | Audience / visibility, markets | note today's value; go-public is a separate decision, do not change |
| PC-9 | Submission options: publishing hold | *Publish manually* / "don't publish until I select Publish now" |
| PC-10 | Pricing, add-ons | Free; no add-ons or in-app products |
| PC-11 | Category | proposed Developer tools › Servers (or Utilities); human choice |
| PC-12 | Privacy policy URL | **GATED** until PRIVACY.md rewritten (#65); note today's value |
| PC-13 | Website / support | `https://github.com/pedroloyola/ServerMonitor` / `…/issues` |
| PC-14 | System requirements | Windows 11 x64; 22H2+ for the widget; SSH to Linux/macOS servers |
| PC-15 | Product declarations | no drivers or NT services; accessibility claim is a human decision (NEEDS HUMAN) |
| PC-16 | Age rating questionnaire | complete; expected 3+ / PEGI 3 / Everyone (HYPOTHESIS answers) |
| PC-17 | Listing languages | exactly en-us, pt-pt, pt-br |
| PC-18 | Description | equal to `description.md` |
| PC-19 | Short description | equal to `short-description.txt` |
| PC-20 | What's new | equal to `whats-new.txt` |
| PC-21 | Product features | `features.txt`, 18 lines each, no manual bullets |
| PC-22 | Keywords | `keywords.txt`, 7 each, ≤ 27 characters |
| PC-23 | Screenshots | the same 8 per language in index order; no older images left |
| PC-24 | Captions | `screenshot-captions.txt`, one per image, ≤ 94 characters, no `08-widget` caption |
| PC-25 | Copyright | "Copyright (c) 2026 ServerAlyzer contributors" |
| PC-26 | Logos, trailers, promotional art | note what exists; remove nothing without a decision |
| PC-27 | Notes for certification | test credentials only there; note stale ones; cert endpoint torn down after certification |
| PC-28 | runFullTrust justification | present; final wording is the human's (NEEDS HUMAN) |
| PC-29 | Account settings → Users | MFA on the account that submits; no other user/role can submit or publish (NEEDS HUMAN) |
| PC-30 | API credentials (Entra app) | Manager role today; read-only role for `store-status` if offered (NEEDS HUMAN) |

PC-29 and PC-30 come from the portal-only *Credentials and accounts* notes in
`docs/release-automation.md`; PC-1…PC-28 are Beacon's REL.0E list.
