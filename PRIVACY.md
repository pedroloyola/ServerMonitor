<!--
DRAFT (REL.0E). Every "[NEEDS HUMAN: ...]" marker must be resolved by the publisher and removed before
this URL is entered in Partner Center (release/store/*/support.txt keeps its GATED line until then).
-->

# ServerAlyzer Privacy Policy

**English** · [Português (Portugal)](PRIVACY.pt-PT.md) · [Português (Brasil)](PRIVACY.pt-BR.md)

_Last updated: 2026-10-16 · applies to ServerAlyzer 1.1.7 and later for Windows_

ServerAlyzer is a **local-first** Windows app for monitoring your own Linux and macOS servers over SSH. It is
designed so that your data stays on your PC and under your control.

## In short

- **No account.** There is no sign-up, sign-in or user identifier.
- **No ServerAlyzer servers.** The app has no backend and sends nothing to the developer: no metrics, no server
  list, no credentials, no usage data.
- **No telemetry, analytics, crash reporting or advertising** in the app.
- **No cloud.** There is no cloud sync and no online account.
- **The only network connections the app makes on its own initiative** are SSH connections to the servers you
  add and, only when you start it, a search of your local network (see "Local network search").
- **Name resolution.** To connect to a server you entered by name, Windows resolves that name through your
  network's DNS service, or through mDNS for `.local` names. For a server reached through a jump host, the
  jump host resolves the server's name. No ServerAlyzer service is involved.

## Who is responsible

ServerAlyzer is published on the Microsoft Store by **[NEEDS HUMAN: publisher legal name and country]**.
The app keeps all data on your device and transmits nothing to the publisher, so the publisher does not
receive or hold your data. **[NEEDS HUMAN: confirm this position with a legal reviewer, including whether a
data controller and contact details under the GDPR or the LGPD must be named here.]**

Contact: open an issue at <https://github.com/pedroloyola/ServerMonitor/issues>. Report security issues
privately through GitHub Security Advisories on the same repository (Security tab), not in a public issue.
**[NEEDS HUMAN: add a privacy contact e-mail address if one is required.]**

## How monitoring works

- ServerAlyzer connects **directly** from your PC to each server you add, over SSH. If you set up a jump host
  for a server, the connection goes through that jump host. No ServerAlyzer service sits in between.
- Before it trusts a server, the app shows the server's host key fingerprint and asks you to confirm it.
  Trusted keys are stored on your PC.
- The app runs **read-only** commands: system metrics (CPU, memory, disk, uptime, host name, OS version), the
  Docker container list and the list of system services (systemd or launchd). It does not install anything,
  change settings or use `sudo` on your servers.
- It connects while the app is open and, if you turn on background monitoring, while it runs in the
  notification area.

## What is stored on your PC

All data is stored locally, in a folder named `ServerMonitor` in your user account's local application data.
When the app is installed from the Microsoft Store, Windows keeps that folder inside the app's own package data
folder (`%LOCALAPPDATA%\Packages\<package name>\LocalCache\Local\ServerMonitor`). The app does not store data
in a roaming profile and does not sync it anywhere.

| Data | File | Contents |
|---|---|---|
| Servers | `servers.json`, `routed-servers.json` | Name, host, port, SSH user, sign-in method, the **path** of a private key you choose, jump host, and server preferences (operating system, refresh interval, hidden or not, date added, an internal reference to the saved password). No passwords. |
| Trusted host keys | `known-hosts.json`, `known-hosts.routes.json` | Host, port, key type and fingerprint. |
| Metrics history | `history.db` (SQLite) | Per server: time, health state, CPU %, memory %, disk %. Kept for **30 days**, then deleted automatically. You can clear it in Settings. |
| Widget snapshot | `widget-state.json` | For the Windows widget: an internal server id, the server's display name (never its host or IP address), health, CPU, memory and disk usage (percent, and GB used and total), uptime and last update time. |
| Ignored network devices | `ignored-devices.json` | One-way fingerprints (SHA-256 hashes) of devices you chose to ignore. No names or addresses. |
| Preferences | `language-settings.json`, `notification-settings.json`, `background-settings.json`, `window-placement.json` | Language, notification and background monitoring choices, window position. |
| Restore safety copy | `restore-journal` folder | A temporary copy used only while a backup is being restored, so that an interrupted restore can be undone. |

**Passwords and key passphrases** are stored in **Windows Credential Manager**, for your Windows account on
this PC. They are never written to the files above. The contents of private key files are never copied: the
app keeps only the path you chose.

Docker container and service lists are shown in the app and are not saved.

The app writes **no log files**.

## Files the app reads on your PC

- `%USERPROFILE%\.ssh\config`, and the files it includes from inside `%USERPROFILE%\.ssh`, only when you choose
  **Import from SSH**. Included files outside that folder, and linked files, are not read.
- To suggest a key, the app checks whether the default key files `id_ed25519`, `id_ecdsa` or `id_rsa` exist in
  `%USERPROFILE%\.ssh`, using file information only (name and size). It does not open them. A key file is read
  only to sign in to a server you configured with that key.

## Local network search

- You can ask ServerAlyzer to look for SSH servers that announce themselves on your local network (mDNS/DNS-SD,
  service type `_ssh._tcp`). The search **never starts on its own**, only when you choose **Search local
  network**.
- While it runs, the app sends standard mDNS queries on your local network and listens for answers (UDP port
  5353). It does **not** scan ports or address ranges, and it does not connect to the devices it finds.
- The search stops when you stop it, leave the page or hide the window; after about **40 seconds** if it finds
  nothing new; and in any case after **10 minutes**.
- Found devices (name, host name, addresses, port) are kept **in memory only** and are discarded when the
  search ends. Additional (TXT) announcement data is ignored.
- The first time you search, **Windows Firewall may ask** whether to allow ServerAlyzer on your networks. The
  app needs this to receive the answers. If you do not allow it, the search may find nothing; everything else
  keeps working. Windows saves your answer as Windows Firewall rules for the app, which stay after the search;
  you can review or remove them in the Windows Firewall settings.
- Adding a found server goes through the same connection test and host key confirmation as any other server.

## Encrypted backup

- If you create a backup, the file contains your server list, routes, trusted host keys, notification and
  background monitoring settings, **and your saved passwords and passphrases**.
- The file's contents are encrypted with **AES-256-GCM**, with a key derived from a backup password you choose
  (PBKDF2-HMAC-SHA256, 1,000,000 iterations). The file cannot be read without that password, and it cannot be
  recovered if you forget it. A short header with the technical encryption parameters (format version,
  algorithm, number of iterations, salt) is stored unencrypted, as this method requires; it contains none of
  your data.
- The file is saved **only where you choose**. The app never uploads it. If you save it in a cloud-synced folder,
  that service's terms apply to the file.
- Private key files and the metrics history are not included.

## Notifications and widget

- Alerts are local Windows notifications. They show the server's display name and its state (for example "is
  in a warning state") and stay in the Windows notification center until they expire or you clear them. The
  app uses no push notification service.
- The ServerAlyzer widget shows the data in `widget-state.json`. To display it, the app hands the card content
  (display name, health, metrics, uptime) to the Windows Widgets board, a Microsoft component.
  **[NEEDS HUMAN: confirm whether data handed to the Widgets board is subject to the Microsoft Privacy
  Statement, and say so here.]**

## Windows and Microsoft components

- The app is distributed and updated through the Microsoft Store. Installs, updates, ratings and reviews are
  handled by Microsoft under the Microsoft Privacy Statement.
- The app is built on Windows and the Windows App SDK. Diagnostic data that Windows itself collects is governed
  by your Windows privacy settings and the Microsoft Privacy Statement, not by this policy. **[NEEDS HUMAN:
  confirm this wording about Windows and Windows App SDK diagnostic data.]**
- The package declares the `runFullTrust` capability, which desktop apps of this kind need in order to run as
  a regular Windows desktop app. ServerAlyzer uses it only for what this policy describes. **[NEEDS HUMAN:
  confirm this wording.]**

## Links that open your browser

Links in the app, such as the GitHub project page in Settings, open in your web browser only when you click
them. The privacy terms of that website apply there.

## Open-source components

ServerAlyzer includes open-source components that run on your PC and send nothing to their authors: SSH.NET,
Tmds.MDns, Microsoft.Data.Sqlite and SQLitePCLRaw (SQLite), WinUIEx, the Windows App SDK, .NET libraries and
Hugeicons icons. Licences are listed in `THIRD-PARTY-NOTICES.md` in the repository.

## Your control over your data

- In the app you can remove a server, clear the metrics history (**Clear history**) and show ignored devices
  again (**Show ignored devices again**).
- You can also view and remove saved passwords in Windows **Credential Manager**.
- **Uninstalling:** **[NEEDS HUMAN: confirm by measuring an uninstall of the Store package. Expected: Windows
  removes the app's package data folder with the files listed above, while passwords and passphrases in
  Credential Manager remain until you remove them.]** Backup files you saved elsewhere are never touched.

## Children

**[NEEDS HUMAN: decide whether a statement about children is needed. The app is a technical tool and collects
no data from anyone.]**

## Changes to this policy

Changes are published in this file with a new "Last updated" date, and the file's history is public in the
repository. If an optional cloud feature is ever added, it will be described here before it is released, and
it will be **opt-in**.
