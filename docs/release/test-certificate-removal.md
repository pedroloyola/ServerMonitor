# Test certificate retirement (REL.0 C4)

The packaged QA of the Windows widget (UI.9) signed the production identity `PedroLoy.ServerAlyzer` with a
local, self-signed **test certificate** whose subject equals the Store publisher. That certificate, its
private key and any exported copy of the key must leave the QA machine. This page is the procedure.
**It is not an authorization**: every destructive step needs a new, explicit human GO given for that step.

Labels: **DOC** (primary documentation, linked) · **EMPIRICAL** (measured in this project's internal QA
evidence) · **HYPOTHESIS** (not measured). The concrete state of the maintainer's machine (which stores,
where any key files are) is kept in internal notes, not here; this page is parameterised.

## 1. The certificate

| Field | Value |
|---|---|
| Full thumbprint (SHA-1) | `529C81F5073EFF2FE37E077C9118FCDAF578ED82` |
| Subject | `CN=32C0A056-FD57-422E-A59C-A8C26434951D` (= the package `Publisher`, already public in the manifest) |
| NotAfter | **2026-11-30 10:56:17 UTC** |
| Depends on it | the Developer-signed QA install of `PedroLoy.ServerAlyzer` (`SignatureKind Developer`) |

The thumbprint is a public fingerprint of the certificate; publishing it gives no signing capability, and
the removal must target it exactly.

Where the certificate and its key can be (the inventory in §3 checks each):

| Location | What it holds | Why it matters |
|---|---|---|
| `Cert:\LocalMachine\TrustedPeople`, `Cert:\CurrentUser\TrustedPeople` | public certificate (trust) | lets this machine accept packages signed with it — **needed for a Developer rollback** |
| `Cert:\CurrentUser\My` | certificate **+ private key** | can sign new packages under the Store publisher name |
| **Exported key files** (`*.pfx` / `*.p12`, and any file holding their password) | a portable copy of the private key | an export of this key was made when the certificate was created (EMPIRICAL, internal evidence); it is outside every certificate store, so removing store entries does **not** retire it |
| Root / TrustedPublisher (LM, CU) | must be absent | checked, never added |

## 2. Dates

| Date | What happens |
|---|---|
| any time after a human GO | **Phase 1** (key retirement) — independent of the Store transition (§4) |
| **2026-11-23 (checkpoint)** | Is the install `SignatureKind Store`? If not, choose a fallback (§7). |
| **2026-11-30 (deadline)** | Certificate expires. **Phase 2** (trust removal) must be done by then. The deadline wins over the 48 h soak: if the transition passes after 2026-11-28, Phase 2 still happens by 2026-11-30 with a shortened soak, and the shortened soak is recorded. |

## 3. Read-only inventory (normal, non-elevated PowerShell)

```powershell
$Thumb     = '529C81F5073EFF2FE37E077C9118FCDAF578ED82'
$Publisher = 'CN=32C0A056-FD57-422E-A59C-A8C26434951D'

# I1 - installs of this publisher and how they are signed
Get-AppxPackage | Where-Object Publisher -eq $Publisher |
  Select-Object Name, Version, SignatureKind, Status

# I2 - certificate stores (exact thumbprint, never a wildcard)
foreach ($s in 'LocalMachine\TrustedPeople','CurrentUser\TrustedPeople','CurrentUser\My',
               'LocalMachine\Root','CurrentUser\Root','LocalMachine\TrustedPublisher','CurrentUser\TrustedPublisher') {
  $c = Get-ChildItem -LiteralPath "Cert:\$s" | Where-Object Thumbprint -eq $Thumb
  [pscustomobject]@{ Store = $s; Present = [bool]$c; HasPrivateKey = [bool]$c.HasPrivateKey }
}

# I3 - key container of the CurrentUser\My entry (MANDATORY; listing only, no key material)
certutil -user -store My $Thumb    # record the "Provider =" and "Key Container =" / "Unique container name" lines

# I4 - exported key files: find candidates by name, never open a .pfx or a password file
#      $Roots = the folders the human names (repositories, Documents, Downloads, release/artifact folders)
Get-ChildItem -LiteralPath $Roots -Recurse -File -Force -ErrorAction SilentlyContinue -Include *.pfx,*.p12,*.cer |
  Select-Object FullName, Length, LastWriteTime
#      a .cer next to a .pfx identifies it without opening the key:
#      (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 '<file.cer>').Thumbprint
#      list sibling files that hold the PFX password (e.g. *password*.txt) by NAME only
```

Record I1–I4 as names, booleans and counts (no personal paths) in the internal evidence folder. A PFX
whose matching `.cer` has thumbprint `$Thumb`, and every file holding its password, are **in scope for
deletion**. A PFX with no matching `.cer` stays in scope if it sits with this certificate's artefacts;
it is never opened to check.

If any of these files lived in a cloud-synced folder at any time, the human also checks the cloud
service (online recycle bin, second-stage recycle bin, version history).

## 4. Recommended order: two phases (proposal; each phase needs its own human GO)

Rolling back to the Developer build (`store-transition-runbook.md` §6.4) re-installs a package that is
**already signed**: it needs only the **trust** entries (public certificate in TrustedPeople), never the
private key. No REL.0 step signs a new Developer build (the rehearsal uses a VM-local certificate). So
the key can be retired now and the trust kept until the soak ends.

| Phase | When | Removes | Keeps |
|---|---|---|---|
| **1 — key retirement** | as soon as the human gives the GO (independent of the transition) | `CurrentUser\My` entry **with its private key**; every exported PFX/P12 of it and every password file | TrustedPeople (LM, CU) — rollback still possible |
| **2 — trust removal** | after the transition acceptance (runbook §7) + 48 h soak, **by 2026-11-30** | `LocalMachine\TrustedPeople`, `CurrentUser\TrustedPeople` | nothing |

If the human prefers a single step, run Phase 1 and Phase 2 together after the soak; the commands are the
same.

### Phase 1 (human, after GO)

```powershell
$Thumb = '529C81F5073EFF2FE37E077C9118FCDAF578ED82'
if ($Thumb -notmatch '\A[0-9A-F]{40}\z') { throw 'thumbprint must be 40 hex chars, no wildcard' }
Remove-Item -Path "Cert:\CurrentUser\My\$Thumb" -DeleteKey -Confirm

# exported copies: the exact files found by I4, one by one, no wildcard, no "backup first"
Remove-Item -LiteralPath '<exact .pfx path>' -Confirm
Remove-Item -LiteralPath '<exact password file path>' -Confirm
```

Then empty the local Recycle Bin of those files (a `Remove-Item` from PowerShell does not use the bin;
deletion from Explorer does), and the cloud bins / version history if I4 found a synced folder.

- In `Cert:`, `Remove-Item` honours only `-Path`, `-DeleteKey`, `-WhatIf` and `-Confirm`; any other
  parameter (including `-LiteralPath`) is ignored (DOC
  [about_Certificate_Provider](https://learn.microsoft.com/powershell/module/microsoft.powershell.security/about/about_certificate_provider)).
  Hence `-Path` with a guarded 40-hex thumbprint, and `-Confirm`. A `-WhatIf` dry run first is allowed.
- `-DeleteKey` deletes the associated private key (DOC; reintroduced in PowerShell 7.1, present in 5.1).
  That it removes the container for this certificate's key provider is **HYPOTHESIS** → V3 is mandatory.
- Never `Export-PfxCertificate`, `certutil -exportPFX`, or any copy of the key "just in case".
- `CurrentUser` stores need no elevation. Phase 1 runs in a **normal** session.

### Phase 2 (human, elevated, after GO)

```powershell
$Thumb = '529C81F5073EFF2FE37E077C9118FCDAF578ED82'
if ($Thumb -notmatch '\A[0-9A-F]{40}\z') { throw 'thumbprint must be 40 hex chars, no wildcard' }
Remove-Item -Path "Cert:\LocalMachine\TrustedPeople\$Thumb" -Confirm   # elevated session
```
```powershell
Remove-Item -Path "Cert:\CurrentUser\TrustedPeople\$Thumb" -Confirm    # normal session of the same user
```

The `CurrentUser` line runs in the user's own non-elevated session: if UAC elevates a **different**
admin account, `CurrentUser` in the elevated window is that other account's store.

Preconditions for Phase 2 (all, or STOP): I1 shows exactly one row, `SignatureKind Store`, `Status Ok`;
no other package of this publisher is Developer-signed (for other local accounts, `Get-AppxPackage
-AllUsers` in the elevated session, before removing anything); runbook §7 passed; soak done or deadline
reached.

## 5. Verification (mandatory; any failure = FAIL, escalate to the human)

```powershell
$Thumb = '529C81F5073EFF2FE37E077C9118FCDAF578ED82'
# V1 - stores (after Phase 1: My absent; after Phase 2: all absent)
foreach ($s in 'LocalMachine\TrustedPeople','CurrentUser\TrustedPeople','CurrentUser\My',
               'LocalMachine\Root','CurrentUser\Root','LocalMachine\TrustedPublisher','CurrentUser\TrustedPublisher') {
  [pscustomobject]@{ Store = $s; Present = [bool](Get-ChildItem -LiteralPath "Cert:\$s" | Where-Object Thumbprint -eq $Thumb) }
}

# V2 - exported copies gone (each exact path from I4)
Test-Path -LiteralPath '<exact .pfx path>'            # False
Test-Path -LiteralPath '<exact password file path>'   # False

# V3 - private key container gone: the container name recorded in I3 must not appear
certutil -user -key | Select-String -SimpleMatch '<container name from I3>'   # no match
certutil -user -store My $Thumb                                              # not found

# V4 (after Phase 2) - the Store install is unaffected
Get-AppxPackage -Name PedroLoy.ServerAlyzer | Select-Object Version, SignatureKind, Status
# SignatureKind Store, Status Ok; the human opens the app and confirms the pinned widget updates
```

If V3 still finds the container, the key outlived `-DeleteKey`: STOP and ask the human (deleting a key
container directly is a separate, reviewed step). Evidence: redacted V1–V4 output in the internal
evidence folder.

## 6. Why both copies matter

A private key plus the public certificate (extractable from any package signed with it) is a signing
capability under the Store publisher name. Removing the trust stops this machine from accepting such
packages, and expiry limits the window, but the retirement goal is that **no copy of the key remains**.

## 7. Fallback if REL.0 slips (decided at the 2026-11-23 checkpoint)

Prefer **roll-forward**: get the Store release candidate certified and published to the human's audience.
The options below are for when that is impossible in time.

| Option | What happens | Data consequences | Label |
|---|---|---|---|
| **F-B (preferred fallback)** | Keep the Developer install; do Phase 1 now and Phase 2 by 2026-11-30. | No data change. The installed app is expected to keep launching (signature validated at install), but repair / re-register / reinstall of the Developer build become impossible; no Store updates until the transition. | HYPOTHESIS (not tested, and not to be tested on the real machine) |
| **F-A (last resort)** | Uninstall the Developer build → install the **currently published** Store version → upgrade Store → Store later. | **Older code on newer data**, see below. | consequences partly HYPOTHESIS |
| F-C | Ask the human to extend the accepted risk. | The certificate still expires on 2026-11-30; a new test certificate needs separate authorization and security review. | — |

Not an option: creating, renewing or re-importing a certificate, or trusting one in `Root`.

A Store version older than the installed Developer build cannot replace it in place: that is a downgrade
(`0x80073D06 ERROR_INSTALL_PACKAGE_DOWNGRADE`, DOC
[Troubleshooting packaging, deployment, and query](https://learn.microsoft.com/windows/win32/appxpkg/troubleshooting)),
which is why F-A uninstalls first.

### F-A hazard: the published Store version is older than the data

The last published Store package is 1.1.1.0 (tag `v1.1.1`, 2026-09-05). The data on the QA machine was
written by 1.1.2.2. Checked against the tag (`git show v1.1.1:<path>`, read-only):

| Data / feature | In 1.1.1.0? | Expected effect under 1.1.1.0 | Label |
|---|---|---|---|
| Routed servers (`routed-servers.json`, `known-hosts.routes.json`, route credentials) | **No** (added 2026-09-29, `041ed4d`) | routed servers are **invisible**; 1.1.1.0 never opens those files, so they stay on disk untouched; their credentials stay in Credential Manager | code EMPIRICAL (absent at tag); "untouched" HYPOTHESIS |
| Direct servers (`servers.json`) | yes | read; a save from 1.1.1.0 rewrites the file from its own model, dropping fields it does not know (System.Text.Json ignores unknown members) | HYPOTHESIS → do **not** edit servers under 1.1.1.0 |
| M14.6 encrypted backup / restore, `restore-journal\` | **No** (added 2026-09-30, `c4653aa`) | a `.serveralyzer-backup` **cannot be restored** by 1.1.1.0; an unfinished restore journal is not recovered | code EMPIRICAL |
| History (`history.db`, schema `user_version` 2) | yes, same schema 2; newer-schema guard present ("History disabled; data preserved") | readable | code EMPIRICAL; runtime HYPOTHESIS |
| Widget pins, language override | lost by the uninstall | re-pin, re-pick | HYPOTHESIS strong |

So F-A requires, **before** the uninstall: (a) the quiescent folder copy of runbook §6.0 step 5
(including `history.db` + `-wal` + `-shm`), outside any synced folder, hash-verified; (b) a fresh M14.6
backup — kept for the later upgrade, since 1.1.1.0 cannot restore it; (c) no pending restore; (d) no
server edits under 1.1.1.0 until the Store → Store upgrade. Measure this behaviour in the VM first
(`rel0d-preparation.md` matrix UPG-STORE with 1.1.2.x data).

## 8. Sources

- [Troubleshooting packaging, deployment, and query of Windows apps](https://learn.microsoft.com/windows/win32/appxpkg/troubleshooting) — error codes.
- [about_Certificate_Provider](https://learn.microsoft.com/powershell/module/microsoft.powershell.security/about/about_certificate_provider) — `Remove-Item -DeleteKey`, accepted parameters.
- Repository: `docs/ui/ui9-windows-widget-v3.md` §11, §13, §15; tag `v1.1.1`.
