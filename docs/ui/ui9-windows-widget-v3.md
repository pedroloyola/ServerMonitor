# UI.9 — Windows Widget V3

**Status: UI.9 COMPLETE (2026-10-08).** PR #32 merged by merge commit pinned to `df15963` → `main` = `c266b38` (parents `9a81dc1` + `df15963`; tree == `df15963`). Post-merge CI run 37850235873: attempt 1 failed only in the pre-existing, non-deterministic `Ui7SaveGuardTests.M1` (Debug; independent of UI.9); a single rerun as a measurement passed both jobs (attempt 2). See §15.
- **Branch:** `ui/ui9-windows-widget-v3`, base `9a81dc1`.
  - Code validated at `dec83f3`: the installed V3 build `1.1.2.2` was built from it, with clean provenance (`.boss/tmp/ui9/beacon-v3build.md`).
  - This document's commit adds docs only.
- **Scope:** the Windows Widgets board card of ServerAlyzer, rebuilt to the live Figma V3 (page 05 · section 16, S/M/L × Healthy/Attention/Stale/No servers × Dark/Light) inside what the Widgets platform actually renders.
  - **Not touched:**
    - MonitoringEngine, SSH, credentials, trust;
    - the provider COM lifetime;
    - CLSID / AppExtension Id / Definition Id / protocol / snapshot path (all frozen);
    - `schemaVersion` (still 1);
    - single-instance, startup order, tray, notifications, StartupTask.
- **Execution:**
  - UI.9A feasibility gate → SPEC (contract-first) + amendments;
  - B1 (contract/mapper/recorder) → B2 (provider/view-model states);
  - C0 board probe (packaged) → C1 (constant templates) → C2 (picker, l10n) → C3 (board adjustments);
  - UI.9D packaged real QA of the V3 build.
- **Evidence labels** (as in the sources): [DOC] Microsoft docs · [MEASURED] this machine · [LEGACY] code before UI.9 · [INFERENCE].
- `.boss/**` paths are cited as evidence. No personal data, host names, server names or board screenshots are reproduced here.

## 1. Outcome

- The widget renders the V3 design on the real native board (Windows build 26300, WebExperience 526.21100.40.0), in dark and light, at S/M/L.
- Prism's verdict: **PASS visual with documented differences D-1…D-18** (`.boss/tmp/ui9/prism-v3-board-verdict.md`).
- Freshness is honest end to end. The board showed both stale→fresh and fresh→stale live, from the app writing and from the app exiting (`.boss/evidence/ui9/ui9d/session-ui9d-v3-20261008a/v3-results.md`).
- Activation keeps its target:
  - card → Standard window on Overview;
  - row → that server's Detail;
  - from Compact → Standard in front, same process.
- Packaged QA was data-safe in every phase: two-channel drift capture, all human sign-offs PASS (§11).

## 2. UI.9A feasibility verdict and platform limits

**Verdict: FEASIBLE WITH DOCUMENTED PLATFORM DIFFERENCES.** No STOP condition was hit by the design itself (`.boss/tmp/ui9/UI9A-FEASIBILITY.md`). Sources: `relay-platform-research.md`, `cortex-inventory.md`, `prism-figma-inventory.md`, `beacon-packaging-feasibility.md`.

| Limit | Label | Consequence for V3 |
|---|---|---|
| The host renderers stop at Adaptive Cards **1.6 classic**: no ProgressBar/Ring, Icon, Badge, Charts, `targetWidth`, Layouts, `roundedCorners` | [MEASURED] strings in both host renderers | bars built from weighted `ColumnSet`s, square ends; no ring |
| No scroll, no L2/pivots, fixed height; overflow is clipped **silently** | [DOC] + [LEGACY] P-017 + [MEASURED] probe P-cap/P-type | row budget per size measured on the board; footer "N of M" as the control signal |
| Touch targets ≤ 1 (S) / 3 (M) / 4 (L); only `Action.Execute` (OpenUrl prompts on every click) | [DOC] + [LEGACY] M13 QA-10 | whole card + up to 3 rows |
| 16 px margin + 48 px host attribution area | [DOC] | Figma's own strip = the host header (no `header` key) |
| No theme callback; `$host.hostTheme` in templates | [DOC]; board re-renders by itself on theme change [MEASURED P-D] | theme variants chosen by `$when` |
| `UpdateWidget` throttling, card px sizes, host termination: undocumented | [DOC gap] | measured where needed; updates only while widgets are active |
| Customization (`IsCustomizable`) has a known "…" menu bug | [DOC] | no per-widget selection (D-UI9-7) |
| Analytics/ErrorInfo need build 26900+; web widgets are EEA-only | [DOC] | not used |
| Images: `ms-appx:///Public/…` does **not** render; compile-time `data:` PNG/SVG URIs do; `backgroundImage` 1×1 PNG `repeat` paints solid bars | [MEASURED] probe P-img | the image rule (§10) |
| `TextBlock` applies markdown (links/bold/lists); `TextRun` keeps it literal, but the native date/time pre-processor rewrites `{{DATE(…)}}` in both | [MEASURED] probe P-text A/B/C | untrusted text only as `TextRun` + `{{` neutralisation (§10) |
| Native Container `style` fills are pastel with dark text (the JS proxy paints nothing) | [MEASURED] probe P-style | not used for row cards (Prism C0 §6) |

## 3. Architecture: before → after (invariants preserved)

**Before** (`9a81dc1`, `.boss/tmp/ui9/cortex-inventory.md`) — sound runtime, legacy presentation:
- dedicated `WinExe` provider referencing only `WidgetContract` + `ActivationContract`;
- COM lifetime protocol, with rehydrate before registering the factory;
- atomic snapshot writer with last-known-good recovery;
- untrusted reader;
- directory watcher with 500 ms debounce and 60 s backstop, armed only while a widget is on screen;
- deep links via the strict `serveralyzer://` grammar.

Gaps:
- stale was computed but never rendered (`vm.Freshness` unused);
- fixed 90 s threshold;
- the recorder wrote only on cycle completions, so deleting the last server left its name in the snapshot and Empty was unreachable;
- `RefreshAll` repainted deactivated widgets;
- the renderer sent a full card with `DataJson = "{}"`.

**After:**
- **App (producer).**
  - `WidgetSnapshotMapper` fills two additive fields (§4).
  - `WidgetSnapshotRecorder` also writes on `IServerService.ServersChanged` and once at startup. These forced writes bypass the 15 s cycle throttle but go through the same single-writer drain under `_gate`: ≤ 1 in flight + ≤ 1 pending. They do not anchor the cycle throttle (DV-5). No new timer. The handler unsubscribes before cancel.
  - An empty fleet is written only when the load status is `Loaded`/`NotFound`, or after a fleet mutation (V-RC-1 + DV-1 + V-B1).
- **Provider (reader/renderer).**
  - Freshness is derived per server (§5).
  - `WidgetViewModelBuilder` produces the V3 card and row states (§6).
  - `WidgetCardRenderer` emits **one constant template per size** plus a data JSON with fixed keys, using `${…}` bindings and `$when` flags. The template is byte-identical across all states and hostile snapshots (tested).
  - `WidgetLayout` is the single place for row caps, bar height/spacing/weight and the meter style.
  - `WidgetImages` is the single source of image constants.
  - Dead code removed with zero-use proof (SPEC §9): `ComServerProcess.EverReferenced/IsExiting`, `WidgetServerRow.MetricsText`, `WidgetViewModel.PrimarySummary/CountsSummary`. Their tested semantics moved to the V3 fields first.
- **Invariants kept and now tested:**
  - provider never references Core/Infrastructure/Collectors/App/Features/SSH.NET (`ProviderBoundaryTests`: assembly closure + TypeRefs/TypeDefs + csproj);
  - provider never reads the wall clock (`SystemClockGuardTests` extended);
  - health is never recomputed outside the engine;
  - Unknown/Offline never read 0 %;
  - state is never colour-only;
  - only allowlisted verbs.

## 4. Contract changes (additive v1, `schemaVersion` stays 1)

| Field | Wire | Producer | Reader rule |
|---|---|---|---|
| `attentionMetric` | `"cpu"\|"memory"\|"disk"\|null` (string, not an enum, so an unknown value cannot fail deserialisation) | the existing `PriorityProblemSelector` over the single server with the engine's `MonitoringThresholds`; only the metric is copied, never a name | ordinal allowlist; unknown → `null`, never invalid; ignored unless Warning/Critical |
| `staleAfterSeconds` | `int?`, bounds `[20, 600]` as `WidgetSchema` constants | `StalePolicy.StaleAfter(RefreshIntervalPolicy.ToInterval(s))` (no policy duplicated) | out of bounds → `MetricOutOfRange` |

- The validator also rejects `lastUpdatedUtc > generatedAtUtc + 1 s` (V-RC-5b).
- An App test proves that every supported interval maps inside the bounds; the counterproof (adding 3600 s) turns it red (V-RC-4).
- **Compatibility proof:**
  - checked-in golden fixtures `widget-state-v1-{old,new}.json`;
  - a frozen copy of the 1.1.1 reader record with its own source-generated context, guarded to have no new members;
  - both directions deserialise valid;
  - the serializer is byte-deterministic.

  App and provider ship in the same MSIX, so a mixed state is only transitional (`.boss/tmp/ui9/SPEC.md` §1, `sentry-b1b2-report.md`).
- **Not added:** CPU core count (D-UI9-1). It is not collected anywhere, so the Large detail line shows uptime instead.

## 5. Provider lifetime and refresh

- **Unchanged:** the COM out-of-proc lifetime, rehydrate, idle exit, watcher/debounce/backstop and `RunGuarded`.
- **Changed:**
  - **Derived thresholds (D-UI9-4).** The snapshot threshold is `max(90 s, max_i staleAfterSeconds_i)` (90 s for old files or an empty fleet).
  - **Per-server freshness.** Each server uses its own threshold. `null` is never fresh, and a server is never fresher than a stale snapshot. All ages are measured on the injected `TimeProvider`.
    - Accepted cost: with a fleet at 300 s, detecting "app not writing" takes up to 600 s.
  - **Repaint scope.** `RefreshAll` repaints only on-screen widgets.
  - **Flakes.** FLAKE-WP-GUID and CI-WP-BOUNDED-CLOCK were fixed (fixed Guids; the drain timer is proven on the injected clock).
- **Measured on the board** (`v3-results.md` V-1, Beacon sampler):
  - 12 snapshot writes at 31.3–31.6 s intervals;
  - 24 distinct board renders in ~3.5 min with the board open;
  - at most 1 app and 1 provider process, 0 consoles;
  - the provider stayed alive through the session.
- "Stopped" is shown honestly as Stale. The provider never claims "app stopped", and no autostart was added (product decision; StartupTask stays out of scope).

## 6. States and server selection

| Card state | Condition | Shown |
|---|---|---|
| Healthy | all Healthy **and** fresh | "All healthy"; rows "Healthy" (text + colour) |
| Attention | ≥ 1 Warning/Critical/Offline | count summary of problems only; reason "{metric} high/critical" from `attentionMetric` + engine health (Critical ≠ Warning) |
| NoCurrentData (DV-3) | no problems, but ≥ 1 Unknown or not-fresh server | "N without recent data"; never folded into healthy |
| Stale | snapshot older than the derived threshold | "No recent data", "Last state: …", muted bars, "Last reading … ago"; health never asserted as current |
| Empty | valid snapshot, 0 servers (including all hidden) | title + body + CTA "Open ServerAlyzer" (M/L also show an icon; S shows title + CTA only) |
| Unavailable | Missing/Oversized/Corrupt/Invalid/IoError | neutral "No monitoring data", no CTA; the card still opens the dashboard |

- Row precedence (D-UI9-5): Offline/Critical/Warning keep their label when stale. Only Healthy/Unknown become "Not updated".
- Offline/Unknown rows show "—" and no bar. A server never read stays "No data" (DV-2).
- **Server selection (D-UI9-7):**
  - a fleet view, worst-first;
  - rows S 0 / **M 3** / L 3, with "N of M servers" always shown on M/L (including "3 of 3");
  - invariant: visible + overflow == total;
  - no customisation, because of the platform bug and because no selection was designed;
  - instances are identical (`AllowMultiple="true"`).

## 7. Activation

- **Verbs unchanged:** card `openDashboard`; rows `openServer` + `serverId`.
- **No new verb or URI (D2 = openDashboard, human decision):** the empty-state CTA reads "Open ServerAlyzer" and leads to the Overview empty state, where the user picks "Add server".
- **Route:** `ExecuteActivationIntent` → onboarding suppressed → UI.8 `RestoreAndActivateStandard` (Compact → Standard) → exit guard → Dashboard / Detail.
- **Board results:**

  | Check | Result | Evidence |
  |---|---|---|
  | Click on a card → Standard "Overview", no protocol picker prompt | PASS | probe P-E |
  | Medium body click with the app closed → app launched by the provider (1 instance, GUI, no console) | PASS | V-1 |
  | Compact visible → card click → Standard in front, same PID, no second window | PASS (V-4) | `v3-results.md` |
  | Large row click → that server's Detail page | PASS (V-3) | `v3-results.md` |
  | Small card click | PASS | human-confirmed |

## 8. Visual: Figma vs platform vs final

Authority: Figma `Qvk5dUFgsWf4UOYzfAkiDV`, page 05 · section 16. Precedence: product/security invariants > a11y/platform > Figma. Prism's tables: `prism-c1-review.md`, `prism-c1-recheck.md`, `prism-c0-debrief.md`, `prism-c3-review.md`, `prism-v3-board-verdict.md`.

| ID | Figma | Platform | Final |
|---|---|---|---|
| D-1 | gradient + border + radius 8 | host owns the surface | host material (Manual `107:1362`) |
| D-2 | own 48 px strip (logo, name, "···") | host attribution area | no `header` key → host renders icon + "ServerAlyzer" + "···" (P-header) |
| D-3 | Inter 18/14/12/11 | Segoe; Small 12 / Default 14 / Medium 18 / Large 20 / XL 28; Bolder 600 | enum map; 11 → 12; `style:heading` ≈ Large Bolder on M/L titles only |
| D-4 | S: 52 px ring "3/3" | no ring element | "3/3" ExtraLarge Bolder (state colour; default when stale) + title Medium Bolder (no heading) + subtitle + footer, all `wrap:false`, no bar |
| D-5 | continuous bars 4/6 px, radius 3, colour per metric | no ProgressBar; `data:` 1×1 PNG backgrounds render | weighted fill/track columns, M 4 px / L 6 px, square ends; metric colour per theme (§9); weight 0/100 exact, 1–2 → 3, 98–99 → 97; ▰/▱ glyph meter kept behind `WidgetLayout.Meter` |
| D-6 | label ↔ value in 76/80 px columns | `auto` columns elide | one `RichTextBlock` "CPU 41%" |
| D-7 | inset row cards, radius 12, border | `emphasis` paints pastel natively, no radius/border | separators between rows |
| D-8 | M 3 / L 3 cards | capacity undocumented | M 3 / L 3 + "N of M"; M bar spacing None (RC-C3-1) |
| D-9 | M healthy = dot only | — | text "Healthy" (a11y > Figma) |
| D-10 | "Disk full" | — | "Disk high/critical" (no exaggeration) |
| D-11 | right-hand summary | no `targetWidth` | problems only, wraps, no-break spaces inside counts |
| D-12 | stale: neutral ring "3/3" | — | last-state fraction in the default colour |
| D-13 | "Updated 8 s ago" | repaint per commit / 60 s backstop | minute granularity |
| D-14 | empty: SVG icon + 34 px button | SVG needs an `http` namespace (forbidden by the image rule) | ServerStack01 40 px PNG `data:` URI (dark/light), M/L only; host-styled `Action.Execute` |
| D-15 | L "4 CPU cores" | data does not exist (D-UI9-1) | "Up 43d 18h" + "used/total GB" |
| D-16 | designed light/dark colours | `$host.hostTheme`; host re-renders on theme change | theme by `$when`; host text colours |
| D-17 | offline row: empty track | a full track reads 0 % | no meter, "—" |
| D-18 | content top-aligned | the host **vertically centres** M/L content even with card `verticalContentAlignment: Top` (V0-1) | documented difference; BACKLOG `UI9-VALIGN` (board probe before any change) |

## 9. Theme, localisation, accessibility

- **Dark/Light:**
  - all image constants have a dark and a light variant, selected by `$host.hostTheme` (`!= 'dark'` falls back to the light set);
  - the board re-rendered every card on theme switch with no provider action (P-D, V-2).
- **Bar palette** (Prism C0 §1). The contrast is computed by the tests from the decoded PNGs, against card surfaces sampled on the board (dark `#323232`/`#353329`, light `#FDFDFD`/`#EFEEE8`/`#E8E8E8`). The criterion is fill↔card **and** fill↔track ≥ 3:1; the track is exempt.

  | Role | Dark | Light | Worst light fill↔card / fill↔track |
  |---|---|---|---|
  | CPU | `#B69AF8` | `#8668CA` | 3.54 / 3.54 |
  | RAM | `#7DB8FF` | `#427EC5` | 3.41 / 3.41 |
  | Disk | `#FFC16E` | `#A86A1F` (Figma `#B87B2F` = 2.90, darkened) | 3.61 / 3.61 |
  | Stale | `#ADADAD` | `#626262` | 4.98 / 4.98 |
  | Track | `#484848` | `#E8E8E8` | exempt (1.00–1.40) |

  Dark fills are ≥ 5.41 against the card and ≥ 3.91 against the track. Colour never encodes state: "Disk critical" stays amber, and state is always text.
- **Localisation:**
  - `WidgetStrings` is the single source, en-US / pt-PT (tu) / pt-BR (você), following Prism's binding copy table, with key parity tested;
  - numbers use `CurrentUICulture` ("3,2/8 GB");
  - picker strings use `ms-resource:` (C2);
  - picker screenshots are synthetic, 300×304, per locale folder (root = en, `pt-pt`, `pt-br`), dark + light, generated by a deterministic offline proxy render.
- **Accessibility, verified:**
  - `style:heading` on M/L titles;
  - state always in text;
  - % always in text;
  - every `Image` has `altText` from `WidgetStrings` (the empty icon reuses the empty title);
  - truncation never splits a surrogate pair;
  - 0/144 proxy cards with clipped text (Prism C3).
- **Not verified:** Narrator (NOT_RUN); real DPI scaling; High Contrast.

## 10. Security (Vigil)

| ID | Requirement | Result |
|---|---|---|
| UI9-SEC-1 | a deleted/hidden server's name does not persist in the snapshot | forced write on `ServersChanged`; delete during a blocked write and a 50-burst coalesce correctly; a hide removes the name; the empty write is authorised after a fleet mutation (V-B1). Residual: `UI9-STARTUP-STALE-NAME` (Low, backlog) |
| UI9-SEC-2 (M-3) | `displayName` never evaluated as template/markdown/link | **CLOSED @ `415e34c`** (`vigil-c3-v3prep.md`). See below |
| UI9-SEC-3 | images only from the package, zero remote URLs | the image rule, re-decided on board evidence (`vigil-c0-debrief.md` §2–3). See below |
| UI9-SEC-4 | no new verb/URI | satisfied (D2 = openDashboard); verb set == {openDashboard, openServer} tested |

**UI9-SEC-2 closure:**
- the name is emitted only as a `TextRun` (proven literal on the board for markdown, `${…}` and JSON);
- `UntrustedText.ForCard()` inserts U+200B between consecutive `{` as the last step after truncation, so the card never carries `{{`;
- tests cover 14 hostile names + date/time variants, checked in the data JSON and in the card expanded both by the harness and by the .NET templating oracle;
- an IL guard fails if production code reads `UntrustedText.Value` outside `ForCard` (`75501f2`).

**UI9-SEC-3 image rule:**
- every image is a compile-time `const` `data:image/png;base64` URI in `WidgetImages` (12 constants);
- URLs are written literally in the template and selected by `$when`, never `${…}`;
- the data JSON never carries a URI;
- no http/https/file/ms-appx/UNC/OpenUrl/Submit/ShowCard/ToggleVisibility;
- PNGs carry only IHDR/IDAT/IEND, ≤ 4 KB each, ≤ 32 KB per template;
- a strict SVG allowlist exists (no SVG shipped);
- Vigil reviewed the bytes.

Correctness/robustness IDs UI9-COR-1..2 and UI9-ROB-1..3 are covered by the V-RC tests (`spec-amendments.md`). Packaging IDs UI9-PKG-1..3, UI9-SIGN-1 and UI9-DATA-1 are in §11 and §13.

## 11. Packaged real QA (UI.9D)

**Human decisions** (`spec-amendments.md`):
- **D1 = A:** in-place update of the real `PedroLoy.ServerAlyzer` identity, test-cert signed. The package version `1.1.2.N` is set at build time only and never committed.
- **H4:** the QA build stays installed until the next Store release.
- **UI9-DATA-1:** the pre-QA backup was moved out of OneDrive.
- **UI9-SIGN-1:** residual risk accepted until REL.0 / 2026-11-30.

**Sessions:**
- **C0 probe `1.1.2.1`** (evidence-only spike build, provenance `ddd7c58`): `.boss/evidence/ui9/ui9d/session-ui9d-probe-20261008a/`, `beacon-preflight-probe.md`.
  - The preflight stopped four times before touching the install (F-1 Store provider running, F-2 pin-list format, F-3 re-stamped SHA ≠ approved, F-4 provider relaunched by the host). Each was resolved by a human/Vigil decision, and nothing was killed by an agent.
  - Installed by a single `Add-AppxPackage -ForceTargetApplicationShutdown`; the old provider was ended by the deployment engine.
  - The original pin survived the update.
  - Probe blocks P-A…P-E gave the platform facts in §2.
  - Phases PostInstall, DuringQA-1 and DuringQA-2: PASS (human sign-off).
- **V3 `1.1.2.2`** from `dec83f3`: `session-ui9d-v3-20261008a/`, `beacon-v3build.md`.
  - Re-stamp: payload identical 0/148, manifest byte-exact except `Identity/@Version`.
  - Audits: Unsigned AUDIT PASS 29/29 with allowlist v3 `550A1E4E…`; the Unsigned re-check and the Signed audit were OK.
  - Guard PASS, then one install.
  - PostInstall: **0 differences** against the baseline.
  - DuringQA-1 and AfterQA: exactly 3 expected app-data modifications (`history.db`, `widget-state.json`, `window-placement.json`). No change to servers, known hosts, settings, `.ssh`, Credential Manager or package settings.
  - **PostInstall, DuringQA-1 and AfterQA: PASS with human sign-off** (`*/combined-verdict.json`).
  - Board blocks V-0…V-4: §1, §7, §8.
- **≥ 5 cycles:** 24 distinct board renders, including metric/state changes, plus 12 snapshot writes (~31.5 s).
- **Cleanup:**
  - the instances created by the runs were removed by the human;
  - the board is back to the original single Medium pin;
  - the theme was restored to dark;
  - the app was exited via the tray (human-confirmed).

**QA instrument story** (`beacon-runbook-fix.md` §7–10, Atlas/Vigil/Cortex instrument reviews):
1. **BOSS §16 "QA instrument fails open".** The first PowerShell instrument was rebuilt as fail-closed by construction: a verdict is PASS only when every required key was evaluated and passed, and metadata is checked by exact allowlist (round 2). The domain was then formally narrowed and enforced: CredMan and MSIX-name grammars, BLOCKED > FAIL > NOT_VERIFIED > PASS (`UI9-QAINSTR-1`).
2. **Atlas gate verification.** It found a culture-dependent `Sort-Object -Unique` false PASS (GATE-1). Human decision "option 2": all comparison moved to a compiled C# instrument outside the product repo. It uses ordinal comparers enforced by `BannedApiAnalyzers`, and its DLL SHA is pinned.
3. **Atlas instrument verification.** **F1**: the C# capture is correct, but its classifier authorises an added *directory* where a file is expected. That is a false PASS, so Atlas declared a final STOP for the C# instrument.
4. **Human decision "Manual QA, two channels"** (`twochannel-contract.md`):
   - channel A = the C# instrument, unchanged;
   - channel B = Atlas's independent Python oracle;
   - both must agree byte-for-byte on canonical inventories, credential-target lists and diffs;
   - any disagreement, error or missing evidence ⇒ BLOCKED;
   - plus a human sign-off of the side-by-side table.

   **F1 stays a recorded C# limitation**, never "fixed" and never ignored: the dry-run proves that F1 fixtures end BLOCKED through channel B's classifier.
5. **Credential Manager is read by target name/type only** (`CredEnumerateW`, the blob is never read). OneDrive `.boss/**` holds only hashes, metadata and sanitised logs.

## 12. Tests and counterproofs

- **Canonical `ServerMonitor.slnx` @ `dec83f3`** (clean `--no-incremental` build, then `--no-build` test):
  - **Debug 6100 passed / 0 failed / 1 skipped**;
  - **Release 5637 / 0 / 1 skipped** (`sentry-c3-report.md`, "C3 fixes").
  - WidgetProvider.Tests 556 (374 after B1/B2, 453 after C2); App.Tests Debug 3438.
  - The 1 skip is pre-existing (Infrastructure).
- **Implementer counterproofs** (baseline → production mutation → red → byte-exact restore → green): **75**, all killed.

  | Slice | Counterproofs | IDs |
  |---|---|---|
  | B1/B2 | 19 | M1a…M13 |
  | B fixes | 4 | F1–F4 |
  | C1 | 12 | C1–C12 |
  | C1-fix | 12 | X1–X11, S1 |
  | C2 | 7 | G1–G7 |
  | C2 nits | 2 | N1–N2 |
  | C3 | 16 | K1–K16 |
  | C3 fixes | 3 | R1–R3 |

  Logs: `sentry-*-report.md`.
- **Key test families:**
  - provider boundary;
  - provider clock guard;
  - golden compatibility;
  - malformed/hostile input through the real reader → `host.Update`;
  - template constancy + fixed data keys;
  - strict AC 1.6 shape harness;
  - **.NET AdaptiveCards.Templating oracle** (test-only dependency) equal to the harness on 72 cases × 2 themes;
  - the host's JS templating cross-check (offline, 144/144);
  - M-3 neutralisation + IL guard;
  - image scan + PNG/SVG checks;
  - computed contrast;
  - row-capacity invariant with measured literals;
  - picker manifest/copy/PNG guards.
- **Reviews:**
  - Cortex: SPEC and the B/C slices → APPROVED or APPROVED_WITH_NITS, re-checks APPROVED; C3 APPROVED;
  - Vigil: SPEC, B1/B2, C1, C2, runbooks, instrument, channel B, C3 → APPROVED after their fix rounds; M-3 CLOSED;
  - Prism: SPEC APPROVED_WITH_CHANGES, C1/C2 APPROVED_WITH_NITS, C3 APPROVED_WITH_CHANGES (RC-C3-1 applied); board verdict PASS with D-1…D-18;
  - Atlas: runbook/instrument/channel-B verification.

## 13. NOT_RUN and residual risks

- **M = 3 rows with ≥ 3 servers on the board: NOT_RUN** (the real fleet has 2; adding servers was forbidden by D1).
  - Measured: 2 rows ≈ 115 of ≈ 236 usable px [INFERENCE: a 3rd row fits].
  - Prism's proxy calibration (×0.88) puts the V3 Medium with RC-C3-1 within the card.
  - Criterion for the next QA with ≥ 3 servers: the "3 of N" footer is whole in healthy / attention pt-BR / stale / overflow, light + dark; otherwise M = 2 (one `WidgetLayout` constant) and the picker is regenerated.
- **Narrator:** NOT_RUN (including whether the empty icon's `altText` is read twice).
- **V2-1** (`UI9-HOST-ICON-LIGHT`): after a live switch to light, the host header icon slot was blank. The same PNG rendered in light during the probe [INFERENCE: host refresh after theme change]. This is an observation, not a PASS.
- **V0-1 / D-18** (`UI9-VALIGN`): the host centres M/L content. Fidelity only: nothing is clipped.
- **`UI7-SAVEGUARD-M1-RELEASE`:** pre-existing since ≤ `9a81dc1`. It fails in Release when isolated and passes in the full run; root cause inconclusive (`astra-ui7-release.md`). CI does not run App.Tests in Release. Outside UI.9 and untouched.
- **`PRIV-PDBPATH`, distribution BLOCK:** UI.9 builds contain a personal PDB/source path (pre-existing class). They were accepted **only** for local QA. No UI.9 MSIX may be distributed (Store, GitHub Release, third parties) until PathMap/DeterministicSourcePaths and a failing gate are in place.
- **REL.0:**
  - the real install stays Developer-signed `1.1.2.2` until the next Store release (`REL0-STORE-TRANSITION`; the Store version must be > the last QA version);
  - the test certificate `529C81F5…` must be removed from TrustedPeople/My by 2026-11-30 at the latest (`REL0-TESTCERT-REMOVAL`, UI9-SIGN-1);
  - the default package language must be decided (`REL0-DEFAULT-LANGUAGE`).
- **Windows Firewall prompt during packaged QA (`UI9-FW-PROMPT`, protocol deviation):**
  - What happened: about 2 s after each packaged app start, the app opened a listening socket (probably mDNS discovery, M7). Windows showed its Firewall prompt and created "Query User" Inbound Allow TCP+UDP rules for the app path. The human saw the prompt.
  - Why it is a deviation: the checklist treated any Firewall prompt as a STOP (B-PROMPT), and this one was not reported during the session.
  - Impact: none on data; both channels verified no data change beyond the expected matrix. This is pre-existing product behaviour, not a UI.9 change.
  - Cleanup (human GO + UAC, exact Names): removed the 4 testhost rules from the UI.9 Floor test runs and the 2 rules for the removed `1.1.2.1` path; kept the 2 rules of the installed `1.1.2.2` app. Result 713 → 707, 0 collateral.
- **C3-N3** (bars inside weighted columns): confirmed on the board. Coloured bars rendered at 4 px (M) and 6 px (L) in dark (V-1), and muted stale bars in light (V-2). Fresh coloured bars in light were not captured.

## 14. Backlog items created

| ID | Item |
|---|---|
| `UI9-VALIGN` | host vertical centring of M/L (D-18); board probe before any change |
| `UI9-HOST-ICON-LIGHT` | header icon blank after a live switch to light; reconfirm; theme-specific icons only with Vigil + reinstall |
| `UI9-STARTUP-STALE-NAME` | crash between a fleet mutation and the forced write can leave an old name locally (Low, minimisation) |
| `UI9-FIRST-CYCLE-FLUSH` | rows can show "No data" until the first cycle on 300 s fleets after startup |
| `UI9-SVC-VM-LAYER` | move `PriorityProblemSelector` to a neutral presentation namespace |
| `UI9-RECORDER-SEAM` | explicit trigger-kind seam (partly addressed by `e89cf8f`) |
| `UI7-SAVEGUARD-M1-RELEASE` | pre-existing Release-isolated test failure; consider App.Tests in Release CI |
| `PRIV-PDBPATH` (+ distribution block) | deterministic source paths + gate before any distribution |
| `REL0-STORE-TRANSITION` / `REL0-TESTCERT-REMOVAL` / `REL0-DEFAULT-LANGUAGE` | REL.0 obligations from H4, UI9-SIGN-1 and C2 N2-4 |
| `UI9-FW-PROMPT` | packaged app listens on start → Windows Firewall prompt on each new install path; listen only on demand / Private UDP 5353 / manifest rule; Vigil before any Store release |
| `APP-COPY-QUASECHEIO` | the App Overview's "almost full" at 80 % exaggerates; align with the widget's "high/critical" |

## 15. Post-merge state and closeout

- **Integration:** PR #32 merged (merge commit, no squash/rebase) at the authorised head `df15963` → `main` `c266b38`.
- **Post-merge CI** (run 37850235873):
  - Attempt 1: Release PASS. Debug FAIL in exactly one test, `Ui7SaveGuardTests.M1_ASavedVisitStillOnScreen_GoesToItsDetail_OnTheNextSubmit`.
    - That test is pre-existing and scheduling-dependent (`UI7-SAVEGUARD-M1-RELEASE`).
    - The UI.9 diff does not touch the editor, navigation or the UI.7 tests.
  - Attempt 2 (one rerun, as a measurement): both jobs PASS (App.Tests 3438/3438). The recurrence is recorded in the backlog, and a test-only fix needs a separate decision.
- **Cleanup:**
  - The UI.9 Floor was removed with its branch kept (triple-check: Maestri floor list, `git worktree list`, directory).
  - The evidence-only spike worktree `ServerMonitor-ui9-c0probe` was removed without `--force` (local branch `spike/ui9-c0probe` kept).
  - Branch `ui/ui9-windows-widget-v3` is kept (local + remote).
- **Preserved on the QA machine (not in Git):** the packaged-QA backups, inventories, checksums, sign-offs and work folders under `%LOCALAPPDATA%\ServerAlyzer-QA-Backup\ui9`. They are kept until REL.0 and a validated Store-managed upgrade.
- **Installed state:**
  - The real installation remains `PedroLoy.ServerAlyzer 1.1.2.2`, Developer-signed (human decision H4), with the original single Medium widget pin.
  - The 2 Firewall rules of the installed `1.1.2.2` app are kept. The start-up Firewall prompt (`UI9-FW-PROMPT`) is tracked separately.
- **REL.0 obligations (not started):**
  - validate a Store-managed upgrade that preserves data and widget pins;
  - replace the Developer build with the Store package;
  - remove the test certificate by 2026-11-30;
  - fix the embedded local paths (`PRIV-PDBPATH`) before any distribution.
- **NOT_RUN (unchanged, not reclassified):**
  - Narrator;
  - Medium with ≥ 3 real servers;
  - header icon after a live theme switch;
  - host vertical centring (D-18);
  - start-up Firewall prompt;
  - the UI.7 test above.
- UI.10 not started.
