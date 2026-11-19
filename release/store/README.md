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

## Website changelog

The version PR also adds `docs/site-changelog/<version>.json`: the same change for the changelog on
serveralyzer.com, in en, pt-PT, pt-BR and es (format in `docs/site-changelog/README.md`). The website
publishes it, and moves to the new version, once the Store offers that version.

## What must never go in here

* Credentials of any kind. Certification test credentials belong in Partner Center under
  *Additional Testing Info* and stay there.
* Internal QA detail, defect IDs, agent or reviewer names, or anything from `.boss/`.
* Anything about unreleased plans.

These files are read by `tools/release/Update-StoreSubmission.ps1` and end up on the public
Store listing.

## Current contents

Notes for **1.1.13** (package 1.1.13.0, tag `v1.1.13`). 1.1.11 is pending in the Store and 1.1.12 was released on
GitHub only, so these notes describe the user-visible changes since 1.1.11: the server detail CPU card shows the
1, 5 and 15 minute load average next to the CPU count on Linux and macOS (never 0 when the server does not report
it), the 1-minute load average is recorded in the local history (both from 1.1.12), and the server detail can open
extra per-server pages added by modules, with a way back to that server (the existing screens do not change).

Known issues that are **not** fixed in 1.1.13 (large text size, switching between two high contrast
themes) are listed in `docs/release/1.1.13-known-issues.md`. Do not describe them as fixed here.
