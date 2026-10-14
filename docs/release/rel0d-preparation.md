# REL.0D preparation — Store RC, install QA and operations

What must be true, tested and decided before and after the Store release candidate is certified, and how
it is operated and rolled back. Companion pages: `store-transition-runbook.md` (C3) and
`test-certificate-removal.md` (C4). Nothing here is an authorization: tags, dispatches, Store actions,
installs and certificate work each need an explicit human GO.

Labels: **DOC** · **EMPIRICAL** · **HYPOTHESIS** (as in the runbook). NOT_RUN ≠ PASS.

## 1. Dependencies (REL.0B/0C work, opened 2026-10-14 as draft pull requests)

| Change | Branch | What REL.0D needs from it | Blocks |
|---|---|---|---|
| .NET 10 runtime inside the MSIX | `rel/rel0c-runtime` | Packaged build carries the .NET runtime for App **and** WidgetProvider; a guard fails if the package becomes framework-dependent again | CLEAN rows; RC build |
| Discovery on demand (Firewall) | `rel/rel0b-firewall-startup` | No listener at startup or in `--background`; discovery only after an explicit user action | FW rows; runbook F6 |
| Version floor + release guards | `rel/rel0c-version-guard`, `rel/rel0c-release-guards` | Floor `1.1.7.0` + Revision 0 enforced in release-build and store-submit; no automatic GitHub Release on a tag; artifact provenance; inputs passed via `env:`; status fails if the publish mode is not Manual; actions pinned by SHA; CODEOWNERS; human settings package | RC tag and submit |
| Default language en-US | `rel/rel0c-language` | `DefaultLanguage` en-US, base-language matching, manifest `Description` via `ms-resource:`; a persisted choice is never overwritten | LANG rows |
| Multi-domain review gate | — | Security, architecture, tests, QA, UI and platform reviews of all the above | merge of each change |

The GitHub-settings part (branch protection / ruleset on `main`, reviewer or least-privilege credential on
the `microsoft-store-status` environment) is **HUMAN_ACTION_REQUIRED** and a hard prerequisite of the Store
submit.

## 2. Install QA matrix

Synthetic data only. Each cell = PASS / FAIL / NOT_RUN with evidence. **Gate** says when the row runs:
- **PRE** — before submit, on the exact `release-build` artifact, sideloaded in a disposable VM with a
  VM-local test certificate (runbook §6.2.1).
- **POST** — after certification, from the Store with a private audience, before the real transition and
  before any go-public decision.

| ID | Gate | Scenario | Checks | Depends on |
|---|---|---|---|---|
| CLEAN-1 | PRE + POST | Clean install, **no .NET 10 runtime** (`C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.*` absent) | App starts; widget provider activates from the picker; About shows the RC version; `Get-AppxPackage` Ok (POST: `SignatureKind Store`) | runtime change |
| CLEAN-2 | PRE | Clean install, .NET 10 Desktop runtime present | as CLEAN-1 (no runtime conflict) | runtime change |
| CLEAN-3 | PRE + POST | AppData virtualization | runbook §4 V1–V3 | — |
| OS-MIN | PRE | Windows 11 21H2 (10.0.22000, the manifest minimum), if an image is available | app works; the widget is **not** expected (Widgets providers need ≥ 22621.1413), and nothing errors | — |
| ARCH | — | ARM64 device | **Out of scope**: the package is x64-only and the support matrix is Windows 11 x64 (ADR-017 §4b). Whether the Store offers the x64 package to ARM64 under emulation = HYPOTHESIS, not tested; record if a device is ever available | — |
| UPG-DEV | POST | Upgrade from a Developer 1.1.2.x install | runbook §6.2 + §7 (takeover F3) — **precondition of the real transition** | rehearsal |
| UPG-STORE | POST | Upgrade from Store 1.1.1.0 with data, then RC; **plus** 1.1.1.0 started on 1.1.2.x data (runbook F10) | §7 checks 1–9 (Store→Store); what 1.1.1.0 shows and writes on newer data | Store audience |
| UPG-BUSY | POST | Upgrade while the app is in the tray, and while a widget is pinned (provider running) | install deferred / `0x80073D02` / completed; app state afterwards; no data change beyond §7.2 | — |
| REINST | POST | Uninstall + reinstall | runbook §3 column R measured: files, credentials, language override, pins | CLEAN-3 |
| BACKUP-XM | PRE | M14.6 backup on VM A → restore on clean VM B (and after REINST on the same VM) | servers, routed servers, trust and credentials restored; connects without new trust prompt | — |
| NOTIF | PRE | Toast click, **warm** (app running) and **cold** (app not running) | warm: existing instance foregrounds; cold: app starts once and opens the target; no second process | — |
| PROTO | PRE | `serveralyzer:` protocol and widget deep link | activates the existing instance (single instance), opens the target; unknown/malformed URIs ignored | — |
| NONADMIN | PRE | Standard (non-admin) user | install; first run; discovery action → Firewall result. A Block rule pair after a cancelled or non-elevated prompt = EMPIRICAL from earlier QA; the exact non-admin behaviour = HYPOTHESIS | discovery change |
| LANG-en | PRE | OS en-US | app, picker, card in English | language change |
| LANG-ptPT | PRE | OS pt-PT | app/picker/card pt-PT | language change |
| LANG-ptBR | PRE | OS pt-BR | pt-BR | language change |
| LANG-fr | PRE | OS fr-FR (unsupported) | app **en-US**, picker en-US, card en | language change |
| LANG-keep | POST | Persisted pt-BR choice on an fr-FR OS, then upgrade | choice kept | language change |
| HC | PRE | Contrast themes Aquatic (dark) and Desert (light); widget board theme recorded separately | readable, focus visible, no invisible text; card legible | — |
| WIDGET | PRE | Pin S/M/L, unpin, re-pin, theme switch | renders, updates; unpin stops updates; no orphan provider after idle | — |
| BG | PRE | Background/tray | close with background on → tray monitoring; Exit → 0 processes; reboot → no autostart (no StartupTask) | — |
| FW-1 | PRE | Firewall, normal start | see §2.1; **0 dialogs**, rule delta 0, no listener | discovery change |
| FW-2 | PRE | Firewall after the explicit discovery action | at most one dialog, after the explanatory copy; record the rule created | discovery change |
| FW-3 | PRE | `--background` / widget activation | as FW-1 | discovery change |
| PRIV | PRE | Payload privacy | `Assert-DistributionPrivacy` PASS on the RC artifact (CI `package-privacy` job + release-build step) | — |

### 2.1 Firewall measurement (FW-1, FW-3)

For 5 minutes after start, every 10 s, for the app PID **and** the WidgetProvider PID (if running):

```powershell
$pids = (Get-Process ServerMonitor.App, ServerAlyzer.WidgetProvider -ErrorAction SilentlyContinue).Id
Get-NetUDPEndpoint | Where-Object OwningProcess -in $pids | Select-Object LocalAddress, LocalPort, OwningProcess
Get-NetTCPConnection -State Listen | Where-Object OwningProcess -in $pids | Select-Object LocalAddress, LocalPort, OwningProcess
```

PASS = no UDP endpoint on `0.0.0.0`/`::` (any port, including 5353), no TCP `Listen`, no Firewall
dialog, and `(Get-NetFirewallRule).Count` equal before and after. Evidence: one CSV per sample series
(timestamp, PID, address, port), plus the two rule counts.

Observation rule: the tester **records** any dialog (screenshot, time, exe path). In a disposable VM they
may answer it; on the maintainer's machine agents never answer it.

## 3. Clean-machine plan

The maintainer's development machine has no Windows Sandbox or Hyper-V, and installing a hypervisor there
is out of bounds without a human GO. Options, all **human decisions**:

| Option | Pros | Cons |
|---|---|---|
| Spare physical PC / laptop, freshly reset Windows 11 | real Store, real Widgets board | needs hardware; reset time |
| Cloud Windows 11 VM | disposable, snapshots | cost, account; Widgets board and Store on cloud SKUs = HYPOTHESIS |
| Desktop hypervisor on another machine | snapshots, cheap | not on the development machine |

The disposable machine must provide:
1. Windows 11 x64 ≥ 22621.1413 (Widgets providers active), updated; a snapshot before every scenario.
   A 21H2 image for OS-MIN if available.
2. **No .NET 10 runtime or SDK** for CLEAN-1; a second snapshot with the .NET 10 Desktop runtime for CLEAN-2.
3. Microsoft Store signed in with an account in the product's audience (POST rows).
4. A standard (non-admin) local account besides the admin one.
5. Language packs en-US, pt-PT, pt-BR, fr-FR.
6. Windows Firewall on, notifications on; no third-party firewall.
7. For PRE rows and UPG-DEV: a **VM-local** test signing certificate with subject
   `CN=32C0A056-FD57-422E-A59C-A8C26434951D`, created inside the VM with `-KeyExportPolicy NonExportable`,
   validity of days, trusted only in the VM's `TrustedPeople` (never `Root`), deleted with the VM. Needs
   human authorization and security review.
8. No real servers, credentials or SSH keys; a test SSH host only if the human provides one.
9. Evidence out: hashes, metadata, screenshots — redacted, no personal paths.

## 4. MSIX identity and capabilities audit (on the RC artifact)

Run on the exact `release-build` artifact (extract `AppxManifest.xml` with System.IO.Compression; never
install it for the audit). `Assert-PackageIdentity.ps1` covers rows marked ★.

| # | Check | Expected |
|---|---|---|
| 1 ★ | `Identity/@Name`, `@Publisher`, `@ProcessorArchitecture` | `PedroLoy.ServerAlyzer`, `CN=32C0A056-FD57-422E-A59C-A8C26434951D`, `x64` |
| 2 ★ | `Identity/@Version` | = tag, Revision 0, ≥ floor `1.1.7.0` |
| 3 ★ | Capabilities | `runFullTrust` only; no `broadFileSystemAccess`, `unvirtualizedResources`, `internetClientServer`, `privateNetworkClientServer` |
| 4 | `TargetDeviceFamily` | `Windows.Desktop`, MinVersion `10.0.22000.0` |
| 5 | `PackageDependency` | `Microsoft.WindowsAppRuntime.2` only (the .NET runtime is inside the package) |
| 6 ★ | Executables | exactly `ServerMonitor.App.exe` + `ServerAlyzer.WidgetProvider.exe`, PE subsystem 2 |
| 7 | Frozen IDs | widget COM CLSID `78CFFBEF-7A95-4400-BB8B-A2376C6642C3`; AppExtension `ServerAlyzerWidgetProvider`; Definition `ServerAlyzer_Widget`; toast CLSID `4B2E9C7A-8F3D-4E1A-9B6C-2D5F7A1E3C48`; `Application/@Id` `ServerMonitor`; protocol `serveralyzer` |
| 8 | No Dev identity leakage | no `ServerAlyzer.Dev`, `(Dev)`, Dev toast CLSID `206ACD0C-…` |
| 9 | Language | default en-US; `Description` = `ms-resource:` |
| 10 | Absent extensions | no `windows.firewallRules`, no `windows.startupTask` (unless a product decision adds it), no `desktop6:*Virtualization` |
| 11 ★ | Payload hygiene | no `.pdb`, `.ps1`, tests, QA harness; privacy gate PASS |
| 12 | Signature | Store-signed after ingestion (on install: `SignatureKind Store`) |

## 5. Release workflow review (summary)

| Gap | Severity | Fix | State |
|---|---|---|---|
| G1 Store credential (Manager role) reachable from `microsoft-store-status` without a reviewer; `main` unprotected; no CODEOWNERS | High | human settings + CODEOWNERS | code part in a draft PR / **HUMAN_ACTION_REQUIRED** |
| G2 tag push creates a public GitHub Release | Medium | Release only by an explicit dispatch input | draft PR |
| G3 no Revision-0 / monotonic floor at submit; examples suggested a downgrade | Medium | floor check + fixed examples | draft PR |
| G4 artifact not bound to tag/commit/workflow | Medium | verify the run via the GitHub API, fail closed | draft PR |
| G5 inputs interpolated into PowerShell | Low–Med | `env:` + shape validation | draft PR |
| G6 status only warns if the mode is not Manual | Low | exit non-zero | draft PR |
| G7 self-review allowed (solo maintainer) | Low | accepted risk, recorded | human |
| G8 actions pinned by tag | Low | full SHA pins | draft PR |
| G9 long-lived Entra client secret | Info | accepted until a federated identity is approved; rotation per `docs/release-automation.md` | human |

Preserved: split between commit to certification and the human "Publish now"; `--noCommit` + read-back
hold check; `Assert-NoPendingSubmission` fail-closed; privacy gate before any upload.

## 6. Gates

### RC-PRE — "ready to submit" (declared before `store-submit`)
1. The §1 changes merged to `main` after the review gate (each reviewer APPROVE / APPROVE_WITH_NITS).
2. GitHub settings done by the human (or explicitly accepted as risk) and verified read-only with `gh api`.
3. Version decided (provisionally 1.1.7.0), bumped in csproj + both manifests in one commit; floor agrees.
4. CI green on that commit, including `package-privacy`; release-build on tag `v1.1.7` green; artifact
   SHA-256 recorded.
5. §4 audit PASS on that artifact.
6. **All PRE rows** of §2 PASS — CLEAN-1/2/3, OS-MIN (or NOT_RUN accepted), BACKUP-XM, NOTIF, PROTO,
   NONADMIN, LANG-en/ptPT/ptBR/fr, HC, WIDGET, BG, FW-1/2/3, PRIV — or NOT_RUN with explicit human
   acceptance per row (NOT_RUN never counts as PASS).
7. Store notes `release/store/{en-US,pt-BR,pt-PT}/whats-new.txt` written (language default, on-demand discovery).
8. Open HYPOTHESIS list reviewed by the human.

### RC-POST — "ready for the real transition / a go-public decision" (after certification, private audience)
1. **All POST rows** PASS: CLEAN-1 and CLEAN-3 from the Store, UPG-DEV, UPG-STORE, UPG-BUSY, REINST,
   LANG-keep — or NOT_RUN with explicit human acceptance. UPG-STORE and REINST block any go-public decision
   (they cover every existing Store user and the uninstall data question).
2. The real transition (runbook) only after RC-POST.

## 7. Security plan

- **Distribution:** only the release-build artifact, after the privacy gate; Debug packages and old QA
  packages never leave the machine; no GitHub Release asset.
- **Secrets:** environment secrets only; never read, printed or rotated by agents; rotation by the human
  per `docs/release-automation.md`.
- **Publishing:** Manual hold asserted twice; "Publish now" is a human click; changing the audience
  (go-public) is a separate human decision.
- **Signing:** the Store signs. The test key — every store entry and every exported copy — is retired per
  C4; a VM-local test certificate (non-exportable key) only with human GO.
- **Network:** discovery on demand; no Firewall rule as a fix; Firewall dialogs observed, not answered by agents.
- **Data:** synthetic data in every test; real-data inventory by hashes and names only, outside cloud
  sync; M14.6 backup and the quiescent folder copy before the transition.
- **Reviews:** security review of every change touching network, signing, workflows or data paths.

## 8. Operation and rollback (release day)

| Step | Who | Command / action | Rollback |
|---|---|---|---|
| 1 Version bump + tag `v1.1.7` | human GO | commit; `git tag v1.1.7`; push tag | delete the tag before any Store step (human) |
| 2 release-build | automatic on tag | record run id, package name, SHA-256 | nothing published |
| 3 store-status | human dispatch | expect idle (`NO SUBMISSION` / `PUBLISHED`) | — |
| 4 store-submit | human dispatch + `SUBMIT` + environment approval | run id, version, SHA-256 | before commit: delete the draft in Partner Center; after commit: cancel the submission in Partner Center (human) |
| 5 certification | Store | watch with store-status | fix and resubmit with a **higher** version (the same version is refused) |
| 6 Publish now (private audience) | human, Partner Center | then run the RC-POST rows | installed copies cannot be pulled back (HYPOTHESIS); ship a higher fixed version, or stop acquisition in Partner Center (human) |
| 7 transition of the QA machine | human | `store-transition-runbook.md` | runbook §6.4 |
| 8 certificate retirement | human | `test-certificate-removal.md` (Phase 1 any time after GO; Phase 2 after the soak, by 2026-11-30) | none needed |

Deadlines: checkpoint **2026-11-23**, certificate expiry **2026-11-30**.
