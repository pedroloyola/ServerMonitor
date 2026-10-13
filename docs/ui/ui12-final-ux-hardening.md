# UI.12 — Final UX Hardening

**Status: UI.12 — COMPLETE (2026-10-13).** PR #42 squash-merged with a specific human GO on head `894a21a` (CI `38451798976` green) → **main `3433bdc`** (single parent `baa6401`; tree identical to `894a21a`). Post-merge CI `38452947919` green on the first attempt. Post-merge startup smoke on `3433bdc` passed (see "Post-merge" below). The recovery run (§0, human present) closed the Firewall blocker and fixed and runtime-proved B12B-01, B12F-01 and B12B-02-R. Squash was a one-off exception for this PR, because intermediate commits do not build in Release. It does not change the merge policy.

**Branch:** `ui/ui12-final-ux-hardening`, base `main` @ `baa6401`. Relay's accessibility work was done on `ui/ui12-a11y` (same base) and merged into the branch four times (`b328a07`, `6e62664`, `118c044`, `f7fe881`), each conflict-free and identical to a recomputed merge-tree. This document's commit adds docs only.

**Scope: hardening, not redesign.** Human GO of 2026-10-12 (overnight, human away; Boss summary in `.boss/tmp/ui12/brief-common.md`):
- In scope: accessibility (top priority), functional reliability, honest states and messages, responsive layout, window and navigation stability, error handling, performance, no regressions, release readiness.
- Out of scope: redesign; new features; motion-system rework; layout, visual-identity or information-hierarchy changes; persisted-contract changes; security-boundary changes; packaging / MSIX identity; REL.0 / M15 / Store.
- Small visual changes were allowed only to fix a demonstrated usability or accessibility defect. Each such change is listed in §5. The UI.9, UI.10, UI.11 and UI.11.1 results are preserved (§9, regression sweep).

**Unchanged:**
- monitoring engine;
- SSH, credentials and trust semantics;
- persisted formats;
- Widget V3;
- Pro;
- packaging.

**Execution:**
- **UI.12A, audit (read-only, `baa6401`):**
  - Prism: global UX / accessibility, static + Figma;
  - Cortex: architecture, lifecycle, windows, error honesty, stress plan;
  - Atlas: test reliability;
  - Beacon: runtime audit with real input.
- **Decisions:** Prism (`prism-ui12-decisions.md`) and Boss / human (§3).
- **Implementation:**
  - Sentry (the Floor's only writer): window, lifecycle, error honesty, states, editor;
  - Relay, on `ui/ui12-a11y`: High Contrast, focus, live regions, automation peers, layout, contrast, text.
- **Reviews:** Atlas r1, Cortex r1 / r2 / a11y-r2 / r3, Prism a11y-r1 / a11y-r2 / r3, Relay r2 / r3, Vigil r1 / r2 (§8).
- **UI.12D runtime re-QA (Beacon):** pass 1 @ `b328a07` (§9); final pass @ `118c044` (**P0 found**, bisected) and @ `d36becb` (UIA-only, §9).

**Evidence labels:** [CODE] · [UNIT] test + counterproof · [UIA] programmatic UI Automation · [REAL-INPUT] `SendInput` into the verified QA window · [HC-REAL] a real Windows contrast theme · [HUMAN] · [STATIC] reading only.

`.boss/**` paths are evidence. No personal data, real hosts or real screenshots are reproduced here; the QA harnesses use fixture data.

## Post-merge (2026-10-13)
- **Merge:** squash `3433bdc` ("UI.12 — Final UX Hardening (#42)"). Its only parent is `baa6401`, and `git diff 894a21a 3433bdc` is empty.
- **CI on main:** run `38452947919`, attempt 1. Release build + vulnerability scan ✓, Debug build + tests ✓.
- **Startup smoke** (Beacon `beacon-postmerge-smoke.md`; QA worktree at `3433bdc`, App Debug rebuilt, launched only through `tools/qa/Start-QaApp.ps1`; HC off; real input). Every item PASS:
  - the app starts and Overview opens;
  - Servers opens by a sidebar click and shows 6 rows;
  - Add server opens the editor, Esc closes it and focus returns to "Adicionar servidor";
  - the app stays alive and responding for ≥ 60 s;
  - closing the window sends it to the tray;
  - tray menu "Sair do ServerAlyzer" (keyboard) exits the process by itself in 0.24 s with exit code 0;
  - 0 Application Error/Hang/WER events.

  One earlier run could not reach the tray menu item through UIA because the native menu exposes no items. That was a harness limitation; the next targeted keyboard run closed the item.
- **Guards:**
  - real data diff 0;
  - the Developer-signed 1.1.2.2 install was not touched;
  - Firewall 709, with no new rules;
  - High Contrast restored;
  - 0 QA processes.
- **Still NOT_RUN:**
  - DPI ≠ 100 %;
  - Narrator steps 7–10 (UIA only);
  - B12B-02-R Narrator in pt-BR/en-US, in High Contrast and on Remove;
  - the remaining items in §10.

  The pre-existing P3s stay in the backlog.

## 0. Recovery run (2026-10-13, human present)

Commits on the branch after `75b47eb` (all local, not pushed):

| Commit | What |
|---|---|
| `ab575d3`, `879c079`, `5016f2a` (Relay, `ui/ui12-modal`), merged in `717b520` | B12F-01: the page behind the editor's modal layer leaves the UIA tree; no live-region change from the inert page; Atlas setter pins |
| `2753ace`, `0bc6ad8`, `389dc7e` | B12B-01 first recovery fix: an HC-only twin path in the SaIcon template + `SaIconSecondaryStyle` for muted icons in state-text hosts + Atlas pin. **Measured later: no effect in HC** (root cause below) |
| `843771e` | B12B-01 final fix: a collapsed `IconElement` probe in the SaIcon template drives the HC path |
| `6f66910` | test-only: Atlas A-B12B-FINAL-01 (`OnApplyTemplate` pinned statement by statement) |
| `45188b0`, `5352fb5` (Relay, `ui/ui12-b12b02`), merged in `90cb99c` | B12B-02-R: the Servers return notice is announced by one UIA notification after the page focus; review fixes (per-page `ReturnNoticeAnnouncer`, never after the page left) |

### Firewall exception — closed
- The 2 inbound **Block** rules added by the accidental `Infrastructure.Tests` testhost run (`wt\ui12-review\…\testhost.exe`, TCP + UDP "Query User") were removed by the human with UAC: **711 → 709**, removed 2, added 0, changed 0, `RESULT=PASS` (`.boss/evidence/ui12/firewall-removal.log`, 09:22–09:24). The notification dialog is gone; real-input QA was possible again. Firewall stayed 709 in every later QA window.

### B12B-01 — High Contrast icons on Highlight: **FIXED** (`843771e`), proven in real HC
- **The P0 of `85a6b08`, measured** [EMP] (spike worktree, variants, life probe 40 s with HC off, no input): `AccessibilitySettings.HighContrastChanged +=` throws **COMException 0x80070490 synchronously on the UI thread**; it escaped `SaIcon.Loaded` through a `Lazy` factory → stowed-exception fail-fast `0xc000027b`. Subscription only → crash; `AccessibilitySettings` created and read without subscribing → alive; subscription in try/catch → alive (the throw logged). [DOC] The event is **not supported in WinUI 3 desktop apps** (Project Reunion 0.5 known issues; `microsoft-ui-xaml#4163`; replacement `ThemeSettings.Changed`) — Cortex `cortex-platform-research.md` §1. A guard forbids `HighContrastChanged` in App code.
- **Why `2753ace` had no effect** [EMP + SRC]: a host's visual state (checked / hover / pressed / disabled) sets **its `ContentPresenter.Foreground`**. The icon inherits the value (`GetValue` is right), but WinUI notifies that change **only to `IconElement` descendants** (`InheritedProperties::GetCorrespondingInheritedProperty`, `InheritedProperties.cpp` L579-598; deliberate since Windows 8.1 for `AppBarButton`). SaIcon is a `Control`, so nothing bound to its Foreground ever updates — neither `{TemplateBinding}` nor `{Binding … TemplatedParent}` nor a property-changed callback. Measured with a diagnostic build in Dark and in real Night sky: `icon.Foreground` = HighlightText, the HC path visible (theme-resource visibility resolves correctly), yet its Stroke stays at the value from template time (WindowText). The Beacon "PASS 11.81 / 7.27" rows of the `717b520` final QA were a **measurement artefact** (the "dominant colour" was the HC text backplate, not the icon) and were corrected (`beacon-final-qa.md` §5).
- **Fix `843771e`:** the SaIcon template carries `<PathIcon x:Name="PART_ForegroundProbe" Visibility="Collapsed" />` (no Foreground of its own); `OnApplyTemplate` binds `PART_StatePath.Stroke` to the probe's Foreground with a **Source** binding (an `ElementName` binding does not resolve inside this template — measured: `DataItem` null, icon empty). The HC path has no XAML Stroke (single source). No events, no `AccessibilitySettings`, no lifecycle code; Light / Dark keep the pre-UI.12 TemplateBinding path (the HC path stays Collapsed outside HC). Cost: +1 collapsed `PathIcon` and +1 binding per icon, on top of the `2753ace` twin path.
- **Real HC on the Floor build** [HC-REAL, REAL-INPUT] (`843771e`, dll `886406EF…`, Beacon `beacon-hcdiag.md` §6d; HC switched by the human): icon stroke measured on the **icon crop** — Glass icon-only "Modo compacto" hover and pressed, checked sidebar item, selected theme segment, "Dados e servidores" and "Sobre" disclosures on hover: **Night sky #2B2B2B on #D6B4FD ≈ 7.8:1** (was #FFFFFF, 1.78:1), **Desert #FFF5E3 on #903909 ≈ 6.5:1** (was #3D3D3D, 1.44:1). At rest WindowText on Window. Servers page (`many-100`): row icons legible before and after PgDn recycling. Diagnostic build (same mechanism, Sentry `sentry-hcfix-proto.md` §6): also the auth segment, the key picker on hover, "Atualizar todos" hover / pressed (7.96:1), disabled = GrayText 8.63:1, and **OFF → Night sky with the window open** follows (7.96:1).
- **Light / Dark identity** (Cortex C1, Beacon `beacon-hcdiag.md` §6b): Floor `843771e` vs `717b520`, 26 surfaces × 2 runs: 19/26 identical (0 px), the rest within the run-to-run noise of the baseline itself (backdrop) or a keyboard-focus ring present in one baseline run → **0/26 above noise**.
- **Not fixed by this (pre-existing, new ID):** B12G-01 (P3) — switching one HC theme to another with the app open leaves the checked item's **fill** in the previous theme; the icon follows the new theme. `baa6401` does the same.

### B12F-01 — modal layer background in UIA: **FIXED** (`ab575d3` + `879c079` + `5016f2a`, merged `717b520`)
- **Cause** [DOC + SRC]: the UIA Control view is a filter over the raw tree; `AccessibilityView=Raw` removes one node, not its descendants (`CUIElement::GetAPChildren` prunes only on Visibility). Even WinUI's `ContentDialog` does not hide the background.
- **Fix:** `SaInertHost` wraps the editor's `PageScroll`; its automation peer returns **no children** while inert and is never a control / content element; each switch raises `StructureChanged(ChildrenInvalidated)` (only when a listener exists). `SetPageInteractive` is the only writer; every close path restores it before focus. `879c079`: the inert page writes neither `ActionHint` nor `EditorSubtitle` while a layer is open (no live-region change over the layer); the held summary is said once after close.
- **Runtime** [REAL-INPUT + UIA] (Beacon final QA @ `717b520`, `ControlViewWalker`): **10/10 paths PASS** — Import → Cancel / Esc / use a profile; test OK → back; test failed → Esc; trust → Cancel; trust → trust and test → back; navigate away (discard guard: keep editing / discard); close the window → tray Open; Tab / Shift+Tab. With the layer open, **0 page elements** in the Control view; on close the form returns and focus goes to the invoker. Note (P3, Prism decision): after "Continuar a editar" in the navigation guard, focus stays on the clicked sidebar item (outside the layer); one Tab re-enters it.

### B12B-02-R — "Servidor ocultado…" not read by the real Narrator: **FIXED** (`45188b0` + `5352fb5`, merged `90cb99c`)
- **Root cause** [UIA + REAL-INPUT, Beacon @ `4ac418e`; Cortex research]: after Hide, two **Polite** `LiveRegionChanged` (the Servers summary and the `ReturnNotice` toast) were raised in the **same DispatcherQueue Low turn** as `FocusHeading`; they arrived 0–1 ms before the `FocusChanged` to the page title, and the focus change interrupts them. The Narrator spoke the title only; 0 notifications.
- **Fix:** after the focus decision (editor opener → row → heading, and on the KeepSidebar path), **one** UIA notification in **one more Low turn**: `RaiseNotificationEvent(ActionCompleted, CurrentThenMostRecent, <the toast's accessible name>, "ServersNoticeShown")` on the `ReturnNotice` peer. Text = the toast's own name (no new strings, same in pt-PT / pt-BR / en-US). `SaToast.AnnouncesItself` (default **true**) is set to **false only on `ReturnNotice`**, which then raises no live-region event and has `LiveSetting=Off`; the other toasts are unchanged. The per-page `ReturnNoticeAnnouncer` (`5352fb5`, Atlas A02-01 / A02-02 / L1) announces once per page instance and never after the page closed or the notice was cleared. Success only: a failed Hide posts no notice and keeps the InfoBar's own notification.
- **Evidence:**
  - [UIA + REAL-INPUT] Beacon @ `5352fb5`: **15/15** — pt-PT hide (keyboard, mouse), hide twice, hide → navigate back (no repeat), Remove, Remove cancelled (0), failed hide (InfoBar only, unchanged), Light, stress 10 hides (10/10, 0 duplicates); pt-BR and en-US hide / Remove / failed hide. Each success: exactly 1 notification after the title focus, 0 toast live-region events.
  - [HUMAN] real Narrator, pt-PT, normal theme: **PASS** (baseline: FAIL heard at `4ac418e`).
  - [HC-REAL] Night sky and Desert: **PASS** (1 notification after the focus; toast legible).
  - [UNIT] `Ui12ReturnNoticeNotificationTests` + updated pins; Relay counterproofs (15 for `45188b0`, plus the `5352fb5` review mutants) all killed.
  - Reviews: Cortex **APPROVE**, Prism **APPROVE**; Atlas CHANGES_REQUESTED on `45188b0` (A02-01, A02-02, L1) fixed in `5352fb5` and **re-checked by Cortex** (Atlas terminal unavailable).
- **NOT_RUN:** Narrator in pt-BR / en-US, with HC on, and for Remove (UIA PASS only).
- Sources: `.boss/tmp/ui12/recovery/{relay,beacon,cortex-review,prism-review}-b12b02.md`.

### Narrator session (human, Beacon `beacon-final-qa.md` §7)
- **Steps 1–6 with the human** [HUMAN]: PASS — 1.1–1.4 (active nav item, Tab through the command bar and page, Shift+Tab, 1 min idle without periodic announcements), 2.1–2.3 (sidebar by arrows, "x de 4"), 3.1 (Overview list, name + state + position), 4.2–4.5 (editor fields, 14 named stops in order; empty Add; Test OK announced, password read as already entered **without its value**; Esc → focus on a named target).
- **FAIL 4B → B12B-02-R** (fixed above; human Narrator PASS on `5352fb5`). 4A, 4C and Q1/Q2 not reported by the human.
- **Steps 7–10 and 4A: UIA + real keyboard, "UIA-verified; Narrator NOT_RUN"** (the human declined further Narrator steps): theme / History range / auth segments, toggles, failed test, failed server state and reason as text, Edit › Save (1 live event), Data toast, Compact Tab and rows, ticker 60 s without spam, Enter on a row → PASS.

### New or re-opened findings (all pre-existing at `baa6401` unless stated)

| ID | Sev | Finding | Decision |
|---|---|---|---|
| B12B-02-R | P2 | "Servidor ocultado…" is **not read by the real Narrator**: after Hide, 2 polite `LiveRegionChanged` (summary + toast) arrive in the same instant as focus moves to the Servers title; the Narrator speaks the focus change only. At `baa6401` the toast was never announced | **FIXED** `45188b0` + `5352fb5` (merged `90cb99c`): one UIA notification after the focus (above) |
| B12H-01 | P3 | pt-PT Narrator reads the placeholder "Ex.:" as "Excelência" (`Resources.resw`, the editor's name / host / user / jump fields; same in pt-BR) | **BACKLOG** (copy: "Por exemplo: …") |
| B12G-01 | P3 | HC → HC switch with the app open: the checked item's fill stays in the previous theme | **BACKLOG** |
| R1-01-R | P3 | Data toast: 2 identical `LiveRegionChanged` 4 ms apart in 1 of 4 runs (coalescing residue) | **BACKLOG** |
| Focus note | P3 | after "Continuar a editar" in the navigation guard, focus stays on the sidebar item outside the layer | **Prism decision** |
| B12F-02 | P3 | Servers stale cue not in the row's accessible name (re-confirmed by real keyboard) | **OPEN** → backlog (unchanged) |

### Stress and real input (Beacon `beacon-stress-r1.md` @ `d36becb` vs `baa6401`; `beacon-final-qa.md` @ `717b520`)
- **Stress C1W–C10:** 0 crashes, 0 not-responding, 0 Firewall / UAC dialogs; handles / GDI / USER stable. **No UI.12 regression.** Per-visit memory growth (navigation, editor, History, rapid selection: ~3–4.5 MB per visit, C8 +41 MB) is **identical on `baa6401` → pre-existing** (Cortex: cache vs retention, backlog). C2c modal test ×50: +32.8 MB vs +28.0 MB on the baseline, not monotonic → note for Cortex. C2b / C3 / C4 / C7 / C10: PASS (focus restored 5/5, maximized kept 10/10, 0 fail-fast in 110 real drags).
- **C-W1 tray menu with real keys** (W3 / W4 / W7b): **PASS** (maximized restored).
- **Real-input pass:** K1–K7 (editor, test / trust / import layers, STATE-03 / 04 / 05, B12-04 / 05) PASS; drag on the selector; Reduced Motion; Compact "Sempre no topo" and row click PASS. Smoke Dark 75/75 surfaces × pt-PT / pt-BR / en-US and Light 3 × 8 pages: 0 unnamed interactive elements. Life probe: 14 harnesses > 35 s with HC off.
- **Guards:** real data diff 0 in every window; Firewall 709; HC restored to the recorded state (diff 0; the default HC scheme also back to "Branco de alto contraste"); 0 QA apps left; 0 crashes (event 1000) of QA executables (the only crash events were Sentry's deliberate spike probes of the `85a6b08` mechanism).

## 1. Outcome

- **No P0 in the audit.** UI12A-STATE-01 (Prism P1, "no servers yet" shown when the configuration is unreadable) was fixed and Cortex ruled out the feared P0 ("Adicionar" overwriting an unreadable configuration).
- **One P0 introduced during the run and reverted:** `85a6b08` (B12B-01, HC icon colour) made the app fail-fast (0xc000027b, Microsoft.UI.Xaml.dll) 4–5 s after every launch, with High Contrast **off** and no input. Every unit test and CI were green. Beacon found it in the final runtime pass and bisected it (parent good, `870b64c` good); it was reverted in `41f8c59` (its test guard) and `d36becb`. **Root cause measured in the recovery (§0):** the `AccessibilitySettings.HighContrastChanged` subscription throws COMException 0x80070490 (unsupported in WinUI 3 desktop apps).
- **Fixed:** about 60 commits on the branch; every fix has a regression test and a counterproof (§7).
  - High Contrast: HC-01 / HC-02 / FOCUS-01.
  - Screen readers: LIVE-01, PEER-01, STATE-02, TEXT-01.
  - Window: COMPACT-EXIT-MAXIMIZE (C-W1).
  - Error honesty:
    - C-E1, failed History query shown as "never recorded";
    - C-E2, failed notification counted as sent;
    - C-E4 / C-E5bis, committed edit reported as failed;
    - **C-E5, data loss:** the ignored-devices list was wiped after a transient read error.
  - Lifecycle: C-L1, C-L8 and the startup exit code (C-S1a).
  - States: STATE-01 / 03 / 04 / 05.
  - Layout: LAYOUT-01 / 02, FORM-01.
  - Keyboard and modal layer: KBD-01, FOCUS-02, MODAL-01 (focus kept in the layer on Accept; the background's removal from UIA completed by B12F-01 in the recovery, §0).
  - Recovery (§0): **B12B-01** (`843771e`, real HC) and **B12F-01** (`ab575d3` / `879c079` / `5016f2a`).
  - Beacon findings: B12-01 / 04 / 05 / 06 / 10 / 11, B12B-02.
  - Small P3s: N-3 (`575abb5`), C-W4 (`f9446db`, unit only), C-L2 (`870b64c`).
- **By design / reverted:** B12-07 editor Tab order (UI.7 DV-26, §3).
- **Platform limitation:** Compact Maximize glyph (B12-02, §4).
- **Backlog:**
  - B12-08 (Alt+← back, `KBD-BACK-ACCEL`);
  - C-S1b (persisted startup-failure record);
  - the open P3 sweep items (§2);
  - the loopback CI flake, as a separate test-only PR proposal.
- **Former open P2s — FIXED in the recovery (§0):**
  - **B12B-01** — HC icons on Highlight were 1.44–1.78:1; now ≈7.8:1 (Night sky) / ≈6.5:1 (Desert), measured on the icon crop in real HC on `843771e`.
  - **B12F-01** — with the editor layer open, 0 page elements in the UIA control view (10/10 paths, real input).
  - **B12B-02-R** (pre-existing) — "Servidor ocultado…" is now read by the real Narrator (one UIA notification after the page focus; human PASS, UIA 15/15).
- **Open after the recovery:**
  - **B12F-02** (P3) — the Servers stale cue TextBlock is Raw-only and in no control-view name (the Overview row name has it).
  - P3s B12H-01, B12G-01, R1-01-R and a focus note (§0).
- **Gates:**
  - every commit was gated on the Debug `.slnx` build `--disable-build-servers` + App.Tests (+ Core.Tests when touched);
  - from fix round r3 on, also the **Release** `.slnx` build, because CI went red once on a Debug-only QA reference in a test (run `38342264081`, fixed in `b568c24`);
  - final local counts on `d36becb`: **Release** App.Tests 3473/3473, Core 682/682; **Debug** App.Tests 3955/3955, Core 682/682 (0 build errors both);
  - recovery: every Floor commit additionally needed an **HC-off launch smoke** (Start-QaApp, ≥ 40–60 s, no input) when it touched XAML or a control; final local counts on `6f66910`: **Release** App.Tests 3491/3491, Core 682/682; **Debug** App.Tests 3973/3973, Core 682/682 (0 build errors both);
  - after the B12B-02-R merge, on `90cb99c`: **Release** App.Tests 3515/3515, Core 682/682; **Debug** App.Tests 3997/3997, Core 682/682 (0 build errors both; no Floor launch smoke, the QA window was held by Beacon — runtime evidence for this fix is Beacon's on `5352fb5`);
  - Infrastructure.Tests ran only as filtered store classes, never in full locally (§6).
- **Real data:** identical before and after every QA window.
- **Firewall:** the app added no rules. An accidental full `Infrastructure.Tests` run in a review worktree (not a QA run) added 2 inbound **Block** rules for its testhost (709 → 711) and opened a Firewall notification dialog at 04:40, which blocked part of the stress run and all real-input QA after 04:40 (§9). **Closed in the recovery:** the human removed both rules with UAC (711 → 709, §0); 709 in every later window.

## 2. Findings and decisions

Severity per the common format (P0 data loss / security / critical crash · P1 important function inaccessible, wrong state or material regression · P2 significant UX issue with a workaround · P3 minor). "Commit" = the fixing commit on the branch.

### Beacon runtime audit (B12-*, `beacon-ui12a-runtime-audit.md`)

| ID | Sev | Surface | Decision |
|---|---|---|---|
| B12-01 | P2 | Light segmented selectors: selected pill ≈1.04:1 vs the track | **FIXED** `fe738d3`: 1 px Light-only outline, token `SaColorSegmentedSelectedOutlineLight #7E7E7E`, on the 5 hosts (Prism spec) |
| B12-02 | P3 | Compact: Maximize caption glyph enabled and invocable, no effect | **PLATFORM LIMITATION** (§4) |
| B12-03 | P3 | Standard → Compact shows ~100 ms of clipped Standard layout | **CANCELLED** (Boss): the reorder experiment was not run; recorded for the backlog |
| B12-04 | P3 | Editor hint "Testa a ligação antes de guardar" after a successful test | **FIXED** `cd9662c` |
| B12-05 | P3 | Password box looks empty after Test (secret retained by design) | **FIXED** `1402f07`: "Já introduzida" placeholder + HelpText; Vigil APPROVED (r2), security behaviour unchanged |
| B12-06 | P3 | Row showed only "Saudável" for a retained, stale reading | **FIXED** `9554269` (with STATE-03), r3 follow-ups `fd68b34` (wrap, R3-01) and `97715d8` (Stacked row, R3-02) |
| B12-07 | P3 | Tab order: editor header button after the footer; Detail breadcrumb after the sidebar | **BY DESIGN.** Editor: UI.7 DV-26 / a11y B-20 (form-centric order). `9ae8ffc` was **reverted** in `fff3f90`. Detail / Services: the breadcrumb is first in tree order; Tab moves forward from the focused H1, Shift+Tab reaches it in one step (Relay A2, Prism confirmed) |
| B12-08 | P3 | No Alt+← / Esc "back" on Detail | **BACKLOG** `KBD-BACK-ACCEL` (Alt+←; Esc rejected) — a feature, not hardening |
| B12-09 | P3/INFO | Home/End no-op in sidebar and lists | **BACKLOG** (no change) |
| B12-10 | P3 | Programmatic heading focus drew the square system rectangle | **FIXED** `c89fc7c`: app ring, radius 4 |
| B12-11 | P3 | "Serviços e containers" title cut at 560 px | **FIXED** `2fd64c4`: 2 lines, ellipsis after 2 |
| B12-12…14 | INFO | focusable cards; modal background in UIA; harness-only loading quirks | B12-13 → MODAL-01 `e033b50`, **not effective** for the background (= B12F-01); the rest no change |
| B12B-01 | P2 | HC: icons and glyphs on Highlight (selected sidebar item, selected segments, icon-only Glass buttons on hover) at 1.44–1.78:1 | **FIXED** `843771e` (recovery, §0; real HC ≈7.8 / 6.5:1). History: `85a6b08` caused the P0 and was reverted (`41f8c59`, `d36becb`); `2753ace` / `0bc6ad8` had no HC effect (ContentPresenter → Control Foreground changes are not notified) |
| B12B-02 | P3 | "Servidor ocultado" toast raises no LiveRegionChanged | **FIXED** `5058c79`: announced once when the page becomes live (UIA listener: 1 event) |
| B12F-01 | P2 | Editor modal layer open: background form still in the UIA control view | **FIXED** `ab575d3` + `879c079` + `5016f2a` (merged `717b520`): `SaInertHost` peer with no children while inert; runtime 10/10 PASS (§0) |
| B12B-02-R | P2 | "Servidor ocultado…" not read by the real Narrator (focus change wins over 2 simultaneous polite live regions) | **FIXED** `45188b0` + `5352fb5` (merged `90cb99c`, §0): one UIA notification after the focus; human Narrator PASS |
| B12H-01 · B12G-01 · R1-01-R | P3 | "Ex.:" read as "Excelência" · HC→HC fill stale · rare duplicate Data-toast event | **BACKLOG** (pre-existing / intermittent, §0) |
| B12F-02 | P3 | Servers stale cue Raw-only, not in any control-view name | **OPEN** → backlog |

### Prism static audit (UI12A-*, `prism-ui12a-audit.md`)

| ID | Sev | Decision |
|---|---|---|
| STATE-01 | P1 | **FIXED** `72eb55f`: Servers shows the Overview's "configuração indisponível" notice, not "Ainda não há servidores" + "Adicionar" |
| STATE-02 | P1 | **FIXED** `8db9de2`: a stale Compact row names its age to screen readers |
| HC-01 | P1 | **FIXED** `331a4c6` (+ `ecc438b`, `8cd1570`): HC-only veil tokens; the import's selected row keeps an HC outline |
| FOCUS-01 | P1 (HC) | **FIXED** `fe51b74`: HC-only inner Window-coloured ring on Highlight-selected items |
| LIVE-01 | P2 | **FIXED** `0bf2e9d` + `73223a9` (one raise per element per turn) + `abb7f89` (periodic "Atualizado há" texts are not live regions) + `7cf9358` (editor handover); B12B-02-R fixed in the recovery (§0) |
| PEER-01 | P2 | **FIXED** `8fe3bb2` (+ `cb078db`, `d87bd1b`, `7cf9358` editor `DialogContent`) |
| HC-02 | P2 | **FIXED** with HC-01 (veil) |
| HC-03 | P2 | **BACKLOG** (switching HC with the app open: measured OK in §9, caption colours not changed) |
| FOCUS-02 | P2 | **FIXED** `1150ebb`: outer app ring on a focused invalid field |
| STATE-03 | P2 | **FIXED** `9554269` |
| STATE-04 | P2 | **FIXED** `416d734`: History "Atual" never shows a stale or offline value as current |
| STATE-05 | P2 | **FIXED** `a65da3a` + r3 `6f5b2e0` (a never-read failing server is not "first reading" on every retry) |
| LAYOUT-01 | P2 | **FIXED** `29506cc` (+ `41271b1`): Sa ContentDialogs fit the minimum window, commands stack, content scrolls |
| LAYOUT-02 | P2 | **FIXED** `a9c869b`: toasts at most 600 wide, never fixed |
| LAYOUT-03 | P2 | **NO CHANGE**, false positive (code-behind already clamps; Prism withdrew it) |
| LAYOUT-04 | P2 | **BACKLOG** (Windows text scaling; needs a system setting to measure) |
| KBD-01 | P2 | **FIXED** `94f6777`: Enter submits only from a text field, a secret field or Save |
| FORM-01 | P2 | **FIXED** `a16d9bc`: host-key subject and the destructive "affected" line wrap |
| MODAL-01 | P2 | **FIXED** `e033b50` (focus kept in the layer on Accept) + B12F-01 in the recovery (background out of the UIA control view) |
| CONTRAST-01 | P2 | **FIXED** `ebb1ae6` (+ `3ca97bf`): Compact "Sem ligação" text uses a text token (Light #A83E36) |
| TEXT-01 | P2 | **FIXED** `9bfbaac`: toggle name = visible label; notifications say "Sem ligação" / "Ligação restabelecida" |
| P3-14 / P3-21 / P3-24 / P3-31 | P3 | **FIXED** `a330a9a` / `a8f6956` / `3b52640` / `d8f67b8` |
| P3-18 (tray "Modo compacto" checked) and the other P3s | P3 | **BACKLOG** |
| R1-07 (HC hover as a 1 px outline) | P3 | **BACKLOG** |

### Cortex audit (C-*, `cortex-ui12a-audit.md`)

| ID | Sev | Decision |
|---|---|---|
| C-S1 APP-STARTUP-FAILURE-EXIT0 | P2 | **C-S1a FIXED** `021ebf2`: non-zero exit code `0x5353` after a startup failure. **C-S1b** (persisted, secret-free record) **OUT OF SCOPE** (human/Boss; separate proposal). Runtime exit code NOT_RUN: no new QA fault-injection flag (decision) |
| C-W1 COMPACT-EXIT-MAXIMIZE, widened | P2 | **FIXED** `e437c39` + `0742147` (matrix) + `5e1df48` (Atlas r1 order pins); runtime PASS with a baseline counterproof (§9) |
| C-W2 Maximize glyph | P3 | = B12-02, **PLATFORM LIMITATION** |
| C-W3 cut frame | P3 | = B12-03, **CANCELLED** |
| C-W4 UI10-H07-DPI-ORDER | P3 | **FIXED** `f9446db` (order-independent; unit-tested; runtime NOT_RUN, DPI not granted) |
| C-W5 N-3 / N-4 / W-1 | P3 | N-3 **FIXED** `575abb5`; N-4 / W-1 **BACKLOG** (watch items) |
| C-L1 form detached after a theme remount | P2 | **FIXED** `61ea7d8` (+ `5e1df48`) |
| C-L2 | P3 | **FIXED** `870b64c` (WorkloadsPage disposable) |
| C-L7 | P3 | **RECORDED, NOT CHANGED** (unreachable: Load already cancels, a 2nd refresh is blocked) |
| C-L3…C-L6, C-L9 | P3 | **BACKLOG** |
| C-L8 async void without catch | P3 | **FIXED** `d6baf10` |
| C-E1 / C-E2 / C-E4 + C-E5bis / C-E5 | P2 | **FIXED** `5f7eb5d` / `bee5179` / `a88734a` / `a4e6cbb` (+ `ac996b3` Reset-while-unreadable pin) |
| C-E3, C-E6, C-E9…C-E14 | P2/P3 | **BACKLOG** |
| C-E15 | P3 | **RECORDED, NOT CHANGED** (unreachable with the production stores) |

### Atlas audit (`atlas-ui12c-audit.md`)

| ID | Sev | Decision |
|---|---|---|
| FLAKE-INFRA-LOOPBACK-CHILD | P2 (CI) | **OUT OF SCOPE → separate test-only PR proposal** (Boss). Mechanism identified; the attempt-1 cause cannot be proven retroactively |
| UI11-TEXTGUARD-CLASS | P3 | **BACKLOG**: behavioural closure needs a production testability seam or a QA harness, not more source-text guards |
| CI-FLAKE-WALLCLOCK-2 residue | — | **CLOSED** at base (static); backlog entry to reconcile |
| `43d` Guid substring flake | — | **CLOSED** at base (static) |
| ATLAS-SQLITE-CLEANUP | P3 | **FIXED** `aefe5b4` (test-only) |

## 3. Human / Boss decisions

- **Overnight GO (2026-10-12):** UI.12 as above; ceiling READY_TO_MERGE; Boss owns PR / commits / state.
- **C-S1:** exit code in scope (C-S1a); the persisted startup-failure record is out of scope (C-S1b, separate proposal); **no new QA fault-injection flag**, so the exit code is unit-proven only.
- **DPI:** **not granted.** The PC has one monitor, so a scale change is a global DPI change (forbidden). All DPI items stay NOT_RUN.
- **High Contrast:** **granted** for one pass, following Beacon's reversible procedure (part A only; Night sky + Desert; restore verified, §9).
- **B12-07 editor:** **by design (DV-26)**; Sentry's change `9ae8ffc` reverted in `fff3f90`. WCAG 2.4.3 asks for a meaningful order, not visual order.
- **B12-03:** cancelled.
- **B12-08:** backlog `KBD-BACK-ACCEL`.
- **Loopback flake:** separate test-only PR proposal.
- **Infrastructure.Tests:** never in full locally (Firewall prompts block QA input). Only filtered store classes, by Boss exception (C-E1, C-E5, ATLAS-SQLITE-CLEANUP).
- **B12B-01 fix reverted** after the P0 (no second attempt overnight: mechanism unconfirmed and no real-input QA possible).
- **MODAL-01 / B12F-01:** no further fix round overnight (needs runtime proof); kept the effective part, background hiding → backlog.
- **Merge:** not done (human GO). Note: the commits from `416d734` up to the parent of `b568c24` do not build in Release (CS0234), so a squash merge avoids carrying them into `main`.

- **Recovery (2026-10-13, human present):**
  - Firewall rules removed by the human (UAC); runtime QA re-granted (real input, stress, Narrator).
  - B12B-01 and B12F-01 fixed in UI.12 (not deferred). For B12B-01: a diagnostic HC session with conditions — the **human** switched HC on and off; original theme recorded and restored (diff 0); no repeat of the `85a6b08` mechanism; PASS only from pixels measured on the icon crop in real HC.
  - The Narrator session stopped after step 6 (human's choice); steps 7–10 recorded as "UIA-verified; Narrator NOT_RUN".
  - A temporary difference (default HC scheme) was accepted by the human during the diagnostic and disappeared at the final restore.

### Open for the human
1. ~~Decide B12B-02-R~~ — fixed in UI.12 (§0).
2. GO for push (CI has not run on the recovery commits) and the merge method (squash recommended, see the Release note above).
3. Optional: the remaining Narrator steps (3.2–3.4, 3.6, 5–11).

## 4. Platform limitations (documented vs empirical)

- **Compact Maximize glyph (B12-02 / C-W2).** `OverlappedPresenter.IsMaximizable = false` is applied in Compact (`Windowing/AppWindowPlacementAdapter.cs`).
  - **Documented:** the property controls whether the window can be maximized. With content extended into the title bar, the caption buttons are system-drawn and system-handled.
  - **Not documented:** that the overlay honours `IsMaximizable`; there is also no documented API to hide or disable one caption button.
  - **Empirical (Beacon A + B):** `WS_MAXIMIZEBOX = 0`, but the UIA `Maximize` button is enabled and invocable. Invoke, Win+↑, `SC_MAXIMIZE`, a real click and a real title double-click all leave the window in its Compact envelope (`HoldCompactRestored`).
  - **Decision:** Prism / Boss — no custom caption (that would be a redesign). The limitation is recorded in `docs/ui/ui8-compact-mode.md`, and a backlog item reopens it if WinAppSDK changes.
- **Windows Widgets host:** Widget Medium with ≥ 3 servers cannot be exercised without installing the MSIX (forbidden in this run). The host's own limits are recorded in the UI.9 record.
- **WinUI automation peers:**
  - `Border` / `Grid` / `StackPanel` / `ItemsRepeater` have no automation peer. Names, `IsDialog` and `LiveSetting` set on them never reach UIA, which is why `SaGroupHost` / `SaListHost` exist (PEER-01).
  - Whether WinUI raises `LiveRegionChanged` by itself for a TextBlock with `LiveSetting` is **not documented** either way. The app raises it explicitly (`SaAnnounce`, LIVE-01); measured by a listener in §9.
  - The managed UIA client cannot read `LiveSetting` / `FullDescription` (QA limitation, not product).
- **High Contrast text backplate:** in HC, Windows draws its own text backplate behind labels whose foreground is not a system brush. That keeps text legible where icons are not (B12B-01), and it fooled a "dominant colour" contrast probe (§0).
- **`AccessibilitySettings.HighContrastChanged`:** [DOC] not supported in WinUI 3 desktop apps (Project Reunion 0.5 known issues; `microsoft-ui-xaml#4163`); [EMP] subscribing throws 0x80070490 here (the `85a6b08` P0). Reading `AccessibilitySettings.HighContrast` works. Replacement for code that really needs a notification: `ThemeSettings.Changed` (known gap: not raised between two HC themes). The app needs neither: theme dictionaries re-resolve.
- **Inherited Foreground notifications:** [SRC] a `ContentPresenter.Foreground` change is notified only to `IconElement` descendants (`InheritedProperties.cpp` L579-598); a `Control` child reads the new value but gets no change notification, so no binding on it updates. [EMP] measured in Dark and real HC. Workaround used: a collapsed `IconElement` probe in the template (B12B-01).
- **`ElementName` inside the SaIcon ControlTemplate:** [EMP] did not resolve (`DataItem` null); the binding is created in `OnApplyTemplate` with a `Source`.
- **UIA Raw view:** [DOC + SRC] `AccessibilityView=Raw` removes one node from the Control view, not its descendants; only a peer whose `GetChildrenCore` returns no children prunes a subtree (B12F-01).

## 5. Visible changes (all to fix a demonstrated defect)

- **Light only:**
  - selected segment outline (B12-01);
  - Compact "Sem ligação" text colour (CONTRAST-01).
- **High Contrast only:**
  - no Highlight hover / pressed fill under unswapped text (HC-01 / 02);
  - import selected-row outline (HC-01);
  - inner focus ring (FOCUS-01).
- **All themes:**
  - outer ring on a focused invalid field (FOCUS-02);
  - heading ring style (B12-10);
  - Services title on 2 lines when narrow (B12-11);
  - dialogs clamp, stack and scroll at the minimum window (LAYOUT-01);
  - the Detail "Guardado" toast is content-sized ≤ 600 like the others (LAYOUT-02);
  - wrapped host-key subject / affected line (FORM-01);
  - stale cue in rows (B12-06 / STATE-03);
  - "—" instead of a stale "Atual" (STATE-04);
  - skeleton while the first reading runs (STATE-05);
  - Servers config-unavailable notice (STATE-01);
  - "Já introduzida" placeholder (B12-05);
  - "verified" editor hint (B12-04).
- **Text only:**
  - TEXT-01 toggle name;
  - notification wording;
  - pt-PT "host" / "confiável" (P3-31).
- Dark resting look otherwise pixel-identical by construction (veil and outline tokens are Transparent in Dark).

## 6. Process notes

- **Two writers, one Floor:** Sentry wrote the Floor; Relay worked in its own worktree on `ui/ui12-a11y`, and its branch was merged in. Copy-based counterproofs; `git stash` is not used (it is shared across worktrees).
- **EOL:** no line-ending churn (`--numstat` vs `--ignore-cr-at-eol` against `baa6401`; R1-06 restored one LF-only file).
- **Release gate added after a red CI:** a Debug-only `ServerMonitor.App.Qa` reference in a test broke the Release build (`b568c24`). Every gate since builds Debug **and** Release.
- **Runtime QA** ran only from a detached QA worktree at a fixed commit, launched only through `tools/qa/Start-QaApp.ps1` with isolated harnesses. Every launch checked the full bin manifest; one instance at a time; stopped by exact PID.

## 7. Guards (each with a counterproof)

Each counterproof breaks the rule in production code, fails the named tests for the intended reason, and is restored byte-exactly (SHA-256). Series end with a `--no-incremental` rebuild + tests. Scripts and logs: `.boss/tmp/ui12/sentry/`, `.boss/tmp/ui12/relay/`, `.boss/tmp/ui12/sentry-cp*`, reviewers' `*-r*` folders.

- **Window:** `Ui12CompactExitMaximizeTests`.
  - 50+ cases: 5 surfacing paths × Normal / Maximized / Minimized-from-Normal / Minimized-from-Maximized, Compact round trips, and the probe order of `WindowActivation`.
  - Counterproof "unconditional restore" → 8 / 30 fail. Atlas's two mutants are killed.
- **Lifecycle:**
  - `Ui12FormDetachOnRemountTests`: Loaded-before-stale-Unloaded and liveness at the deferred read;
  - `Ui12StartupFailureExitCodeTests`: code recorded before `RequestExit`; logger unresolvable;
  - `Ui12FireAndForgetGuardTests`.
- **Error honesty:**
  - `Ui12HistoryQueryFailureTests` (real SQLite store on a temp DB);
  - coordinator / service delivery tests (C-E2);
  - `ServerProfileServiceTests` (C-E4);
  - `JsonIgnoredDeviceStoreTests`, with a real sharing violation on a temp path: Expected {a,b,c} vs the defect's {c}.
- **Accessibility:**
  - `Ui12HighContrastStateGuardTests`;
  - `Ui12FocusRingGuardTests`;
  - `Ui12LiveRegionGuardTests` + `SaAnnounce` coalescing;
  - `Ui12AutomationPeerGuardTests`;
  - `Ui12SegmentedOutlineContrastTests` (contrast computed; Prism's rejected #8A8A8A fails at 2.87–2.98);
  - `Ui12StatusTextContrastTests` (theme-resolved after Cortex N1);
  - `Ui12LayoutGuardTests` (dialog clamp, toast width, wrapping subjects);
  - `Ui12TextConsistencyTests`;
  - `Ui12CheapP3Tests`;
  - `Ui8CompactPresentationTests` (stale name).
- **States and editor:**
  - STATE-01 / 03 / 04 / 05 tests;
  - KBD-01 predicate `ShouldSubmitOnEnter`;
  - MODAL-01 Raw restored on every close path;
  - B12-04 hint;
  - B12-05 retained-secret flags.
- **Recovery:**
  - `Ui12HighContrastIconTests` (static, reads the sources): theme-dictionary visibility of the two paths; closed attribute lists of both paths, the HC path without a XAML Stroke; the collapsed `PathIcon` probe without a Foreground of its own and no app style giving one; `OnApplyTemplate` and the binding helper pinned statement by statement (Atlas A-B12B-FINAL-01); no `HighContrastChanged`, no `ElementName`; no local Foreground on an icon inside a state-text host; `SaIconSecondaryStyle` sets no Foreground in HC. Counterproofs F1–F14 (`.boss/tmp/ui12/sentry/rec/cp-final.py`: baseline must pass, each case must fail the **expected** test, restore verified, exit ≠ 0 on a survivor) — **14/14 killed**. Static guards: the real re-template and HC rendering are runtime evidence (§0).
  - `Ui12EditorModalLayerTests` (B12F-01): peer child rule, layout-only peer, StructureChanged, XAML placement, single `IsInert` writer, setter body pinned (Atlas), live-region gate; counterproofs 6/6, 4/4 and Atlas A1 / A3 / R1 / R4 / X1 killed (Relay).
  - `XamlContractAndResourceGuardTests`: `PageHost` and `PART_ForegroundProbe` in the contract inventory (the guard caught the probe on the first run).
- **Survivors:** none accepted against BOSS §10 at the end of each round (Atlas r1 survivors were closed in `5e1df48`; Cortex a11y-r2 N1 / N3 in `3ca97bf` / `41271b1`; Relay r2 M4b in `ac996b3` — pinned structurally, because no behavioural file lock reproduces it).

## 8. Reviews

| Review | Scope | Verdict |
|---|---|---|
| Atlas r1 | `e437c39`, `0742147`, `021ebf2`, `61ea7d8` | CHANGES_REQUESTED → closed by `5e1df48` |
| Cortex r1 / r2 | Sentry queue 1 | APPROVED_WITH_NITS |
| Atlas a11y r1 | Relay @ `fe738d3` | APPROVED (code / tests) |
| Prism a11y r1 / r2 | Relay items | r1 findings closed (`73223a9`, `abb7f89`, `cb078db`, `d87bd1b`); **r2 APPROVE** |
| Cortex a11y r2 | Relay tests | APPROVED_WITH_NITS (N1 / N3 fixed) |
| Relay r2 | Sentry error-honesty commits (Atlas stand-in) | APPROVE (4 × P3) |
| Vigil r1 / r2 | logging / secret retirement / fire-and-forget; B12-05 | **APPROVED** (no C/H/M) |
| Prism r3 / r5 | STATE-*, B12-04 / 05 / 06, B12-07 revert | r3 CHANGES_REQUESTED (R3-01 / 02 / 03) → fixed `fd68b34`, `97715d8`, `6f5b2e0`; **r5 APPROVE** |
| Cortex r3 | Sentry `5e1df48..fff3f90` | **APPROVED_WITH_NITS** (R3-C1 = R3-03, closed in `6f5b2e0`; R3-C2 MODAL-01 runtime gate → confirmed as B12F-01) |
| Relay r3 | Sentry `5e1df48..fff3f90` (Atlas stand-in; `7cf9358` is Relay's own handover, disclosed) | **APPROVE** (5 × P3 test gaps → backlog) |
| Prism r4 / Cortex r4 | Relay fix-r3 `85a6b08`, `5058c79` | Prism APPROVE · Cortex APPROVED_WITH_NITS (R4-N1 → `4d5510b`) — **`85a6b08` later reverted (P0)** |
| Recovery: Cortex / Prism / Atlas on B12F-01 | `ab575d3` (+ `879c079`) | Cortex **APPROVED** · Prism **APPROVE** (1 P3 for the Narrator) · Atlas CHANGES_REQUESTED (2 surviving mutants) → closed by `5016f2a` |
| Recovery: Cortex / Prism / Atlas on B12B-01 | `2753ace` | Cortex APPROVED_WITH_CONDITIONS · Prism APPROVE + P3 (→ `0bc6ad8`) · Atlas CHANGES_REQUESTED (Opacity mutant) → `389dc7e`. **Superseded:** the mechanism had no HC effect (§0) |
| Recovery: Cortex HC-fix review | P′ probe (prototype) and `843771e` | **APPROVE_WITH_CONDITIONS** — conditions 1 (single Stroke source) and 2 (guards + killed mutations) met in `843771e`; condition 3 (real HC on the Floor build) met by Beacon §6d |
| Recovery: Atlas final | `843771e` | CHANGES_REQUESTED (A-B12B-FINAL-01: a `??=` mutant survived the `OnApplyTemplate` guard) → closed by `6f66910` (F11–F14 killed) |

## 9. Runtime evidence

### UI.12A audit @ `baa6401` (Beacon)

- 25+ surfaces, **0 unnamed interactive elements** [UIA].
- [REAL-INPUT] checks:
  - Tab cycles;
  - destructive confirmations (safe default, trap, focus restore);
  - discard, import, test and trust modals;
  - theme keyboard commit;
  - minimum size 560×640;
  - rapid resize;
  - B111-1 recheck;
  - drag interrupted by a modal;
  - all harness states;
  - 3 languages × Dark / Light.
- Found B12-01…14 (§2).

### UI.12D re-QA pass 1 @ `b328a07` (Beacon, `beacon-ui12b-reqa.md`)

| Item | Result | Source |
|---|---|---|
| Regression sweep of every pass-A PASS (UIA, Tab, keyboard, window, states, 3 langs × Dark / Light at 560×640 and 780×760) | **PASS**, identical except intended changes | UIA + REAL-INPUT |
| C-W1, 9 surfacing paths incl. Cortex N1 maximized → minimize → tray Abrir | **PASS** on `b328a07`. **Counterproof:** 5 paths FAIL on a `baa6401` export | REAL-INPUT; the tray icon click simulated with the app's documented callback message to its own tray window, menu items chosen with real keys |
| `--qa-activation` (RestoreAndActivateStandard) | **PARTIAL**: Compact → Detail works; maximize not measurable at launch | launch |
| C-L1 under a real HC remount | PASS on the candidate, **but not reproduced on the baseline** → not discriminating; the guarantee rests on the unit tests | HC-REAL |
| B12-01 Light outline, 5 selectors (unfocused) | **PASS** 3.38–3.71:1 vs track, 3.71–3.76:1 vs pill | pixel probe |
| CONTRAST-01 | **PASS** 5.22:1 | pixel probe |
| LAYOUT-01 / 02, FOCUS-02, FORM-01 at 560×640 | **PASS** | UIA + REAL-INPUT |
| TEXT-01, PEER-01, STATE-02, B12-10, B12-11 | **PASS** | UIA + capture |
| LIVE-01 | **PARTIAL**: steady Overview / Services 0 events / 120 s; Data and "Guardado" toasts 1 each; empty Save 5 (1 per element); **"Servidor ocultado" 0 → B12B-02** | native UIA `LiveRegionChanged` listener (programmatic ≠ Narrator) |
| HC Night sky + Desert (granted) | HC-01 / 02 and FOCUS-01 **PASS**; **B12B-01 FAIL** (icons 1.44–1.78:1); restore to "Nenhum" **diff 0** | HC-REAL |
| Stress (Cortex plan §5) | idle: no regression (CPU ≈ 0 %, no new polling); C1 navigation grows **identically** on both builds (pre-existing, for Cortex); candidate C2–C10 **NOT_RUN** (Firewall dialog) | programmatic sampling on the QA PID |

### Final

- **Final pass @ `118c044` — FAIL (P0):** every harness died 4.4–5.3 s after launch with no input (0xc000027b, `Microsoft.UI.Xaml.dll`). Bisect on git-archive exports: `b328a07` / `97715d8` / `870b64c` good, `85a6b08` bad (parent good). Real data diff 0.
- **Final pass @ `d36becb` — UIA only, no `SendInput` (Firewall dialog in the foreground):** life probe 13 harnesses > 15 s + overview 60 s, 0 crashes in 114 launches; smoke Dark 75/75 surfaces × pt-PT / pt-BR / en-US and Light 3 × 8 pages, 0 unnamed interactive; PASS B12B-02 (1 event), B12-04, B12-05, STATE-05 retry (0/8 "A ligar"), STATE-01 (exactly 1 event), stale cue visible without clipping, C-W1 W1/W2/W5/W6/W7a/W9; **FAIL B12F-01**; P3 B12F-02; NOT_RUN W3/W4/W7b (tray menu needs real input). Prism check: Servers header "Adicionar servidor" still visible in the config-unavailable state (the decision named the empty-state CTA) → backlog. Real data diff 0, Firewall 711 unchanged.
- **Recovery (2026-10-13, details in §0):**
  - stress C1W–C10, tray W3 / W4 / W7b and the keyboard pass @ `d36becb` vs `baa6401` (`beacon-stress-r1.md`);
  - final QA @ `717b520` (`beacon-final-qa.md`): life probe 14 / 14, B12F-01 10 / 10, C1 identity, real input, Narrator 1–6 with the human, 7–10 by UIA;
  - B12B-01 diagnostic (Dark + real Night sky, diagnostic build) and HC validation of the prototype (`sentry-hcdiag.md`, `sentry-hcfix-proto.md`);
  - B12B-01 on the Floor build `843771e`: real HC Night sky + Desert, icon-crop pixels, Servers rows with recycling; C1 0 / 26 above noise (`beacon-hcdiag.md` §6b / §6d);
  - Floor launch smokes with HC off on `843771e` (overview detail, settings, compact, editor; 62 s each, 0 WER).
- **Final CI:** `38352809526` @ `d36becb` green on attempt 1 (Debug + tests incl. Infrastructure; Release + vulnerability scan). Earlier: `38342264081` Release red (fixed `b568c24`); `38350167959` attempt 1 red on FLAKE-INFRA-CREDMIG-POOL (credential-migration test, code untouched), attempt 2 green.

## 10. NOT_RUN (accepted unless the human decides otherwise)

Updated after the recovery (§0): the Firewall blocker is gone, so the real-input pass, C-W1 W3 / W4 / W7b and stress C1W–C10 **were run**; Narrator steps 1–6 were run with the human.

- **Narrator:** steps 3.2 / 3.3 / 3.4 / 3.6, 5, 6 and 7–11 (incl. the B12F-01 scan-mode check) were not done with the Narrator — 7–10 and 4A are "UIA-verified; Narrator NOT_RUN". UIA properties and the listener are not a Narrator PASS. B12B-02-R: Narrator heard only in pt-PT with the normal theme for Hide; pt-BR / en-US, HC on and Remove are UIA PASS, Narrator NOT_RUN.
- **High Contrast:** a disabled item in GrayText measured only in the diagnostic build (no stable disabled icon state on the Floor build); HC theme transitions with the window open measured only in the diagnostic build (OFF → Night sky → Desert → OFF); Light measured by Beacon C1 only, not by a separate Light HC run (HC replaces Light).
- **B12B-01 re-template at run time** (template A → B on one icon): not exercised; covered by a static guard only.
- **C9W CPU after leaving History:** not measured (stop rule hit first on both builds). C5 / C6 / idle not requested in the recovery.
- **DPI ≠ 100 %:** not granted. This includes C-W4 and LAYOUT-01 at 150 %.
- **Real pen, real touch:** no touch digitizer; the human's real-pen pass was not available.
- **Windows toasts:** packaged-only registration; this covers TEXT-01 notification wording and C-E2 at runtime.
- **Widget Medium with ≥ 3 servers:** needs the installed MSIX.
- **Startup exit code at runtime:** no new QA fault-injection flag (decision).
- **Onboarding in Light:** follows the Windows app theme.
- **Windows text scaling (LAYOUT-04):** needs a system setting.

## 11. Figma (file `Qvk5dUFgsWf4UOYzfAkiDV`, page 05 Free; Prism)

New section **"21 · Acessibilidade — notas de spec (UI.12)" `291:474`**. Text notes only; no frame, variant or layout change; page 06 Pro untouched.

| Note | Node | Content |
|---|---|---|
| B12-01 | `291:475` | Light selected segment outline (rect `112:2850`, pill `112:3517`, theme `112:7935`) |
| STATE-01 | `292:182` | Servers shows the Overview's config-unavailable notice (refers to §13 `112:14007` / `112:14100`) |
| B12-05 | `292:185` | "Já introduzida" placeholder (Vigil N1 recorded) |
| B12-06 / STATE-03 | `292:188` (body `292:190`) | stale cue in rows (refers to `112:1434`); updated to the landed state (Stacked + 2-line wrap, `fd68b34` / `97715d8`) |

Optional notes not applied (Boss decides): CONTRAST-01 (Compact offline text in Light), FOCUS-02 (field error + focus state on `112:16924` / `112:17088`), HC behaviour (outline selection, no hover fill).

## 12. Backlog created or updated

- `KBD-BACK-ACCEL` (B12-08)
- C-S1b persisted startup-failure record
- ~~B12B-01~~ / ~~B12F-01~~ (fixed in the recovery, §0); B12F-02
- ~~B12B-02-R~~ (fixed in the recovery, §0); B12H-01 ("Ex.:" copy), B12G-01 (HC → HC fill), R1-01-R (duplicate Data-toast event), focus note after "Continuar a editar" (Prism)
- QA harness: `--qa-start=servers` never loads data (`Qa/QaShellStartup.cs`) — the Servers page stays "A carregar…"; use overview / history and navigate
- Light / Dark: the checked item's icon keeps its rest colour while its text uses the selected colour (same mechanism as B12B-01; deliberately unchanged in UI.12 — Prism to decide)
- N-4 / W-1
- C-L3…C-L6, C-L9
- C-E3, C-E6, C-E9…C-E14
- HC-03, LAYOUT-04
- P3-18 tray checked state; R1-07 HC hover outline
- B12-03 cut frame
- B12-09 Home/End
- Compact Maximize glyph re-check on WinAppSDK updates
- UI11-TEXTGUARD-CLASS
- FLAKE-INFRA-LOOPBACK-CHILD (separate PR)
- reconcile the CI-FLAKE-WALLCLOCK-2 entry
- C1 / C8 memory growth (pre-existing, Cortex)
- C2c modal-test memory (+32.8 vs +28.0 MB, not monotonic) — Cortex note
- FLAKE-INFRA-CREDMIG-POOL (new CI flake, separate test-only PR)
- implementer gates must include an HC-off launch smoke (the P0 passed every unit test and CI)
