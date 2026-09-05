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

The notes for **1.1.1**, matching what was submitted for that version.
