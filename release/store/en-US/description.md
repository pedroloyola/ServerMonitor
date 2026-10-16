ServerAlyzer is a calm, compact Windows 11 dashboard for the Linux and macOS servers you run yourself: a homelab, a few self-hosted services, or the machines you look after as a sysadmin. It connects to each server directly from your PC over SSH. There is no agent to install on your servers, no account to create and no ServerAlyzer cloud in between.

Every server at a glance
The Overview shows how each server is doing, points out the one that needs attention and tells you when a reading is out of date. The Servers list adds CPU, memory and disk for each server, and each server's page shows its uptime. Servers are checked automatically in the background and you can refresh at any time. When a value cannot be read, ServerAlyzer says so instead of showing zero.

History on your PC
CPU, memory and disk readings are kept in a local history for 30 days. Charts cover the last hour up to the last 30 days.

Services and containers, read-only
From each server's page, see its Docker containers and system services (systemd on Linux, launchd on macOS). ServerAlyzer only reads: it never starts, stops or changes anything on your servers.

SSH you can trust
Before a server is trusted, ServerAlyzer shows its SHA-256 host key fingerprint and asks you to confirm it. If the key changes later, the connection is blocked. Passwords and passphrases are kept in Windows Credential Manager. For key authentication only the location of your private key is saved, never its contents. On your servers the app runs a fixed set of read-only commands, with no sudo and no commands you type in.

Quick to set up
A first-run guide helps you add your first server. You can fill in a server from a host in your SSH config file, pick one of your standard private keys, and use a connection test that shows each step so you can see what failed. Servers reached through a single jump host (ProxyJump) are supported.

At home in Windows 11
ServerAlyzer can keep running in the notification area when you close the window, and can tell you with Windows notifications when a server's health changes. Compact mode gives you a small view of all your servers that can stay always on top. The Windows 11 widget shows your servers on the Widgets board in three sizes and always shows how old its data is. Light, dark and system themes, keyboard navigation, screen readers and high contrast are supported. Available in English, Portuguese (Portugal) and Portuguese (Brazil).

Local network search, only when you ask
You can search your local network for SSH servers that announce themselves (mDNS/DNS-SD), from the empty Overview or from Settings › Data and servers. The search runs only when you choose it and never scans ports or address ranges. It ends on its own after about 40 seconds if no new server is found, and after 10 minutes at most; you can also stop it at any time.

Encrypted backup
Save your servers, trusted host keys and saved passwords to a file protected by a passphrase you choose, and restore them later. Private key files are never included.

Private by design
No account, and ServerAlyzer collects no telemetry. Your server list, history and saved credentials stay on your PC, unless you export an encrypted backup, which is saved wherever you choose. ServerAlyzer uses the network only to reach the servers you add (and your local network when you start a search); it does not send your data to ServerAlyzer.

Requirements
A Windows 11 PC (x64). The widget needs Windows 11 version 22H2 or later. Your servers must run Linux or macOS and be reachable over SSH from your PC; service lists need systemd (Linux) or launchd (macOS). ServerAlyzer runs on Windows only: it monitors Macs, but there is no macOS version of the app.
