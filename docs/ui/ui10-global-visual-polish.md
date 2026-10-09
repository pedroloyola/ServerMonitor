# UI.10 — Global Visual Polish

**Status: UI.10 READY_TO_MERGE — not merged (2026-10-09).** PR #36 stays DRAFT until the human GO.
- **Branch:** `ui/ui10-global-visual-polish`, base `main` @ `9440118`.
  - Code validated at `edbde65` (H07 + final batch).
  - This document's commit adds docs only.
- **Scope: polish, not redesign** (binding human GO, `.boss/tmp/ui10/human-go-prompt.md` §1, §8). Correct and refine the existing system; never replace it.
  - **Unchanged:**
    - information architecture, navigation, page structure, flows and features;
    - core colours and typography;
    - monitoring, SSH, credentials, trust and persisted formats;
    - Widget V3 (closed in UI.9);
    - Pro, motion (UI.11) and hardening (UI.12).
- **Execution:**
  - UI.10A independent visual / heuristic / semiotic audit + runtime baseline;
  - UI.10B Figma refinement (+ fix round);
  - UI.10C application polish (+ fix round 1, tests-review addendum);
  - UI10-H07 (new human feedback) STEP 1 Figma / STEP 2 app;
  - UI.10D runtime regression + final Compact check.
- **Evidence labels** (as in the sources): [CODE] · [FIGMA] live node · [MEASURED] runtime, 96 DPI, px = DIP · [INFERENCE].
- `.boss/**` paths are cited as evidence. No personal data, host names, real server names or screenshots are reproduced here. The QA harnesses use fixture data only.

## 1. Outcome

- **All seven human findings are resolved and measured before/after** (§2): H01–H06 from the GO, H07 added during UI.10.
- The **39 audit findings** (Prism UI.10A) are resolved or recorded as accepted differences (§3).
- **Anomaly A-1** (six QA harnesses exiting at start) is fixed in an isolated QA-only commit, guarded, and confirmed at runtime.
- **Reviews:**
  - Prism (fidelity + semiotics): **APPROVED**;
  - Cortex (structure): **APPROVED_WITH_NITS**;
  - independent tests review (Atlas fallback): **APPROVED**, findings fixed;
  - Beacon UI.10D: **PASS**, 0 regressions in 423 paired captures;
  - Beacon final Compact (H07): **PASS**, 24/24.
- **Gates @ `edbde65`:**
  - canonical `.slnx` Debug **6164 passed / 1 skipped**;
  - Release **5690 passed / 1 skipped** (`--disable-build-servers`);
  - PR CI green (Debug build + tests; Release build + vulnerability scan).
- **Real data:** 0 differences in every QA window. **Firewall:** 0 rules added by the app during any QA window.

## 2. Human findings H01–H07, before → after [MEASURED]

Before = Beacon UI.10A baseline @ `9440118` (`.boss/tmp/ui10/beacon-ui10a-baseline.md`). After = Beacon UI.10D @ `03ff707` (`beacon-ui10d-regression.md`) and the final Compact check @ `edbde65` (`beacon-final-compact.md`). Sentry's own self-checks are in `sentry-ui10c-report.md`.

| # | Before | After | Fix (findings) |
|---|---|---|---|
| **H01** sidebar selection | Pill 160×46 correct, but the keyboard ring inherited the RadioButton margin (−7,−3): ≈174×52, off-centre. Rail item 48×46. Settings item 44 high | Ring **on the pill edge** (margin 0), 2 px, concentric by construction, D and L. Rail **46×46** (a circle). Settings **46** like every item | F01, F02, F46; RC-1 |
| **H02** CPU/Memory/Disk cards (Detail) | Fixed 288 px chart, left-aligned: empty band **78 px (21 %)** min, **298 px (51 %)** default, **30 px (9 %)** wide | Charts fill the card's inner column: band **0 px** at every size; the same real samples (30 / 28 / 14), only wider | F04 (+ F05 Overview bars) |
| **H03** button radii | Same "Add server" action in 6 shapes; Detail/Workloads "Refresh"/"Edit" r14; empty-fleet buttons r15/16 at h46 | **Every text button is a pill, r = h/2**, via radius tier tokens (44/42/40/36/34/28). Justified exceptions keep their shape: key-file picker (a field) r12, disclosure rows r20, compact rows, breadcrumb | F06 (F07), F08 |
| **H04** page-header actions (Overview vs Servers) | "+ Add" top 69 vs 61 → **8 px jump**; title also shifted ≈4–5 px; line 2 12 vs 13 px | One shared page header on Overview, Servers and Settings: action top 59, centre 81 = title centre (**Δ 0**) on both pages; line 2 13/18 reserved even when empty; right margin 40 unchanged | F09, F10 |
| **H05** Compact "Always on top" toggle | Switch box 52×**24**: vertically clipped in **24/24** combinations | Footer row MinHeight 34: box **52×34**, track 44×26 fully visible, **0/24 clipped** (3 languages × D/L × min/max × 2 scenarios) | F11 |
| **H06** Services & containers empty states | One static "no results" panel: B (filter only, 0 problems) said "try another name" + "Clear search"; C silently cleared the filter; only "Loading…" was reachable at runtime (A-1) | Per case: **A** search → "No results for “x”" + **Clear search**; **B** filter → positive tick + "No issues found" + **Show all**; **C** both → "… in With issues" + **Clear search and filter**. **F14:** a section that is not a list (loading/error/not installed/unsupported) keeps its own card. **36/36** at runtime (3 languages × D/L) | F12, F13, F14 |
| **H07** Compact "Expand" right-aligned (added during UI.10) | Button ended at the native caption reserve: **118 px** short of the content's right edge (max and min) | Title strip below the **measured** native caption (caption height + 2, no literal); button right edge = content right edge (**dR = 0**, 24/24); 5 px below the captions (captions end at y 32, button starts at y 37); Win32 hit-test **HTCLIENT** at the button centre (the drag strip is HTCAPTION). At min, the full wordmark and the "Expand" text now show (previously "ServerAl…" + icon only) | Prism option D (`prism-h07-review.md`) |

## 3. Findings inventory (summary)

Source: `.boss/tmp/ui10/prism-ui10a-audit.md` (+ addendum A), plan `ui10-plan.md`. After the addendum: **39 findings** (9 High / 14 Medium / 12 Low / 4 Nit). F03 and F26 left the list at the addendum; F26 came back after the UI.10B review.

| Group | Findings | Result |
|---|---|---|
| Global system | F01/F02/F46 nav · F06/F07/F08 pill radius + legacy dialogs · F09/F10 page header · F15/F16/F19 icon pairs · F26 neutral toggle | Fixed with tokens / shared styles, no per-page overrides |
| Local layout | F04/F05 charts and bars · F11 compact footer | Fixed |
| States and semantics | F12/F13/F14 Workloads · F21/F37/F39 destructive look · F24/F25 severity · F29 backup with warnings | Fixed |
| Icons | F17 identity = Shield · F18 testing = progress · F20 one sign per result · F40 neutral "nothing here" · F42 not-reached stage | Fixed |
| Copy (3 cultures) | F27 "Show" for hidden servers · F28 · F30 · F32 · F33 · F34 pt-PT "tu" (27 + 2 found by the guard) · F35 pt-BR "frase secreta" · F36 majority terms | Fixed (T11 "chaves de anfitrião": no clear majority, unchanged) |
| Legacy | F43 unused `EmptyStateControl`, `DiscoveredServerCard` + 7 legacy styles | Removed with zero-use proof (debt ledgers lowered) |
| Not defects / human decisions | F22/F23 (HD-1/HD-2), F31, F38, F41, C7 | Recorded, see §8 |
| **+ A-1** | Six `--qa-*` harnesses exited 0 at start | Fixed (§7) |
| **+ H07** | Compact "Expand" alignment | Fixed (§2) |

## 4. Design principles and semiotics (summary)

Sources: `prism-ui10a-audit.md` §3–§4, `prism-ui10c-review.md` §3, `prism-final-review.md`.

- **Principles:** every change names its principle, never "looks nicer".
  - Alignment/grid: H04 shared header; H07 right edge = content edge.
  - Repetition: H03 one pill rule.
  - Continuity / balance / negative space: H02/F05 charts fill their cards.
  - Affordance: H05 nothing clipped.
  - Visibility of state: H06, F14 errors never hidden.
  - Predictability: F10 no jump; CTA = real effect.
  - Perceptual accessibility: focus ring on the pill edge; severity never colour-only.
- **Semiotics, one sign per meaning:**
  - chevron = navigate in;
  - ArrowShrink / ArrowExpand = window mode;
  - Cpu / RamMemory / HardDrive = metric;
  - Shield = identity;
  - Key = key authentication;
  - Tick / Alert = result;
  - Package = "nothing here";
  - Refresh = action only.
- **Colour:**
  - green only for health (toggle "on" is neutral, F26);
  - metric identity colours never encode state (HD-1);
  - severity is said in words: "97% critical", "88% needs attention" — the same shared rule on the Servers rows, the Compact rows and the Detail values.
- **Language:**
  - pt-PT "tu" everywhere, guarded;
  - pt-BR "você";
  - "Restore" reserved for the backup;
  - CTAs say what they do.

## 5. Figma changes (file `Qvk5dUFgsWf4UOYzfAkiDV`)

Records: `.boss/tmp/ui10/figma-before.md`, `figma-after.md`, `sentry-ui10b-figma-report.md` (§ Fix round), `sentry-ui10c-report.md` (§ Fix round 1 / FU-1), `sentry-h07.md`. Page 06 (Pro), sections 16 (Widgets V3), 17 (macOS) and 18/19 untouched.

**Page 04 · Brand manual (additions only)**
- **A1** `234:182`: pill rule and height tiers.
- **A2** `235:182`:
  - `Nav item` `235:243`, Focus ring on the pill edge, r23;
  - `Page header` `235:270`, with `Ação = Botões | Texto` (`246:478` / `246:492`).
- **A3** `238:226`:
  - `Toggle` `238:246` with neutral "on";
  - Hugeicons Cpu `238:250`, RamMemory `238:253`, ArrowExpand01 `238:256`, ArrowShrink01 `238:259`;
  - destructive rule.

**Page 05 · Free, sections 01–15**
- Pill radius on every text button (664 + 16 onboarding); nav 46 (178 items).
- 16 page headers → `Page header` instances; Settings → `247:418` / `247:424`.
- Detail charts and Overview bars in Fill; medium-width variant `244:418` / `244:514`.
- Compact:
  - always-on-top row 34;
  - H07 title strip below a dashed native-caption reference `254:428/432/436/440`;
  - wordmark Fill.
- Workloads cases A/B/C (`112:15289`, `243:422`, `243:641` + light), state icons Package/Alert02 (`250:604–610`).
- Test and trust dialogs (sections 08/09): progress / tick / alert / info title icons; Shield for identity.
- "Show again ignored devices" (8 buttons + toast).
- Copy F27.

## 6. Deliberate and platform differences

| ID | Difference | Source |
|---|---|---|
| **D-UI10-1** | Overview health bar keeps its VM-computed 213 px (segment widths are data); Figma grows it up to 426 above 1440. Identical up to 1440 | `prism-ui10c-review.md` §7 |
| **D-UI10-2** | Nav focus ring on the pill edge (margin 0); action buttons keep −3 | RC-1 |
| **D-UI10-3** | F29, F30, F32–F37, F39 exist only in the app (no Figma nodes) | Prism §2.5 |
| **D-UI10-4** | Compact title strip below the native caption (−14 DIP of list); the caption buttons are native, shown in Figma only as a dashed reference | `prism-final-review.md` |
| **FU-1** | Figma ProxyJump test stages are text with glyphs (⚠ failed, not-reached at 60 % opacity); the app uses icons | `sentry-ui10c-report.md` |

**Platform differences (no action):**
- WinUI draws the focus ring with the control's CornerRadius; programmatic (UIA) focus does not draw the ring on every control.
- Mica / Acrylic vs Liquid Glass (D7): the backdrop depends on the capture environment, it is not an app regression (`beacon-ui10d-regression.md` §3).
- Widget V3 D-1…D-18 unchanged.

## 7. Anomaly A-1 (QA tooling only)

- **Root cause** (`.boss/tmp/ui10/cortex-a1-rootcause.md`):
  - The composition root registers `IServerLoadStatusSource` as a fail-closed cast of `IServerService` (since UI.6 c1).
  - Six QA server doubles did not implement it. `MainWindow` → `OnboardingViewModel` resolution threw.
  - The startup catch exited 0 with no window.
  - **No production impact.**
- **Fix** `790e0cb fix(qa): …`: the six doubles implement the interface (Loaded; NotFound for discovery). The production cast is untouched and no fallback was reintroduced.
- **Guard** `QaShellGraphCompositionTests`: resolves the shell graph's non-UI head for all 10 QA compositions.
- **Runtime:** all six harnesses open a window. UI.10D covered Workloads, Health, History, Discovery and Notifications, which were previously NOT_RUN.

## 8. Human decisions

- **HD-1:** Disk colour = Attention colour stays (Manual authority). Mitigation by text only (F24/F25, severity words).
- **HD-2:** Critical colour = No connection colour stays. Mitigation:
  - the health bar's accessible name lists the counts;
  - segment order = chips order (guarded).
- **H07 envelope default:** the Compact envelope stays the UI.8 size (client 432×704 max, 320×340 min). The −14 DIP of list height is accepted. Growing the envelope (Prism alternative E5) would need a new human decision.

## 9. Guards (each with a counterproof: rule broken → fails for the right reason; SHA restored; `--no-incremental`)

`Ui10PolishGuardTests` covers the following rules:
- pill tier = h/2 for every Button style;
- no local re-shape of a tiered button;
- nav focus ring and one 46 geometry;
- shared page header on the three pages;
- compact footer ≥ toggle height;
- irreversible actions look destructive;
- Detail values bind severity and the meters stay severity-free;
- H07 right edge + measured caption.

Plus:
- `ComponentR1GuardTests`: neutral toggle; on-track contrast ≥ 3:1.
- `Ui5R2Tests`: meters fill the card.
- `Ui10WorkloadsNoResultsTests`: A/B/C + F14 on both Docker and Services sides.
- `Ui10CopyTests`: F27/F28/F30/F32–F35, 3 cultures; the pt-PT formal-voice regex includes possessives.
- `Ui10HealthBarOrderTests`: HD-2.
- `Ui5ServerDetailTests`: Detail severity = row severity; words in the accessible names.
- `TitleBarInsetCalculatorTests`: strip top = measured caption + 2.
- `QaShellGraphCompositionTests`: A-1.
- `IconResourceGuardTests`: 27 vendored icons, integrity unchanged.

Pins that changed value on purpose are commented with the UI.10 finding. Debt ledgers only went down (`sentry-ui10c-report.md` §3).

## 10. Reviews

| Reviewer | Scope | Verdict | Evidence |
|---|---|---|---|
| Prism | UI.10A audit + addendum; UI.10B (R1 CHANGES_REQUESTED → R2 APPROVED); UI.10C (APPROVED_WITH_CHANGES → R2); H07 STEP 1; final batch | **APPROVED** | `prism-ui10a-audit.md`, `prism-ui10b-review(-r2).md`, `prism-ui10c-review(-r2).md`, `prism-h07-review.md`, `prism-final-review.md` |
| Cortex | A-1 root cause; UI.10C structure; H07 caption/drag region | **APPROVED_WITH_NITS** (nits backlogged) | `cortex-a1-rootcause.md`, `cortex-ui10c-review.md`, `cortex-final-review.md` |
| Tests review (Atlas fallback) | guards, counterproofs, pins | **APPROVED**; M-1, L-1, L-2, L-5, L-7 fixed; L-3, L-4, L-6 backlogged | `tests-ui10c-review.md` |
| Beacon | UI.10A baseline; UI.10D regression (612 PNG, 423 pairs); final Compact (24/24) | **PASS** / **PASS** | `beacon-ui10a-baseline.md`, `beacon-ui10d-regression.md`, `beacon-final-compact.md` |

## 11. NOT_RUN (with reason)

- **DPI ≠ 100 %:** single monitor at 100 %. The H07 measured-caption rule is unit-tested at 150 % and with taller captions.
- **Keyboard-drawn focus ring and tab order:** no synthetic input. The nav ring was observed with UIA focus; for the Compact "Expand" the ring position is proven by geometry and the guard.
- **Toast visual** (`--qa-notifications`): no toast window observed in the Debug unpackaged host. The in-app state was verified, and packaged QA (UI.9/M13) covers the presentation.
- **Light theme in onboarding and discovery:** the theme is changed only in Settings.
- **Compact icon-only fallback:** not reachable at 100 % in the 3 languages (the label always fits at 320).
- **Widget V3:** out of UI.10 scope (no host changes).

## 12. Backlog (not in UI.10)

- **UI.12 focus / a11y:**
  - Detail heading focus rectangle (pre-existing);
  - segmented-control focus ring touching its neighbour;
  - "·" separators exposed as UIA Text in card layout.
- **Tests:**
  - L-3: page-local Button styles outside the F06 guard;
  - L-4: health-bar order on the built output;
  - L-6: QA compositions discovered by reflection.
- **Cortex nits:**
  - `MaxReserveDips` used as a height bound;
  - a stale "caption reserve" comment;
  - rename `CompactMetric*Accessible` keys to neutral names;
  - redundant `RaiseNoResults` notifications;
  - a startup-failure exit code / persistent log for `StartupFailure`.
- **Figma:**
  - `FIGMA-MACOS-TU` (section 17 formal copy);
  - optional 320×340 icon-only Compact frame.
- **Copy:**
  - `APP-COPY-QUASECHEIO` ("Disk almost full" wording);
  - F36 T11 term pair.
- **Observations:**
  - O-1 Servers subtitle wrap at min;
  - O-2 empty-fleet primary vs secondary contrast in Dark;
  - Compact maximize button shown as enabled.
- **HD-1 / HD-2:** colour changes would need a Manual decision.
