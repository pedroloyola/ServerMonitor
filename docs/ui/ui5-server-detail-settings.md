# UI.5 — Server Detail, Definições, Dados e servidores

Figma `Qvk5dUFgsWf4UOYzfAkiDV`:

| Page | Dark | Light | First reading |
|---|---|---|---|
| Server Detail | `112:1785` | `112:2022` | `112:16007` / `112:16164` |
| Definições | `112:7909` | `112:8003` | — |
| Dados e servidores | `112:8230` | `112:8276` | — |

This file records the **deliberate deviations and derivations** of the implementation. The Figma values themselves are not repeated here: they live in the XAML comments next to each element.

## Breakpoints

| Window width | Detail | Definições / Dados |
|---|---|---|
| ≥ 1120 | Three metric cards side by side. The content is ≥ 1040 and each card ≥ 336, which is the Figma geometry. | Controls at the right of each row. |
| 700–1119 | Metric cards stacked, then Ligação / Explorar stacked. The actions stay beside the name. | Controls at the right of each row. |
| < 700 | Compact padding. The actions move under the identity. | Each control moves under its text, and the next row starts 16 below it. |

The window at 1040 is **Medium**: its content is about 960, so it is stacked. The B2 report said "Wide ≥ 1040"; that was wrong (Prism C1 N-10).

## Metric visuals

- **Fixed track.** All three meters keep the fixed Figma track of **288 × 40**, left-aligned, in every state:
  - pulse: 30 × 5.73, gap 4;
  - memory: 28 × 6.43, gap 4;
  - disk: 14 × 15.93, gap 5.

  They never stretch with the card.
- **CPU pulse scale** (Boss, fix round 2). This is a **deliberate derivation**: it revises the round-1 absolute 0–100 % scale.
  - The bars are relative to a **ceiling**: the smallest of 25 / 50 / 75 / 100 % that is at or above the highest visible sample. The pure rule is `MetricVisualPresentation.PulseCeiling`.
  - Why:
    - Figma fidelity: `112:1818` draws a ~24 % pulse with 13–35 px bars.
    - Quantisation keeps it honest: the step is deterministic.
  - The CPU value's accessible name states the real values, for example "CPU: 24%. Pulso das últimas 12 amostras, escala até 25%".
  - DERIVED: a sample above 0 keeps a 3 px minimum height, and a measured 0 draws no bar.
  - Only real samples from local history are drawn (H-UI5-3).
- **No severity colour on the Detail** (Prism C1 N-4 decision). The metric colours are identity colours, stale or not. The view model therefore exposes no `*Severity` property.

## Header, info strip, Ligação

- **Subtitle.** The format is `"{system}   ·   {address}"`, with three spaces each side, as in Figma `112:1802`. With no known system it is the address alone.
- **Stale chip.** It always says "Leitura desatualizada". The age is said once, by "Última atualização". That age is one timestamp (the engine's last success) read with one clock read per reading.
- **First reading.**
  - The collecting card is 216 high, the metric cards' height.
  - The info strip is a solid card 86 high, padding 24, gap 32, with skeleton values.
  - **Intervalo stays shown** during the first reading (Prism C1 N-12, accepted exception). It is known configuration, and a skeleton there would hide real information.
- **Ligação.**
  - The 4 Figma rows are 23 high, with a gap of 17.
  - The DERIVED "Estado da ligação" row appears only when the connection is not verified: not yet tested, testing, or failed.
  - The "Rota" row appears only with a jump host.
- **One notice at a time.** A connection problem replaces the generic "Não foi possível atualizar as métricas".

## Notices and errors

- **Transient notices auto-dismiss** (Boss decision 2). This covers the Servidores return notice and the Dados success toast.
  - They close after **8 s** (`TransientNoticeTimer.Duration`, DERIVED), when the user closes them, or when the page is left.
  - A later visit never shows them again.
  - They are polite live regions.
  - They sit in **their own row under the content**, so they never cover an action at any size. This is a deliberate deviation from Figma §3.2, which draws them over the content.
- **Server-scoped errors** (Boss decision 3). A failed Editar / Ocultar / Remover is reported with the server it was about.
  - It shows only on that server's Detail.
  - Global errors (load, add, discovery) still show everywhere (UI.4 SHOULD-3).
  - The scope is App-layer bookkeeping (`DashboardViewModel.OperationErrorServerId`). Nothing in Core changed.

## Confirmations

Remover servidor, Limpar histórico and Repor histórico all use `DestructiveConfirmDialog`.

- **Style.** `SaDialogStyle` + `SaDialog.Kind="Destructive"` (Figma section 11: `112:8559`, `112:8876`).
- **Content.**
  - Body.
  - The "affected data" row, 564 × 42, r11.
  - A note.
- **Behaviour.** Cancelar is the default button and takes the first focus, so Enter never deletes. This keeps the UI.4/M14 semantics.
- **Button order.** The Windows order, as accepted in UI.2 B-10.
- **Repor histórico.** It has no Figma frame and reuses the pattern: "Histórico local · base indisponível".

## Keyboard and screen readers

- **Theme selector.** One Tab stop, entry on the selected item, and the arrows move and select (UI.2 `SaGroupNavigation`).
- **Section requests.** "Sobre o ServerAlyzer" and the Background request bring their section into view **and** focus its control, cold or warm.
- **Hidden-servers list.** Every Restaurar is a Tab stop and says "n of N". The list is named by its card title.
- **List rows.** An explicit accessible name on an `SaListRow` (for example "Histórico de prod-web-01") is never overwritten by its title.
- **Refreshing.** "A atualizar…" is a polite live region, raised when a refresh starts.
- **Glass on a live theme switch.** The glass surfaces of an open page re-resolve their theme on a live theme switch (`SaThemeRefresh`). Before this, the previous theme's acrylic was left in place.
