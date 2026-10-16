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

ServerAlyzer is developed and published on the Microsoft Store by **Pedro Loyola**, a natural person resident in
Portugal, under the publisher name **Pedro Loy** (the "publisher").

Contact for privacy questions: **serveralyzer@pedroloyola.com**. You can also open an issue at <https://github.com/pedroloyola/ServerMonitor/issues>. Report security issues
privately through GitHub Security Advisories on the same repository (Security tab), not in a public issue.

## What the publisher can and cannot receive

- **Data processed only on your PC.** Everything described in "What is stored on your PC", "Files the app reads
  on your PC", "Local network search", "Encrypted backup" and "Notifications and widget" is processed by the app
  on your PC. The app never sends it to the publisher, so the publisher never receives, holds or sees it.
- **Data the publisher may receive.** Only:
  - what you choose to send: an e-mail to the address above, or an issue, comment or security report on GitHub
    (on GitHub, GitHub's own terms and privacy statement also apply);
  - the reports, ratings and reviews that Microsoft makes available to Store publishers (see "Windows and
    Microsoft components").
- **Data under Microsoft's responsibility.** The Microsoft Store, Windows diagnostic data and the Widgets board
  are run by Microsoft under the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement),
  not under this policy.
- **The publisher's obligations.** For the data in the second point, the publisher is the controller under the
  EU General Data Protection Regulation (GDPR). You can ask him, at the e-mail address above, for access to that
  data, its correction or its deletion, and you can lodge a complaint with a data protection supervisory
  authority (in Portugal, the Comissão Nacional de Proteção de Dados, CNPD). **[NEEDS HUMAN: a legal reviewer
  confirms whether the purpose, legal basis and retention period for this correspondence and these reports must
  be stated here, and their wording; and whether the Brazilian LGPD also applies.]**

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
  (an internal server id, display name, health, metrics, uptime) to Windows on your PC, which shows it in the
  Widgets board, a Windows feature provided by Microsoft. ServerAlyzer sends it to no online service. Microsoft
  states that Windows diagnostic data is collected from the Widgets board according to your Windows diagnostic
  data settings, and that each widget is controlled by the settings of the app that powers it, here ServerAlyzer
  ([Microsoft Support: Widgets](https://support.microsoft.com/windows/stay-up-to-date-with-widgets-in-windows-7ba79aaa-dac6-4687-b460-ad16a06be6e4)).

## Windows and Microsoft components

- The app is distributed and updated through the Microsoft Store. Installs, updates, ratings and reviews are
  handled by Microsoft under the Microsoft Privacy Statement.
- **Reports Microsoft gives the publisher.** Through Partner Center, Microsoft gives Store publishers reports it
  compiles from Store and Windows data, for example: Store page views, installs and uninstalls, active devices,
  sessions and their duration, broken down by market, device type, Windows version and app version; crash and
  hang counts and rates, with technical failure details such as stack traces (the crash and hang rates use
  devices that share optional diagnostic data); and the ratings and reviews published in the Store
  ([Partner Center analytics](https://learn.microsoft.com/windows/apps/publish/analyze-app-performance/msix), [Health report](https://learn.microsoft.com/windows/apps/publish/analyze-app-performance/health-report)). ServerAlyzer adds nothing to these
  reports: it does not use the Microsoft Store Services SDK and logs no custom events. **[NEEDS HUMAN: once the
  app is live, the publisher checks read-only in Partner Center → Insights that these are the reports shown for
  ServerAlyzer, and updates this paragraph if they are not.]**
- **Diagnostic data.** The app is built on Windows and the Windows App SDK, a Microsoft runtime installed with
  it. Windows may send diagnostic data to Microsoft, including reports of app crashes and hangs, according to
  your Windows diagnostic data settings and under the Microsoft Privacy Statement. ServerAlyzer adds nothing to
  it.
- ServerAlyzer is a Windows desktop app and declares the Microsoft Store `runFullTrust` capability, so it runs
  as a regular desktop program rather than inside the Store app sandbox. It needs this to connect over SSH to
  the servers you add, to look for servers on your local network only when you start a search, to keep your
  server passwords and passphrases in Windows Credential Manager on your device, and to provide notifications,
  the notification-area (tray) icon and the Windows widget. This capability does not give the app administrator
  rights.

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
- **Uninstalling:** Windows removes the app's package data folder with the files listed above. Two things stay
  until you remove them yourself:
  - **The SSH passwords and passphrases you saved**, in Windows Credential Manager. The entry names contain no
    server name, address or user name. To remove them, open **Control Panel** → **Credential Manager** →
    **Windows Credentials** → **Generic Credentials** and remove each entry that starts with
    `ServerMonitor:v1:ssh` (or, if you used an early version, `pedroloyola.ServerMonitor:v1:ssh`).
  - **The Windows Firewall rules named `ServerMonitor.App`**, if you chose **Allow** when Windows Firewall asked
    during a local network search. Windows creates these rules; the app neither creates nor removes them. To
    remove them, open **Windows Security** → **Firewall & network protection** → **Advanced settings** →
    **Inbound Rules** and delete the rules named `ServerMonitor.App`.

  The app never changes your `.ssh` folder. Backup files you saved elsewhere are never touched.

## Children

ServerAlyzer is a technical tool for the general public and is not directed at children. It has no features
aimed at children and collects no personal data from anyone, including children.

## Changes to this policy

Changes are published in this file with a new "Last updated" date, and the file's history is public in the
repository. If an optional cloud feature is ever added, it will be described here before it is released, and
it will be **opt-in**.
