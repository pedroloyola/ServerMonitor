# Test certificate removal (REL.0 C4)

The UI.9 packaged QA signed the real `PedroLoy.ServerAlyzer` identity with a local test certificate
whose subject equals the Store publisher. That certificate must leave this machine once the install is
Store-managed. This page is the procedure. **It is not an authorization**: running step 3 needs a new,
explicit human GO given for that step.

Labels: **DOC** (primary documentation), **EMPIRICAL** (measured, evidence cited), **HYPOTHESIS** (not
measured).

## 1. The certificate

| Field | Value | Label |
|---|---|---|
| Full thumbprint (SHA-1) | `529C81F5073EFF2FE37E077C9118FCDAF578ED82` | EMPIRICAL (`.boss/evidence/ui9/preqa/cert-state.txt`) |
| Subject | `CN=32C0A056-FD57-422E-A59C-A8C26434951D` (= Store `Publisher`) | EMPIRICAL |
| NotBefore / NotAfter | 2026-08-31 / **2026-11-30 10:56:17 UTC** | EMPIRICAL |
| `LocalMachine\TrustedPeople` | present, no private key | EMPIRICAL (REL.0A Gate C §3.1) |
| `CurrentUser\TrustedPeople` | present, no private key | EMPIRICAL |
| `CurrentUser\My` | present, **with exportable private key** | EMPIRICAL |
| Root / TrustedPublisher (LM and CU) | absent | EMPIRICAL |
| Depends on it | the real install `PedroLoy.ServerAlyzer 1.1.2.2`, `SignatureKind Developer` | EMPIRICAL |

Why it matters: while the key exists, anything signed with it under the Store publisher name is
trusted by this machine for sideloading (accepted risk UI9-SIGN-1, until REL.0 / 2026-11-30). While the
install stays Developer-signed it gets no Store updates.

## 2. Dates

| Date | What happens |
|---|---|
| **2026-11-23 (checkpoint)** | Boss asks the human: is the install `SignatureKind Store`? If not, choose a fallback (§5). |
| **2026-11-30 (deadline)** | Certificate expires. The removal must be done by then, whatever path was taken. |

## 3. Preconditions (all must hold; any failure = STOP, no removal)

1. A new human GO naming this step ("remove test cert 529C81F5… from the three stores").
2. The transition runbook (`store-transition-runbook.md`) has passed its acceptance checks (§7 there),
   in particular step P1 below.
3. Nothing installed for this user still depends on the certificate (P2).

### Read-only verification (normal, non-elevated PowerShell)

```powershell
$Thumb     = '529C81F5073EFF2FE37E077C9118FCDAF578ED82'
$Publisher = 'CN=32C0A056-FD57-422E-A59C-A8C26434951D'

# P1 - the real install is Store-managed
Get-AppxPackage -Name PedroLoy.ServerAlyzer |
  Select-Object Name, Version, SignatureKind, Status, PackageFamilyName
# expected: one row, Version >= the Store RC (provisionally 1.1.7.0), SignatureKind Store, Status Ok

# P2 - no package of this publisher is still Developer-signed for this user
Get-AppxPackage | Where-Object { $_.Publisher -eq $Publisher -and $_.SignatureKind -ne 'Store' } |
  Select-Object Name, Version, SignatureKind
# expected: no rows

# P3 - where the certificate is today (exact thumbprint, never a wildcard)
foreach ($s in 'Cert:\LocalMachine\TrustedPeople','Cert:\CurrentUser\TrustedPeople','Cert:\CurrentUser\My') {
  $c = Get-ChildItem -LiteralPath $s | Where-Object Thumbprint -eq $Thumb
  [pscustomobject]@{ Store = $s; Present = [bool]$c; HasPrivateKey = $c.HasPrivateKey; NotAfter = $c.NotAfter }
}
# record the output (no key material is printed) in .boss/evidence/rel0d/cert-before.txt
```

P2 is per-user. Other local user accounts are out of scope on this single-user machine; if another
account ever installed a QA build, `Get-AppxPackage -AllUsers` (elevated) answers it — run it in the
same elevated session as step 4, before removing anything.

## 4. Removal (human, elevated, after the GO)

Run in an **elevated** Windows PowerShell 5.1 or PowerShell 7 session (UAC prompt answered by the
human). Each line names the full thumbprint; no wildcard, no `-Recurse`.

```powershell
$Thumb = '529C81F5073EFF2FE37E077C9118FCDAF578ED82'
if ($Thumb -notmatch '\A[0-9A-F]{40}\z') { throw 'thumbprint must be 40 hex chars, no wildcard' }
Remove-Item -Path "Cert:\LocalMachine\TrustedPeople\$Thumb" -Confirm
Remove-Item -Path "Cert:\CurrentUser\TrustedPeople\$Thumb" -Confirm
Remove-Item -Path "Cert:\CurrentUser\My\$Thumb" -DeleteKey -Confirm   # also deletes the private key
```

- In `Cert:`, `Remove-Item` honours only `-Path`, `-DeleteKey`, `-WhatIf` and `-Confirm`; any other
  parameter (including `-LiteralPath`) is ignored (DOC: `about_Certificate_Provider`). Hence `-Path`
  with a guarded 40-hex thumbprint, and `-Confirm` so the human sees each target. A dry run with
  `-WhatIf` first is allowed.
- `-DeleteKey` deletes the associated private key (DOC, reintroduced in PowerShell 7.1; present in
  5.1). Whether it removes the key for the key storage provider used here is **HYPOTHESIS** → verified
  by V3 below.
- Never `Export-PfxCertificate`, `certutil -exportPFX`, or any "backup" of the key first. A key that is
  being retired is not backed up.
- `CurrentUser` stores do not need elevation; they are in the elevated session only so the whole step
  runs once, under one UAC.

## 5. Verification after removal

```powershell
$Thumb = '529C81F5073EFF2FE37E077C9118FCDAF578ED82'
# V1 - gone from the three stores (and still absent from Root / TrustedPublisher)
foreach ($s in 'LocalMachine\TrustedPeople','CurrentUser\TrustedPeople','CurrentUser\My',
               'LocalMachine\Root','CurrentUser\Root','LocalMachine\TrustedPublisher','CurrentUser\TrustedPublisher') {
  [pscustomobject]@{ Store = $s; Present = [bool](Get-ChildItem -LiteralPath "Cert:\$s" | Where-Object Thumbprint -eq $Thumb) }
}
# expected: Present = False everywhere

# V2 - the Store install is unaffected
Get-AppxPackage -Name PedroLoy.ServerAlyzer | Select-Object Version, SignatureKind, Status
# expected: SignatureKind Store, Status Ok. Then the human opens the app and confirms the pinned widget updates.

# V3 - the private key container is gone
certutil -user -store My $Thumb     # expected: "Cannot find object or property" / no match
```

V3 only proves the certificate is gone from `My`. Before step 4, the human may note the key container
name shown by `certutil -user -store My 529C81F5073EFF2FE37E077C9118FCDAF578ED82` ("Key Container ="),
and after step 4 confirm `certutil -user -key` no longer lists it (read-only listing).

Evidence: redacted output of P1–P3 and V1–V3 in `.boss/evidence/rel0d/` (names, versions and booleans
only; no personal paths).

## 6. Fallback if REL.0 slips (decided at the 2026-11-23 checkpoint)

| Option | What happens | Data consequences | Label |
|---|---|---|---|
| **F-A (recommended if no Store RC is live for the human's account by 2026-11-23)** | Encrypted backup (M14.6) → uninstall Developer 1.1.2.2 → install the currently published Store version from the Store → verify `SignatureKind Store` → remove the certificate (§4). REL.0 then upgrades Store → Store normally. | Same as the "uninstall + install" column of the transition runbook (§4 there): widget pins, language override and package `Settings` are lost; `%LOCALAPPDATA%\ServerMonitor` survives on this machine because the folder predates any packaged install (HYPOTHESIS, measured in REL.0D VM). | Procedure DOC; consequences partly HYPOTHESIS |
| F-B | Keep the Developer install past 2026-11-30 and remove the certificate anyway. | No data change at removal time. The installed app is expected to keep launching (signature validated at install), but repair / re-register / reinstall of 1.1.2.2 become impossible. | HYPOTHESIS — not tested, and not to be tested on this machine |
| F-C | Ask the human to extend the accepted risk. | The certificate still expires on 2026-11-30; a new test certificate would need a separate human authorization and Vigil review. | — |

Not an option: creating, renewing or re-importing a certificate, or trusting one in `Root`.

A Store package older than 1.1.2.2 (e.g. the published 1.1.1.0) **cannot** replace the Developer
install in place: it is a downgrade (`0x80073D06 ERROR_INSTALL_PACKAGE_DOWNGRADE`, DOC
[Troubleshooting packaging, deployment, and query](https://learn.microsoft.com/windows/win32/appxpkg/troubleshooting)).
That is why F-A uninstalls first.

## 7. Sources

- `.boss/BACKLOG.md` REL0-TESTCERT-REMOVAL / UI9-SIGN-1; `.boss/tmp/rel0a/relay-gate-c.md` §3;
  `docs/ui/ui9-windows-widget-v3.md` §11, §13, §15.
- [Troubleshooting packaging, deployment, and query of Windows apps](https://learn.microsoft.com/windows/win32/appxpkg/troubleshooting) (error codes).
- [about_Certificate_Provider](https://learn.microsoft.com/powershell/module/microsoft.powershell.security/about/about_certificate_provider) (`Remove-Item -DeleteKey`).
