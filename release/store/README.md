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

Notes for **1.1.11** (package 1.1.11.0, tag `v1.1.11`). The Store still offers 1.1.9, and 1.1.10 was released
on GitHub only, so these notes describe the user-visible changes since 1.1.9: the Overview health bar is
never cut off (it moves below the server counts in a narrow window, and its last segment keeps its rounded
end), scrolled page content clears the title bar area, the Settings cards stack instead of being cut off
and are reached by Tab and screen readers in visual order, and the sidebar is slightly wider (from 1.1.10).

Known issues that are **not** fixed in 1.1.11 (large text size, switching between two high contrast
themes) are listed in `docs/release/1.1.11-known-issues.md`. Do not describe them as fixed here.
