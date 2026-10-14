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
| CLEAN-1 | PRE + POST | Clean install, **no .NET anywhere** (§2.2 step T2) | §2.2: cases R1–R5 PASS and counterproof X1 FAILS as expected; POST: the same R1–R5 on the Store install, `SignatureKind Store` | runtime change |
| CLEAN-2 | PRE | Clean install, .NET 10 Desktop runtime present | §2.2 R1–R5 (no conflict with a global runtime) | runtime change |
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

`Get-NetUDPEndpoint` also lists outbound UDP **client** sockets (e.g. DNS, SSH helpers), which are bound
to a wildcard address on an ephemeral port; those are not listeners. Exact criteria:

- **FAIL** if any of these holds: a UDP endpoint with `LocalPort` 5353 (any address); a TCP socket in
  state `Listen` (any address, any port); a Firewall dialog; `(Get-NetFirewallRule).Count` differs
  before vs after.
- **INVESTIGATE** (record, not FAIL by itself): a UDP endpoint on `0.0.0.0`/`::` with a port outside the
  dynamic range 49152–65535; any wildcard UDP endpoint that persists across all samples. It becomes FAIL
  only if it coincides with a dialog or rule delta, or is shown to receive unsolicited inbound traffic.
- **PASS**: no FAIL and every INVESTIGATE item explained.

Evidence: one CSV per sample series (timestamp, PID, protocol, address, port, state), plus the two rule
counts.

Observation rule: the tester **records** any dialog (screenshot, time, exe path). In a disposable VM they
may answer it; on the maintainer's machine agents never answer it.

### 2.2 Clean-machine runtime procedure (CLEAN-1, CLEAN-2)

Goal: prove that the package starts — app, widget provider (COM, activated by the system), background
start and notification activation — on Windows 11 with **no .NET installed**, and that the check is able to
fail (counterproof X1). Two disposable VMs (§3): a **signing VM** (has the .NET SDK, Git and the Windows
SDK signing tools; builds and signs) and a **test VM** (clean; only receives signed packages and a public
certificate). Nothing from the maintainer's machine is copied into either VM except a Git checkout.

**S1 — Package source (gate: the tester records which source was used; any other source = STOP).**

| Package | Allowed source | Not a source |
|---|---|---|
| Candidate (`P-RC`) | (a) after a tag: the `release-build` workflow artifact `serveralyzer-msix-<version>`, downloaded by the human (`gh run download <run-id> -n serveralyzer-msix-<version>`), SHA-256 equal to the run summary; (b) before a tag: a local build in the signing VM of the candidate commit (below) | the CI `package-privacy` job (it builds the package but uploads no artifact); any package built on the maintainer's machine; Debug builds |
| Counterproof (`P-X`) | a local build in the signing VM of commit **`e5ceb26`** (framework-dependent, before the runtime change; package version 1.1.1.0) | the retained 1.1.2.2 QA package (pre-privacy-gate, never leaves the maintainer's machine) |
| Windows App Runtime set (`P-WAR`) | the **complete** 2.3.1 set — Framework, Main, Singleton, DDLM — from one source of that exact version: (a) the folder `tools/MSIX/win10-x64/` of the `Microsoft.WindowsAppSDK.Runtime` NuGet package at the version in `ServerMonitor.App.csproj` (2.3.1): `Microsoft.WindowsAppRuntime.2.msix`, `Microsoft.WindowsAppRuntime.Main.2.msix`, `Microsoft.WindowsAppRuntime.Singleton.2.msix`, `Microsoft.WindowsAppRuntime.DDLM.2.msix` (identities listed in that folder's `MSIX.inventory`); or (b) Microsoft's official Windows App Runtime installer for 2.3.1 (`WindowsAppRuntimeInstall-x64.exe`), which installs the same set | the framework alone; mixed versions; the build's `Dependencies` folder alone (framework only); the Store (only for the POST variant) |

Local build in the signing VM, for each commit (`<commit>` = the candidate or `e5ceb26`). Install the **same
.NET SDK version** that CI used for the candidate commit (read it from that CI run's "Setup .NET" step) and
record it: the packaged build embeds the runtime bundled with the SDK, and the privacy allowlist has
version-specific entries for the .NET runtime (today 10.0.11–10.0.13). A different SDK can embed a runtime
the allowlist does not cover, and the privacy gate then fails **closed, correctly** — that is a source
mismatch to fix (use the CI SDK), never a reason to edit the allowlist for the test.

```powershell
git clone https://github.com/pedroloyola/ServerMonitor.git sm; cd sm; git checkout <commit>
dotnet build src/ServerMonitor.App/ServerMonitor.App.csproj -c Release -p:Packaged=true --no-incremental --disable-build-servers
$Msix = Get-ChildItem -Recurse -Filter *.msix src/ServerMonitor.App/AppPackages | Where-Object FullName -notmatch '\\Dependencies\\'
# gates of that commit, on that package - both must PASS
./tools/release/Assert-PackageIdentity.ps1 -PackagePath $Msix.FullName -ExpectedVersion '<manifest version>'
New-Item -ItemType Directory out | Out-Null; Copy-Item $Msix.FullName out/
./tools/release/Assert-DistributionPrivacy.ps1 -Path out -AllowlistPath tools/release/distribution-privacy-allowlist.json
```

If the candidate commit has a packaged-runtime check (`tools/release/Assert-PackageRuntime.ps1`), run it too.
Record the embedded runtime version: the `includedFrameworks` version in `ServerMonitor.App.runtimeconfig.json`
inside the package. The release artifact must later carry the **same** runtime version as the package
tested here; otherwise this procedure is repeated with the release artifact.

**S2 — Signing (signing VM only; human authorization + security review).** The packages are unsigned by
design (`AppxPackageSigningEnabled=false`). Sign them with a certificate created **inside the signing VM**:

```powershell
$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=32C0A056-FD57-422E-A59C-A8C26434951D' `
  -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(7) `
  -CertStoreLocation Cert:\CurrentUser\My `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
Export-Certificate -Cert $cert -FilePath vm-test-signing.cer      # PUBLIC part only
signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My <P-RC.msix>
signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My <P-X.msix>
```

The subject must equal the package `Publisher` (production identity). `P-WAR` is Microsoft-signed and is
not re-signed. Never import the DEV certificate, the QA-machine certificate or any `.pfx` into a VM.
Transfer to the test VM: `P-RC`, `P-X`, `P-WAR`, `vm-test-signing.cer`, and their SHA-256 list.

**T1 — Test VM snapshot `clean`** (Windows 11 x64 ≥ 22621.1413, updated, Firewall notifications on).

**T2 — No-.NET check (gate; every line must hold, else this VM is not clean for CLEAN-1):**

```powershell
$hits = @()
foreach ($p in "$env:ProgramFiles\dotnet", "${env:ProgramFiles(x86)}\dotnet",
               "$env:USERPROFILE\.dotnet", "$env:LOCALAPPDATA\Microsoft\dotnet") { if (Test-Path $p) { $hits += $p } }
foreach ($k in 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions',
               'HKLM:\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions',
               'HKCU:\SOFTWARE\dotnet\Setup\InstalledVersions') { if (Test-Path $k) { $hits += $k } }
foreach ($scope in 'Machine','User','Process') {
  foreach ($v in 'DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ROOT(x86)') {
    if ([Environment]::GetEnvironmentVariable($v, $scope)) { $hits += "${scope}:$v" } } }
if (Get-Command dotnet -ErrorAction SilentlyContinue) { $hits += 'dotnet on PATH' }
$hits    # must be empty
$WarSet = { Get-AppxPackage | Where-Object Name -match 'WindowsAppRuntime|WinAppRuntime' |
            Sort-Object Name, Version | Select-Object Name, Version, Status }
& $WarSet | Export-Csv war-before.csv -NoTypeInformation   # record (inbox apps may already bring some)
```

The registry `InstalledVersions\<arch>\InstallLocation` value and `DOTNET_ROOT` are how a
framework-dependent app finds a global runtime outside the default folder, so all of them are checked.

**T3 — Trust and install (test VM, elevated, after T2):**

```powershell
Import-Certificate -FilePath vm-test-signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople

# 1) Windows App Runtime 2.3.1, framework first (Main, Singleton and DDLM depend on it)
$War = '<folder with the P-WAR set>'
Add-AppxPackage -Path "$War\Microsoft.WindowsAppRuntime.2.msix"
Add-AppxPackage -Path "$War\Microsoft.WindowsAppRuntime.Main.2.msix"
Add-AppxPackage -Path "$War\Microsoft.WindowsAppRuntime.Singleton.2.msix"
Add-AppxPackage -Path "$War\Microsoft.WindowsAppRuntime.DDLM.2.msix"
#    (or, instead of these four lines: the official WindowsAppRuntimeInstall-x64.exe for 2.3.1)

# 2) verify the set before installing the app - all four present at 2.3.1.0, Status Ok
& $WarSet | Export-Csv war-after.csv -NoTypeInformation
#    expected names (from MSIX.inventory): Microsoft.WindowsAppRuntime.2, MicrosoftCorporationII.WinAppRuntime.Main.2,
#    MicrosoftCorporationII.WinAppRuntime.Singleton, Microsoft.WindowsAppRuntime.DDLM.2  - each Version 2.3.1.0

# 3) the app
Add-AppxPackage -Path <P-RC.msix>
Get-AppxPackage PedroLoy.ServerAlyzer | Select-Object Name, Version, Status
```

Any failure in step 1 or 2 (for example `0x80073CF3`, a missing prerequisite) is an **environment** failure:
record the code and stop; R1–R5 are not run on an incomplete runtime set.

Why the full set: the framework package is the only one an app manifest can declare. Main and Singleton
cannot be declared; the Windows App SDK deploys them through its Deployment API, which the default
auto-initializer calls at app start (`WindowsAppSdkDeploymentManagerInitialize` defaults to `true`), and
Singleton is needed for features outside the framework (DOC, [deployment guide for framework-dependent
packaged apps](https://learn.microsoft.com/windows/apps/windows-app-sdk/deploy-packaged-apps)). The app
code notes that `AppNotificationManager` may need Singleton in the unpackaged deployment
(`App/Services/WindowsAppNotificationService.cs:534-537`); whether the packaged toast path (R4/R5) needs it =
HYPOTHESIS, which the full set makes irrelevant for this test.
Installing the set up front removes "runtime set incomplete" as an explanation for an R1–R5 failure.

**Cases, in this order** (each: PASS / FAIL / NOT_RUN; no case is skipped silently):

| ID | Case | Steps | Expected |
|---|---|---|---|
| R1 | **Cold widget activation, app never launched** | right after T3, without opening the app: Win+W → Add widgets → ServerAlyzer → pin Medium | the provider process starts (`Get-Process ServerAlyzer.WidgetProvider`), the card renders its no-data state; no console window, no ".NET" dialog, no error card |
| R2 | First app launch | Start → ServerAlyzer | main window opens; About shows the candidate version; exactly one `ServerMonitor.App` process; no ".NET" dialog |
| R3 | `--background` start | tray → Exit; then `Invoke-CommandInDesktopPackage -PackageFamilyName PedroLoy.ServerAlyzer_htb92ajfwgw1e -AppId ServerMonitor -Command "$((Get-AppxPackage PedroLoy.ServerAlyzer).InstallLocation)\ServerMonitor.App.exe" -Args '--background'` | tray icon, no window, one process, no dialog. Equivalence of this launch to a system background launch = HYPOTHESIS |
| R4 | Toast, warm | needs a disposable test SSH host on the VM network (human-provided; synthetic account). Add it, wait for Healthy, stop its SSH service → Offline toast (alerts fire only on transitions from a known state, `Core/Alerts/ServerAlertPolicy.cs`); click it while the app runs | the existing instance comes to the front with the server; no second process |
| R5 | Toast, cold | restart then stop the host's SSH service to get a new toast; tray → Exit; click the toast in Notification Center | the app starts once and opens the server; one process |

Without a test SSH host, R4/R5 are **NOT_RUN** (never PASS).

**X1 — Counterproof** (restore snapshot `clean`, repeat T2, then T3 with `P-X` instead of `P-RC`):
- launch from Start → expected: the .NET host's "install .NET" dialog and no window (exact dialog text =
  HYPOTHESIS; screenshot it);
- pin the widget → expected: the provider does not activate (no card / error card).

X1 counts only if the failure is the **.NET host's**: the .NET host's install-.NET dialog, and/or a `.NET
Runtime` event, and/or a host error code `0x80008083` (`CoreHostLibMissingFailure`, a hosting component
such as hostfxr is missing) or `0x80008096` (`FrameworkMissingFailure`, no compatible framework) — DOC
[host error codes](https://github.com/dotnet/runtime/blob/main/docs/design/features/host-error-codes.md);
which of the two the apphost reports when no .NET exists at all = HYPOTHESIS, record the one seen.

| X1 outcome | Verdict |
|---|---|
| .NET host failure as above | **PASS** (the counterproof can fail, so CLEAN-1's PASS means something) |
| app starts normally | **CLEAN-1 invalid**: T2 missed a .NET install (FAIL of the procedure, not of the product) |
| any other failure (Windows App Runtime / DeploymentManager / bootstrap error, `0x80073CF3`, signature or deployment error) | **INCONCLUSIVE**: fix the environment (P-WAR set, trust) and rerun X1 from `clean` |

**Evidence for every case** (redacted; no personal paths):
```powershell
$since = (Get-Date).AddMinutes(-10)
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } |
  Where-Object ProviderName -in '.NET Runtime','Application Error','Windows Error Reporting' |
  Select-Object TimeCreated, ProviderName, Id, Message
Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-DistributedCOM'; StartTime = $since } |
  Select-Object TimeCreated, Id, Message
Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-AppModel-Runtime/Admin'; StartTime = $since } -ErrorAction SilentlyContinue |
  Select-Object TimeCreated, Id, Message
```
plus screenshots (card, window, any dialog), process lists, and the T2 output.

**CLEAN-2:** restore `clean`, install the official .NET 10 Desktop Runtime x64, then T3 with `P-RC` and
R1–R5. Expected: identical results (the package's own runtime is used; no conflict).

**POST variant (Store, private audience):** restore `clean`, T2 (including `war-before.csv`), install from
the Store (no S2/T3), then R1–R5 and `SignatureKind Store`. X1 is not repeated. Record the Windows App
Runtime set **after the install and again after R2** (`& $WarSet`, `war-store-install.csv`,
`war-store-after-launch.csv`) and compare with the PRE set:
- the **framework** package is installed by the Store as the declared `PackageDependency` — DOC (deployment
  guide above: the package dependency "ensures that the Framework package is installed when your app is
  deployed");
- **Main and Singleton** arrive through the Deployment API at first app start, not from the manifest — DOC
  for the mechanism; that the Store install plus our auto-initializer yields the same four packages as the
  PRE set = HYPOTHESIS, measured by these two recordings;
- **DDLM** = HYPOTHESIS (record whether it appears).

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
   `CN=32C0A056-FD57-422E-A59C-A8C26434951D`, created inside the disposable **signing VM** with
   `-KeyExportPolicy NonExportable`, validity of days; only its **public** `.cer` goes to the test VM,
   trusted there in `LocalMachine\TrustedPeople` (never `Root`); both VMs are deleted afterwards. No copy of
   the DEV or the QA-machine certificate or key, ever. Needs human authorization and security review
   (§2.2 S2).
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
