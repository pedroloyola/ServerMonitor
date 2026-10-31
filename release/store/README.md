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

Notes for **1.1.8** (package 1.1.8.0, tag `v1.1.8`). They replace the 1.1.7 text and describe only
the user-visible changes in that release: the stale data age on the Servers page, the announced
end of a local network search, a rare case in which an alert could be held back after a server
stopped being monitored and was monitored again, and the clearer Portuguese example text in the server editor.

**DRAFT-GATE — check before submission.** The screen reader sentences (paragraph two, and the last clause
of the "Por exemplo:" paragraph, in all three files) are true in the code but must also be confirmed
by the human Narrator session (stale data age, end of search, example text). If a check fails,
remove or narrow the matching sentence in all three files, and if paragraph two goes, also edit
the first sentence.

Known issues that are **not** fixed in 1.1.8 (large text size, switching between two high contrast
themes, theme choice not remembered) are listed in `docs/release/1.1.8-known-issues.md`. Do not
describe them as fixed here.
