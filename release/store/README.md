# Store listing metadata

One folder per Microsoft Store listing language. Each holds the **What's new in this
version** text for the release currently being prepared.

```
release/store/
  en-US/whats-new.txt
  pt-BR/whats-new.txt
  pt-PT/whats-new.txt
```

The folder name is matched case-insensitively against the listing keys in the submission
JSON (`en-us`, `pt-br`, `pt-pt`).

## Rules

* Plain text. The Store field renders no Markdown, so no `**bold**`, no headings, no lists
  built from `-`. Write paragraphs separated by blank lines.
* 1500 characters maximum, enforced by the pipeline before anything is sent.
* Every folder here must contain a non-empty `whats-new.txt`. A missing or empty file stops
  the release rather than shipping a language with blank notes.
* Each language is written for its own audience: `pt-PT` and `pt-BR` are not the same text.
* Update these files as part of the version bump, in the same commit, so the notes are
  reviewable in the pull request rather than typed into a web form at release time.

## What must never go in here

* Credentials of any kind. Certification test credentials belong in Partner Center under
  *Additional Testing Info* and stay there.
* Internal QA detail, defect IDs, agent or reviewer names, or anything from `.boss/`.
* Anything about unreleased plans.

These files are read by `tools/release/Update-StoreSubmission.ps1` and end up on the public
Store listing.

## Current contents

Draft notes for the next release candidate, covering what changed since 1.1.x. Not submitted yet;
review them again before the version bump.

**DRAFT-GATE — check each line before submission.** Some sentences are only true once a gate
passes. Rows refer to `docs/release/rel0d-preparation.md` §2. If a gate has not passed, edit the
sentence in all three files as described, then re-check the 1500-character limit.

* **.NET included** (last paragraph, all three files): keep it only if the self-contained .NET
  change (PR #48) is merged **and** CLEAN-1 passes, with counterproof X1 failing as expected.
  Otherwise remove the paragraph.
* **Local network search on request** (search paragraph, all three files): keep it only if the
  on-demand discovery change (PR #47) is merged. Re-read it against the merged behaviour: an
  empty Overview, the button label, and that users who already have servers get no suggestions.
  Otherwise remove the paragraph.
  **Re-read (REL.0E, 2026-10-16):** #47 and #52 are merged. The paragraph now names both entry
  points (an empty Overview, Settings > Data and servers) and the bounded search (about 40 seconds
  when no new server is found, 10 minutes at most). The old "no suggestions if you already have
  servers" sentence was removed: a user with servers can now search from Settings.
* **Language** (language paragraph): needs the language change (PR #46) merged. Otherwise remove
  the paragraph.
* **Backup on another computer:** the text deliberately says only "restore them later". If
  BACKUP-XM passes, you may mention restoring on another computer, but then also say that servers
  using a key file need that file on the other computer, if the length allows.
