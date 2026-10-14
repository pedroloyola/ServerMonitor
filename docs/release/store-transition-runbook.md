# Store transition runbook: Developer 1.1.2.2 → Store-managed (REL.0 C3)

The real install on the QA machine is a **Developer-signed** build of the production identity
(UI.9 decision H4). REL.0 must replace it with the **Store-signed** package, keep the user's data, and only
then retire the test certificate (`test-certificate-removal.md`). This runbook is the procedure. **It does
not authorize anything**: the rehearsal, the real transition and any uninstall each need an explicit
human GO.

Target version: **≥ 1.1.7.0 (provisional, human decision H-VER)**, Revision 0, tag `v1.1.7`.
1.1.3.0–1.1.6.0 were already installed in local QA (Firewall rules prove it), 1.1.2.2 is installed now.

Labels on every claim: **DOC** = primary documentation (linked) · **EMPIRICAL** = measured in this
repository's history (evidence cited) · **HYPOTHESIS** = not measured; says how to measure it.

## 1. Current state

| Item | Value | Label |
|---|---|---|
| Installed package | `PedroLoy.ServerAlyzer` **1.1.2.2**, x64, `SignatureKind Developer`, Status Ok | EMPIRICAL (REL.0A closeout §14, Gate C §2.1) |
| PFN / Publisher | `PedroLoy.ServerAlyzer_htb92ajfwgw1e` / `CN=32C0A056-FD57-422E-A59C-A8C26434951D` | EMPIRICAL; identical to the Store identity (ADR-017 §4) |
| Signer | test cert `529C81F5…ED82`, expires 2026-11-30 | EMPIRICAL |
| Last Store package recorded | 1.1.1.0 (submission 4, recorded "PUBLIC/PUBLISHED" in `.boss/STATE.md` M13); audience must be re-read before REL.0D (`store-status.yml`, human dispatch) | EMPIRICAL record, current audience UNVERIFIED |
| `%LOCALAPPDATA%\ServerMonitor` on this machine | created by unpackaged dev builds **before** any packaged install | EMPIRICAL (history of M1–M11 unpackaged builds; UI.9 instruments saw packaged writes land in the real folder, ui9 doc §11) |
| .NET 10 runtime on this machine | machine-wide (`Program Files\dotnet`, 10.0.12) — hides REL0B-DOTNET-RUNTIME here | EMPIRICAL (B1 brief) |

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
| Toast activator | COM CLSID `4B2E9C7A-8F3D-4E1A-9B6C-2D5F7A1E3C48` + `desktop:ToastNotificationActivation` (manifest) | `Package.appxmanifest:78-92` |
| Protocol | `serveralyzer:` | `Package.appxmanifest:94-104` |
| Single instance | `AppInstance.FindOrRegisterForKey("ServerMonitor")`; no named mutex | `App/Services/SingleInstancePolicy.cs:12`; `App/Program.cs:112-207` |
| StartupTask | **none** (no `windows.startupTask`, no `StartupTask` API use) | manifest; `docs/ui/ui9-windows-widget-v3.md:120` |
| Package `Settings` / `LocalState` | not used by code (no `ApplicationData.Current`) — only Windows-owned state such as the language override | grep of `src/` |
| Logs / dumps | none on disk (Debug output / ETW only) | `App.xaml.cs:79`; `WidgetProvider/Diagnostics/EtwWidgetProviderLog.cs` |

Manifest declares neither `desktop6:FileSystemWriteVirtualization` nor `RegistryWriteVirtualization`, and
the opt-out needs the restricted capability `unvirtualizedResources` that is meant for certain games
only (DOC [desktop6:FileSystemWriteVirtualization](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-filesystemwritevirtualization)).
So default MSIX behaviour applies (§4).

## 3. Survival matrix

Two paths: **(U) in-place upgrade** — Store installs a higher version over the Developer package, same
PFN — and **(R) uninstall + install**.

| Item | (U) in-place upgrade | (R) uninstall + install |
|---|---|---|
| Files in `%LOCALAPPDATA%\ServerMonitor\` (servers, routed, known-hosts(+routes), history(+WAL/SHM), ignored devices, notification/background settings, window placement, widget-state(+.bak), restore-journal) | **Survive.** EMPIRICAL for Developer→Developer (UI.9 V3: PostInstall 0 differences; only the 3 expected files changed during QA). DOC: package updates keep app data ([behind the scenes](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes): "All writes are kept during package upgrade"). Developer→Store specifically: HYPOTHESIS (rehearse, §6). | **On this machine: expected to survive** — the folder predates every packaged install, so it is the real folder, not a redirected one (HYPOTHESIS strong, §4). **On a machine where the Store package created it: deleted** with the package if virtualized (§4, DOC + HYPOTHESIS). |
| Credential Manager targets | Survive (vault is per user, not per package) — HYPOTHESIS strong; UI.9 two-channel instrument saw no CredMan change across Developer updates (EMPIRICAL for U). | Survive — HYPOTHESIS strong (CredWrite is not package-redirected; DOC lists only files and HKCU as virtualized). Measure in rehearsal. |
| `PrimaryLanguageOverride` | Survives — HYPOTHESIS strong (package state kept on update) | **Lost** — HYPOTHESIS strong (per-package state removed). User re-picks language; first run resolves from OS culture (C2 front changes that rule to en-US base). |
| Package `Settings` / `LocalState` | Survive (DOC: kept on upgrade) | Lost (DOC: removed on uninstall) — app keeps nothing there |
| HKCU writes | Kept (DOC: "kept during package upgrade") | Deleted (DOC) — app writes none deliberately |
| Widget pins | Survive — EMPIRICAL Developer→Developer ("The original pin survived the update", ui9 §11); Developer→Store HYPOTHESIS | **Lost** — HYPOTHESIS strong (host drops widgets of removed packages); human re-pins |
| `widget-state.json` content | Survives (file) — provider re-reads on start | File survives with the folder; board starts empty until re-pinned |
| StartupTask | N/A — not declared | N/A |
| Single-instance key | Unaffected (runtime registration only; dies with the process) | Unaffected |
| Toast COM / `serveralyzer:` protocol / widget COM server | Re-registered from the new manifest; CLSIDs unchanged → existing toasts in Action Center keep routing (HYPOTHESIS) | Unregistered on uninstall, registered on install; old toasts in Action Center may stop activating (HYPOTHESIS, harmless) |
| Windows Firewall rules | New version = new `WindowsApps\…_<ver>_…` path → **new prompt** on first listen unless the B2 front lands (discovery only on user action) — EMPIRICAL (one Allow pair per installed version path, Gate C §1.2) | Same; old rules are not removed by uninstall (EMPIRICAL: rules of uninstalled versions still present) |
| Test certificate | Unaffected (removed later, separately) | Unaffected |

## 4. AppData write virtualization — the open question

**DOC** ([Understanding how packaged desktop apps run on Windows](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes), ms.date 2026-09-30), for virtualized packaged desktop apps on Windows 10 1903+:
- "All newly created files and folders in the user's `AppData` folder … are written to a private per-user,
  per-app location; but merged at runtime to appear in the real `AppData` location", which "enables the
  system to clean up those files when the app is uninstalled".
- "Modifications to existing files under the user's `AppData` folder is allowed".
- On open, "the OS will open the file from the per-user, per-package location first … If the file is
  opened from the real `AppData` location, then no virtualization for that file occurs."
- Uninstall removes "any redirected writes to `AppData` or the registry".

**Consequences:**

| Machine | Expected | Label |
|---|---|---|
| This QA machine (folder created by unpackaged builds long before packaging) | Reads and writes hit the real `%LOCALAPPDATA%\ServerMonitor` — consistent with UI.9, whose out-of-package instruments saw `history.db`/`widget-state.json`/`window-placement.json` change in the real folder | EMPIRICAL for existing files; **new** files created inside the existing real folder (e.g. a first `routed-servers.json`, `restore-journal\`, `.corrupt-*`) = HYPOTHESIS |
| A clean machine (Store user, folder first created by the packaged app) | The whole `ServerMonitor` folder lives under `%LOCALAPPDATA%\Packages\PedroLoy.ServerAlyzer_htb92ajfwgw1e\LocalCache\Local\ServerMonitor\`; the app and the widget provider (same package) both see it at the real path; **uninstall deletes all of it**. Contradicts ADR-017 §12 ("uninstall does not remove data"). | HYPOTHESIS (DOC strongly implies; whether a full-trust `packagedClassicApp` without a `uap10:TrustLevel` attribute counts as "virtualized" is the unmeasured part) |

**How to measure (REL.0D rehearsal VM only, never on this machine):** after first launch and adding one
synthetic server, from an **unpackaged** PowerShell (outside the package):

```powershell
$Pfn = 'PedroLoy.ServerAlyzer_htb92ajfwgw1e'
Test-Path "$env:LOCALAPPDATA\ServerMonitor\servers.json"                        # real
Test-Path "$env:LOCALAPPDATA\Packages\$Pfn\LocalCache\Local\ServerMonitor\servers.json"  # redirected
# same view from inside the package (what the app sees):
Invoke-CommandInDesktopPackage -PackageFamilyName $Pfn -AppId ServerMonitor `
  -Command 'cmd.exe' -Args '/c dir "%LOCALAPPDATA%\ServerMonitor" > "%TEMP%\in-package-view.txt"'
```

Run three cases: (V1) clean profile, folder absent; (V2) real folder pre-created empty before first
launch; (V3) real folder pre-existing with data + one file created later by the app. Then uninstall and
repeat the two `Test-Path` lines. Record which path held each file and what survived.

**Product consequence if V1 confirms:** Store users lose servers, known hosts and history on uninstall
(credentials stay in Credential Manager as orphans). Options for a separate human/ADR decision: document it
(privacy-friendly clean uninstall) and correct ADR-017 §12 / PRIVACY.md; or move to explicit package
storage; the M14.6 encrypted backup is the user-facing safety net either way. **Not** a change for
REL.0 tonight.

## 5. Failure modes during the transition

| ID | Failure | Expected signal | Handling | Label |
|---|---|---|---|---|
| F1 | Store version ≤ 1.1.2.2 | `0x80073D06 ERROR_INSTALL_PACKAGE_DOWNGRADE` | Version floor (C1/G1 fronts) blocks this before submit; never submit ≤ floor | DOC ([troubleshooting](https://learn.microsoft.com/windows/win32/appxpkg/troubleshooting)) |
| F2 | Same version, different bytes/signature (e.g. a Store 1.1.2.2) | `0x80073CFB ERROR_PACKAGE_ALREADY_EXISTS` — "not bitwise identical … the digital signature is also part of the package" | Always a strictly higher version | DOC + EMPIRICAL (ADR-018: sideload over Store 1.0.1.0 refused with this code) |
| F3 | Developer → Store takeover by the Store client (no Store licence on the installed package) | Store page shows "Get/Install" instead of "Open"; install upgrades in place, `SignatureKind` becomes `Store` | Rehearse in VM first; if the Store refuses or offers nothing, fall back to path (R) with backup | HYPOTHESIS (no Microsoft page states it; the reverse direction is EMPIRICAL, ui9 §11) |
| F4 | App or tray still running (`--background`/tray keeps pooled `history.db` handles) | update deferred, or `0x80073D02 ERROR_PACKAGES_IN_USE` | Human exits via tray → "Exit" before installing; never kill by name | DOC (code) + HYPOTHESIS (Store client behaviour) |
| F5 | Widget provider relaunched by the Widgets host (COM out-of-proc, activated through DCOM) while a pin exists | same as F4; UI.9 preflight hit it (F-4: "provider relaunched by the host") | Close the Widgets board; retry; if it persists, sign out/in and install before opening Widgets. Do not unpin (pins are what we want to keep) | EMPIRICAL (ui9 §11 F-4) |
| F6 | First launch of the Store version prompts the Firewall | Windows Firewall dialog ~2 s after start | If B2 is merged: no prompt until the user starts discovery. Otherwise: human records it and answers as they choose; agents never answer | EMPIRICAL (UI9-FW-PROMPT) |
| F7 | Clean machine without .NET 10 | app/provider do not start | B1 front (self-contained .NET in the MSIX); invisible on this machine (machine-wide .NET 10) | HYPOTHESIS until clean VM run |
| F8 | Human account not in the Store audience | product not visible / "not available" | Check audience with `store-status.yml` before the window | EMPIRICAL (audience model, release-automation.md) |
| F9 | Certificate removed before the Store package is in place | repair/re-register of 1.1.2.2 impossible | Cert removal is strictly after §7 acceptance and a soak period | HYPOTHESIS (Gate C §3.2) |

## 6. Procedure

### 6.0 Preconditions (all, or STOP)
1. REL.0B/0C fronts merged: B1 (.NET runtime), B2 (discovery on demand), C1/G1 (version floor, release
   guards), C2 (en-US default). G1 GitHub settings (branch protection, status-env reviewer) done by the
   human or explicitly accepted.
2. Store RC **≥ 1.1.7.0** certified and published to an audience that includes the human's account
   (human pressed "Publish now"); version confirmed with `store-status.yml`.
3. **Rehearsal (§6.2) passed** in a disposable VM, including §4 measurements.
4. Fresh **M14.6 encrypted backup** made by the human (Settings → Data → Backup and restore;
   `.serveralyzer-backup`, PBKDF2 + AES-256-GCM, includes servers, routed servers, referenced known-hosts
   and **credential secrets**; does **not** include history, widget state, window placement, ignored
   devices). Stored outside OneDrive. Plus the existing UI.9 QA backup under
   `%LOCALAPPDATA%\ServerAlyzer-QA-Backup\ui9` stays untouched.
5. Disk ≥ 13 GB free.
6. Human GO for the real transition, today.

### 6.1 Inventory before (read-only; run by the human or by Boss with the human's GO)

```powershell
$Pfn = 'PedroLoy.ServerAlyzer_htb92ajfwgw1e'
Get-AppxPackage -Name PedroLoy.ServerAlyzer | Select-Object Version, SignatureKind, Status, InstallLocation
# data: names, sizes, SHA-256 only - never contents; output kept outside OneDrive
Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\ServerMonitor" -Recurse -File |
  ForEach-Object { [pscustomobject]@{ Rel = $_.FullName.Substring($env:LOCALAPPDATA.Length); Len = $_.Length;
                   Sha = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }
# is anything already redirected? (expected: False on this machine)
Test-Path "$env:LOCALAPPDATA\Packages\$Pfn\LocalCache\Local\ServerMonitor"
# credential target NAMES only (no secrets)
cmdkey /list | Select-String -SimpleMatch 'ServerMonitor:v1:ssh'
# firewall rules for the package (count + paths)
Get-NetFirewallApplicationFilter | Where-Object Program -like '*pedroloy.serveralyzer_*' | Measure-Object
```

Plus: screenshot of the Widgets board (pin size/position), the language shown in Settings, and the
servers list. The UI.9 two-channel instrument (C# + Python oracle) is the preferred comparator if Boss
re-authorises it; otherwise the hash list above.

### 6.2 Rehearsal (disposable Windows 11 VM, see `rel0d-preparation.md` §3)
1. Install a **Developer-signed** 1.1.2.x of the production identity (needs a VM-local test signing
   certificate — **new human decision**, the real key is never exported), add synthetic data (no real
   hosts, no real credentials), pin a Medium widget, set language to pt-PT.
2. Run §4 V1–V3.
3. From the Store (private audience), install the RC. Record what the Store page offers (F3).
4. Run §7 checks. Then uninstall + install from the Store; record what survived (§3 column R).

### 6.3 Real transition (human)
1. Exit the app from the tray (Exit, not close). Close the Widgets board. Wait ~1 min.
2. Microsoft Store → ServerAlyzer (`9N6ZBSBN1TD2`) → Get/Install/Update. **Do not uninstall first.**
3. If the Store reports a conflict: STOP, note the exact text and code (`Get-AppxLog` first lines only,
   ActivityId + HRESULT), no retry with other tools, no `Add-AppxPackage`. Decide path (R) with Boss.
4. Launch the app once from Start. Observe and record any Firewall dialog (F6).

### 6.4 Rollback paths

| Situation | Path | Data consequence |
|---|---|---|
| Store install fails before completing | Nothing to do: MSIX deployment is atomic; 1.1.2.2 stays | none (DOC: updates are atomic, ADR-017 §13) |
| Store version installed but broken | (a) wait for a fixed Store version (preferred); (b) uninstall → re-sideload Developer 1.1.2.2 from the retained QA package (only while the test cert is still trusted) → restore M14.6 backup if needed. A Developer build cannot be installed over a higher Store version in place (F1). | (b) = path R: pins and language override lost; files survive on this machine (§4) |
| Takeover impossible (F3 refused) | Path R: backup → uninstall 1.1.2.2 → install Store RC | pins, language override lost; files expected to survive here; credentials survive |
| Data damaged after transition | M14.6 restore (servers, trust, credentials, 2 settings); history not recoverable from backup — copy-of-folder backup from §6.0 is the only source | as restored |

Because rollback (b) needs the test certificate, **certificate removal waits ≥ 48 h of normal use** after
§7 passes, and still before 2026-11-30.

## 7. Acceptance after transition (all must pass)

1. `Get-AppxPackage -Name PedroLoy.ServerAlyzer` → exactly one row, `Version` = the RC (≥ 1.1.7.0),
   `SignatureKind Store`, `Status Ok`, same PFN.
2. Hash list of `%LOCALAPPDATA%\ServerMonitor` equal to §6.1 except `history.db*`, `widget-state.json*`,
   `window-placement.json` (the UI.9 expected-change set). No `…\Packages\<PFN>\LocalCache\Local\ServerMonitor`.
3. Credential target names identical to §6.1; open one server → connects with **no** new host-key
   trust prompt and no password prompt.
4. Original widget pin present and updating within one refresh cycle; no duplicate pin.
5. Language shown = the one before (override survived).
6. Notifications: a toast click opens the existing instance (single instance); second launch from Start
   focuses the first window.
7. Close the window with background monitoring on → tray keeps monitoring; reopen from Start → same
   process (one `ServerMonitor.App.exe`). Reboot → nothing auto-starts (no StartupTask), the pinned widget
   shows Stale until the app runs, then updates.
8. Store page shows "Open" (licensed), not "Get".
9. Firewall: no new rule unless the user started discovery (B2); if a prompt appeared, recorded.
10. Evidence (redacted, no personal paths) in `.boss/evidence/rel0d/`.

Then: certificate removal (`test-certificate-removal.md`), after the soak period.

## 8. Sources

- [Understanding how packaged desktop apps run on Windows](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes) — AppData redirection, upgrade, uninstall.
- [Troubleshooting packaging, deployment, and query of Windows apps](https://learn.microsoft.com/windows/win32/appxpkg/troubleshooting) — `0x80073CFB`, `0x80073D02`, `0x80073D06`.
- [desktop6:FileSystemWriteVirtualization](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-filesystemwritevirtualization) — opt-out needs `unvirtualizedResources`.
- Repository: ADR-017 §8/§12/§13, ADR-018 (0x80073CFB), `docs/ui/ui9-windows-widget-v3.md` §11/§13/§15,
  `.boss/tmp/rel0a/relay-gate-c.md` §2–§3, `.boss/reports/rel0a-closeout.md`.
