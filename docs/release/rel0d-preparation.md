# REL.0D preparation — Store RC, install QA and operations

What must be true, tested and decided before the Store release candidate leaves the pipeline, and how
it is operated and rolled back. Companion pages: `store-transition-runbook.md` (C3) and
`test-certificate-removal.md` (C4). Nothing here is an authorization: tags, dispatches, Store actions,
installs and certificate work each need an explicit human GO.

Labels: **DOC** · **EMPIRICAL** · **HYPOTHESIS** (as in the runbook). NOT_RUN ≠ PASS.

## 1. Dependencies (REL.0 Recovery fronts, in progress 2026-10-14, Draft PRs pending)

| Front | Branch | What REL.0D needs from it | Blocks |
|---|---|---|---|
| **B1** — .NET 10 runtime in the MSIX (Cortex) | `rel/rel0c-runtime` | Packaged build carries the .NET runtime for App **and** WidgetProvider; guard that fails if the package becomes framework-dependent again | clean-machine rows of §2; RC build |
| **B2** — Firewall / discovery on demand (Relay) | `rel/rel0b-firewall-startup` | No listener at startup or in `--background`; discovery only after an explicit user action | §2 rows FW-*, transition F6 |
| **C1 / G1** — version floor + release guards (Astra / Beacon) | `rel/rel0c-version-guard`, `rel/rel0c-release-guards` | Floor `1.1.7.0` + Revision 0 enforced in release-build and store-submit; no auto GitHub Release on tag (G2); artifact provenance (G4); `env:`-passed inputs (G5); status fails if ≠ Manual (G6); SHA pinning (G8); CODEOWNERS; human settings package | RC tag and submit |
| **C2** — default language en-US (Prism) | `rel/rel0c-language` | `DefaultLanguage` en-US, base-language matching, manifest `Description` via `ms-resource:`; persisted choice never overwritten | §2 language rows |
| **G1 review gate** | — | Multi-domain review of all the above (Cortex/Vigil/Atlas/Beacon/Prism/Relay) | merge of each front |

The GitHub-settings part of G1 (branch protection / ruleset on `main`, reviewer or least-privilege
credential on `microsoft-store-status`) is **HUMAN_ACTION_REQUIRED** and a hard prerequisite of the Store
submit (REL.0A closeout §11).

## 2. Install QA matrix

Run on disposable machines (§3) with the RC package (Store flight / private audience, or the exact
`release-build` artifact sideloaded with a VM-local test certificate where the row says so). Synthetic
data only. Each cell = PASS / FAIL / NOT_RUN with evidence.

| ID | Scenario | Environment | Checks | Depends on |
|---|---|---|---|---|
| CLEAN-1 | Clean install from Store, **no .NET 10 runtime** | Win11 x64 23H2/24H2, never had .NET 10 (`C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.*` absent) | App starts; widget provider activates from the picker; About shows RC version; `Get-AppxPackage` Store/Ok | B1 |
| CLEAN-2 | Clean install from Store, .NET 10 Desktop runtime present | same + runtime | as CLEAN-1 (proves no runtime conflict) | B1 |
| CLEAN-3 | AppData virtualization | CLEAN-1 machine | runbook §4 V1–V3 (where files land; what uninstall removes) | — |
| UPG-DEV | Upgrade from Developer 1.1.2.x | VM with VM-local test cert (human decision) | runbook §6.2 + §7 | rehearsal |
| UPG-STORE | Upgrade from Store 1.1.1.0 (published) | VM: install 1.1.1.0 from Store, data, pin, then RC | §7 checks 1–9 (Store→Store) | Store flight |
| REINST | Uninstall + reinstall | after UPG-STORE | runbook §3 column R measured: files, credentials, override, pins | — |
| NONADMIN | Standard (non-admin) user | VM second account | install from Store; first run; discovery action → Firewall result (non-admin prompts create **Block** rules, DOC Windows Firewall rules) | B2 |
| LANG-en | OS en-US | VM | app, picker, card in English | C2 |
| LANG-ptPT | OS pt-PT | VM language pack | app/picker/card pt-PT | C2 |
| LANG-ptBR | OS pt-BR | VM language pack | pt-BR | C2 |
| LANG-fr | OS fr-FR (unsupported) | VM language pack | app **en-US**, picker en-US, card en | C2 |
| LANG-keep | Persisted pt-BR choice on fr-FR OS, then upgrade | VM | choice kept after upgrade | C2 |
| HC | High contrast (Aquatic / Desert), light + dark | VM | readable, focus visible, no invisible text; widget card legible | — |
| WIDGET | Pin S/M/L, unpin, re-pin, theme switch | VM, build ≥ 22621.1413 | renders, updates, unpin stops updates, no orphan provider after idle | — |
| BG | Background/tray | VM | close with background on → tray monitoring; Exit → 0 processes; reboot → no autostart (no StartupTask) | — |
| FW-1 | Firewall observation, normal start | VM, Firewall notifications on, fresh install path | **0 dialogs**, `Get-NetFirewallRule` delta 0, no `0.0.0.0:5353` (`Get-NetUDPEndpoint -OwningProcess <pid>`) for 5 min | B2 |
| FW-2 | Firewall after explicit discovery action | VM | at most one dialog, after the explanatory copy; record the rule created | B2 |
| FW-3 | `--background` / widget activation | VM | no dialog, no listener | B2 |
| PRIV | Payload privacy | CI + local extract | `Assert-DistributionPrivacy` PASS on the RC artifact (CI `package-privacy` + release-build step) | REL.0A |

Observation rule for Firewall rows: the tester **records** any dialog (screenshot, time, exe path); in a
disposable VM they may answer it, on the real machine agents never answer.

## 3. Clean-machine plan

This machine is **Windows 11 Home**: no Windows Sandbox, no Hyper-V (EMPIRICAL, B1 brief). Installing a
hypervisor here is an install → forbidden without a human GO. Options, all **human decisions**:

| Option | Pros | Cons |
|---|---|---|
| Spare physical PC / laptop, freshly reset Windows 11 | real Store, real Widgets board, no host risk | needs hardware; reset takes time |
| Cloud Windows 11 VM (e.g. Azure / Windows 365) | disposable, snapshots | cost, account; Widgets board and Store availability on cloud SKUs = HYPOTHESIS; audio/GPU irrelevant |
| Desktop hypervisor on another machine | snapshots, cheap | not on this host |

The disposable machine must provide:
1. Windows 11 x64, ≥ 22621.1413 (Widgets provider active), all updates; snapshot before every scenario.
2. **No .NET 10 runtime** (and no SDK) for CLEAN-1; a second snapshot with the .NET 10 Desktop runtime for CLEAN-2.
3. Microsoft Store signed in with an account that is in the product's audience.
4. A standard (non-admin) local account besides the admin one.
5. Language packs en-US, pt-PT, pt-BR, fr-FR (display language switch per row).
6. Windows Firewall on with notifications on; no third-party firewall.
7. For UPG-DEV only: a **VM-local** test signing certificate with subject
   `CN=32C0A056-FD57-422E-A59C-A8C26434951D`, created and trusted **inside the VM**, deleted with it. The
   real key in `CurrentUser\My` is never exported. Needs human authorization + Vigil review.
8. No real servers, credentials or SSH keys; synthetic data only; a test SSH host only if the human
   provides one.
9. Evidence out: hashes, metadata, screenshots — redacted, no personal paths.

## 4. MSIX identity and capabilities audit (on the RC artifact)

Run on the exact `release-build` artifact (extract `AppxManifest.xml` with System.IO.Compression; never
install it for the audit). `Assert-PackageIdentity.ps1` covers rows marked ★.

| # | Check | Expected |
|---|---|---|
| 1 ★ | `Identity/@Name`, `@Publisher`, `@ProcessorArchitecture` | `PedroLoy.ServerAlyzer`, `CN=32C0A056-FD57-422E-A59C-A8C26434951D`, `x64` |
| 2 ★ | `Identity/@Version` | = tag, Revision 0, ≥ floor `1.1.7.0` (C1/G1 guard) |
| 3 ★ | Capabilities | `runFullTrust` only; no `broadFileSystemAccess`, `unvirtualizedResources`, `internetClientServer`, `privateNetworkClientServer` |
| 4 | `TargetDeviceFamily` | `Windows.Desktop`, MinVersion `10.0.22000.0` |
| 5 | `PackageDependency` | `Microsoft.WindowsAppRuntime.2` only (after B1 no .NET framework dependency; runtime inside the package) |
| 6 ★ | Executables | exactly `ServerMonitor.App.exe` + `ServerAlyzer.WidgetProvider.exe`, PE subsystem 2 |
| 7 | Frozen IDs | widget COM CLSID `78CFFBEF-7A95-4400-BB8B-A2376C6642C3`; AppExtension `ServerAlyzerWidgetProvider`; Definition `ServerAlyzer_Widget`; toast CLSID `4B2E9C7A-8F3D-4E1A-9B6C-2D5F7A1E3C48`; `Application/@Id` `ServerMonitor`; protocol `serveralyzer` |
| 8 | No Dev identity leakage | no `ServerAlyzer.Dev`, `(Dev)`, Dev toast CLSID `206ACD0C-…` |
| 9 | Language | `Resources` default en-US first; `Description` = `ms-resource:` (C2) |
| 10 | Absent extensions | no `windows.firewallRules`, no `windows.startupTask` (unless a product decision adds it), no `desktop6:*Virtualization` |
| 11 ★ | Payload hygiene | no `.pdb`, `.ps1`, tests, QA harness; privacy gate PASS |
| 12 | Signature | Store-signed after ingestion (verified on install: `SignatureKind Store`) |

## 5. Release workflow review (summary of REL.0A Gate C §5 + fronts)

| Gap | Severity | Fix | State tonight |
|---|---|---|---|
| G1 Manager credential in `microsoft-store-status` without reviewer; `main` unprotected, no CODEOWNERS | High | human settings + CODEOWNERS file | CODE_READY (CODEOWNERS) / **HUMAN_ACTION_REQUIRED** |
| G2 tag push creates a public GitHub Release | Medium | Release only by dispatch input | front G1 |
| G3 no Revision-0 / monotonic floor at submit; examples suggest `1.1.2.0` | Medium | floor script + fixed examples | fronts C1/G1 |
| G4 artifact not bound to tag/commit/workflow | Medium | `gh api actions/runs/<id>` verification, fail closed | front G1 |
| G5 `${{ inputs.* }}` interpolated in pwsh | Low–Med | `env:` + shape validation | front G1 |
| G6 status only warns if mode ≠ Manual | Low | exit non-zero | front G1 |
| G7 self-review allowed (solo maintainer) | Low | accepted risk, recorded | human |
| G8 actions pinned by tag | Low | full SHA pins | front G1 |
| G9 long-lived Entra client secret (expires 2027-03-04) | Info | accepted until OIDC identity approved | human |

Preserved: split commit (certification) vs. human "Publish now"; `--noCommit` + read-back hold check;
`Assert-NoPendingSubmission` fail-closed; privacy gate before any upload.

## 6. "RC READY" — acceptance criteria

RC READY is declared by Boss only when **all** hold:
1. B1, B2, C1/G1, C2 merged to `main` after the G1 review gate (each reviewer APPROVE / APPROVE_WITH_NITS).
2. Human GitHub settings done (or explicitly accepted as risk): `main` protected, CODEOWNERS enforced,
   `microsoft-store-status` reviewer or least-privilege credential; verified read-only with `gh api`.
3. Version decided (H-VER, provisional 1.1.7.0), bumped in csproj + both manifests in one commit; floor
   file agrees.
4. CI green on that commit, incl. `package-privacy`; release-build run on tag `v1.1.7` green (tests,
   self-tests, identity, privacy, floor); artifact SHA-256 recorded.
5. §4 audit PASS on that artifact.
6. Clean-machine rows CLEAN-1/2/3, FW-1/3, LANG-* PASS, or NOT_RUN with an explicit human acceptance
   (NOT_RUN never counts as PASS).
7. Store notes `release/store/{en-US,pt-BR,pt-PT}/whats-new.txt` written for the RC (mention the language
   default change and on-demand discovery).
8. Open HYPOTHESIS list (report) reviewed by the human.

## 7. Security plan

- **Distribution:** only the release-build artifact, after the privacy gate; Debug packages never leave
  the machine; no GitHub Release asset.
- **Secrets:** environment secrets only; never read, printed or rotated by agents; rotation per
  `docs/release-automation.md` (human).
- **Publishing:** Manual hold asserted twice; "Publish now" is a human click; audience change (go-public)
  is a separate human decision (H-AUDIENCE).
- **Signing:** Store signs. The test key is retired per C4; never exported; a VM-local test certificate
  for the rehearsal only with human GO.
- **Network:** discovery on demand (B2); no Firewall rule as a fix; Firewall dialogs observed, not
  answered by agents.
- **Data:** synthetic data in every test; real-data inventory by hashes/names only, outside OneDrive;
  M14.6 backup before the transition.
- **Reviews:** Vigil on every front touching network, signing, workflows or data paths.

## 8. Operation and rollback (release day)

| Step | Who | Command / action | Rollback |
|---|---|---|---|
| 1 Version bump + tag `v1.1.7` | human GO, Boss | commit; `git tag v1.1.7`; push tag | delete tag before any Store step (human) |
| 2 release-build | automatic on tag | record run id, package name, SHA-256 | nothing published (no Release with G2 fix) |
| 3 store-status | human dispatch | expect idle (`NO SUBMISSION`/`PUBLISHED`) | — |
| 4 store-submit | human dispatch + `SUBMIT` + environment approval | run id, version, SHA-256 | before commit: draft can be deleted in Partner Center; after commit: cancel submission in Partner Center (human) |
| 5 certification | Store | watch with store-status | fix and resubmit with a **higher** version (same version is refused) |
| 6 Publish now | human, Partner Center | audience stays private unless H-AUDIENCE | installed copies cannot be pulled back (HYPOTHESIS: Store has no rollback of installed versions); ship a higher fixed version, or stop acquisition in Partner Center (human) |
| 7 transition of the QA machine | human | `store-transition-runbook.md` | runbook §6.4 |
| 8 certificate removal | human, UAC | `test-certificate-removal.md` | none needed (key retired by design) |

Deadlines: checkpoint **2026-11-23**, certificate expiry **2026-11-30**.
