# Website changelog

One file per release, `<version>.json`, with the changelog entry shown on
[serveralyzer.com/changelog](https://serveralyzer.com/changelog/) in English, European Portuguese,
Brazilian Portuguese and Spanish.

The website checks the Microsoft Store every 3 hours. When the Store starts offering a new version, it
copies the files from this folder on `main` (up to that version), updates the version shown across the
site and publishes the changelog. A file for a version the Store does not offer yet stays hidden until
the Store offers it.

If the Store offers a version that has no file here, the website keeps showing the previous version
and its sync run fails until the file is merged.

## When to add a file

In the version PR (the same commit that bumps the version and updates `release/store/*/whats-new.txt`),
so the text is reviewed before the release.

## Format

```json
{
  "version": "1.1.10",
  "date": "2026-11-11",
  "en":    { "title": "...", "blocks": [ ... ] },
  "pt-pt": { "title": "...", "blocks": [ ... ] },
  "pt-br": { "title": "...", "blocks": [ ... ] },
  "es":    { "title": "...", "blocks": [ ... ] }
}
```

* `version` matches the file name and the release tag without the `v` (three numbers).
* `date` is the release date, `YYYY-MM-DD`.
* All four languages are required, each with a non-empty `title` and at least one block.
* Blocks: `{"kind": "ul", "items": ["...", "..."]}` for a list, `{"kind": "p", "text": "..."}` for a
  paragraph, `{"kind": "note", "text": "..."}` for a highlighted note (known issues).
* Inline formatting: `**bold**`, `` `code` `` and `[text](https://...)`.
* Write for users, as in the Store "What's new" text: what changed for them, no internal detail, defect
  IDs or plans. Each language is written for its own audience. The app has no Spanish interface, so the
  Spanish text names settings and menus in English, as the app shows them.
