# UI.2 — Componentes / primitivas

Estado: implementado na worktree `ServerMonitor-ui2`, branch `ui/ui2-components` (base `f40b25c`), slices S0–S8. Compilado Debug + Release e testado (ver §7); auto-verificação runtime `--qa-tokens` verde; galeria Debug com 17 separadores × 3 temas capturada. QA humana (Beacon, Narrator, HC real) por fazer — §8.
Data: 2026-10-01. Fontes vinculativas: decisões Boss B-1…B-6 (`.boss/tmp/ui2/boss-decisions.md`), inventário Prism (Figma 05 lido ao vivo, `.boss/tmp/ui2/prism-inventory.md`), arquitetura Cortex (`.boss/tmp/ui2/cortex-architecture.md`). Precedência UI.0 §0.3: invariantes/segurança > a11y/plataforma > Figma.

**Nenhum ecrã de produção mudou.** As primitivas e estilos existem, estão provados na galeria e prontos para adoção a partir de UI.3; nenhuma página os usa ainda. O único ficheiro de produção tocado fora de `Styles/Components/**` e `Controls/Primitives/**` é `App.xaml` (uma linha: merge **último** de `Sa.Components.xaml`) e `App.xaml.cs` (construção do router movida para a 1.ª linha + ramos `#if DEBUG` da galeria).

## 1. Arquitetura (resumo)

- **Forma:** estilo com chave sobre o controlo WinUI nativo quando a semântica nativa serve (botões, campos, CheckBox, ToggleSwitch, segmentado/nav em `RadioButton`, `ContentDialog`); **controlo templado** (`Control`/`ContentControl`, sem XAML próprio, `DefaultStyleKey`) quando há estado/partes próprias. `UserControl` proibido em `Controls/Primitives/**`. Nenhuma primitiva conhece VMs, `Core`, `Infrastructure` (T-11).
- **Merge:** `App.xaml` → … → `DesignTokens` → `Controls` → **`Styles/Components/Sa.Components.xaml`** (agregador: Icons, Text, Surfaces, Buttons, Forms, Navigation, Dialogs, Primitives). Só acrescenta chaves `Sa*` (T-5, T-7). Cada dicionário de componentes faz merge aninhado dos tokens que referencia por `StaticResource` (T-2, agora restrito ao próprio dicionário e ascendentes — Cortex F-7).
- **Estilos por defeito** dos tipos `Sa*` vivem em `Sa.Primitives.xaml` como estilos implícitos `primitives:Sa*` dentro de `Application.Resources` — **provado em runtime** (S1): o `SaStatusIndicator` é templado e o estado visual re-resolve por tema. `Themes/Generic.xaml` não foi necessário. Únicos estilos implícitos da app (T-6).
- **Templates WinUI**: TextBox/PasswordBox/CheckBox/ToggleSwitch/RadioButton/Button usam templates Sa que **mantêm os nomes de partes e estados WinUI** (`ContentElement`, `PlaceholderTextContentPresenter`, `CombinedStates`, `SwitchKnobBounds`/`KnobTranslateTransform`/`SwitchThumb`…), por isso entrada, IME, arrastar e UIA continuam nativos. O ComboBox mantém o template Fluent (depende do `ComboBoxHelper` interno) e é estilizado por propriedades + scope F-3.

## 2. Catálogo

MEASURED = valor lido no nó Figma citado; DERIVED = sem fonte Figma (regra B-4: hover = `SaHoverBrush`, pressed = `SaSelectedBrush`, foco = foco de sistema `SaFocusRingBrush` 2 px, disabled = opacidade .58 de `112:3399`). Consumidores = milestones previstos (UI.0).

| Primitiva | Figma | Variantes | Estados (M = MEASURED, D = DERIVED) | Tokens principais | Consumidores |
|---|---|---|---|---|---|
| `SaPrimaryButtonStyle` | `112:1004` D / `112:1216` L | pill h44 r22, ícone 18 | Rest M, Disabled M (.58); Hover/Pressed/Focus D | `SaPrimaryFill/Border/TextBrush` (assimétrico por tema), `SaFontSizeButton` | UI.4–UI.6 |
| `SaSecondaryButtonStyle` / `SaSecondaryRectButtonStyle` | `112:3323`/`112:3484`; `112:1806`/`112:2043` | pill h42 r21; rect h42 r14 | Rest/Disabled M; resto D | `SaSelectedBrush`, `SaGlassHighlightBrush`, `SaRadiusRect` | UI.3–UI.7 |
| `SaDialogButtonStyle` / `SaDestructiveButtonStyle` | `112:8573`/`112:8759`; `112:8575`/`112:8761` | r13, min 168/196 | Rest M; resto D | `SaDialogButtonFill/Border`, `SaDanger{Fill,Border,Text}Brush` | UI.5, UI.7 |
| `SaSolidButtonStyle` | `112:14098`/`112:14192` | Manual r12 h40, 14 Semibold | Rest M; resto D | `SaInteriorBrush`, `SaBorderInsetBrush` | UI.3–UI.5 |
| `SaIconButtonStyle` / `SaGhostIconButtonStyle` | `112:1001`/`112:1213`; `112:19619` | 44 circular; 28 sem fill | Rest M; resto D | `SaSelectedBrush`; `SaTextSecondaryBrush` | UI.4; toast |
| `SaFormField` (templado) | `112:3337`, erro `112:16924`/`112:17088` | label / helper / erro | Valid, Invalid M | `SaLabelTextStyle`, `SaHelperTextStyle`, `SaDangerTextBrush` | UI.5, UI.7 |
| `SaTextFieldStyle` / `…ErrorStyle` | `112:3337`/`112:3498` | h40 r12 | Rest/Filled/Error M; Hover/Focus/Disabled D | `SaInputFillBrush`, `SaBorderSubtleBrush`, `SaErrorBorderThickness` | UI.5, UI.7 |
| `SaPasswordField` (templado) | `112:3693` | olho (View 17) | Rest M; revelado/hover/foco D | idem + `SaRevealToggleStyle` | UI.7 |
| `SaPageSearchFieldStyle` / `SaPillSearchFieldStyle` | `112:1429`/`112:1611`; `112:1051`/`112:1263` | r14 h44; pill h38 | Rest M; resto D | `SaSearchFillBrush`; `SaSelectedBrush` | UI.3, UI.4 |
| `SaSelectorFieldStyle` / `SaSelectorPillStyle` (ComboBox) | `112:3388`/`112:3549`; `112:7944`/`112:8105` | campo; pill h36 | Rest M; resto D (scope F-3) | `SaInputFillBrush`; `SaSelectedBrush` | UI.5, UI.7 |
| `SaCheckBoxStyle` | `155:240`, `159:394` | caixa 15 r4, tick 11 | Unchecked/Checked M; Indeterminate/Hover/Pressed/Disabled D | `SaBorderNeutralBrush`, `SaTextBrush`, `SaInputFillBrush` (tick) | UI.7 |
| `SaToggleSwitchStyle` | `112:7956`/`112:7982` | trilho 44×26 r13, thumb 22 | On/Off M; resto D | `SaHealthyBrush` (on, B-2), `SaToggleOffTrackBrush`, `SaToggleThumbBrush` | UI.5, UI.8 |
| `SaSegmentedRect/PillItemStyle` + `…TrackStyle` | `112:2634`/`112:2850`; `112:3356`/`112:3517` | rect r14/r11; pill r21/r17 | Selected/Unselected M; resto D | `SaSegmentedTrack/PillTrack/PillSelectedBrush` | UI.3, UI.7 |
| `SaNavItemStyle` | `112:969`/`112:976` | h46 r23, ícone 20 | Selected/Unselected M; resto D | `SaSelectedBrush`, `SaSelectedTextBrush` | UI.6 |
| `SaStatusIndicator` (templado) | `112:1066`, `112:1448` | ponto 6 + label | Healthy/Attention/Offline/Unknown M; Error M (`dark/danger`); **Stale D** (texto secundário a .6, nunca o brush disabled — Cortex F-3) | `SaStatusDotSize`, status brushes do Manual (D8) | UI.3–UI.5, UI.8 |
| `SaMetricBar` (templado) | `112:1040`/`112:1252` | CPU/Memória/Disco | valor; **desconhecido = NaN → trilho vazio + "—", nunca 0** | `SaBarEmptyBrush`, `SaCpu/Memory/DiskBrush`, `SaRadiusMiniBar` | UI.4, UI.8 |
| `SaMetricValueTextStyle` + `SaMetricUnitTextStyle` | `112:1816`/`112:2053` | 44 Light + "%" 20 | — | tokens B-3 | UI.5 |
| `SaKeyValueRow` (templado) | `112:1906`; `112:1919` | Stacked; Inline (190) | — | `SaValueTextStyle`, `SaCaptionTextStyle` | UI.5 |
| `SaListRow` (Button templado) | `112:1057`; `112:1933` | Simple h48; Rich h70 | Rest M; resto D | `SaButtonTextStyle` (14 — Figma 15, desvio), ícones | UI.4, UI.5 |
| `SaDataTableHeader/RowStyle` + `SaTableCell*TextStyle`, `SaTableHeaderTextStyle` | `112:1434`/`112:1616` | cabeçalho h34, linha ≥78, **sem divisores** | — | B-3 10/14 Medium (Cortex F-5) | UI.3, UI.4 |
| `SaInlineNotice` (templado) | erro `112:18273`/`112:18441`, `112:19265`; info `112:2756` | Error (± ação); Info plain | Error assertivo, Info educado | `SaInteriorBrush`, `SaBorderInsetBrush`, `SaDangerTextBrush` | UI.3, UI.5, UI.7 |
| `SaToast` (templado, **só visual**) | `112:19613`/`112:19779` | sucesso | Rest M | `SaRadiusToast`, `SaSuccessBrush`, `SaSurfaceBrush` | UI.4–UI.7 (host com ciclo de vida fica para UI.5/UI.7) |
| `SaSkeleton` (templado) | `112:15602`/`112:15726` | estático; pulso D | pulso só com `AnimationsEnabled` | `SaSkeletonBrush`, `SaRadiusSkeleton`, Motion | UI.3–UI.5 |
| `SaEmptyState` (templado) | `112:14088`/`112:14182` | ícone 40, título 24/30, ação | — | `SaEmptyTitleTextStyle` | UI.3–UI.5, UI.7 |
| `SaBreadcrumb` (templado) | `112:1786`/`112:2023` | 2 níveis | pai = botão; hover D | `SaCaptionTextStyle`, chevron 14 | UI.3, UI.5 |
| Superfícies `SaGlass/Solid/Inset/InsetTinted/ModalSurfaceStyle`, `SaOverlayStyle` | `112:1010`; `112:14088`; `112:22183`; `112:8562`; `112:8559`; `112:8558` | Glass (acrílico in-app) e Solid (Manual) | HC: 2 px WindowText no modal (F-07) | `SaGlassSurfaceBrush`, `SaSurfaceBrush`, `SaModalSurfaceBrush`, `SaOverlaySmokeBrush`, `SaModalBorderThickness` | UI.3–UI.8 |
| `SaDialogStyle` (ContentDialog) | `112:8559`/`112:8745` | pad 28, r24 | HC 2 px | idem + `SaDialogButtonStyle` | UI.5, UI.7 |
| `SaIcon` (templado) | `107:613`, Prism §3 | 22 ícones, 11–48 px | traço 1.5 constante | `SaIconSize*`, `SaIcon*Data` | todas |

Não construído em UI.2 (B-4): visualizações de MetricCard, HealthSegments, cartão de prioridade, IconTile, seletor rico, segmentado pequeno de tema, disclosure, TabStrip (Pro), restyle do HistoryChart, AppShell/host da sidebar, primitiva Divider (nenhuma desenhada).

### 2.1 Tokens acrescentados (B-2/B-3, só aditivos)
Cores com paridade Dark/Light/HC (HC → `SystemColor*`) e comentário de nó: `SaAttentionText`, `SaDangerText/Fill/Border`, `SaPrimaryFill/Border/Text`, `SaInputFill`, `SaSearchFill`, `SaSegmentedTrack/PillTrack/PillSelected`, `SaToggleOffTrack`, `SaToggleThumb`, `SaSkeleton`, `SaInsetTinted`, `SaBarEmpty`, `SaDialogButtonFill/Border` (`…Brush`). Raios `SaRadiusToast 20`, `SaRadiusRect 14`, `SaRadiusDialogButton 13`, `SaRadiusSegment 11`, `SaRadiusSkeleton 6`; `SaSpace28`; `SaIconSizeSmall/Medium/Large 16/20/24`; `SaStatusDotSize 6` (Cortex F-4); `SaErrorBorderThickness 1.5`. Estilos `SaPageTitle 32/40`, `SaMetricValue 44/56 Light`, `SaEmptyTitle 24/30`, `SaValue 17/24`, `SaHelper 11/16`, `SaButton 14/20`; tamanho `SaFontSizeTableHeader 10/14`. Nenhum valor existente mudou. Manifesto QA: 242 chaves (T-1/T-3 exatos).

**Display 44/56 e Heading 30/40** — a galeria (Typography) mostra Display 44/56 (`107:527`), Heading 30/40 e a variante candidata **30/42** (Figma lh 1.4, `112:3319`) lado a lado para decisão Prism (B-3). **Pendente: decisão Prism.**

## 3. Ícones (decisão B-1)

Hugeicons Free (Stroke Rounded), `@hugeicons/core-free-icons` **4.3.5**, licença **MIT** (Copyright (c) 2025 Hugeicons), integrity `sha512-Sv+NjHRPnQk+yZsGCMcGznCHdTfJR9PMMOEKcJYAQU4gy90dmlc9PwdtvYOImBZIjiheMsTLL5eMn2DmHxUiUg==` (verificada pelo Boss no tarball). **Dados de path vendorizados, sem dependência de pacote**: `tools/ui/vendor-hugeicons.py` gera `Styles/Components/Sa.Icons.xaml` (22 `x:String SaIcon{Nome}Data`; círculos → dois arcos; um path por ícone) e `Icons.manifest.json` (pacote, versão, integrity, licença, ficheiro fonte por ícone); texto MIT em `THIRD-PARTY-NOTICES.md`. T-18 trava o conjunto (exatamente os 22 de B-1, nenhum fora do manifesto, licença presente).
`SaIcon` escala a **geometria** (`Geometry.Transform` = Size/24), nunca o elemento: o traço fica 1.5 DIP. **Prova em píxeis** (`--qa-tokens`, RenderTargetBitmap da barra vertical do Add01): 1.50 DIP a 16, 24 e 48 px (escala 1.0). Contraprova: escalar o elemento lê 1.00 a 16 e 3.00 a 48 → exit 2.
**Achado empírico:** uma `ScaleTransform` identidade (24 px) faz o WinUI desenhar o path **vazio** — o tamanho de desenho não aplica transform (`IsIdentityScale`, teste + sonda 24 px no self-check).
Caps/joins: o template usa round em todos os ícones; o SVG de alguns subcaminhos (Settings01 externo, Folder01, Sun03, View) não declara join (miter) — diferença ótica mínima, aceite. Segoe Fluent continua só na produção atual e como fallback de plataforma.

## 4. Fecho de gates

| Gate | Fecho | Evidência |
|---|---|---|
| **F-1** resolução runtime de todos os `Sa*` | `--qa-tokens`: probe por chave (Tag), Dark→Light na **mesma** página (re-resolução runtime), HC por simulação de recursos; fail-closed; exit 0/2/3 | 573 PASS / 0 FAIL; brushes/estilos = mesma instância do dicionário; T-1/T-3 estáticos |
| **F-3** accent local | `Sa.AccentNeutralScope.xaml` (25 chaves WinUI → `Sa*`, por tema) só no contentor que adota primitivas; nunca app-wide (T-7); isenção no ratchet **por nome de `x:Key`** (Cortex F-11) | ComboBox dropdown **herda** o scope (pill e superfície neutras); **Flyout não herda** (o ProgressBar, cuja chave está no scope, fica azul) → limitação até UI.6 |
| **F-4** Motion em C# | `MotionTokens` invariante, fail-closed, regex `\A…\z` (F-8); separador Motion | T-9 com cultura pt-PT; `AnimationsEnabled` respeitado |
| **F-07** smoke/borda HC | smoke HC opaco (`SystemColorWindowColor`, = legado); modal/diálogo 2 px `SystemColorWindowTextColor` via `SaModalBorderThickness` temático | testes `HighContrastSurfaceGuardTests`; **QA com HC real: NOT_RUN** (B-5) |
| Color.Primitives duplicado | não consolidado; T-10 prova inofensivo (só `Color`) | verde |
| `Default` em DesignTokens | não tocado; T-12 `Default ≡ Dark` | verde |
| **G-3** nomes em runtime | inventário inclui Storyboard/Setter Target/FindName/GetTemplateChild/TemplatePart, **literais e `const string`**; partes/estados por template | verde; contraprovas |
| **G-4** | só rename (`HarnessDeltaRegistersOnlyQaDoublesOrInMemoryState`), sem seam (decisão Boss) | — |
| **G-5** screenshots determinísticos | `Apply` apaga placement obsoleto (fail-closed a `%TEMP%\ServerMonitor-QA`); `--qa-store-screenshot` com placement fixo em memória; galeria com janela fixa 1440×900 | T-13 + testes de recusa/armazenamento |

## 5. Desvios deliberados do Figma

- **D8 cores RAW**: pontos de estado, métricas Light, toggle-on, badge do toast e segmentos usam os tokens do Manual; as cores RAW divergentes (Prism §0.5) não são adotadas.
- **Placeholder** (a11y): Figma `#808080`/`#7A7A7A` (3.9–4.0:1) → `SaTextSecondaryBrush`.
- **Fonte**: SF Pro → Segoe UI Variable (D1); pesos Medium/Semibold mapeados.
- **Glass**: refração Figma → Acrylic in-app nativo (D7) com fallback opaco (galeria simula "efeitos de transparência desligados", rotulado). **Sombras e highlights internos de superfície não reproduzidos** nos estilos Border (ThemeShadow por página a partir de UI.3); nos botões o highlight é uma linha de 1 px no topo.
- **Pílulas**: raio = h/2 em literal — um raio acima de h/2 (o `SaRadiusPill` 99) desenha **elipses** em WinUI (achado empírico).
- **ListRow**: título 14 Medium (Figma 15 — sem token em B-3).
- **InlineNotice**: corpo lh 16 (Figma 18) pela rampa.
- **ComboBox**: chevron Fluent (E70D, ACCEPTABLE) — o ArrowDown01 exigiria copiar o template interno.
- **ContentDialog**: o template Fluent pinta o `DefaultButton` com `AccentButtonStyle` (azul legado) por cima do estilo Sa → diálogos Sa usam `DefaultButton = None` até UI.6.
- **Estados hover/pressed/focus**: DERIVED (B-4), rotulados na galeria.
- **HC**: tokens mapeiam para `SystemColor*`; o toggle-off usa `GrayText` para se distinguir do on (`WindowText`).

## 6. Galeria Debug (contrato de screenshots)

`ServerMonitor.App.exe --qa-components [--qa-gallery-page <id>] [--qa-gallery-theme dark|light|hc-sim]` (Debug; recusado em Release; exclusivo com outros harnesses; qualquer opção `--qa-gallery-*` sem `--qa-components`/`--qa-tokens` é recusada — Cortex F-1). Não constrói o host, não toca dados do utilizador. Páginas: tokens, typography, colors, spacing, materials, status, motion, icons, buttons, forms, navigation, data, feedback, materials-fallback, popup-combo, popup-flyout, popup-dialog. Estados hover/pressed forçados por `QaForcedState` (VisualStateManager.GoToState) em instâncias dedicadas; foco por teclado real numa amostra por página. Cada amostra comparável tem o nó Figma no rótulo.
`tools/qa/ui2-gallery-shots.ps1` lança o exe Debug exato por (página × tema), verifica a linha de comandos, captura só essa janela e para exatamente esse PID.
**HC-sim**: copia as entradas HighContrast dos tokens para um host interno; prova a resolução (52/52 brushes de página). **Limite empírico**: brushes definidos em setters de `Style`/`VisualState` dos templates resolvem a partir de `Application.Resources`, por isso a simulação não os altera — o visual HC dos componentes só se valida com HC real.

## 7. Evidência runtime

Ver `.boss/tmp/ui2/relay-s4-s8-report.md` (contagens Debug/Release, contraprovas, PIDs, manifesto AppData) e `.boss/evidence/ui2/gallery/*.png` (51 capturas).
Incidente durante S7: uma contraprova apagou `%LOCALAPPDATA%\ServerMonitor\window-placement.json` (só posição/modo da janela); o Boss repôs os bytes exatos (mesmo SHA-256). Causa e correção no relatório; novo guard `TestsConstructTheRealAppDataPathOnlyWhereJustified`.

## 8. NOT_RUN

Beacon (comparação lado a lado com os PNG Figma), Narrator, contraste AA medido em ecrã, HC real (4 temas), escalas 150/200 %, `AnimationsEnabled = false` (definições do utilizador não são alteradas por agentes), decisão Prism sobre Heading 30/40 vs 30/42, página 06 (B-6: não legível; nenhuma primitiva Pro construída).
