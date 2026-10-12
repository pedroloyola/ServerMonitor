# UI.11.1 — Motion Refinement

**Status: READY_TO_MERGE — pending the human GO.** PR #40 (`ui/ui11-1-motion-refinement`); code at `280c300`. Merge method approved: **squash merge** (§13, N-2).
- Real-input runtime QA PASS on the five selectors (Beacon final, §11b); B111-1 found and fixed (§11a); focused re-QA on `42ee796` PASS (§11c).
- Reviews: Prism APPROVED_WITH_NITS, Cortex APPROVED_WITH_NITS, Atlas APPROVED, all on `280c300` (§10).
- No merge without the human GO.

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
- **Implementation (Sentry):** slices B, C1, C3, C2, fix rounds 1–3, then the B111-1 fix (§14).
- **Figma (Relay r1 / r2):** pages 04 and 05 (§7).
- **Reviews:** Prism, Cortex, Atlas (§10).
- **UI.11.1D runtime QA (Beacon):** first pass @ `8d9b6d1` PARTIAL (environment block, §11); final real-input QA @ `fac20d4` PASS except B111-1 (§11b); re-QA @ `42ee796` PASS (§11c).

**Evidence labels:** [CODE] · [FIGMA] live node · [MEASURED] runtime · [SYNTHETIC] injected pen/touch · [INFERENCE].

`.boss/**` paths are evidence. No personal data, real hosts or screenshots are reproduced here; the QA harnesses use fixture data.

## 1. Outcome

- **H01 timings: implemented and measured** in runtime (UIA-driven, §11).
- **H02 drag-to-select on the five segmented selectors: implemented, unit-proven and runtime-proven with real input.** The pure controller and the adapter's decisions (`SegmentDragWiring`) are driven through their handlers against re-entrant fakes (§9); the WinUI adapter passed Beacon's real `SendInput` matrix on all five hosts and the human's manual mouse + keyboard pass (§11b).
- **H03 focus after a theme change: implemented, unit-proven and runtime-proven** (keyboard / focus 7/7 with real input, §11b; 7/7 again on `42ee796`, §11c).
- **Fix round 2 (Atlas, §9a):** two real defects found by Atlas's deterministic probes are fixed — A-3 (a re-entrant cancel during the release frame threw a NullReferenceException) and A-2 (the delayed theme focus restore could steal a later user choice) — and three test gaps (A-1, AT-03, AT-05) are closed with behavioural seams.
- **B111-1 (High, found by Beacon's final QA):** a window width change during a drag on the editor selectors crashed the app; fixed in `280c300` (§11a) and re-verified with real input (§11c).
- **Reviews** (§10): Prism APPROVED_WITH_NITS, Cortex APPROVED_WITH_NITS, Atlas APPROVED — all on `280c300`.
- **Gates @ `280c300`:**
  - `.slnx` Debug `--disable-build-servers`: 0 errors, App 3744 (Infrastructure.Tests not run locally in this round: its Firewall prompts had blocked QA);
  - Release: 0 errors, App 3262;
  - PR CI green on `42ee796` (Debug build + tests; Release build + vulnerability scan).
- **Real data:** identical before and after every QA window (S0, S0b, UI.11.1D, final QA, B111-1 repro, re-QA). **Firewall:** the app added no rules; the four test-host rules added by the Floor's test runs were removed by the human (UAC), 713 → 709 (§13).

**Before merging:** the human GO only. Accepted as NOT_RUN: real pen, real touch, High Contrast, an isolated modal-capture-loss case (§12).

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
- **Fix rounds 2 and 3 (Atlas A-2):** `ThemeRemountFocus` is one instance per window (`MainWindow`). The later restore never overrides a newer choice. **One rule, by identity:** focus is ours to move only while it sits exactly on the element OUR remount left it on (recorded per pass), nowhere, or on the captured element; any other element — another item of the same group included — is the user's. A newer theme pass supersedes a pending restore; it inherits the pending capture only under the same rule (focus still on the previous pass's exact fallback, or nowhere), otherwise the user's new choice is the new capture. (Round 2's "another item of the same group" rule could not tell the fallback from a keyboard choice and was removed in round 3.)
- **Runtime caveat (by design):** if the platform moved focus *asynchronously* after the pass onto some element other than the recorded fallback, the restore does not run (it cannot be told from a user choice). Beacon's real-input H03 checks (7/7 on `fac20d4` and on `42ee796`, §11b / §11c) did not hit it.

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
- **P-5 (accepted; clarified in Prism addendum 3):** the drag cancels **if and only if the track's geometry changes** (a `SizeChanged` of the host or of an item, or a DPI change), instead of preserving the drag position (e.g. a "Com problemas · 2→3" refresh ends the gesture). A window resize that leaves the track unchanged is not a drag event: on the fixed-width hosts (theme, History, filter) the drag continues and commits once on release; on the editor's stretching selectors it cancels with zero commits (measured, §11c).
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
- **Taken (2026-10-12):** squash merge (N-2 / `ac36036`); the four Floor test-host Firewall rules removed (UAC); manual mouse + keyboard pass "tudo ok".
- **Pending:** the merge GO. Real pen / real touch stay in the backlog (§13).

## 9. Guards (each with a counterproof)

Each counterproof breaks the rule in production code, fails the named tests, restores the SHA-256 byte-exactly; series end with a `--no-incremental` rebuild + full tests. Script and logs: `.boss/tmp/ui11-1/sentry-cp/`.

- **B (timings), CP-B1…B9:** exact durations + hierarchy (`MotionTokensTests.Ui111Durations_AreTheHumanDecision`), the per-host key rule and inventory (`Ui111DurationContractTests`), toggle swap key (`Ui11ToggleGuardTests`).
- **C1 (engine), C1-01…25:** `SegmentDragControllerTests` (fake port with synchronous re-entrancy: commit once, cancel never commits, transfer phase, refused capture, deactivation during transfer, stale notifications, one pointer at a time) and `SegmentDragRulesTests` (threshold, interpolation, measured midpoint 145 → second segment, hysteresis, disabled, planner drag-end causes, hub).
- **C2 (integration), C2-01…07:** `Ui111DragContractTests` (exactly five opted-in hosts, sidebar / styles / toggles never, V2 rule, default-false opt-in and window wiring as labelled source guards), hub value comparison.
- **C3 (focus), C3-01…05:** `ThemeRemountFocusTests` (fake window whose focus falls to "Sistema" on every remount).
- **Fix round 1, FR-01…05:** P-1 both directions; C-1 width-stability guard (`EveryAllowDragHost_HasWidthStableItems`).
- UI.11 wiring guards were **extended, not relaxed** (`T1_TheIndicatorPresenter_WiresThePlannerExactly` 6→7 requests incl. `EndPreview`; `B11_4` with the cancel-first line; `Ui5R2Tests` with the batched remount).
- **Fix round 2, FR2-01…12:** see §9a.
- **B111-1:** `SegmentDragWiringTests.ALayoutCancel_EndsTheDragNow_ButReleasesThePanelCaptureOutsideTheLayoutPass` and `ALayoutCancelsPostedRelease_LeavesANewDragsCapture_AndOtherCancelsStaySynchronous`; the no-deferral mutant is KILLED (Sentry, and independently Atlas, whose two own mutations are detected too).
- **Limit:** the unit tests prove the controller, the adapter's decisions (`SegmentDragWiring`) and the focus port (`RemountFocusPort`). The one-call WinUI leaves (the routed-event registration, the surface members themselves, e.g. `SetItemManipulation`, `ReleaseItemCapture`, `GoToState`, `Focus`, and the window-id match) are proven by the real-input runtime QA (§11b, §11c), not by unit tests.

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

**Round 3 (`6f9081d`, Atlas residual A-2 — the second fix in this class, BOSS §16):** two Atlas probes failed on intact `0e0c10d` through the real `RemountFocusPort`: a keyboard choice of "Claro" (same group) after the remount dropped focus on "Sistema" was overridden back to "Escuro", with or without a second theme pass. Fixed by the identity rule (§2 H03); the same-group rule and `SharesGroup` are gone from every interface. Permanent tests: `ANewChoiceInTheSameGroup_BeforeTheRestore_Wins` (both Atlas variants), `FocusArrivingLaterOnAnotherElement_IsNeverOverridden`, `ASecondPass_WithAnotherFallback_StillRestoresTheOriginalChoice`, `ANewChoice_ThenAPassWithoutRemount_KeepsTheChoice`, `FocusLostEntirely_IsRestored`, `FocusLostAfterTheFallback_IsRestored`. Counterproofs FR3-01…04 (any element is ours; unconditional inheritance; inherited instead of per-pass fallback; "nowhere" not ours) all KILLED — FR3-04 first SURVIVED, which exposed a missing test (focus lost after a non-null fallback); the test was added and FR3-04 re-run KILLED.

**Honest limit:** the one-call WinUI surface leaves are runtime-only — e.g. emptying `SegmentDragAdapter.SetItemManipulation` or `ReleaseItemCapture` would still pass the unit tests. Their proof is the real-input matrix (§11b / §11c), which passed.

## 10. Reviews

| Reviewer | Rounds | Final |
|---|---|---|
| Prism (direction, motion, semiotics, Figma) | 1A UX; S0 review; UI.11.1 r1 CHANGES_REQUESTED (P-1 blocking) → re-check after fix round 1; Figma review (F-1…F-3, applied by Relay r2); addendum 2 (fix rounds 2–3); addendum 3 (B111-1) | **APPROVED_WITH_NITS on `280c300`.** Figma F-1 / F-2 CLOSED by a live read of the nodes (the M3 `285:259` part of F-2 rests on Relay's read-back). P-4 / P-5 accepted (P-5 clarified: cancel iff the track geometry changes); P-6 optional; P-2 / P-3 passed in runtime |
| Cortex (architecture, lifecycle, platform) | 1A feasibility; S0 review (B0–B4, D1, P1 / P2, U1, X1); UI.11.1 r1 (C-1…C-6); addenda 2–4 (fix rounds 2–3, B111-1) | **APPROVED_WITH_NITS on `280c300`.** C-1 / C-3 / C-4 closed; C-2 and R-2 (runtime conditions) passed (§11b, §11c); nits N-1, N-2 (squash, decided), **N-3**, **N-4**, **W-1** (§13) |
| Atlas (tests, counterproofs) | 1A testability; S0 review; final review on `8d9b6d1` (A-1, AT-03, AT-05, A-2, A-3) → fix rounds 2–3; B111-1 addendum | **APPROVED on `280c300`** (tests / reliability scope: the B111-1 mutant and two own mutations detected; byte-exact restores) |
| Beacon (runtime) | S0, S0b, UI.11.1D @ `8d9b6d1` (PARTIAL), final real-input QA @ `fac20d4`, re-QA @ `42ee796` | **PASS on `42ee796`** (§11b, §11c) |

The implementer (Sentry) did not approve its own work.

## 11. UI.11.1D runtime QA, first pass (Beacon, `8d9b6d1`, fixed worktree, bin hash guard on every launch and capture)

**Verdict: PARTIAL (superseded by §11b / §11c).** Everything measurable without real input PASSED; every real pointer and keyboard case was blocked by the environment.

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

**Consequence at the time:** the WinUI drag adapter had no runtime proof. The real-input matrix ran later on the final head (§11b).

## 11a. B111-1 — crash on a window width change mid-drag (Beacon final QA, `fac20d4`; fixed in `280c300`)

- **Defect (High):** an active drag on the editor's Auth / jump-host Auth selector + a window width change (`SetWindowPos`, Win+← snap) killed the app: `0xc000027b`, `E_UNEXPECTED` in `Microsoft.UI.Xaml.dll` +0x3a9c5d. Regression of PR #40 (base `c69d0a0`: no crash).
- **Root cause (reproduced under a call trace, Sentry):** only the editor's items stretch with the window, so only they raise `SizeChanged` mid-drag. That handler runs inside the resize's layout pass and cancels the drag (`DragCancelReason.Layout`); the cancel called `panel.ReleasePointerCapture` there, and that call never returned (native fail-fast, no managed exception).
- **Fix:** a layout cancel still ends the drag synchronously (no preview write after it; `EndPreview` unchanged), but the panel's capture release is posted to the dispatcher, out of the layout pass; it is skipped if a new drag owns the panel by then. Other cancels are unchanged.
- **Proof:** `SegmentDragWiringTests` (2 new; the no-deferral mutant KILLED by Sentry and Atlas). Runtime (Sentry, instrumented Floor build, real `SendInput`): auth +120 / +10 px, jump-host +120 px, Win+← snap: no crash, 0 commits; the posted release returns. Independent re-QA on the real `42ee796` binary: §11c.

## 11b. Final real-input QA (Beacon, `fac20d4`, `beacon-ui11-1d-final.md`)

Real `SendInput` (mouse + keyboard) only into the verified QA window; commits counted by UIA `IsSelected` events + logical state; resting look diffed against UIA references.

| Area | Result |
|---|---|
| Mouse matrix × 5 hosts (theme, History, Services filter, editor auth, jump-host auth): click under the threshold, click on the selected, horizontal drag = 1 commit with nothing changing before release, diagonal grab (P-1), drag back to the origin = 0 + settle, release outside = clamped end, Escape, window deactivation (C-2), 6 rapid clicks, slow drag across ≥ 2 label changes (C-1), fast drag | **PASS**, except resize / snap mid-drag on the two editor hosts = **B111-1** (§11a) |
| Keyboard / focus (H03, Cortex R-1) | **7/7 PASS** (focus == selected after click, arrow + Space and drag; Tab order after the remount; no focus steal at 20 / 120 ms) |
| Dark / Light × pt-PT / pt-BR / en-US (5 selectors × 2 themes × 3 languages, one real drag each) | **30/30 PASS** |
| Reduced Motion | **PASS** (1:1 during the drag; snap after release) |
| Prism P-2 / P-3 (filmstrips) | **PASS**: continuous pill, width interpolates, exactly one lit label, no stuck PointerOver, never two pills |
| Human manual pass (real mouse, real keyboard) | **PASS** ("tudo ok") |
| Real pen | **NOT_RUN** (the human could not test it) |

## 11c. B111-1 focused re-QA (Beacon, `42ee796` = code `280c300`, `beacon-ui11-1d-recheck.md`)

- **Verdict: PASS — B111-1 fixed.** 0 crashes in the whole re-test (6/6 on `fac20d4`).
- **Editor hosts (auth, jump-host auth):** resize +120 / +10 px and Win+← mid-drag → no crash, **0 commits**, the drag cancels and the pill settles on the origin (rest 0 px); the next drag commits once.
- **Fixed-width hosts (theme, History, filter):** a resize leaves the track unchanged, so the drag **continues** and commits once on release with a clean rest (the §5 P-5 rule); Win+← cancels (0 commits). No crash; the next drag works.
- Smoke on the two editor hosts + theme: PASS. H03 keyboard / focus: 7/7 PASS. Guards: real data identical; Firewall set unchanged during the run; 0 processes left.

## 12. NOT_RUN (with reason)

- **Real pen:** the human could not test it in the manual pass. Synthetic evidence only (S0b) — never reported as PASS.
- **Real touch:** no touch digitizer on this PC.
- **High Contrast, DPI ≠ 100 %, Narrator:** would change the session for every app and agent.
- **Focus / capture lost to a modal mid-drag, as an isolated case:** a modal cannot be opened mid-drag without human input; covered by window deactivation (C-2) and the snap.
- **Language grid and Reduced Motion on `42ee796`:** not repeated (the fix only touches the layout-cancel path); PASS on `fac20d4` (§11b).
- **Toggle swap 100 ms in isolation:** the discrete swap falls inside the toggle window; the method does not isolate it.
- **Figma prototype playback:** no presentation player.

## 13. Backlog / exceptions (not in UI.11.1)

- **UI11.1-TOUCH-REAL:** drag with a real pen and on a real touch device (V2), including the vertical-scroll anti-regression.
- **U2 (Prism S0, Low):** a ~24 ms residual overlay on the origin after a theme commit (frame f048); re-check on a selector without remount; reset the origin's visual state only if seen.
- **N-1 (Cortex, nit):** widen the C-1 width-stability guard to every Checked setter (Padding / Margin / FontSize), not only FontWeight.
- **N-2 / `ac36036`:** the intermediate commit `ac36036` does not compile on its own (the V2 constant moved to the adapter only in `54e7e05`); the head compiles and is fully gated. **Decided: squash merge.** No force-push was made.
- **N-3 (Cortex, nit, optional):** `SegmentDragWiring.Cancel` resets `_inLayout = false` in `finally`; restoring the previous value would survive a future nested `Cancel`.
- **N-4 (Cortex, info):** the result of `TryEnqueue` in `SegmentDragAdapter.Post` is ignored; if it fails (dispatcher shutting down) the capture lasts until the pointer is released. Bounded; accepted.
- **W-1 (Cortex, watch):** the deferral keys on the cancel *reason* (`Layout`), not on the real layout context. If an `Unloaded` / `Disabled` / `CaptureLost` cancel ever arrived from inside a layout pass with a drag active (e.g. an adaptive visual state hiding the selector during a resize), the release would still be synchronous. Not observed; if the same signature appears, defer every release of the `Cancel` path.
- **Prism P-6 (optional):** an invalid `DurationKey` fails at animation time rather than at load.
- **Firewall exception — CLOSED:** the four Inbound Block rules for the Floor's `ServerMonitor.Infrastructure.Tests` test host (Debug + Release), created by the Floor's test runs, were removed by the human with UAC: 713 → 709 rules, none left for the Floor's test host. The app itself added none.
- Carried from UI.11: UI11-TEXTGUARD-CLASS; FLAKE-INFRA-LOOPBACK-CHILD.

## 14. Commits (`c69d0a0..280c300`, code; docs commits listed for the record)

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
| `2206eb5` | Docs — this record, fix round 2 |
| `6f9081d` | Fix round 3 — Atlas residual A-2: restore only onto our own fallback, by identity; inheritance under the same rule |
| `280c300` | B111-1 — a layout cancel releases the panel capture outside the layout pass (crash on a width change mid-drag) |
| `42ee796` | Docs — this record, B111-1 |

Evidence: `.boss/tmp/ui11-1/` — `sentry-implementation-report.md`, `sentry-cp/`, `beacon-s0-matrix.md`, `beacon-s0b-matrix.md`, `beacon-ui11-1d-qa.md`, `cortex-ui11-1-review.md`, `prism-ui11-1-review.md`, `prism-ui11-1-figma-review.md`, `atlas-ui11-1-review.md`, `relay-ui11-1-figma.md`, `beacon-ui11-1d-final.md`, `beacon-ui11-1d-recheck.md`, `sentry-b111-1/`, `atlas-cp-b111/`.
