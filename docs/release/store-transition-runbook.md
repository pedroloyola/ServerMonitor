# Store transition runbook: Developer QA build → Store-managed (REL.0 C3)

The maintainer's QA machine runs a **Developer-signed** build of the production identity (1.1.2.2, from
the UI.9 packaged QA). REL.0 replaces it with the **Store-signed** package, keeps the user's data, and only
then retires the test certificate (`test-certificate-removal.md`). This runbook is the procedure. **It does
not authorize anything**: the rehearsal, the real transition and any uninstall each need an explicit
human GO.

Target version: **≥ 1.1.7.0 (provisional, human decision)**, Revision 0, tag `v1.1.7`. Versions
1.1.3.0–1.1.6.0 were already used in local QA installs, and 1.1.2.2 is installed, so the Store version
must be strictly higher than all of them.

Labels on every claim: **DOC** = primary documentation (linked) · **EMPIRICAL** = measured in this
project (code at a cited line, or internal QA evidence) · **HYPOTHESIS** = not measured; says how to
measure it. Machine-specific state (backup locations, installed runtimes, rule history) lives in internal
notes, not here.

## 1. Starting point

| Item | Value | Label |
|---|---|---|
| Installed package | `PedroLoy.ServerAlyzer` **1.1.2.2**, x64, `SignatureKind Developer` | EMPIRICAL (internal) |
| PFN / Publisher | `PedroLoy.ServerAlyzer_htb92ajfwgw1e` / `CN=32C0A056-FD57-422E-A59C-A8C26434951D` | identical to the Store identity (ADR-017 §4) |
| Signer | test certificate `529C81F5…ED82`, expires 2026-11-30 | EMPIRICAL |
| Last published Store package | 1.1.1.0 (tag `v1.1.1`); current audience to be re-read with `store-status.yml` before REL.0D | record EMPIRICAL, audience UNVERIFIED |
| `%LOCALAPPDATA%\ServerMonitor` on the QA machine | created by unpackaged dev builds **before** any packaged install | EMPIRICAL (UI.9 out-of-package instruments saw packaged writes land in the real folder, `docs/ui/ui9-windows-widget-v3.md` §11) |
| .NET 10 on the QA machine | installed machine-wide → the QA machine cannot reveal a missing-runtime failure | EMPIRICAL (internal) |

## 2. What the package touches, and where it lives

From the code (`src/`), not from assumptions:

| Data | Location / mechanism | Source |
|---|---|---|
| Servers, routed servers | `%LOCALAPPDATA%\ServerMonitor\servers.json`, `routed-servers.json` (+ `*.corrupt-<ts>` quarantine, `.tmp`) | `Infrastructure/Persistence/ServerStorageOptions.cs:6,15-17,26`; `JsonServerRepository.cs:23,29` |
| Known hosts (+ routes) | `known-hosts.json`, `known-hosts.routes.json` | `HostKeyTrustStorageOptions.cs:14`; `RoutedHostKeyTrustStorageOptions.cs:10,21-23` |
| History | `history.db` + `history.db-wal` + `history.db-shm` (WAL, `synchronous=NORMAL`, pooled connections) | `SqliteServerHistoryStore.cs:40-46,333,341-343,562` |
| Ignored devices | `ignored-devices.json` | `IgnoredDeviceStorageOptions.cs:19` |
| Notification / background settings | `notification-settings.json`, `background-settings.json` (tray-on-close + notice flag; **not** autostart) | `App/Services/NotificationSettingsStorageOptions.cs:13`; `JsonBackgroundMonitoringSettingsService.cs:17,22-27` |
| Window placement | `window-placement.json` | `App/Windowing/WindowPlacementStorageOptions.cs:13` |
| Widget state | `widget-state.json` (+ `.bak`, `widget-state.<guid>.tmp`), written by the app, read by the provider | `WidgetContract/WidgetStateLocation.cs:11-20`; `AtomicWidgetStateWriter.cs:20,92` |
| Restore journal | `restore-journal\` (M14.6) | `Infrastructure/Backup/RestoreJournal.cs:99-108` |
| SSH secrets | Credential Manager, GENERIC, `CRED_PERSIST_LOCAL_MACHINE`, target `ServerMonitor:v1:ssh:{server}:{kind}:{ref}` (legacy `pedroloyola.ServerMonitor:v1:ssh` read and migrated) | `Infrastructure/Security/CredentialTargetName.cs:9,13,37`; `WindowsCredentialStore.cs:15-16,164` |
| UI language override | `ApplicationLanguages.PrimaryLanguageOverride` (stored by Windows per package; no app file) | `App/Services/LocalizationService.cs:14-46` |
| Widget pins | Widgets host, keyed by PFN + AppExtension `ServerAlyzerWidgetProvider` + Definition `ServerAlyzer_Widget` + COM CLSID `78CFFBEF-7A95-4400-BB8B-A2376C6642C3` (FROZEN) | `Package.appxmanifest:106-144` |
| Toast activator | COM CLSID `4B2E9C7A-8F3D-4E1A-9B6C-2D5F7A1E3C48` + `desktop:ToastNotificationActivation` | `Package.appxmanifest:78-92` |
| Protocol | `serveralyzer:` | `Package.appxmanifest:94-104` |
| Single instance | `AppInstance.FindOrRegisterForKey("ServerMonitor")`; no named mutex | `App/Services/SingleInstancePolicy.cs:12`; `App/Program.cs:112-207` |
| StartupTask | **none** (no `windows.startupTask`, no `StartupTask` API use) | manifest; `docs/ui/ui9-windows-widget-v3.md:120` |
| Package `Settings` / `LocalState` | not used by code (no `ApplicationData.Current`) — only Windows-owned state such as the language override | `src/` |
| Logs / dumps | none on disk (Debug output / ETW only) | `App.xaml.cs:79`; `WidgetProvider/Diagnostics/EtwWidgetProviderLog.cs` |

The manifest declares neither `desktop6:FileSystemWriteVirtualization` nor `RegistryWriteVirtualization`,
and the opt-out needs the restricted capability `unvirtualizedResources`, which is meant for certain games
only (DOC [desktop6:FileSystemWriteVirtualization](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-filesystemwritevirtualization)).
So default MSIX behaviour applies (§4).

The M14.6 encrypted backup (`.serveralyzer-backup`) contains servers, routed servers, the referenced
known-hosts (direct and routed), **credential secrets** and two settings (notifications, background). It
does **not** contain history, widget state, window placement or ignored devices
(`Infrastructure/Backup/BackupPayloadSerializer.cs:26-32,76-91`). The folder copy in §6.0 covers those.

## 3. Survival matrix

Two paths: **(U) in-place upgrade** — Store installs a higher version over the Developer package, same
PFN — and **(R) uninstall + install**.

| Item | (U) in-place upgrade | (R) uninstall + install |
|---|---|---|
| Files in `%LOCALAPPDATA%\ServerMonitor\` | **Survive.** EMPIRICAL for Developer→Developer (UI.9: 0 differences after install; only the 3 expected files changed during QA). DOC: package updates keep app data ([behind the scenes](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes): "All writes are kept during package upgrade"). Developer→Store specifically: HYPOTHESIS (rehearse, §6.2). | **QA machine: expected to survive** — the folder predates every packaged install (HYPOTHESIS strong; §4, and the §6.1 STOP check). **A machine where the Store package created it: deleted** with the package if virtualized (§4). |
| Credential Manager targets | Survive — HYPOTHESIS strong; UI.9 saw no Credential Manager change across Developer updates (EMPIRICAL for U) | Survive — HYPOTHESIS strong (`CredWrite` is not package-redirected; DOC lists only files and HKCU as virtualized); measured in the rehearsal |
| `PrimaryLanguageOverride` | Survives — HYPOTHESIS strong | **Lost** — HYPOTHESIS strong; the user re-picks the language |
| Package `Settings` / `LocalState` | Survive (DOC: kept on upgrade) | Lost (DOC) — the app keeps nothing there |
| HKCU writes | Kept (DOC) | Deleted (DOC) — the app writes none deliberately |
| Widget pins | Survive — EMPIRICAL Developer→Developer ("The original pin survived the update", ui9 §11); Developer→Store HYPOTHESIS | **Lost** — HYPOTHESIS strong; re-pin |
| StartupTask | N/A — not declared | N/A |
| Single-instance key | Unaffected (runtime registration only) | Unaffected |
| Toast COM / `serveralyzer:` / widget COM server | Re-registered from the new manifest; CLSIDs unchanged (HYPOTHESIS: existing toasts keep routing) | Unregistered, then registered; old toasts may stop activating (HYPOTHESIS, harmless) |
| Windows Firewall | New version = new install path → **new prompt** on first listen, unless discovery is on demand (REL.0B) — EMPIRICAL (one rule pair per installed version path) | Same; uninstall does not remove old rules (EMPIRICAL) |

> **Update 2026-10-18.** The `%LOCALAPPDATA%\ServerMonitor` and Credential Manager rows were measured in
> REL.0F and REL.0G with 1.1.7.0. The results are in §4.1. The table above keeps the expectations as they were
> written before the transition.

## 4. AppData write virtualization (answered 2026-10-18; original analysis kept below)

### 4.1 Measured results (REL.0F 2026-10-17, REL.0G 2026-10-18; Store package 1.1.7.0)

All measurements are **EMPIRICAL**.
- **Environment:** disposable Windows 11 VM (pt-BR, clean snapshot `GA-BASE-OFF`) with synthetic servers and
  credentials, plus the maintainer's QA machine for the real transition.
- **Methods:** `Remove-AppxPackage` for the current user; Store installs through `winget --source msstore`
  (VM); on the QA machine, an install by the owner from the Microsoft Store (`SignatureKind Store`).
- **Evidence:** internal REL.0F/REL.0G reports.

| Case | Where the app's files live | After uninstall | Credential Manager | After reinstall |
|---|---|---|---|---|
| **Clean Store install**: folder absent (REL.0G C1–C6) | `%LOCALAPPDATA%\Packages\PedroLoy.ServerAlyzer_htb92ajfwgw1e\LocalCache\Local\ServerMonitor\`; the real `%LOCALAPPDATA%\ServerMonitor` is not created (C1–C6) | **removed** with the whole package folder | entries **stay** (orphans) | fresh start (first-run screen) |
| **Pre-existing real folder**, Store build (REL.0G C7: folder created by an unpackaged process before install) | the real `%LOCALAPPDATA%\ServerMonitor\`, **including files the app creates later** (history, widget state, language settings); no `LocalCache` copy | **stays**, byte-identical | entries stay | not measured in C7 (it ended at uninstall); reuse was measured in R5, next row |
| **Pre-existing real folder**, Developer → uninstall → Store (REL.0F R0–R6, VM) | the real folder | stays, byte-identical (Developer uninstall) | entries stay | the Store 1.1.7.0 install reused it; rollback Store → Developer restored with 0 hash differences |
| **QA machine, real transition** (REL.0F, 2026-10-17) | the real folder (created by earlier unpackaged builds) | Developer 1.1.2.2 uninstall: 12/12 files intact; the package folder (`Settings` + `SystemAppData`) was removed | 1 entry stayed | the Store 1.1.7.0 install used the existing data |

**Answers to the questions below:**
- (V1) Confirmed: on a clean machine the folder is redirected and uninstall deletes it.
- (V3) Confirmed: with an existing folder, both existing files and new ones are read and written in the real folder.
- Credentials are never removed by uninstall.
- Package `Settings` (`settings.dat`) is always removed with the package. The language override set through
  `ApplicationLanguages.PrimaryLanguageOverride` is package-level state (HYPOTHESIS: kept there, not inspected),
  so a reinstall that reuses the data files is still expected to start with the default language setting.

**Not tested:**
- (V2) an **empty** pre-existing folder;
- Windows 10;
- several users on one PC;
- a redirected or roaming `%LOCALAPPDATA%`;
- a real `%LOCALAPPDATA%\ServerMonitor` created **after** a clean Store install, for example by running a build
  from source (which copy wins is untested; §4.2 DOC says the package copy is opened first);
- an in-place Store → Store upgrade, 1.1.7.0 to a later version (HYPOTHESIS: app data is kept on upgrade;
  not measured).

**Consequences, already applied:**
- `PRIVACY*.md` was corrected in PR #72;
- ADR-017 §8/§12 carry an amendment;
- the README summary was updated.

### 4.2 Original analysis (written before measurement; kept for the record)

**DOC** ([Understanding how packaged desktop apps run on Windows](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes), ms.date 2026-09-30), for virtualized packaged desktop apps on Windows 10 1903+:
- "All newly created files and folders in the user's `AppData` folder … are written to a private per-user,
  per-app location; but merged at runtime to appear in the real `AppData` location", which "enables the
  system to clean up those files when the app is uninstalled".
- "Modifications to existing files under the user's `AppData` folder is allowed".
- On open, "the OS will open the file from the per-user, per-package location first … If the file is
  opened from the real `AppData` location, then no virtualization for that file occurs."
- Uninstall removes "any redirected writes to `AppData` or the registry".

| Machine | Expected | Label |
|---|---|---|
| QA machine (folder created by unpackaged builds long before packaging) | Existing files are read and written in the real folder (consistent with UI.9 evidence). **New** files created later inside it (a first `routed-servers.json`, `restore-journal\`, `.corrupt-*`) may still be redirected. | existing files EMPIRICAL; new files HYPOTHESIS → §6.1 STOP check |
| Clean machine (folder first created by the packaged app) | The whole folder lives under `%LOCALAPPDATA%\Packages\PedroLoy.ServerAlyzer_htb92ajfwgw1e\LocalCache\Local\ServerMonitor\`; app and provider (same package) see it at the real path; **uninstall deletes it**. Contradicts ADR-017 §12 ("uninstall does not remove data"). | HYPOTHESIS (DOC strongly implies it; whether this full-trust app counts as "virtualized" is the unmeasured part) |

**Measurement (rehearsal VM only, never on the QA machine):** after first launch and adding one synthetic
server, from an **unpackaged** PowerShell:

```powershell
$Pfn = 'PedroLoy.ServerAlyzer_htb92ajfwgw1e'
Test-Path "$env:LOCALAPPDATA\ServerMonitor\servers.json"                                 # real
Test-Path "$env:LOCALAPPDATA\Packages\$Pfn\LocalCache\Local\ServerMonitor\servers.json"  # redirected
# the package's own view:
Invoke-CommandInDesktopPackage -PackageFamilyName $Pfn -AppId ServerMonitor `
  -Command 'cmd.exe' -Args '/c dir "%LOCALAPPDATA%\ServerMonitor" > "%TEMP%\in-package-view.txt"'
```

Cases: (V1) clean profile, folder absent; (V2) real folder pre-created empty before first launch; (V3)
real folder with data, plus one file the app creates later. Then uninstall and repeat the `Test-Path`
lines. Record where each file was and what survived.

If V1 confirms, Store users lose servers, known hosts and history on uninstall (credentials stay as
orphans in Credential Manager). That is a separate human/ADR decision (correct ADR-017 §12 and
PRIVACY.md, or change storage); the M14.6 backup is the user-facing safety net either way.

## 5. Failure modes

| ID | Failure | Expected signal | Handling | Label |
|---|---|---|---|---|
| F1 | Store version ≤ installed | `0x80073D06 ERROR_INSTALL_PACKAGE_DOWNGRADE` | Version floor in the release workflows blocks it before submit | DOC ([troubleshooting](https://learn.microsoft.com/windows/win32/appxpkg/troubleshooting)) |
| F2 | Same version, different bytes or signature | `0x80073CFB ERROR_PACKAGE_ALREADY_EXISTS` ("not bitwise identical … the digital signature is also part of the package") | Always a strictly higher version | DOC + EMPIRICAL (ADR-018: sideload over a Store install refused with this code) |
| F3 | Developer → Store takeover by the Store client (installed package has no Store licence) | Store offers "Get/Install"; install upgrades in place, `SignatureKind` becomes `Store` | Rehearse first; if refused, path R with the §6.0 backups | HYPOTHESIS (the reverse direction is EMPIRICAL, ui9 §11) |
| F4 | App or tray still running (pooled `history.db` handles) | update deferred, or `0x80073D02 ERROR_PACKAGES_IN_USE` | Exit from the tray before installing; never kill by name | DOC (code) + HYPOTHESIS (Store client behaviour) |
| F5 | Widget provider relaunched by the Widgets host while a pin exists | same as F4 (seen in UI.9 preflight) | Close the Widgets board; retry; if it persists, sign out/in and install before opening Widgets. Do not unpin | EMPIRICAL (ui9 §11) |
| F6 | First launch prompts the Firewall | dialog ~2 s after start | With REL.0B: no prompt until the user starts discovery. Otherwise the human records it and answers as they choose | EMPIRICAL |
| F7 | Clean machine without .NET 10 | app/provider do not start | REL.0C runtime front (.NET inside the MSIX) | HYPOTHESIS until a clean VM run |
| F8 | Human account not in the Store audience | product not offered | Check the audience before the window | EMPIRICAL (audience model, `docs/release-automation.md`) |
| F9 | Trust removed before the Store package is in place | Developer rollback impossible | Trust removal (Phase 2) only after §7 + soak | HYPOTHESIS |
| F10 | **Older code on newer data** — rollback (b) runs 1.1.2.2 on data written by ≥ 1.1.7.0; fallback F-A runs Store 1.1.1.0 on data written by 1.1.2.2 | features silently absent, a rewrite dropping fields, a backup that cannot be restored | Prefer roll-forward. Before any downgrade: §6.0 folder copy + M14.6 backup; no server edits on the older build; "history disabled" after a downgrade is the newer-schema guard, not loss. Read-only check at the tag: `git diff v<old>..v<new> -- src/ServerMonitor.Infrastructure/Persistence src/ServerMonitor.Infrastructure/Backup src/ServerMonitor.Core/Models` | code-level facts for 1.1.1.0 in `test-certificate-removal.md` §7; runtime behaviour HYPOTHESIS (VM: UPG-STORE) |

## 6. Procedure

### 6.0 Preconditions (all, or STOP)
1. REL.0B/0C changes merged: .NET runtime inside the MSIX, discovery on demand, version floor and release
   guards, en-US default language. GitHub settings for the Store environments done by the human or
   explicitly accepted.
2. Store RC **≥ 1.1.7.0** certified and published to an audience that includes the human's account; version
   confirmed with `store-status.yml`.
3. **Rehearsal (§6.2) passed** in a disposable VM, including §4.
4. Fresh **M14.6 encrypted backup** made by the human (Settings → Data → Backup and restore), stored
   outside any cloud-synced folder.
5. **Quiescent folder copy (mandatory; the only source for history, widget state, window placement and
   ignored devices):**
   1. Exit the app from the tray (**Exit**, not close). Close the Widgets board.
   2. Confirm nothing of the package is running: `Get-Process ServerMonitor.App, ServerAlyzer.WidgetProvider -ErrorAction SilentlyContinue` returns nothing. If the provider keeps reappearing, wait for its idle exit (do not kill it).
   3. Create a **new** folder outside any cloud-synced location, restrict it to the current user, then copy
      the folder into it. `robocopy /COPY:DAT` copies no ACLs: copied files inherit the destination's ACL,
      so the restriction is set on `$Dst` **before** the copy.
      ```powershell
      $Src = Join-Path $env:LOCALAPPDATA 'ServerMonitor'
      $Dst = '<new folder outside cloud sync>\ServerMonitor-pre-transition-<yyyyMMdd>'
      $Sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
      New-Item -ItemType Directory -Path $Dst | Out-Null                  # fails if it already exists
      icacls $Dst /inheritance:r /grant:r "*${Sid}:(OI)(CI)F"              # drop inherited entries, user only
      robocopy $Src $Dst /E /COPY:DAT /DCOPY:DAT /R:0 /W:0 /NP            # exit codes 0-7 = success
      ```
      `history.db`, `history.db-wal` and `history.db-shm` must be copied together (all three or none).
   4. Verify, any failure = STOP:
      - ACL: `$a = Get-Acl -LiteralPath $Dst` → `$a.AreAccessRulesProtected` is `True` and every entry of
        `$a.Access` has `IdentityReference` translating to `$Sid`
        (`$_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -eq $Sid`);
        the same check on every file under `$Dst` (inherited from `$Dst`, so only that user's entry).
        Local administrators can still take ownership; the restriction is against other standard accounts.
      - Content: the §6.1 hash list recomputed on `$Dst` equals the one on `$Src` (same relative names,
        sizes and SHA-256).
   5. The copy contains server configuration and host-key trust but **no secrets** (those are in Credential
      Manager and in the encrypted backup). The human deletes it after the soak (§7) together with the
      M14.6 file they no longer need.
6. **Retained Developer package (only for rollback (b)):** the human confirms that the signed 1.1.2.2
   package from the UI.9 QA still exists, records its SHA-256, and checks it equals the hash approved in
   UI.9; `Get-AuthenticodeSignature` on it is `Valid`. Without this, rollback (b) does not exist. It is a
   pre-REL.0A build with local paths inside: it **never leaves the machine**.
7. Disk ≥ 13 GB free. Human GO for the real transition, today.

### 6.1 Inventory (read-only; after step 6.0.5.1–2, so no file is locked or moving)

```powershell
$Pfn = 'PedroLoy.ServerAlyzer_htb92ajfwgw1e'
$Out = '<folder outside cloud sync>'   # same place as the folder copy
Get-AppxPackage -Name PedroLoy.ServerAlyzer |
  Select-Object Version, SignatureKind, Status | Export-Csv "$Out\package-before.csv" -NoTypeInformation

$Root = Join-Path $env:LOCALAPPDATA 'ServerMonitor'
Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
  [pscustomobject]@{ Rel = $_.FullName.Substring($Root.Length); Len = $_.Length
                     Sha = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } } |
  Export-Csv "$Out\files-before.csv" -NoTypeInformation

# STOP check: anything redirected on this machine?
Test-Path "$env:LOCALAPPDATA\Packages\$Pfn\LocalCache\Local\ServerMonitor"
# credential target NAMES only, sorted (cmdkey never prints secrets; the names hold only prefix + GUIDs + kind)
cmdkey /list | Select-String -Pattern 'ServerMonitor:v1:ssh:\S+' -AllMatches |
  ForEach-Object { $_.Matches.Value } | Sort-Object -Unique |
  ForEach-Object { [pscustomobject]@{ Target = $_ } } | Export-Csv "$Out\credential-targets-before.csv" -NoTypeInformation
```

**If the `LocalCache` test is True: STOP** before any path R / fallback F-A. Copy and inventory that
redirected subtree the same way first, because uninstall deletes it; then decide with the human.

Plus: screenshot of the Widgets board (pin size/position), the language shown in Settings, the servers list.

### 6.2 Rehearsal (disposable Windows 11 VM, `rel0d-preparation.md` §3)
1. Install a **Developer-signed** 1.1.2.x of the production identity, signed with a **VM-local** test
   certificate (new human decision): created inside the VM with `-KeyExportPolicy NonExportable`, validity of
   days, trusted only in the VM's `TrustedPeople` (never `Root`), deleted with the VM — same signing and
   package-source rules as `rel0d-preparation.md` §2.2 S1–S2 (never the retained QA package). Add synthetic data
   (no real hosts or credentials), pin a Medium widget, set language to pt-PT.
2. Run §4 V1–V3 (V1 and V3 answered in §4.1 on 2026-10-18; V2 still open).
3. From the Store (private audience), install the RC. Record what the Store offers (F3).
4. Run §7. Then uninstall + install from the Store; record what survived (§3 column R).

### 6.3 Real transition (human)
1. Exit the app from the tray; close the Widgets board; §6.0.5.2 shows nothing running.
2. Microsoft Store → ServerAlyzer (`9N6ZBSBN1TD2`) → Get/Install/Update. **Do not uninstall first.**
3. If the Store reports a conflict: STOP; record the exact text and code (`Get-AppxLog`: ActivityId + HRESULT +
   first line only); no retry with other tools, no `Add-AppxPackage`. Decide with the human.
4. Launch the app once from Start. Record any Firewall dialog (F6).

### 6.4 Rollback paths

| Situation | Path | Data consequence | Available until |
|---|---|---|---|
| Store install fails before completing | nothing to do: MSIX deployment is atomic, the old version stays | none (ADR-017 §13) | — |
| Store version installed but broken | (a) **preferred**: wait for a fixed, higher Store version. (b) uninstall → re-sideload the retained 1.1.2.2 (§6.0.6) → restore from the §6.0.5 copy (files) and, if needed, the M14.6 backup | (b) = path R + F10 (1.1.2.2 on data written by the RC); pins and language override lost | (b) only while the trust entries exist **and** before **2026-11-30** (certificate expiry), and only if §6.0.6 confirmed the package |
| Takeover refused (F3) | path R: uninstall the Developer build → install the Store RC | pins and language override lost; files expected to survive on the QA machine (§6.1 STOP check passed); credentials survive | — |
| Data damaged after transition | app exited → restore the affected files from the §6.0.5 copy (history: all three `history.db*` files together); servers/trust/credentials alternatively from the M14.6 backup | as restored | as long as the copy is kept |

Because rollback (b) needs the trust entries, **trust removal (Phase 2 of the certificate retirement)
waits ≥ 48 h of normal use** after §7 passes. The deadline wins: Phase 2 is done by 2026-11-30 even with
a shorter soak, recorded as such. Key retirement (Phase 1) does not affect rollback and can happen earlier.

## 7. Acceptance after transition (run in this order)

1. `Get-AppxPackage -Name PedroLoy.ServerAlyzer` → one row, `Version` = the RC (≥ 1.1.7.0), `SignatureKind Store`, `Status Ok`, same PFN.
2. **Before opening any server:** exit the app from the tray, recompute the §6.1 file list and compare with
   `files-before.csv`: equal except `history.db*`, `widget-state.json*`, `window-placement.json` (changed by
   the §6.3 launch). No `…\Packages\<PFN>\LocalCache\Local\ServerMonitor`.
3. Credential target **names**: the sorted list (same command as §6.1, written to
   `credential-targets-after.csv`) is identical to `credential-targets-before.csv`, entry for entry (names
   only, never secret values). Open one server → connects with **no** new host-key trust prompt
   and no password prompt.
4. Original widget pin present and updating within one refresh cycle; no duplicate pin.
5. Language shown = the one before.
6. A toast click opens the existing instance; a second launch from Start focuses the first window.
7. Close the window with background monitoring on → tray keeps monitoring; reopen from Start → one
   `ServerMonitor.App.exe`. Reboot → nothing auto-starts (no StartupTask); the widget shows Stale until the
   app runs, then updates.
8. Store page shows "Open" (licensed), not "Get".
9. Firewall: no new rule unless the user started discovery; any prompt recorded.
10. Redacted evidence (no personal paths) in the internal evidence folder.

Then the certificate retirement (`test-certificate-removal.md`): Phase 1 any time after its GO, Phase 2
after the soak and by 2026-11-30.

## 8. Sources

- [Understanding how packaged desktop apps run on Windows](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes) — AppData redirection, upgrade, uninstall.
- [Troubleshooting packaging, deployment, and query of Windows apps](https://learn.microsoft.com/windows/win32/appxpkg/troubleshooting) — `0x80073CFB`, `0x80073D02`, `0x80073D06`.
- [desktop6:FileSystemWriteVirtualization](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-filesystemwritevirtualization) — opt-out needs `unvirtualizedResources`.
- Repository: ADR-017 §4/§8/§12/§13, ADR-018, `docs/ui/ui9-windows-widget-v3.md` §11/§13/§15, tag `v1.1.1`.
