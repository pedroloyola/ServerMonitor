# UI.11 — Motion & Transitions

**Status: final QA PASS (UI.11D r2, §14), NOT MERGED.** PR #38 (`ui/ui11-motion-transitions`) is a draft; code validated at `0777aba`.
- No merge without a separate human GO.

**Branch:** `ui/ui11-motion-transitions`, base `main` @ `3ec2473`. This document's commit adds docs only.

**Scope: motion polish, not redesign** (binding human GO, `.boss/tmp/ui11/human-go-prompt.md` §2). The UI.10 resting look, layouts, palette, typography, components, navigation, flows and features are preserved. The only visual change at rest is the deliberate difference DD-UI11-1 (§6).

**Unchanged:**
- monitoring engine, SSH, credentials, trust, persisted contracts;
- Widget V3;
- Pro;
- hardening (UI.12).

**Execution:**
- **UI.11A:** independent motion & semiotic audit (Prism), feasibility (Cortex), runtime baseline (Beacon).
- **UI.11B:** Motion System in Figma (Relay r1–r5).
- **UI.11C:** implementation (Sentry) + fix rounds 1–3.
- **UI.11D:** runtime motion QA (Beacon), r1 at `6dd7870`; r2 pending.

**Evidence labels:** [CODE] · [FIGMA] live node · [MEASURED] runtime frame series at 55–60 captures/s (capture rate, **not** app FPS) · [INFERENCE].

`.boss/**` paths are evidence. No personal data, real hosts or screenshots are reproduced here; the QA harnesses use fixture data.

## 1. Outcome

- **H01–H03 (the human's priority) are implemented and measured** (§2): sliding segmented selection, toggle motion, interruptible selection.
- The **Motion System** is defined once (tokens, easing, patterns, rules, Reduced Motion: §4) and documented in Figma (§5).
- **Findings F01–F21** from the audit, plus **B11-1…B11-11** from runtime QA, are resolved, reverted with a documented platform limit, accepted, or left pending a human decision (§3).
- **F16** (hide Standard before the Compact resize) **failed** its effectiveness gate (C-8). It was **reverted to UI.8 behaviour**, and A-7 is recorded as a **platform limit** (§6).
- **Reviews** (§10):
  - Prism: APPROVED_WITH_NITS (r3; N-1 = this document + report addendum).
  - Cortex: APPROVED (r3; R3-N1 optional).
  - Independent tests review, **internal subagent acting as Atlas fallback**: APPROVED (r2).
  - Beacon UI.11D r1: FAIL on B11-1 / B11-2, both fixed in round 3; r2: PASS with the accepted B11-4 limit (§14).
- **Gates @ `0777aba`:**
  - `.slnx` Debug `--no-incremental` all green (App 3613, Infrastructure 887 + 1 skipped, others green);
  - Release all green (App 3131);
  - PR CI green (Debug build + tests; Release build + vulnerability scan).
- **Real data:** identical before and after every Sentry QA window. **Firewall:** the app added no rules; see §12 for the four test-host rules.

## 2. Human priorities H01–H03, before → after [MEASURED]

| ID | Before (Beacon baseline @ `3ec2473`) | After | Evidence |
|---|---|---|---|
| **H01** Sliding segmented selection: theme, History range, Workloads filter, editor auth ×2 (+ sidebar, F07) | One pill vanishes and another appears: **0 intermediate frames** | One Composition indicator per track slides from the presented position to the target. Offset + Size animate, so the width follows the filter's variable segment and the radius is never distorted. Fixed 250 ms point-to-point. 8–14 intermediate frames, monotonic, 1 pill, final position within ±3 px of the target | Sentry run1 / r3 (History dark 12 / light 13 intermediate frames; F07 8–12 after the B11-4 fix); Beacon UI.11D: Dark 24/30, Light 17/30 strict PASS, the failures being start-latency margins accepted by Prism D4 |
| **H02** Toggle (×4, incl. Compact "Sempre no topo") | Thumb teleports (Duration 0) | Thumb travels end to end (PtP 167 ms). Track fill crossfades (Linear 167). Thumb colour **swaps at mid-travel** (83 ms, Prism D1). Exactly one thumb ellipse at rest (D3). Reversible mid-travel from the presented value | Beacon: 7–10 intermediate frames, reversal correct; Sentry S-2 PASS; r3 D1 / D3 inspection |
| **H03** Interruptible | n/a | Retarget, never queue: every new target starts from the presented value (`this.StartingValue`). One indicator per track. The theme slide survives the theme remount (S-1). The final state always equals the logical state | Beacon: theme rapid 6/6, History ping-pong PASS, toggle on/off/on 8/8 PASS |

## 3. Findings (summary)

**Audit findings (Prism UI.11A rev. 4).**

| ID | Sev. | Subject | Resolution |
|---|---|---|---|
| F01–F03 | High | H01 / H02 / H03 | Implemented (§2) |
| F04 | High | Theme selector vs theme remount | Composition objects are kept across Unloaded, and a remount on the same target is a NoOp. S-1 PASS, so the PH-2 fallback was not needed |
| F05 | Medium | Hover / pressed hard cut | 83 ms `BrushTransition` on every Sa overlay, from an explicit Transparent. Never on a selection Shell. Removed under Reduced Motion |
| F06 | Medium | Page cut | `EntranceThemeTransition` on `Frame.ContentTransitions`: vertical 12, **no lateral offset**, armed after the startup page, no replay on the theme remount (S-4). Settle timing is still under evaluation (B11-9, Prism D4: conditional) |
| F07 | Medium | Sidebar jump | Same primitive, vertical. Runtime fixes: remount lifecycle (live-tree gate) and B11-4 (the slide starts in the click's input turn) |
| F08 / F14 | Medium / Low | Editor modal layer, step swap | SaDialogStyle pattern (fade 167 + scale 1.05→1 at 250 on open; fade-out 83 on close); new step fades in |
| F09 | Medium | Dialog ignored Reduced Motion | `MotionVisualStateManager` gate |
| F10 | Medium | Toasts / notices | Rise in (167 + 8 px / 250); the toast fades out in 83 ms |
| F11 | Medium | Content-enter | **First appearance only** (`ContentFade`). Never on a filter, search, range change or refresh (P-1, B11-2) |
| F12 / F13 | Low | Onboarding, editor reveals | Step fade + dot width (the one documented dependent animation); rise on reveals; siblings instant |
| F15 | Low | Two-step theme change | Measured as baseline; kept (D-B4) |
| F16 | Medium | Clipped Standard→Compact frame (A-7) | **Implemented, then REVERTED** (C-8 FAIL, B11-1). Platform limit (§6) |
| F17 | Low | Indeterminate bars in idle | Bound to IsBusy: 0.00 % CPU with the backup dialog open (was 0.62 %) |
| F18 | Nit | Hygiene | Stale MotionTokens comment fixed; the rest goes to UI.12 |
| F19 | Nit | Animated ✓ | Not done (pending human, default no) |
| F20 | High | Light rect pill invisible (Δ1) | Light-only hairline: **DD-UI11-1**, extended to nav (P-2) and auth (D2) |
| F21 | Low | Focus ring vs pill timing | Kept: the ring is instant by design (D-B2); real-keyboard retest is NOT_RUN |

**Runtime QA findings (Beacon UI.11D r1 @ `6dd7870`).**

| ID | Sev. | Resolution |
|---|---|---|
| B11-1 | High | F16 C-8 FAIL → **reverted**, A-7 platform limit |
| B11-2 | Medium | History range change re-faded the metric cards → `ContentFade` (first appearance only) |
| B11-3 | Medium | Light sidebar pill invisible → P-2 hairline (already in `7eab500`) |
| B11-4 | Medium | F07 started after the page build → Checked flushes in the input turn. Measured firstChange +7..+30 ms (was +84..+356) |
| B11-5 | Low | Toggle start latency → accepted (D4). Cause: UIA call + 1 frame + PtP slow start; no deferral in the code |
| B11-6 | Low | Dark toggle thumb vanished mid-crossfade → discrete thumb swap at mid-travel (D1) |
| B11-7 / B11-8 | Low | Theme remount and History start margins → accepted (D-B4 / D4) |
| B11-9 | Low | F06 settle +370–710 ms → conditional on Beacon's offline `settle − firstChange` (Prism D4) |
| B11-10 | Low | Light auth pill Δ3 → hairline extended to auth (D2) |
| B11-11 | Nit | Two ellipses at rest in Light On → On hides the Off ellipse (D3) |

## 4. Motion System

**Tokens.** `Styles/Tokens/Motion.xaml` with `Services/Motion/MotionTokens.cs`. Semantic aliases sit on the Fluent scale, which is unchanged.

| Token | Value | Use |
|---|---|---|
| `SaMotionHoverDuration` | 83 ms | hover / pressed |
| `SaMotionToggleDuration` | 167 ms | toggle travel + track |
| `SaMotionSelectDuration` | 250 ms | slide-select (segmented, sidebar, onboarding dot) |
| `SaMotionEnterDuration` | 167 ms | opacity of an entrance |
| `SaMotionEnterOffsetDuration` | 250 ms | translation / scale of an entrance |
| `SaMotionExitDuration` | 83 ms | exits |
| `SaMotionExpandDuration` / `SaMotionCollapseDuration` | 250 / 167 ms | reserved for animated height (not used) |
| `SaMotionFadeDuration` | 83 ms | discrete thumb swap point |

Hierarchy: 83 < 167 < 250. Nothing exceeds 250 ms. An exit is always shorter than its entrance. `SaMotionSlowDuration` (333 ms) is reserved and unused.

**Easing:**
- Point-to-point `0.55,0.55,0,1` (indicator, thumb, dot).
- Decelerate `0,0,0,1` (entrances).
- Exit `1,0,1,1` (new token `SaMotionExitKeySpline`).
- Linear `0,0,1,1` (opacity).
- No spring.

**Patterns.** Every pattern snaps to its final state under Reduced Motion.

| Pattern | Implementation |
|---|---|
| slide-select | `SaSlidingSelection`, driven by the pure `SelectionIndicatorPlanner` |
| thumb-toggle | `SaToggleSwitchStyle` VisualTransitions |
| hover-fade | `SaMotion.HoverFade` |
| page-enter | `Frame.ContentTransitions` |
| dialog | ContentDialog VSM; editor layer via `SaMotion.PlayEnter` |
| transient | `SaToast` / `SaInlineNotice` (`SaMotion.Enter=Rise`, toast `ExitFade`) |
| content-enter | `SaMotion.Enter=ContentFade` |
| content-swap / reveal | `Fade` / `Rise` |

**Rules:**
- Logical state (IsChecked, IsOn, UIA patterns) never waits for motion.
- Animations are one-shot, finite, and started only by input or a state change. No timers, polling, or work at idle.
- Composition animations run on Translation, Opacity, Scale or geometry, off the UI thread.
- Resize, DPI change and remount snap.
- Metrics, status colour, charts, lists, filters, resize and the theme itself stay **instant**.
- First presentation never animates.

**Reduced Motion:**
- One source, `IReducedMotionSource`: a single `UISettings`, with `AnimationsEnabledChanged` marshalled to the UI dispatcher.
- Consumers: `MotionVisualStateManager` gates template transitions, the planner snaps, `SaMotion` goes instant, and the page transition is cleared.
- QA uses the Debug-only `--qa-reduced-motion` (a strict modifier, never on in Release).
- Beacon UI.11D: 25/25 cases snap.

## 5. Principles and semiotics (summary)

- **Continuity:** a single pill that slides; a continuous thumb.
- **Causality:** the indicator leaves the previous item; a reveal grows from its trigger.
- **Hierarchy:** motion never competes with data. Metrics are instant, because interpolation would fabricate readings and trends.
- **Predictability:** fixed durations, retarget without queueing, final state = logical state.
- **Perceptual accessibility:** Reduced Motion is global and dynamic; nothing is communicated by motion alone.

**Semiotic decisions:**
- The page entrance has no lateral direction (the sidebar has no spatial order).
- The content-enter means "data arrived", so it plays only on first appearance, never on a filter or range change.
- The toggle thumb "flips" at mid-travel like a physical switch (D1).
- No colour interpolation on status.

## 6. Deliberate differences and platform limits

| ID | Difference | Source |
|---|---|---|
| **DD-UI11-1** | Light-only 1 px `SaDialogButtonBorderBrush` hairline on the sliding indicator of the **rect** (History, Workloads filter), **nav** (sidebar, normal and rail) and **auth** (editor ×2) families. The app does not reproduce the Figma DROP_SHADOW, without which these pills are invisible in Light (Δ1–3). The theme selector does not get it. Not in Dark or HC | Prism rev.4 D-B1, P-2, D2 |
| F12 dot | The onboarding active dot animates Width: the single dependent animation (3 dots, once per step) | Prism (accepted) |

**Platform limits (no action):**
- **A-7 (Standard→Compact):** 1–2 frames of clipped Standard content appear during the window resize. Hiding with Opacity before the resize (F16) was implemented under Cortex conditions C-1..C-7 and **failed** its C-8 frame gate: the resize is presented before the opacity is committed. **F16 is reverted**; the switch is UI.8 again.
- No window-geometry animation (Compact↔Standard).
- Widget V3: no motion (Adaptive Cards / Widgets Host).
- The theme change does not crossfade (remount + acrylic).
- Popups and flyouts keep the WinUI defaults.
- **B11-4 (heavy-page sidebar slide):** the slide starts in the input turn, but on heavy pages (History +247 ms, Servers +162 ms) the UI thread builds the page synchronously and blocks the composition commit, so no motion can appear before the build ends. Light pages start at +66–90 ms. Accepted as a platform limit (Prism `prism-ui11d-r2-ruling.md` §1a).
- **B11-9 (F06 page entrance):** the system `EntranceThemeTransition` measures about 316–350 ms including its deceleration tail. The trigger is not met, so it is accepted and kept: Windows 11 parity, 12 px, no lateral offset, no replay on remount, cleared by Reduced Motion (Prism ruling §2).

## 7. Figma changes (file `Qvk5dUFgsWf4UOYzfAkiDV`; Relay r1–r5)

**r1, Motion System section in 04 Manual:**
- M1 tokens `262:242`;
- M2 curves `262:297`;
- M3 patterns `263:242`;
- M4 before/mid/after `263:320`;
- M5 limits / Reduced Motion / semiotics `263:508`.

**r1, page 05 Free:**
- section `260:428` with 5 component sets;
- 32 SMART_ANIMATE reactions;
- playground `261:449` + flow;
- sidebar annotated `261:443`;
- "não reproduzível no Figma" marked (`261:446`, `263:526`).

**r2, Prism nits:** RB-1 `262:296`; RB-2 literal tokens on `262:319 / 337 / 355 / 373`; RB-3 `263:528` / `261:448`.

**r3, P-3:** M1 split into `SaMotionEnterDuration` / `SaMotionEnterOffsetDuration` (`262:265–267`); M5 DD-UI11-1 (`263:528`).

**r4, Prism D5:**
- M5 A-7 platform limit (`263:525`);
- DD-UI11-1 = rect + nav + auth (`263:528`);
- M3 content-enter never on range / filter / search (`263:299`);
- M3 thumb-toggle discrete swap (`263:265`);
- M4 toggle mid-frame in the destination colour (`263:356` / `357` / `349` / `507`);
- 05 toggle card `261:442`; auth card `261:439`.

**r5, Prism r3 nits:** content-enter "só na primeira aparição" (`263:299`); toggle card text merged (`261:442`).

Pro is untouched. UI.10 decisions are preserved.

## 8. Human decisions (pending; the conservative default applies)

- **PH-1:** live OS "Animation effects" toggle test. **NOT_RUN** (it would change the user's session setting). QA uses `--qa-reduced-motion`.
- **PH-3:** switching navigation to `Frame.Navigate` / NavigationTransitionInfo is out of scope.
- **Theme-apply debounce** during rapid H03 theme clicks (each click applies the theme, giving intermediate flashes, D-B4). Default: unchanged.
- **Defer the page build by one frame** so the B11-4 sidebar slide can appear before a heavy page is built. It changes navigation timing. Default: no (Prism ruling §1b).
- Skeleton delay on fast loads. Default: none.
- Toast overlay conversion (removes the Auto-row jump; a layout change). Default: no.
- Animated height with sibling reposition. Default: no (siblings instant).
- Directional drill-in for Detail. Default: no.
- Animated ✓ in the connection test (F19). Default: no.

## 9. Guards (each with a counterproof)

Each counterproof breaks the rule, fails for the right reason, restores the SHA-256 byte-exactly, and ends with a `--no-incremental` rebuild + full tests.

- **Pure rules:**
  - `SelectionIndicatorPlannerTests`: retarget, burst, remount NoOp, layout snaps, reduced snaps, hide/re-show;
  - `ReducedMotionSourceTests`: marshalling, real-change only, QA pin, Dispose, Install disposes the replaced source;
  - `MotionTokensTests`: aliases on the scale;
  - rule theories (`PlaysEnter`, `PlaysContentEnter`, `DrawsLightOutline`, `SelectedIndex`).
- **XAML / source guards:**
  - `Ui11SelectorGuardTests`: Checked never paints Shell/Highlight (Setter or Storyboard); one host per group; brush and hairline per family; theme without hairline.
  - `Ui11ToggleGuardTests`: 4 transitions with no From; discrete thumb swap; one ellipse at rest; gated.
  - `Ui11FindingsGuardTests`:
    - F05, F06, F07, F08 / F14, F09, F10, F17;
    - F11 forward (VM-resolving) and reverse (filter/search methods);
    - presenter / SaMotion wiring (T-1);
    - live-tree remount fix (T-3);
    - B11-1 revert, B11-2, B11-4;
    - R-1 / R-1b / R-2 exact shapes;
    - Light-hairline call (T2-2).
  - `QaReducedMotionPolicyTests`: strict flag; Release never on; the App constructor reads it only in `#if DEBUG`.
- **Counterproof runs** (`.boss/tmp/ui11/sentry/`):
  - delivery: 15 mutations;
  - fix round 1: 8;
  - fix round 2: the reviewer's O2, O3, O5, O6, O8, O9, O11 + S1;
  - fix round 3: 11 + the reviewer's N1, N2, N4 (adapted), N5 + N1b.
  - All fail the expected named tests. Details in `sentry-ui11c-report.md` and its addenda.

## 10. Reviews

| Reviewer | Rounds | Final |
|---|---|---|
| Prism (direction, consistency, semiotics, Figma) | UI.11B review; UI.11C r1 (CHANGES_REQUESTED: P-1, P-2) → r2 APPROVED → r3 APPROVED_WITH_NITS; UI.11D decisions D1–D5; UI.11D r2 ruling (B11-4, B11-9) | **r3 APPROVED_WITH_NITS, all nits closed** (N-1 = this record + report addendum) |
| Cortex (architecture, lifecycle, performance) | feasibility; D-B3 verdict SAFE_WITH_CONDITIONS; UI.11C r1 / r2 APPROVED_WITH_NITS (R-1…R-6, R-1b) → r3 | **r3 APPROVED** (R3-N1 optional, backlog) |
| Tests & counterproofs: **internal subagent acting as Atlas fallback** (Atlas unavailable; not presented as Atlas's approval) | r1 CHANGES_REQUESTED (T-1…T-6) → r2 APPROVED (T2-1…T2-4, fixed in round 3) → r3 on `0777aba` (T3-1 / T3-2 → backlog UI11-TEXTGUARD-CLASS) | **r3 APPROVED** |
| Beacon (runtime, a11y, Reduced Motion, visual QA) | UI.11A baseline; UI.11D r1 @ `6dd7870` (B11-1…B11-11); UI.11D r2 @ `0777aba` | **UI.11D r2 PASS** with the accepted B11-4 limit (§14) |

The implementer (Sentry) did not approve its own work.

## 11. NOT_RUN (with reason)

- **PH-1:** live OS animation toggle (would change the user's session setting).
- **Real keyboard / mouse:** arrows with SelectionFollowsFocus, Space, real-cursor hover (F05), real-input toggle latency. Needs a free foreground with the human present.
- **High Contrast, DPI ≠ 100 %, Narrator:** would change the session for every app and agent.
- **C-8 in screen (BitBlt) mode:** no foreground. Moot after the F16 revert.
- **Timed loading→content (F11 first appearance):** no harness has a timed transition.
- **B11-2 runtime:** inconclusive in the `mixed` scenario. Proven by tests and counterproofs.
- **Jump-host card reveal (F13):** appears below the visible area.
- **Idle / CPU / memory plateau series** beyond the F17 measurement.

## 12. Backlog (not in UI.11)

- **Cortex R-5:** per-selection Composition allocations (GC'd; no leak) could be cached.
- **Cortex R3-N1:** the disabled-dim opacity can lag one frame. `UpdateOpacity` picks the first checked item, which can still be the previous sibling during the immediate flush.
- **Page build cost of History and Servers** (performance, out of UI.11): the root of the B11-4 heavy-page limit.
- **UI11-TEXTGUARD-CLASS:**
  - T3-1: the `_shownOnce` reset;
  - T3-2: the `SelectedIndex` argument at the call site.
  - Source-text guards do not prove the wiring (the same class as T-1 / T2-1 / T2-2). Under BOSS §16 a third round of the same class is a decision gate, so these are declared a limitation. Both behaviours (B11-2, B11-4) passed at runtime.
  - **UI.12:** replace them with behavioural tests on the real XAML tree.
- **Prism F06 trigger rule:** replace the system entrance with the tokenised `SaMotion` Rise only if the median `settle − firstChange` is > 333 ms or any case is ≥ 354 ms (333 + one capture interval).
- **F18 nits → UI.12:** unused `using …Media.Animation` in four primitives; `SaSkeleton` pulse not stopped on Collapsed and not reacting to setting changes.
- **Firewall exception (pending human UAC).** Four inbound **Block** rules ("Query User" defaults, created when nobody answered the prompt), for `<Floor>\tests\ServerMonitor.Infrastructure.Tests\bin\{debug,release}\net10.0\testhost.exe`:
  - 2 Debug (pre-existing at my first listing);
  - 2 Release (created by the canonical Release `.slnx` test run).
  - The app created none. Rules are never removed without the human.
- **Disk incident.** C: reached 0 bytes free during UI.11D r1 (full-frame captures plus builds). Boss freed regenerable `bin`/`obj`. Since then builds are `.slnx`-only, with temporary OutDirs deleted and ROI-only captures. About 6 GB free at `0777aba`. A human cleanup decision is advisable.
- **Shared-Floor build incident (UI.11A).** One early Sentry build attempt overwrote outputs Beacon was capturing from. Beacon superseded that pass and moved to a pinned export with a bin-manifest SHA. Lesson: QA on a fixed worktree / export.

## 13. Commits (`3ec2473..0777aba`)

| SHA | Scope |
|---|---|
| `01df767` | infra: tokens, Reduced Motion source, planner, QA flag |
| `89b6104` | H01 / H03 sliding indicator on 5 selectors |
| `f080dfe` | H02 / H03 toggle motion |
| `96045af` | F05 / F06 / F07 / F09 / F17 |
| `18325da` | F08 / F10–F14 |
| `beeeeff` | F20 rect hairline |
| `7796a34` | F16 (reverted in `6dd52e2`) |
| `b97848c` | Release test compile fix |
| `f432dfb` | F07 remount lifecycle fix |
| `d3f8962` | F07 live-tree fix |
| `6dd7870` | live-tree guard |
| `7eab500` | fix round 1: P-1 / P-2 |
| `a5e7123` | fix round 1: R-1…R-6 |
| `d654f77` | fix round 2: T-1…T-6 (test only) |
| `92ebed9` | fix round 3: Prism D1–D3 |
| `6dd52e2` | fix round 3: F16 revert, B11-2, R-1b, B11-4 |
| `0777aba` | fix round 3: T2-1…T2-4 (test only) |

## 14. UI.11D r2 final QA (Beacon, binary `0777aba`, fixed worktree, full-bin hash guard on every launch and capture)

Verdict: **PASS**, except one sub-criterion (B11-4 residual). It is accepted as a platform limit by the Prism ruling (`prism-ui11d-r2-ruling.md`) and is listed below. Evidence is in `.boss/tmp/ui11/beacon-ui11d-qa-r2.md` (local) and `C:\Users\pfloy\wt\ui11-qa\after-r2\` (ROI captures only).

| Item | Result |
|---|---|
| B11-1 F16 reverted | PASS. Standard↔Compact matches the baseline `3ec2473`, normal and maximized. There is 1 clipped Standard frame, which is the documented A-7 limit. |
| B11-2 History range change | PASS. The cards change in 1 frame with 0 intermediates, in Dark and Light. They still fade on first appearance after loading. |
| DD-UI11-1 Light hairline | PASS. Δ against the track: sidebar rail 21, sidebar normal 20, History 19, filter 19, auth 21, jump-host auth 20. The theme selector has no hairline (Δ7). Dark has no hairline. |
| D1 / B11-6 Dark toggle thumb | PASS. Minimum contrast during travel is 70–80 (r1: 9–13). |
| D3 / B11-11 Light toggle On at rest | PASS. Pixel-identical to the baseline (0 px). |
| H01, re-scoped D-B5 | PASS. Dark: theme 8/8, History 18/18 (pt-PT, pt-BR, en-US), filter 8/8, auth 3/3, jump 3/3. Light: theme 8/8, jump 3/3, rect by edges 23/26 (the 3 others are a measurement limit: the same transition passes on a repeat). Always a single pill, monotonic, on target to ±3 px. |
| H02 | PASS. 24/24 in Settings, plus Compact min/max 8/8. Duration 133–167 ms, 8–10 intermediates. |
| H03 | PASS. Rapid theme at 50/80 ms, toggles on/off/on, History ping-pong: logical state == visual state, and the indicator converges to the last target. |
| Reduced Motion (`--qa-reduced-motion`) | PASS 40/40. Snap, 0 intermediates, logical state correct. |
| Resting look vs baseline | PASS. The only deltas are DD-UI11-1. |
| P-1: no fade on search or filter | PASS for Servers, and for Workloads with data. |
| pt-BR / en-US | PASS. Segment widths and slides follow the language. |
| B11-4 sidebar slide start | Light pages (Settings, Overview): +66–90 ms, PASS. Heavy pages (History +247 ms, Servers +162 ms): **ACCEPTED LIMIT**. The UI thread builds the page synchronously and the window presents no frames meanwhile; the Composition animation starts in the input turn but commits only when the UI thread frees. Deferring the page build is out of scope and pending a human decision. The page build cost of History and Servers is in the backlog. |
| B11-9 F06 entrance | 316–318 ms in 6/8 navigations; 335 and 350 ms in 2/8, which is within one capture interval (~17–20 ms) of 333. Trigger NOT met. The system `EntranceThemeTransition` is kept and documented. Future rule: the trigger fires if the median is > 333 ms or any case is ≥ 354 ms. |
| Performance (exact PID) | Idle returns to 0 % after bursts. Memory: +33 / +77 MB against the base's +29 / +69 MB over 70 navigations, with no growth at idle. Burst CPU is higher only while interacting. No FPS claims. |
| Real data / Firewall / processes | 0 diffs; Firewall 715→715 (0 new); 0 QA processes. |

NOT_RUN in final QA:
- Real keyboard and mouse, Tab order, A-5 retest, F05 hover and pressed with a real cursor, H02 latency with real input. The desktop foreground is held by the system `PickerHost`, and keys are never injected into other windows.
- High Contrast and DPI ≠ 100 %: these would change the system session for every app.
- Narrator.
- Live OS "Animation effects" toggle (PH-1).

Test review: the internal subagent (Atlas fallback, Codex quota exhausted) r3 on `0777aba` is **APPROVED**. Two Low guard gaps (T3-1, T3-2) remain from the same class as T-1 and T2-1/T2-2: source-text guards do not prove what the wiring does. Under BOSS §16, a third round of the same class is a decision gate, not another patch. They are declared a limitation and moved to the backlog as `UI11-TEXTGUARD-CLASS` (replace with behavioural tests on the real XAML tree in UI.12). Both behaviours (B11-2, B11-4) passed at runtime above.
