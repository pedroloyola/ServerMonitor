# UI.11.1 — Motion Refinement

**Status: DRAFT / BLOCKED. Not READY_TO_MERGE.** PR #40 (`ui/ui11-1-motion-refinement`) is a draft; code at `0e0c10d`.
- The drag-to-select adapter (the core of H02) has **no runtime proof**: every real pointer and keyboard case of the UI.11.1D QA was blocked by the environment (§11).
- No merge without a separate human GO.

**Branch:** `ui/ui11-1-motion-refinement`, base `main` @ `c69d0a0`. This document's commit adds docs only.

**Scope: motion refinement, not redesign** (human decisions of 2026-10-11, `.boss/tmp/ui11-1/overnight-go.md`). The UI.10 / UI.11 resting look, layouts, palette, typography, components, navigation and flows are preserved; Beacon measured the resting screens pixel-identical to `c69d0a0` (§11).

**Unchanged:**
- monitoring engine, SSH, credentials, trust, persisted contracts;
- Core / Infrastructure;
- Widget V3;
- Pro;
- hardening (UI.12).

**Execution:**
- **UI.11.1A:** feasibility and design (Cortex), UX and semiotics (Prism), QA plan (Beacon), testability (Atlas). Analysis only.
- **S0 / S0b:** practical drag spikes on a throwaway branch (`spike/ui11-1-s0-drag`, never merged), instrumentation by Sentry, real-input matrix by Beacon (§3).
- **Implementation (Sentry):** slices B, C1, C3, C2, then fix round 1 (§14).
- **Figma (Relay r1 / r2):** pages 04 and 05 (§7).
- **Reviews:** Prism, Cortex, Atlas (§10).
- **UI.11.1D runtime QA (Beacon) @ `8d9b6d1`: PARTIAL** (§11).

**Evidence labels:** [CODE] · [FIGMA] live node · [MEASURED] runtime · [SYNTHETIC] injected pen/touch · [INFERENCE].

`.boss/**` paths are evidence. No personal data, real hosts or screenshots are reproduced here; the QA harnesses use fixture data.

## 1. Outcome

- **H01 timings: implemented and measured** in runtime (UIA-driven, §11).
- **H02 drag-to-select on the five segmented selectors: implemented and unit-proven.** The pure controller and, since fix round 2, the adapter's decisions (`SegmentDragWiring`) are driven through their handlers against re-entrant fakes; 46 + 5 + 12 counterproofs (§9). **The WinUI adapter's one-call surface is not runtime-proven.**
- **H03 focus after a theme change: implemented and unit-proven** (incl. the fix round 2 no-steal / supersede rules). Runtime check INCONCLUSIVE (§11).
- **Fix round 2 (Atlas, §9a):** two real defects found by Atlas's deterministic probes are fixed — A-3 (a re-entrant cancel during the release frame threw a NullReferenceException) and A-2 (the delayed theme focus restore could steal a later user choice) — and three test gaps (A-1, AT-03, AT-05) are closed with behavioural seams.
- **Reviews** (§10): Prism APPROVED_WITH_NITS; Cortex APPROVED_WITH_NITS (N-1, N-2), structure re-check of fix round 2 pending; Atlas pending (see its report).
- **Gates @ `0e0c10d`:**
  - `.slnx` Debug `--no-incremental --disable-build-servers` all green (App 3736, Infrastructure 887 + 1 skipped, others green);
  - Release all green (App 3254);
  - PR CI green on `d0cc3b8`, `8d9b6d1`, `54e7e05` and `f0686cd` (Debug build + tests; Release build + vulnerability scan); `0e0c10d` running when this was written.
- **Real data:** identical before and after every QA window (S0, S0b, UI.11.1D). **Firewall:** the app added no rules; the Floor's test runs added four test-host rules (§13).

**Blockers before READY_TO_MERGE:**
1. The real pointer / keyboard matrix of UI.11.1D (incl. Cortex C-2, Prism P-2 / P-3, the P-1 diagonal grab, H03 focus) must run on the final head. It needs the human to close the Windows Firewall notification dialog first (§11).
2. Atlas's final verdict, and the Cortex re-check of the fix round 2 structure.
3. Merge-strategy decision for the non-compiling intermediate commit `ac36036` (§13).

## 2. Human priorities H01–H03

### H01 — timings (human decision 2026-10-11)

| Pattern | Before | After | Token |
|---|---|---|---|
| Five segmented selectors (theme, History range, Services filter, editor auth, jump-host auth) | 250 ms PtP | **300 ms PtP** | new `SaMotionSegmentSelectDuration`, per host via `SaSlidingSelection.DurationKey` |
| Sidebar (F07), onboarding dot | 250 ms | **250 ms** (unchanged) | `SaMotionSelectDuration` |
| Toggle thumb travel + fill | 167 ms | **200 ms** | `SaMotionToggleDuration` |
| Toggle thumb colour swap at mid-travel | 83 ms (`SaMotionFadeDuration`) | **100 ms**, own key (= ½ toggle, same 92 % travel point as Prism D1) | new `SaMotionToggleSwapTime` |
| Drag settle after release | — | **200 ms Direct** | new `SaMotionSettleDuration` + `SaMotionDirectKeySpline` |
| Drag cancel return | — | **300 ms PtP** (the selector's selection animation) | host `DurationKey` |
| Hover / fade | 83 ms | **83 ms** (unchanged) | `SaMotionHoverDuration` / `SaMotionFadeDuration` |

- 200 / 100 / 300 are **deliberately off the Fluent 83 / 167 / 250 scale** (the `Motion.xaml` comment records the decision). No pattern exceeds 300 ms; `SaMotionSlowDuration` (333) stays unused.
- `SelectionIndicatorRules.DurationKey` fails closed: a host key that is not a time token throws, never falls back silently.

### H02 — drag-to-select (five segmented selectors only)

- **Opt-in** `SaSlidingSelection.AllowDrag` (false by default) on exactly the five hosts. The sidebar (vertical, Click-driven navigation) and the toggles (native `ToggleSwitch`, not a sliding selection) are excluded by construction; only horizontal tracks are accepted.
- **Grab only the confirmed segment** past the Windows drag threshold (`GetSystemMetricsForDpi` SM_CXDRAG / SM_CYDRAG in DIP; 8 DIP for pen/touch). A press on another segment stays the native click.
  - **Mouse:** leaving the drag rectangle on either axis starts the drag (SM_CYDRAG semantics; review P-1).
  - **Pen / touch:** a vertical-first move abandons, so the page keeps its vertical pan.
- **Option B** (Cortex S0 B0–B4):
  - B0: past the threshold the item releases the pointer and the track's panel captures it, so the item never raises a Click for the drag;
  - B1: a refused capture abandons (no preview, no commit);
  - B2: the origin's own CaptureLost during the transfer is expected (the phase is marked before the release); the panel's CaptureLost while dragging is a real conclusion and cancels;
  - B3: the commit moves focus to the target as a pointer focus (no ring) when focus was in the group;
  - B4: the commit is `IsChecked = true` only, never a synthetic Click.
- **Pen / touch: S0b verdict V2.** The opted-in items carry `ManipulationMode = System, TranslateX`: one path for mouse, pen and touch, with no pointer-type gate.
- **Preview:**
  - the indicator follows the pointer 1:1, keeping the grab offset, clamped to the first / last segment (no rubber band);
  - its position and width interpolate between neighbouring segments (unequal widths arrive with the target's width);
  - the **predicted label** flips at the midpoint of the real segment centres with 4 DIP hysteresis, shown through the items' Checked / Unchecked visual states (mandatory in High Contrast) while `IsChecked`, the view model and UI Automation stay on the confirmed selection;
  - disabled segments are never a target.
- **Release:** at most ONE commit, computed from the release point; release outside the control commits the **clamped end segment**; release over the confirmed segment = settle with no write.
- **Cancel** (zero writes): Escape (consumed), arrow keys / Tab (cancel first, then their normal meaning), PointerCanceled, the panel losing the capture, **window deactivation** (S0 measured that it raises no CaptureLost; `MainWindow` publishes its activation per `AppWindow.Id` to `WindowActivationHub`), resize / DPI, a Reduced Motion change, an item being disabled, a real unload, an external selection.
- **Reduced Motion:** the drag stays 1:1 (direct manipulation); settle and cancel snap.
- **Planner:** `SelectionIndicatorPlanner` gains the `DragEnd` (settle) and `DragCancel` (return) causes, forced even onto the rect last planned (the preview displaced the visual outside the planner — the "stuck pill" bug Cortex R3 named).

### H03 — focus after a theme change

- **Bug (Beacon S0):** after a theme change focus landed on "Sistema", not on the chosen theme. Root cause: `MainWindow.OnActualThemeChanged` remounts four roots in one pass, and each remount captured and restored focus on its own; once the first root left the tree focus had fallen to the first focusable item, so the later restores put it there.
- **Fix:** `ThemeRemountFocus.RemountAll` captures focus once, before any root leaves the tree, and restores it once afterwards with the same focus kind (pointer stays ring-less, keyboard keeps its ring). Nothing focused, nothing restored.
- **Fix round 2 (Atlas A-2):** `ThemeRemountFocus` is one instance per window (`MainWindow`). The later restore never steals a newer choice: it runs only if focus is still where the remounts left it, nowhere, on the captured element, or on another item of the captured element's group (where the remount dropped it). A newer theme pass supersedes a pending restore and inherits its capture, so rapid theme changes keep the original choice.

## 3. S0 / S0b evidence (spike, throwaway branch)

**S0** (`8de3c21`, Beacon `beacon-s0-matrix.md`), Settings theme selector, real mouse via `SendInput` + a 7-gesture human pass:

| Case | Option A (observe) | Option B (capture transfer) |
|---|---|---|
| Panel receives Moved while dragging | PASS | PASS |
| Commit once on release over another segment | PASS | PASS |
| Release on the origin | **spurious Click** from the origin | PASS, no Click |
| Escape / window deactivation cancel | PASS | PASS |
| Outside the control / window | clamp; release received | clamp; release received |
| Pen [SYNTHETIC] | FAIL (contact claimed before the threshold) | FAIL |

- **Mouse: Option B chosen** (Cortex S0 review). Deactivation raises no CaptureLost → cancel from the window activation (D1).
- **S0b** (`d79962e`, `beacon-s0b-matrix.md`), pen/touch variants on top of B: **V2 shipped** (static `System, TranslateX`): synthetic pen + touch drag commit once, cancels zero, taps native, vertical scroll and wheel unchanged. V1 (drop `System` per press) acceptable but not chosen; V3 (`CancelDirectManipulations`) FAIL. The cause was isolated to the ScrollViewer (History, without a scrollable viewer, already passed in the control run).
- **Real pen and real touch: NOT_RUN** (human away; this PC has no touch digitizer).

## 4. Implementation map [CODE]

| Area | Files |
|---|---|
| Tokens | `Styles/Tokens/Motion.xaml`, `Services/Motion/MotionTokens.cs`, `Styles/Components/Sa.Forms.xaml` (toggle swap keyframes), QA token manifest / probe |
| Pure drag engine | `Services/Motion/SegmentDrag.cs` (`SegmentDragRules`, `SegmentDragController`, `ISegmentDragPort`, `WindowActivationHub`) |
| Planner | `Services/Motion/SelectionIndicatorPlanner.cs` (`DragEnd`, `DragCancel`, `Settle`, `DurationKey`) |
| Adapter decisions (WinUI-free) | `Controls/Primitives/SaSegmentDrag.cs`: `SegmentDragWiring` over `ISegmentDragSurface` |
| WinUI adapter (one call per member) | `Controls/Primitives/SaSegmentDrag.cs`: `SegmentDragAdapter` (routed events + `ISegmentDragSurface`); `Controls/Primitives/SaSlidingSelection.cs` (`AllowDrag`, `DurationKey`, preview presenter) |
| Focus | `Services/ThemeRemountFocus.cs` (`ThemeRemountFocus`, `RemountFocusPort` over `IFocusSurface`), `Controls/Primitives/XamlFocusSurface.cs`, `Controls/Primitives/SaThemeRefresh.cs`, `MainWindow.xaml.cs` |
| Hosts | `Views/SettingsPage.xaml`, `Views/HistoryPage.xaml`, `Views/WorkloadsPage.xaml`, `Controls/ServerFormControl.xaml` ×2; gallery `Qa/Gallery/QaFormsPage.xaml` (width-stable samples only) |

## 5. Deliberate differences and accepted behaviour

- **Off-scale durations** 300 / 200 / 100 (human decision).
- **P-4 (accepted):** a Reduced Motion change mid-drag cancels (Prism 1A had proposed snapping and keeping the drag).
- **P-5 (accepted):** a resize / relayout mid-drag cancels instead of preserving the drag position (e.g. a "Com problemas · 2→3" refresh ends the gesture).
- **Grab during a running slide** anchors to the confirmed segment's geometry, not the presented position.
- **Hover / pressed do not follow the drag** (the panel owns the pointer; the indicator and the predicted label are the only feedback).
- Platform limits A-7 and B11-4 from UI.11 still apply.

## 6. Principles (summary)

- The **text follows the pill, the logical selection does not**: the predicted label lights instantly; `IsChecked` / view model / UIA change once, on release.
- **No new cursor, no fling, no inertia, no overshoot.** The target depends on position only.
- **Behaviour Apple, physics Windows 11:** drag only from the selected segment, label under the pill, commit on release; Fluent curves, fixed token durations, Arrow cursor.

## 7. Figma changes (file `Qvk5dUFgsWf4UOYzfAkiDV`; Relay r1 / r2, `relay-ui11-1-figma.md`)

**r1, page 04 Manual:**
- M1 tokens: table `262:248` (Toggle `262:259-261` 167→200; Select `262:268-270` = sidebar + onboarding; new ToggleSwapTime `285:242-244`, Settle `285:245-247`, SegmentSelect `285:248-250`); note `262:280`; hierarchy `262:281` (new bars `285:251-254`, scale `285:255`); rule `262:296` ("nada > 300 ms").
- M3 drag-select pattern: row `285:256-261` + separator `285:262`; slide-select `263:258`; thumb-toggle `263:265`.
- M4 frames: row `286:242` / `286:243`; press `286:244`, dragging `286:251`, release→settle `286:258`, cancel `286:265`, Reduced Motion `286:274`; footnote `286:281`; retimed `263:331 / 339 / 343`, `263:349 / 357 / 361`; note `263:507`.
- M5 limits: `263:516`, `263:519`, `263:522`, `263:525`, `263:528` (real pen/touch NOT_RUN; drag-select not reproducible in Figma).
- M2: Decelerate usage `262:338` (text only).

**r1, page 05 Free (section `260:428`):**
- spec cards `261:430 / 433 / 436 / 439` (250→300), `261:442` (toggle 200 + swap 100), `261:445` (sidebar 250, no drag), `261:448`, `261:451`;
- new card **"Spec · Drag-select" `288:474`** (state diagram `288:477`);
- reactions 41 → **47**: 38 click @ 300, 3 toggle @ 200, 6 ON_DRAG @ 300 on the two-segment sets only (filter `260:578↔260:586 / 260:585↔260:579`, auth `260:604↔260:621 / 260:620↔260:605`), illustrative.

**r2, Prism Figma nits:** F-1 card body `288:476` (pen/touch vertical-first only; mouse starts on either axis — matches P-1), F-2 `288:476` + M3 `285:259` (release on the confirmed segment = settle without write), F-3 M4 subtitle `286:243`.

No new variants; resting look of the component sets unchanged. Page 06 Pro untouched. Prototype playback: NOT_RUN.

## 8. Human decisions

- **Taken (2026-10-11):** 300 / 200 / 100 / settle 200 / sidebar + dots 250 / hover 83; drag on the five selectors only; Option B with B1–B4; cancel via window activation; release outside = clamped end segment; H03 fixed in UI.11.1; pen/touch per S0b (V2).
- **Pending:**
  - close the Windows Firewall notification dialog and re-grant the QA window (§11);
  - remove the four Floor test-host Firewall rules (UAC, §13);
  - merge strategy for `ac36036` (§13);
  - real pen / real touch device test (backlog).

## 9. Guards (each with a counterproof)

Each counterproof breaks the rule in production code, fails the named tests, restores the SHA-256 byte-exactly; series end with a `--no-incremental` rebuild + full tests. Script and logs: `.boss/tmp/ui11-1/sentry-cp/`.

- **B (timings), CP-B1…B9:** exact durations + hierarchy (`MotionTokensTests.Ui111Durations_AreTheHumanDecision`), the per-host key rule and inventory (`Ui111DurationContractTests`), toggle swap key (`Ui11ToggleGuardTests`).
- **C1 (engine), C1-01…25:** `SegmentDragControllerTests` (fake port with synchronous re-entrancy: commit once, cancel never commits, transfer phase, refused capture, deactivation during transfer, stale notifications, one pointer at a time) and `SegmentDragRulesTests` (threshold, interpolation, measured midpoint 145 → second segment, hysteresis, disabled, planner drag-end causes, hub).
- **C2 (integration), C2-01…07:** `Ui111DragContractTests` (exactly five opted-in hosts, sidebar / styles / toggles never, V2 rule, default-false opt-in and window wiring as labelled source guards), hub value comparison.
- **C3 (focus), C3-01…05:** `ThemeRemountFocusTests` (fake window whose focus falls to "Sistema" on every remount).
- **Fix round 1, FR-01…05:** P-1 both directions; C-1 width-stability guard (`EveryAllowDragHost_HasWidthStableItems`).
- UI.11 wiring guards were **extended, not relaxed** (`T1_TheIndicatorPresenter_WiresThePlannerExactly` 6→7 requests incl. `EndPreview`; `B11_4` with the cancel-first line; `Ui5R2Tests` with the batched remount).
- **Fix round 2, FR2-01…12:** see §9a.
- **Limit:** the unit tests prove the controller, the adapter's decisions (`SegmentDragWiring`) and the focus port (`RemountFocusPort`). The one-call WinUI leaves stay runtime-only (§11): the routed-event registration, the surface members themselves (e.g. `SetItemManipulation`, `ReleaseItemCapture`, `GoToState`, `Focus`), and the window-id match.

## 9a. Fix round 2 (Atlas review, `0e0c10d`)

**Test gaps closed with behavioural seams** (no new source-text guards — UI11-TEXTGUARD-CLASS):
- **A-1 (High) / AT-03:** emptying the V2 `PrepareItem` or removing the origin's capture release survived every test. The adapter is now split:
  - `SegmentDragWiring` (no WinUI types) holds every decision: V2 on every item at attach, the PRESSED item releasing the PRESS's pointer before the panel captures it, snapshot, threshold from the real metrics, predicted labels, commit + focus, external selection, window subscription;
  - `ISegmentDragSurface`, implemented by `SegmentDragAdapter` with one WinUI call per member;
  - `SegmentDragWiringTests` drive the real wiring against a recording fake.
- **AT-05:** restoring the *current* focus instead of the captured element survived. The focus port is now `RemountFocusPort` over a one-call `IFocusSurface` (`XamlFocusSurface`); tests drive the real port.

**Real defects** (Atlas's deterministic probes are now permanent tests; restoring each defect makes its probe fail):
- **A-3 (Medium):** `Release → Follow → Present` could re-enter a conclusion (e.g. a layout cancel) that cleared the geometry; the release then threw a NullReferenceException. The release now re-checks the session after the frame's effect and stops with zero writes (`Release_APresentThatCancelsReentrantly_EndsWithoutCommit`, `Move_APresentThatCancelsReentrantly_LeavesTheGestureEnded`).
- **A-2 (Medium):** the Low-priority theme focus restore could steal a later user choice. Fixed as in §2 H03 (`AQueuedThemeRestore_DoesNotStealFocusFromALaterUserChoice` + its port variant, `ASecondPass_BeforeTheRestore_KeepsTheOriginalChoice_AndRestoresOnce`, `APassAfterACompletedRestore_CapturesAfresh`, `FocusFallingLateOntoTheSameGroup_IsStillRestored`).

**Counterproofs FR2-01…12** (byte-exact restores):

| CP | Mutation | Result |
|---|---|---|
| FR2-01 | Atlas AT-04 at its new location: V2 not applied | KILLED |
| FR2-02 | Attach without preparing the items | KILLED |
| FR2-03 | Atlas AT-03: the origin keeps its capture | KILLED |
| FR2-04 | A second press overwrites the pointer / origin | KILLED |
| FR2-05 | Atlas AT-05: restore the current focus | KILLED |
| FR2-06 | Commit focus ignores whether focus was in the group | KILLED |
| FR2-07 | The `_committing` flag cleared | **SURVIVED — equivalent mutant**: the session is already terminal when the commit's own Checked arrives; the flag is kept as defence in depth |
| FR2-08 | A-3: no session re-check after the release frame | KILLED (the probe fails) |
| FR2-09 | A-2: always restore | KILLED (both probes fail) |
| FR2-10 | A-2: no generation (a superseded restore runs) | KILLED |
| FR2-11 | A-2: a new pass recaptures instead of inheriting | KILLED |
| FR2-12 | A-2: no same-group fallback | KILLED |

**Honest limit:** the one-call WinUI surface leaves are runtime-only — e.g. emptying `SegmentDragAdapter.SetItemManipulation` or `ReleaseItemCapture` would still pass the unit tests. Their proof is the UI.11.1D real-input matrix (§11), still blocked.

## 10. Reviews

| Reviewer | Rounds | Final |
|---|---|---|
| Prism (direction, motion, semiotics, Figma) | 1A UX; S0 review; UI.11.1 r1 CHANGES_REQUESTED (P-1 blocking) → re-check after fix round 1; Figma review (F-1…F-3, applied by Relay r2) | **APPROVED_WITH_NITS** (P-2 / P-3 runtime checks for Beacon; P-4 / P-5 accepted; P-6 optional) |
| Cortex (architecture, lifecycle, platform) | 1A feasibility; S0 review (B0–B4, D1, P1 / P2, U1, X1); UI.11.1 r1 (C-1…C-6) → re-check after fix round 1; **structure re-check of fix round 2 pending** | **APPROVED_WITH_NITS** (before fix round 2): C-1 / C-3 / C-4 closed, P-1 approved; open: C-2 (runtime), **N-1** (the C-1 guard checks FontWeight only; optional: assert drag styles' Checked setters target only Foreground / FontWeight), **N-2** (`ac36036` does not compile alone → squash merge or fixup) |
| Atlas (tests, counterproofs) | 1A testability; S0 review; UI.11.1 final review on `8d9b6d1`: reproduced C1-01 / C1-09 / C1-15; findings A-1, AT-03, AT-05 (test gaps) and A-2, A-3 (real defects) → fixed in fix round 2 (§9a) | **Pending** — re-check of `0e0c10d` and final verdict; see `.boss/tmp/ui11-1/atlas-ui11-1-review.md` |
| Beacon (runtime) | S0, S0b, UI.11.1D @ `8d9b6d1` | **PARTIAL** (§11) |

The implementer (Sentry) did not approve its own work.

## 11. UI.11.1D runtime QA (Beacon, `8d9b6d1`, fixed worktree, bin hash guard on every launch and capture)

**Verdict: PARTIAL.** Everything measurable without real input PASSES; every real pointer and keyboard case is NOT_RUN.

| Area | Result |
|---|---|
| Timings vs base `c69d0a0` (UIA Select / Toggle) | **PASS**: the five selectors +≈20–50 ms (250→300), sidebar unchanged, toggles +≈16–31 ms (167→200); monotonic, one pill, final on target ±3 px, UIA logic == target |
| Reduced Motion (`--qa-reduced-motion`) | **PASS**: 0 intermediate frames, final on target |
| Widths / languages (pt-PT, pt-BR, en-US) | **PASS**: theme fixed 88; filter unequal in en-US; slides end on target |
| Resting look vs `c69d0a0` | **PASS**: 0 px differences except a 2 px anti-aliasing delta in the editor (Light) |
| Performance / idle | **PASS**: idle 0.00 %; no extra memory growth |
| Guards | real data 0 differences; QA added no Firewall rules; 0 processes left |
| Toggle swap 100 ms, settle, cancel | not isolated / NOT_RUN (need a drag) |
| H03 focus after a theme change | **INCONCLUSIVE**: the system focus belonged to the blocking dialog |
| Pointer and keyboard matrix (all) | **NOT_RUN — blocked** |

**The block:** a Windows Security notification dialog (`PickerHost`, `FirewallNotificationDialogServer`, the Firewall prompt raised by the Floor's test host) held the foreground. A probe showed that no real input reached the QA window: real click, synthetic touch and synthetic pen all left the selection unchanged. **Hypothesis (unverified): UIPI** — `SendInput` is subject to UIPI and fails silently, and the dialog likely runs at a higher integrity level. Per the rules, nobody answered or closed the dialog.

**Consequence: the WinUI drag adapter (`SegmentDragAdapter`) has NO runtime proof.** Pending once the human closes the dialog (scripts ready): click / slow / fast drag, release on origin / elsewhere / outside, Escape, deactivation (Cortex C-2: window-id match), resize, the five hosts with ≥ 2 predicted-label changes (C-1), focus after drag, Prism P-2 / P-3, the P-1 diagonal grab, synthetic pen / touch on the theme (V2) and History, keyboard from the restored focus, sidebar / toggles not draggable with a pointer, H03. The re-run must target the final head (`8d9b6d1` predates fix round 1).

## 12. NOT_RUN (with reason)

- **Entire real pointer / keyboard matrix:** blocked by the Firewall notification dialog (§11).
- **Real pen, real touch:** human away; no touch digitizer on this PC. Synthetic evidence only (S0b) — never reported as PASS.
- **H03 runtime:** inconclusive for the same block.
- **Toggle swap 100 ms in isolation:** the discrete swap falls inside the toggle window; the method does not isolate it.
- **Settle 200 Direct / cancel PtP timing:** only exist after a drag.
- **Figma prototype playback:** no presentation player.
- **High Contrast, DPI ≠ 100 %, Narrator:** would change the session for every app and agent.

## 13. Backlog / exceptions (not in UI.11.1)

- **UI11.1-TOUCH-REAL:** drag with a real pen and on a real touch device (V2), including the vertical-scroll anti-regression.
- **U2 (Prism S0, Low):** a ~24 ms residual overlay on the origin after a theme commit (frame f048); re-check on a selector without remount; reset the origin's visual state only if seen.
- **N-1 (Cortex, nit):** widen the C-1 width-stability guard to every Checked setter (Padding / Margin / FontSize), not only FontWeight.
- **N-2 / `ac36036`:** the intermediate commit `ac36036` does not compile on its own (the V2 constant moved to the adapter only in `54e7e05`); the head compiles and is fully gated. Decision at merge: **squash merge**, or a fixup before a merge commit. No force-push was made.
- **Prism P-6 (optional):** an invalid `DurationKey` fails at animation time rather than at load.
- **Firewall exception:** four Inbound Block rules for the Floor's `ServerMonitor.Infrastructure.Tests` test host (`…servermonitor--uiui11-1-motion-refinement\tests\servermonitor.infrastructure.tests\bin\{debug,release}\…\testhost.exe`, Debug + Release), created by the Floor's test runs; removal pending the human (UAC). The app itself added none.
- Carried from UI.11: UI11-TEXTGUARD-CLASS; FLAKE-INFRA-LOOPBACK-CHILD.

## 14. Commits (`c69d0a0..0e0c10d`, code)

| Commit | Slice |
|---|---|
| `d0cc3b8` | B — H01 timings: segmented 300, toggle 200 + swap 100, settle token, per-host `DurationKey` |
| `a5023ee` | C1 — pure drag engine: `SegmentDragController` + rules + port, planner `DragEnd` / `DragCancel` / `Settle`, `WindowActivationHub` |
| `f832f0d` | C3 — H03: one focus capture / restore per theme pass |
| `8d9b6d1` | C2 — drag-to-select on the five selectors: `AllowDrag`, adapter, V2, activation publish |
| `ac36036` | Fix round 1 — review P-1 (mouse starts the drag on either axis); C-3 / C-4 (**does not compile alone**, §13) |
| `54e7e05` | Fix round 1 — review C-1 (width-stable AllowDrag hosts + guard), C-3 adapter constant |
| `f0686cd` | Docs — this record (first version) |
| `0e0c10d` | Fix round 2 — Atlas A-1 / AT-03 / AT-05 (behavioural seams `SegmentDragWiring` / `ISegmentDragSurface`, `RemountFocusPort` / `IFocusSurface`) and real defects A-2 / A-3; one commit on purpose (every state compiles) |

Evidence: `.boss/tmp/ui11-1/` — `sentry-implementation-report.md`, `sentry-cp/`, `beacon-s0-matrix.md`, `beacon-s0b-matrix.md`, `beacon-ui11-1d-qa.md`, `cortex-ui11-1-review.md`, `prism-ui11-1-review.md`, `prism-ui11-1-figma-review.md`, `atlas-ui11-1-review.md`, `relay-ui11-1-figma.md`.
