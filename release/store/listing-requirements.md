# Microsoft Store listing — field rules and sources

Rules for the listing text kept under `release/store/<lang>/`. Limits are quoted from Microsoft Learn
(MSIX app pages, read 2026-10-14). If Partner Center shows a different limit, Partner Center wins:
update this file.

Only `whats-new.txt` is sent by the pipeline (`tools/release/Update-StoreSubmission.ps1`). Every
other file here is the reviewed source for text a person enters in Partner Center by hand.

## Files per language

| File | Partner Center field | Limit | Source |
|---|---|---|---|
| `description.md` | Description (required) | 10,000 characters, plain text. No HTML, code or URLs. If there is no short description, the first paragraph (up to 500 characters) is shown at the top instead. | [1], [1] FAQ |
| `short-description.txt` | Short description (supplemental, optional) | 1,000 characters. Only the first 270 are shown in some views, so keep it under 270. | [1] |
| `features.txt` | Product features | Up to 20, no more than 200 characters each. One feature per line. The Store adds the bullets, so do not add your own. | [1] |
| `whats-new.txt` | What's new in this version | 1,500 characters, enforced by the pipeline. | [1], `README.md` |
| `keywords.txt` | Keywords (formerly Search terms) | Up to 7, no more than 40 characters each, and no more than 21 words across all keywords. Not shown to customers. Only relevant terms; avoid other companies' brand names and filler such as "free" or "best". | [2] |
| `screenshot-captions.txt` | Screenshot caption | 200 characters or less per screenshot. Captions are entered per language. | [3] |
| `support.txt` | Support contact info, Website, Privacy policy (Properties page); Additional system requirements; Copyright and trademark info | See below | [4], [5], [1], [2] |

`description.md` keeps the `.md` extension so it can be reviewed in pull requests, but it holds
plain text. Headings are lines of their own, with no `#`, `**` or `-`.

`screenshot-captions.txt` has one line per screenshot, `NN-slug: caption`. The `NN-slug` part
matches the screenshot file name and is not part of the caption.

### `support.txt` fields

| Field | Rule | Source |
|---|---|---|
| Website | Optional, recommended. Must point to a page on your own website, not the Store listing. | [4] |
| Support contact info | URL or email address. Optional for PC apps, recommended. Microsoft does not support the app for you. | [4] |
| Privacy policy | Required if the app accesses, collects or transmits personal information, or if the package capabilities could allow it. One policy is used for every market. | [4], [5] |
| Additional system requirements | Up to 11 items each for Minimum and Recommended hardware, no more than 200 characters each. The Store adds the bullets. | [1] |
| Copyright and trademark info | 200 characters. | [2] |
| Developed by | 255 characters. "Published by" always shows the publisher display name. | [2] |
| Additional license terms | Leave blank to use the Standard Application License Terms. Otherwise one URL, or up to 10,000 characters of plain text. | [2] |

In `support.txt`, each field is a block: the field label on the first line, then its value.

## Screenshots

| Rule | Value | Source |
|---|---|---|
| Format | PNG, landscape or portrait, no larger than 50 MB | [3] |
| Desktop size | 1366 × 768 pixels or larger; 4K (3840 × 2160) supported. This project captures at 1920 × 1080. | [3] |
| Count | At least 1 to submit; up to 10 desktop screenshots. At least 4 suggested. | [3], [1] |
| Per language | Upload images and captions separately for each listing language | [3] |
| Content | Keep critical visuals and text in the top two-thirds, because overlays may cover the bottom third. Do not add extra logos, icons or marketing messages. | [3] |

Screenshots show only synthetic data: no real host names, addresses, user names or keys. They show
only features that are in the release being submitted.

**Folder conflict.** `Update-StoreSubmission.ps1` treats every folder under `release/store/` as a
listing language and stops if one has no `whats-new.txt`. A `release/store/screenshots/` folder
would therefore stop every Store submission. Final screenshots go in
`release/store-screenshots/<lang>/NN-slug.png`, outside `release/store/`, with no change to the
script.

Each caption must describe exactly one screen. Slot 07 is Settings. Slot 09 is Settings › Data and
servers (backup and restore).

## Category

Category is required. For apps that are not games, you choose one category, at most one
subcategory, and optionally one secondary category. Relevant options [6]:

- **Developer tools**: Database, Design tools, Development kits, Networking, Reference + training,
  Servers, Utilities, Web hosting.
- **Utilities + tools**: Backup + manage, File managers.

## Copy rules for this product

Each listing must say:

- what ServerAlyzer is and who it is for: people who run homelab or self-hosted servers, and
  sysadmins;
- that it monitors Linux and macOS servers directly from the PC over SSH, with no agent;
- that it runs on Windows 11 only. It monitors Macs, but there is no macOS app.

Do not claim:

- "offline" or "fully offline". SSH needs the network.
- anything about uninstall behaviour or where data is stored, until it has been measured on a
  Store install.
- paid or "Pro" features. Every shipped feature is free.
- features that are not merged.
- statements that depend on a release gate.

Each language is written for its own readers, not translated literally:

- **pt-PT**: "tu", with pt-PT vocabulary (ficheiro, ecrã, palavra-passe, Definições).
- **pt-BR**: "você", with pt-BR vocabulary (arquivo, tela, senha, Configurações).
- **en-US**: plain and professional.

Feature names follow the app's own UI strings in that language.

## Gated lines

These lines are kept out of the listing until their gate passes. They follow the same gates as the
DRAFT-GATE block in `README.md`.

- **".NET included / no need to install .NET"**: not in the description, features or requirements.
  It may be added only after the clean-machine test (CLEAN-1) passes, as `whats-new.txt` already
  requires.
- **Restoring a backup on another computer**: not claimed. The backup text says "restore them
  later" until BACKUP-XM passes.
- **Local network search entry points** (`description.md`, "Local network search" paragraph, all
  three files). The current text says the search is available "with no servers added yet". That is
  true while the empty Overview is the only entry point. If the pull request that adds the search
  to Settings › Data and servers (#52) is merged before submission, replace that sentence pair in
  all three files with the text below, and reconcile `whats-new.txt` through its own DRAFT-GATE
  block. "Only when you ask" stays true either way.
  - en-US: "You can search your local network for SSH servers that announce themselves (mDNS/DNS-SD), from the empty Overview or from Settings › Data and servers. The search runs only when you choose it and never scans ports or address ranges."
  - pt-PT: "Podes procurar na tua rede local servidores SSH que se anunciam (mDNS/DNS-SD), a partir da Visão geral vazia ou em Definições › Dados e servidores. A pesquisa só corre quando a escolhes e nunca varre portas nem intervalos de endereços."
  - pt-BR: "Você pode procurar na sua rede local servidores SSH que se anunciam (mDNS/DNS-SD), a partir da Visão geral vazia ou em Configurações › Dados e servidores. A pesquisa só é feita quando você escolhe e nunca varre portas nem faixas de endereços."
  - Check the Settings labels against the merged resource strings before pasting.
  - **Applied (REL.0E, 2026-10-16):** #52 is merged, so the sentence pair above is in all three
    files, with the labels checked against the resource strings. One sentence was added for the
    bounded search (O-7/O-8): it ends after about 40 seconds if no new server is found, and after
    10 minutes at most. `whats-new.txt` was reconciled the same way.
- **Privacy policy URL (submission gate)**: `support.txt` in all three files carries the URL with
  a `GATED:` line. Do not enter it in Partner Center, and do not submit the listing, until the
  gate is cleared. REL.0E rewrote the policy: `PRIVACY.md` (English, the single URL the Store uses
  for every market) plus `PRIVACY.pt-PT.md` and `PRIVACY.pt-BR.md`. It now covers the on-demand
  local network search, the encrypted backup (which includes saved passwords), the widget
  snapshot, notifications, logs (none) and the Store data location. The gate stays until:
  - every `[NEEDS HUMAN: ...]` marker in the three files is resolved and removed. R2
    (2026-10-16) resolved publisher identity, privacy contact, Widgets board, children and
    Uninstalling (measured in the QA VM); R3 resolved the `runFullTrust` wording; R6 resolved
    the Partner Center reports (owner's read-only check); R8 wrote the final internal GDPR
    Art. 12-14 and LGPD wording with recommended proposals. Still open: the owner's final
    approval of that section (one NEEDS HUMAN line per file);
  - the policy is merged on main, so the URL resolves publicly.

  Then remove the `GATED:` lines.
- **Widget screenshot (slot 08)**: it can only be captured from an installed package in the VM.
  If no VM capture exists at submission time, remove the `08-widget` line from all three caption
  files. Slot 09 keeps its name; only the order of the uploaded images matters.
  - **Applied (REL.0E, 2026-10-16):** there is no 08 capture, so the `08-widget` line was removed
    from all three caption files. Add it back only together with a VM widget capture.

## Sources

1. Add and edit Store listing info for MSIX app — https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info
2. Add additional information for MSIX app — https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/add-additional-information
3. App screenshots, images, and trailers for MSIX app — https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/screenshots-and-images
4. Support info for MSIX app — https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/support-info
5. Enter app properties for MSIX app — https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/enter-app-properties
6. Categories and subcategories for MSIX app — https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/categories-and-subcategories
