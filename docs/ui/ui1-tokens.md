# UI.1 — Tokens / foundations

Estado: implementação aditiva revista, compilada (Debug + Release) e testada (3664/3664); smoke runtime `--qa-health` passou; cleanup de código morto feito com prova (§7.1). Validação Light/HC e comparação Beacon pendentes (ver §8).
Worktree: `ServerMonitor-ui1`; branch `ui/ui1-foundations`; baseline `5a88d8d`.
Data: 2026-09-30. Owner: Sentry (concluído por agente de continuação após fim de quota; revisão crítica em §10). Sem commit/push. Testes de contrato: Atlas; comparação de screenshots: Beacon.

## 1. Contrato e nomenclatura

Prefixo único **Sa** (ServerAlyzer), seguido do papel e tipo: `SaColor{Role}{Dark|Light}` para cores primitivas; `Sa{Role}Brush` para brushes semânticos; `SaFont*`, `SaLineHeight*`, `Sa*TextStyle`, `SaSpace*`, `SaRadius*`, `SaMotion*` para as restantes famílias.
São novos nomes; não são overrides WinUI. ThemeDictionaries usam somente Dark, Light e HighContrast; todos os temas contêm as mesmas chaves semânticas.
Consumidores futuros usam `ThemeResource` para brushes e `StaticResource` para medidas/estilos.

Ordem em App.xaml: XamlControlsResources → Color.Semantic (que funde internamente Color.Primitives via `ms-appx:///Styles/Tokens/Color.Primitives.xaml`) → Typography → Spacing → Radius → Borders → Elevation → Motion → DesignTokens → Controls.
Color.Primitives **não** é irmão em App.xaml: os `{StaticResource SaColor*}` das ThemeDictionaries de Color.Semantic resolvem dentro do próprio âmbito do dicionário, sem depender da ordem de parse de App.xaml nem de lookup entre dicionários irmãos. As chaves primitivas continuam alcançáveis a partir de Application.Resources (merged aninhado).
Os novos dicionários vêm **antes** de DesignTokens/Controls e têm nomes exclusivos: nenhum pode sobrepor uma chave antiga. Vêm **depois** de XamlControlsResources, pelo que só sobreporiam chaves WinUI homónimas — não existe nenhuma chave WinUI `Sa*`.
Nenhum estilo implícito, nenhum token aplicado a página/controlo, nenhum código de VM/core alterado.
`DesignTokens.xaml` e `Controls.xaml` permanecem intactos. Os aliases legados conservam os valores antigos; o mapa abaixo é um **destino de migração**, não um redirecionamento já efetuado.
O azul legado #1846E1 não aparece nos novos dicionários.

## 2. Evidência Figma

Leitura direta via Figma MCP das páginas 04 (`106:238`) e 05 (`112:238`) de [ServerAlyzer](https://www.figma.com/design/Qvk5dUFgsWf4UOYzfAkiDV), em 2026-09-30.
Manual: cores nas págs. 04–05 (texto `107:424`, `107:429`, `107:439`, `107:451`, `107:466`); tipografia `107:528/531/534/537/540`; adaptação Windows `107:543`; geometria `107:603`.
Valores oficiais completos: UI.0 §3.1 e instrução aprovada UI.1. Prevalecem sobre coleções e cores divergentes de ecrãs.

Ecrãs lidos: Dark `112:930`, Light `112:1141`, adicionar `112:3252/3412`, confirmação `112:8392/8577`.
Todas as conversões RGBA → #AARRGGBB arredondam ao byte mais próximo; o alfa é de paint, separado da opacity do nó.

## 3. Catálogo de cor

Cada linha define `SaColor{Papel}Dark`, `SaColor{Papel}Light` (Color fixo, independente do tema) e `Sa{Papel}Brush` (resolução abaixo).
Em HC os brushes são SolidColorBrush com Color ligado por ThemeResource à cor SystemColor correspondente: mantêm a paleta escolhida pelo utilizador, sem Acrylic.
Primitivas não são usadas diretamente em HC.

| Papel / chaves conforme convenção | Dark | Light | HighContrast | Fonte |
|---|---|---|---|---|
| Canvas | #141414 | #E7E7E7 | SystemColorWindowColor | Manual págs. 04–05 |
| Surface | #242424 | #F7F7F7 | SystemColorWindowColor | Manual págs. 04–05 |
| Interior | #313131 | #E8E8E8 | SystemColorWindowColor | Manual págs. 04–05 |
| Text | #F5F5F5 | #202020 | SystemColorWindowTextColor | Manual págs. 04–05 |
| TextSecondary | #ADADAD | #626262 | SystemColorWindowTextColor | Manual págs. 04–05 |
| Cpu | #B69AF8 | #8668CA | SystemColorWindowTextColor | Manual págs. 04–05 |
| Memory | #7DB8FF | #427EC5 | SystemColorWindowTextColor | Manual págs. 04–05 |
| Disk | #FFC16E | #B87B2F | SystemColorWindowTextColor | Manual págs. 04–05 |
| Attention | #FFC16E | #B87B2F | SystemColorWindowTextColor | Manual págs. 04–05 |
| Healthy | #53DAB1 | #299985 | SystemColorWindowTextColor | Manual págs. 04–05 |
| Success | #53DAB1 | #299985 | SystemColorWindowTextColor | Manual págs. 04–05 |
| Error | #FF858D | #BD5766 | SystemColorWindowTextColor | Manual págs. 04–05 |
| Offline | #FF858D | #BD5766 | SystemColorWindowTextColor | Manual págs. 04–05 |
| BorderNeutral | #99999999 | #99737373 | SystemColorWindowTextColor | Checkbox 112:8522 / 112:8708 |
| BorderSubtle | #21999999 | #99FFFFFF | SystemColorWindowTextColor | Input 112:3339 / 112:3500 |
| Divider | #484848 | #D3D3D3 | SystemColorWindowTextColor | Variável Figma `dark/border` / `light/border` (112:12427 / 112:12820). Uso 1: separador |
| BorderInset (só brush `SaBorderInsetBrush`, alias de `SaColorDivider*`) | #484848 | #D3D3D3 | SystemColorWindowTextColor | Mesma variável; uso 2: contorno dos insets de métrica (112:12056 / 112:12449) e segmentos vazios (112:12108) |
| DisabledSurface | #22525252 | #4AFFFFFF | SystemColorWindowColor | 112:3399 / 112:3560; alfa do paint × opacity 0.58 |
| DisabledText | #ADADAD | #626262 | SystemColorGrayTextColor | Figma 112:3400 / 112:3561 = `#808080` / `#7A7A7A` @ opacity .58 (efetivo ≈ #5E5E5E sobre #242424, ≈ #B4B4B4 sobre #F7F7F7). **Desvio deliberado** (exceção 3 de UI.0 §0.2, legibilidade): usa o secundário do Manual a 100 % |
| Hover | #1E525252 | #40FFFFFF | SystemColorHighlightColor | **DERIVED – no Figma source, validate in UI.2 gallery.** A página 05 não tem estado pointer-over de controlos Windows (os nós 112:12043/12436/12829/13111 são popovers opacos do companion macOS, não hover). Regra reproduzível: paint de Selected (112:969 / 112:1180) a metade do alfa (alfa 0x3B → 59/2 = 29,5 → 0x1E; 0x80 → 0x40) — progressão Fluent rest → hover → selected |
| HoverText (só brush `SaHoverTextBrush`) | #F5F5F5 | #202020 | SystemColorHighlightTextColor | = Text em Dark/Light; em HC garante contraste sobre o fill Highlight do hover (Cortex F-5) |
| Selected | #3B525252 | #80FFFFFF | SystemColorHighlightColor | Navegação 112:969 / 112:1180 |
| SelectedText | #F5F5F5 | #202020 | SystemColorHighlightTextColor | Manual; em HC HighlightText sobre Highlight. Nota: 112:975 ainda usa `--color-text #F4F6F8` da coleção `Primitives` antiga — prevalece o Manual; não "corrigir" o token |
| FocusRing | #F5F5F5 | #202020 | SystemColorHighlightColor | DERIVED; texto neutro do Manual; pesquisa em 05 sem nenhum nó focus/foco (confirmado por Prism) |
| GlassBorder | #5EF0F0F0 | #F2FFFFFF | SystemColorWindowTextColor | Contorno do painel glass (Manual pág. 07 "contorno fino"): 112:1010 `rgba(240,240,240,.37)` / 112:1222 `rgba(255,255,255,.95)` |
| GlassHighlight | #1FFFFFFF | #CCFFFFFF | Transparent | Highlight interno `inset 0 1px 1px rgba(255,255,255,.12)` / `.8` (112:1010/1222; também 112:8559/8745, 112:969/1180, 112:3399/3560) |

DisabledSurface tem o alfa combinado (0.23×0.58 Dark; 0.50×0.58 Light); **não** voltar a aplicar opacity 0.58 ao mesmo brush. DisabledText é separado para manter legibilidade.
Hover é uma derivação declarada sem fonte Figma (validar na galeria UI.2). Divider e BorderInset são a mesma variável Figma `border` com dois usos: separadores (`SaDividerBrush`) e contorno de insets de métrica/segmentos vazios (`SaBorderInsetBrush`).
Os ecrãs 05 ainda referenciam a coleção `Primitives` antiga em alguns nós (ex.: 112:975 `#F4F6F8`, 112:1019 `--color-green #B7F76B`; ver UI.0 §4.3.2). Os tokens seguem o Manual (§0.1) — `#F5F5F5`, `#53DAB1`; ninguém deve "corrigir" os tokens para os valores da coleção antiga.
FocusRing é uma adaptação de acessibilidade explicitamente derivada, não uma medição Figma.
Cor de estado deve ser acompanhada por texto/ícone. Unknown não é zero. As cores oficiais de métricas não implicam autorização para texto pequeno sem medir contraste. Cálculo sRGB WCAG sobre #F7F7F7: atenção #B87B2F = **3.32:1**, erro #BD5766 = **4.15:1**, secundário #626262 = **5.69:1**, texto #202020 = **15.21:1**. Atenção/erro falham 4.5:1 para texto normal: em UI.2 usar texto neutro com indicador semântico, ou propor token de texto acessível separado; não escurecer silenciosamente o token oficial.

## 4. Tipografia, espaço e geometria

Cada papel define `SaFontSize{Papel}` (Double), `SaLineHeight{Papel}` (Double) e `Sa{Papel}TextStyle` (TextBlock explícito).
LineStackingStrategy = BlockLineHeight; LineHeight em DIPs **inteiros** (evita baselines em sub-pixel). O 140 % dos estilos Manrope (descartados por D1) não é usado: o Manual não define altura de linha, e os valores seguem os ecrãs 05 (SF Pro) ou o type ramp Fluent de Segoe UI Variable. TextBox/Control não suportam este mesmo contrato; adoção posterior deve respeitar as limitações reais.
Valores idênticos em Dark/Light/HC, sem Foreground imposto pelo estilo.

| Papel | Família SaFont* | Tamanho | Peso WinUI | LineHeight | Fonte |
|---|---|---|---|---|---|
| Display | Display | 44 | SemiBold | 56 | Tamanho: Manual 107:528. LH: calibração Prism, inteiro ≈ 1,27× (ecrã 05 não tem Display; Fluent Display 68/92 não escala linearmente) — validar na galeria |
| Heading | Display | 30 | SemiBold | 42 | Tamanho: estilo Figma ServerAlyzer/Heading. LH: **42** (UI.2 R1, decisão Prism B-3: Figma lh 1.4 em `112:3319`; era 40) |
| Metric | Display | 28 | SemiBold | 36 | Tamanho: Manual 107:531. LH: ecrã 05 112:12058 (28/36) = Fluent Title 28/36 |
| Title | Display | 20 | SemiBold | 28 | Tamanho: Manual 107:534. LH: Fluent Subtitle 20/28 (ecrã 112:12053 usa 26 com SF Pro) |
| TitleCompact | Text | 18 | SemiBold | 24 | Tamanho: estilo Figma ServerAlyzer/Title. LH: Fluent Body Large 18/24 |
| Body | Text | 14 | Normal | 20 | Tamanho: Manual 107:537. LH: ecrã I112:12146;112:11857 (14/20) = Fluent Body 14/20 |
| Caption | Text | 12 | Normal | 16 | Tamanho: Manual 107:540. LH: Fluent Caption 12/16 (ecrã 112:12054 usa 18 com SF Pro) |
| Control | Text | 13 | Medium | 18 | 05: nav 112:975, botões 112:3400, inputs 112:3340, chips 112:8570 (SF Pro 13, ≈1,4). LH 18 = inteiro mais próximo de 13×1,4 |
| Label | Text | 12 | Medium | 16 | Tamanho/peso: 05 112:3338/3342/3346 (SF Pro Medium 12). LH: Fluent Caption 12/16 |
| Micro | Text | 10 | SemiBold | 14 | Tamanho: estilo Figma ServerAlyzer/Micro. LH: inteiro (Fluent não tem 10) |
| Mono | Mono | 12 | Normal | 16 | Adaptação Windows para conteúdo técnico (DERIVED). LH: alinhado a Caption 12/16 |

Famílias: `SaFontDisplay` = Segoe UI Variable Display; `SaFontText` = Segoe UI Variable Text; `SaFontMono` = Cascadia Code, Cascadia Mono, Consolas (fallback ordenado).
Reconciliação: Title 20 é a secção do Manual; TitleCompact 18 é uma variante explícita do estilo Figma; Heading 30 não substitui Display 44. Os estilos locais Figma estão em Manrope (Display/Body/Caption Medium); o Manual prevalece com Semibold/Regular, e os ecrãs usam SF Pro.
Calibração inicial SF Pro → Segoe: manter tamanhos e pesos semânticos; alturas de linha inteiras pelo ecrã 05 quando coincide com Fluent, senão pelo ramp Fluent (Title 28 em vez de 26 e Caption 16 em vez de 18: a métrica vertical de Segoe UI Variable difere da de SF Pro, e o ramp Fluent é a calibração nativa); Display nos tamanhos 20+, Text nos restantes; sem compensação arbitrária de tracking. Não se declara equivalência ótica validada: clipping, quebra de linha e culturas são gate de UI.2 na galeria. Nenhuma fonte Apple é distribuída.

| Chave | Dark = Light = HC | Fonte |
|---|---|---|
| SaSpace4/8/12/16/24/32 | Double 4/8/12/16/24/32 | Manual 107:603 |
| SaRadiusPanel, SaRadiusCard | CornerRadius 24 | Manual 107:603; 112:1010/1222 |
| SaRadiusControl | CornerRadius 12 | Manual; Input 112:3339/3500 |
| SaRadiusPill | CornerRadius 99 | UI.0 §3.1, coleção Figma. **Não usar em WinUI** (UI.2: um raio > h/2 desenha uma elipse); os componentes usam h/2 literal — guard `ComponentsNeverConsumeTheFigmaPillRadius` |
| SaRadiusMiniBar | CornerRadius 3 | 112:1040/1252 |
| SaRadiusSmall | CornerRadius 4 | 112:8522/8708 |
| SaRadiusHealthSegment | CornerRadius 6 | 112:1019/1231 |
| SaBorderThickness | Thickness 1 | Manual pág.07, contorno fino |
| SaDividerThickness | Thickness 0,0,0,1 | Adaptação do separador de 1 DIP |
| SaFocusRingThickness | Thickness 2 | DERIVED - needs Prism check; foco Windows |

Raios: os botões e a navegação de 05 têm raio 20/22/23 = metade da altura (40/44/46 px), ou seja são **pills** → `SaRadiusPill` (variável Figma `radius/pill = 99`). "Controlos 12" do Manual corresponde, em 05, a **inputs e insets** (112:3339) → `SaRadiusControl`. Não há conflito. Nenhum botão foi alterado.

## 5. Materiais / elevação (definidos, não aplicados)

| Chave | Dark | Light | HC | Fonte |
|---|---|---|---|---|
| SaNeutralSurfaceBrush | #242424 | #F7F7F7 | SystemColorWindowColor | Manual |
| SaElevatedSurfaceBrush | #313131 | #FFFFFF | SystemColorWindowColor | DERIVED - needs Prism check; interior Manual / highlight neutro |
| SaGlassSurfaceBrush | Acrylic tint #292929, opacity .43, luminosity .18; fallback #242424 | Acrylic tint #FFFFFF, opacity .42, luminosity .90; fallback #F7F7F7 | Solid SystemColorWindowColor | Paint 112:1010/1222; luminosity DERIVED - needs Prism check |
| SaSidebarMaterialBrush | Acrylic tint #121212, opacity .28, luminosity .18; fallback #141414 | Acrylic tint #FAFAFA, opacity .27, luminosity .90; fallback #E7E7E7 | Solid SystemColorWindowColor | Paint 112:936/1147; luminosity DERIVED - needs Prism check |
| SaModalSurfaceBrush | #1A1A1A opaco | #F7F7F7 opaco | SystemColorWindowColor | 112:8559/8745, opacity .94 elevada a 1 para contraste sustentável |
| SaOpaqueFallbackBrush | #242424 | #F7F7F7 | SystemColorWindowColor | Manual |
| SaOverlaySmokeBrush | #5C000000 | #33000000 | SystemColorWindowColor | 112:8558/8744 |
| SaSurfaceShadow | ThemeShadow sem receivers | igual | não anexar em HC | D7, substituição nativa das sombras Figma. **Nunca mutar** esta instância partilhada (ex.: `Receivers`); cada primitiva cria o seu próprio `ThemeShadow` |

Todas as cores de material vêm de primitivas nomeadas em Color.Primitives (`SaColorElevatedSurface*`, `SaColorGlassTint*`, `SaColorSidebarTint*`, `SaColorModalSurface*`, `SaColorOverlaySmoke*`, mais `SaColorSurface*`/`SaColorCanvas*` para os fallbacks), merged dentro de Elevation.xaml como em Color.Semantic; só as opacidades Acrylic ficam literais.
Sombras medidas em 05 (referência para afinar `Translation.Z` em UI.2, não aplicadas): Dark painéis `0 12px 12px rgba(0,0,0,.2)`, controlos `0 3px 5px rgba(0,0,0,.16)`; Light painéis `0 10px 12px rgba(0,0,0,.06)`, controlos `0 4px 6px rgba(0,0,0,.07)`.

Desvio deliberado D7: Acrylic nativo não simula refração/dispersion GLASS do Figma. TintOpacity não é uma promessa de igualdade ao alpha de um paint; é ponto inicial documentado para galeria. Todos os FallbackColor são opacos. Material desativado/sem transparência deve usar fallback; HC nunca usa Acrylic. ThemeShadow é apenas recurso; a primitiva futura decide receivers/Z e suprime sombra em HC. Não há vidro dentro de vidro nem aplicação automática ao shell.

## 6. Motion (definido, não aplicado)

| Chave | Valor D/L/HC | Fonte / uso |
|---|---|---|
| SaMotionFocusPeakTime | x:String `0:0:0.180` | ServerFullCard.xaml.cs:143; instante (KeyTime) do pico |
| SaMotionFocusFadeEndTime | x:String `0:0:1.600` | :144; instante final, não duração após o pico |
| SaMotionFocusReducedHoldTime | x:String `0:0:2` | :149; hold estático quando animações desligadas |
| SaMotionFadeDuration | x:String `0:0:0.083` | Fluent, opacidade |
| SaMotionFastDuration | x:String `0:0:0.167` | Fluent, entrada/saída direta (= WinUI ControlFastAnimationDuration) |
| SaMotionNormalDuration | x:String `0:0:0.250` | Fluent, deslocação (= WinUI ControlNormalAnimationDuration) |
| SaMotionSlowDuration | x:String `0:0:0.333` | Fluent, deslocação |
| SaMotionLinearKeySpline | x:String `0,0,1,1` | Foco existente / fade |
| SaMotionDirectKeySpline | x:String `0,0,0,1` | Fluent direct (= WinUI ControlFastOutSlowInKeySpline) |
| SaMotionPointToPointKeySpline | x:String `0.55,0.55,0,1` | Fluent point-to-point |

Tipo: `x:String`, o mesmo padrão que o próprio WinUI usa em generic.xaml (`ControlFastAnimationDuration`, `ControlFastOutSlowInKeySpline`), convertido pelo parser no atributo consumidor (`KeyTime`, `Duration`, `KeySpline`). A versão anterior usava elementos `<Duration>`/`<KeySpline>` como recursos: o pulso usa KeyTime (não Duration) e, sem consumidor, esses recursos nunca são instanciados, pelo que um erro só apareceria em UI.2; trocado por um formato comprovado pela plataforma.

Fonte de plataforma: [Microsoft — Motion in Windows](https://learn.microsoft.com/en-us/windows/apps/design/motion/), consultada em 2026-09-30. Consumidores têm de respeitar AnimationsEnabled; definir recursos não altera a animação existente.

## 7. Compatibilidade, cleanup e plano de remoção

**Nenhum alias antigo é reencaminhado em UI.1.** O dicionário `Default` duplicado e as indireções HC de DesignTokens permanecem sem alterações de conteúdo: não há prova runtime suficiente para os remover sem drift. UI.2 deve contraprová-los antes de eliminar duplicação.

### 7.1 Cleanup de código morto — **feito**, com prova

Removidos: `Views/AddServerDialog.xaml`, `Views/AddServerDialog.xaml.cs`, `Views/EditServerDialog.xaml`, `Views/EditServerDialog.xaml.cs`, `Converters/ConnectionStateToBrushConverter.cs` e a linha `<converters:ConnectionStateToBrushConverter x:Key="ConnectionStateToBrushConverter" />` de `Styles/Controls.xaml`.
O csproj não lista Page/Compile destes ficheiros (globbing SDK; só existe `Compile Remove="Qa\**\*.cs"`), logo não foi alterado.
**Chaves resw mantidas** nas 3 culturas (6 por cultura: `AddServerDialog.{Title,PrimaryButtonText,CloseButtonText}`, `EditServerDialog.{…}`): `Controls/ServerEditorModal.xaml.cs:25–27` lê-as com `GetString("AddServerDialog/Title")` etc.

Prova (worktree, antes da remoção):
```bash
git grep -n -E 'AddServerDialog|EditServerDialog|ConnectionStateToBrush' -- . ':!*.resw'
```
Resultado — só: 2 linhas em `docs/ui/ui0-figma-audit.md` (plano); 3 lookups resw em `ServerEditorModal.xaml.cs:25–27` (strings de chave resw, não tipos); as próprias declarações (`x:Class`/`x:Uid` nos 2 XAML, classe + construtor nos 2 `.xaml.cs`, classe do converter) e o registo em `Controls.xaml:7`. **Zero** consumidores em `src/**` (incl. `Qa/**`), `tests/**`, csproj e slnx.
```bash
git grep -n -E 'GetTypes\(|GetExportedTypes|Type\.GetType|Activator\.CreateInstance|ContentDialog|x:Uid|nameof\((Add|Edit)Server' -- tests
```
Resultado: reflexão só em `TrayCapabilityBoundaryTests` e `TrayOwnershipCompletenessTests` (`AppAssembly.GetTypes()`), que são verificações negativas/allowlist (nenhum tipo `WinUIExTray*`; só membros permitidos nomeiam a capability) — remover tipos não pode falhar essas asserções. `CommunityBoundaryGuardTests` só contém as strings proibidas `Type.GetType(`/`Activator.CreateInstanceFrom`. Nenhum teste verifica chaves resw órfãs nem os x:Uid destes diálogos (OnboardingLocalizationTests só lê ServerFormControl.xaml e DashboardPage.xaml). Nenhum XAML/código usa a chave `ConnectionStateToBrushConverter` → remover o registo não muda a resolução de nenhuma outra chave (zero drift).
Contraprova: build Debug e Release e a suite completa verdes **depois** da remoção (§8); smoke `--qa-health` arranca e renderiza.

### 7.2 Gates obrigatórios de UI.2 (das revisões Cortex/Prism de UI.1)

> **Fechado em UI.2** → `docs/ui/ui2-components.md` §4 (F-1, F-3, F-4, F-07, Color.Primitives, Default, G-3, G-4, G-5). Texto abaixo mantido como registo histórico.

UI.2 não adota nenhum token `Sa*` em páginas antes de estes gates estarem verdes:
1. **Resolução runtime (Cortex F-1):** página de galeria Debug / `--qa-tokens` que aplica `RequestedTheme` Dark → Light → HC num host e resolve **todas** as chaves `Sa*Brush`/`Sa*TextStyle` via `{ThemeResource}` em elementos reais; fail-closed se alguma faltar (o merge aninhado de Color.Primitives dentro de ThemeDictionaries ainda não foi instanciado em runtime).
2. **Accent legado (Cortex F-3):** enquanto os overrides globais `SystemAccentColor*`/`AccentFill*` (#1846E1) vivem (até UI.6), cada primitiva UI.2 que aloje/templatize controlos WinUI (CheckBox, ToggleSwitch, RadioButton, ProgressBar, focus visuals, seleção) sobrepõe localmente os recursos dependentes do accent com `Sa*`; a galeria verifica visualmente "sem #1846E1". Alternativa: antecipar a remoção do override global com scan de zero referências. Registar a decisão aqui.
3. **Motion a partir de C# (Cortex F-4):** o único consumidor (pulso em `ServerFullCard.xaml.cs`) é C#; `Resources["SaMotion*"]` devolve `string`. Ao migrar: acessor `MotionTokens` com parse invariante (`TimeSpan.Parse(..., CultureInfo.InvariantCulture)` e KeySpline), com teste unitário e falha explícita em formato inválido; a galeria inclui um storyboard XAML que consome cada `SaMotion*`; `AnimationsEnabled` continua respeitado.
4. **Smoke HC (Prism F-07):** `SaOverlaySmokeBrush` em HC é `SystemColorWindowColor` opaco (igual ao legado `ModalSmokeBrush`, logo sem drift) e esconde o contexto por trás do modal; validar na galeria com HC ativo; alternativa `Transparent` em HC, deixando a borda `SystemColorWindowTextColor` do diálogo separar.

**UI.2 follow-ups (revisão Cortex dos guards UI.1; registados, não implementados em UI.1):**
- **G-3 — contrato XAML para nomes resolvidos em runtime:** alargar a deteção de consumidores do inventário a `Storyboard.TargetName`/`TargetName`, `Setter Target="X.Prop"` de VisualState e literais `FindName("X")`/`GetTemplateChild("X")` em qualquer `.cs`, antes de as primitivas UI.2 introduzirem VisualStates.
- **G-4 — "só doubles QA" prova apenas o delta do harness:** renomear para `…HarnessDeltaRegistersOnlyQaDoubles` ou, quando existir um seam de flag, testar a composição completa via `App.ConfigureApplicationServices` e verificar que persistência/credenciais/SSH resolvidos são QA ou isolados.
- **G-5 — determinismo de screenshots:** o ficheiro de placement isolado em `%TEMP%\ServerMonitor-QA\<harness>\` persiste entre execuções; apagá-lo no `Apply` (ou usar subpasta por processo) e, para `--qa-store-screenshot`, considerar placement fixo em memória como `QaCompactPlacementStore`.

Plano por milestone (não é autorização para executar agora):
- UI.2: primitivas/galeria adotam Sa*; validar HC/fallback, contraste e calibração; rever Default/converter; manter aliases com consumidores.
- UI.3: History/Workloads migram status/texto/superfícies; remover aliases específicos apenas com zero referências e testes.
- UI.4: overview/lista migram cores/spacing; preservar unknown/stale.
- UI.5: detalhe/settings migram superfícies, mantendo backup e ligações existentes.
- UI.6: shell/onboarding migram canvas/sidebar/navegação; rever overrides de accent global.
- UI.7: editor/diálogos migram input/focus/modal; reavaliar diálogos mortos, conservar resw lido.
- UI.8: compacto migra últimas medidas/brushes de cards; validar DPI.
- UI.9: widgets só após spike; retirar aliases restantes apenas após busca global + testes + QA. SystemAccent* pode ser removido como override (voltar à plataforma), nunca silenciosamente remapeado.

## 8. Verificação e handoff

Comandos exatamente como o CI (a `.slnx` já mapeia os projetos Windows para x64; **sem** `-p:Platform`, que falha com MSB4126), executados no worktree depois de todas as correções e do cleanup:

```bash
~/.dotnet/dotnet build ServerMonitor.slnx -c Debug --no-incremental --disable-build-servers
~/.dotnet/dotnet test ServerMonitor.slnx -c Debug --no-build --disable-build-servers
~/.dotnet/dotnet build ServerMonitor.slnx -c Release --no-incremental --disable-build-servers
```

| Verificação | Resultado |
|---|---|
| Debug build | PASS, 0 erros / 10 avisos (CS8524, xUnit2031 — pré-existentes, fora dos ficheiros tocados) |
| Debug testes | **3664 passed, 0 failed, 0 skipped** — ActivationContract 38, WidgetContract 69, Features 52, Collectors 342, Core 626, WidgetProvider 295, App 1404, Infrastructure 838 |
| Release build | PASS, 0 erros / 10 avisos (mesmas categorias) |
| Novo namespace | 154 chaves únicas `Sa*`; interseção com chaves XAML existentes = só os nomes de tema `Dark`/`Light`/`HighContrast` (chaves de ThemeDictionaries, não recursos) |
| Theme dictionaries | Paridade de chaves Dark/Light/HC em Color.Semantic (26) e Elevation (7) |
| Legado | `git diff -- src/ServerMonitor.App/Styles/DesignTokens.xaml` vazio; Controls.xaml só perde a linha do converter morto |
| Runtime | `ServerMonitor.App.exe --qa-health --qa-ui-language pt-PT` (Debug), PID próprio **43820**: vivo e `Responding` após 12 s, janela "ServerAlyzer" 1400×900, dashboard QA (Healthy/Warning/Critical) renderizado em pt-PT com o visual legado; sem evento de crash no Application log; parado por PID exato após verificar caminho + linha de comando |
| AppData | `window-placement.json` copiado antes para `.boss/evidence/ui1/guard/window-placement.json.bak`; SHA-256 antes = depois = backup = `924F663A…A3AB55`; **nenhum** ficheiro em `%LocalAppData%\ServerMonitor` mudou (hash de todos antes/depois), restauro desnecessário |
| Light, HC, pt-BR/en-US, 560×640, Narrator | NOT_RUN (Beacon) |
| Zero drift por screenshots | Pendente Beacon |

Limite do smoke: o arranque prova que todos os dicionários (incl. o merge aninhado `ms-appx:///Styles/Tokens/Color.Primitives.xaml`) carregam sem erro de parse e que as chaves antigas continuam a resolver. **Não** prova a resolução dos brushes `Sa*` nas ThemeDictionaries nem dos `Sa*TextStyle`, porque em UI.1 nada os consome (por desenho) e o WinUI instancia recursos de forma diferida. Recomendado: teste de Atlas / galeria UI.2 que faça lookup de cada chave `Sa*` em Dark, Light e HC.

Output app executado: `src/ServerMonitor.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/ServerMonitor.App.exe` (neste worktree).
Logs locais (ignorados pelo git via `*.log`): `docs/ui/ui1-build-debug.log`, `ui1-test-debug.log`, `ui1-build-release.log` (substituem os da tentativa anterior; o antigo `ui1-test-release.log` foi removido por não corresponder a esta execução).
Figma: os valores derivados (bordas, hover, selected, disabled, materiais) e os node ids citados vêm da leitura MCP do implementador anterior; **não** foram re-lidos nesta continuação. Os valores oficiais do Manual foram conferidos um a um contra a instrução aprovada (todos corretos).

## 9. Mapa completo de aliases legados

Mapa de todas as 69 chaves de DesignTokens.xaml (UI.8 removeu `TitleBarSurfaceBrush`, sem consumidores). Valores antigos continuam no ficheiro original intacto. Correspondência semântica não significa igualdade de cor/tamanho.

| Chave antiga | Destino futuro (não aplicado) | UI.1 |
|---|---|---|
| AccentColor | Retirar override após migração global; sem novo accent global | Mantido, valor antigo |
| AccentFillColorDefaultBrush | Estilo neutro UI.2 com SaSelected/Hover/Text/DisabledSurface; sem alias direto | Mantido, valor antigo |
| AccentFillColorDisabledBrush | Estilo neutro UI.2 com SaSelected/Hover/Text/DisabledSurface; sem alias direto | Mantido, valor antigo |
| AccentFillColorSecondaryBrush | Estilo neutro UI.2 com SaSelected/Hover/Text/DisabledSurface; sem alias direto | Mantido, valor antigo |
| AccentFillColorSelectedTextBackgroundBrush | Estilo neutro UI.2 com SaSelected/Hover/Text/DisabledSurface; sem alias direto | Mantido, valor antigo |
| AccentFillColorTertiaryBrush | Estilo neutro UI.2 com SaSelected/Hover/Text/DisabledSurface; sem alias direto | Mantido, valor antigo |
| AccentSoftBrush | Estilo neutro UI.2 com SaSelected/Hover/Text/DisabledSurface; sem alias direto | Mantido, valor antigo |
| AccentTextBrush | Estilo neutro UI.2 com SaSelected/Hover/Text/DisabledSurface; sem alias direto | Mantido, valor antigo |
| AppBackgroundBrush | SaCanvasBrush | Mantido, valor antigo |
| BrandAccentBrush | Estilo neutro UI.2 com SaSelected/Hover/Text/DisabledSurface; sem alias direto | Mantido, valor antigo |
| BrandAccentHoverBrush | Estilo neutro UI.2 com SaSelected/Hover/Text/DisabledSurface; sem alias direto | Mantido, valor antigo |
| BrandAccentPressedBrush | Estilo neutro UI.2 com SaSelected/Hover/Text/DisabledSurface; sem alias direto | Mantido, valor antigo |
| ComboBoxBorderBrushFocused | SaFocusRingBrush | Mantido, valor antigo |
| ComboBoxBorderBrushFocusedPointerOver | SaFocusRingBrush | Mantido, valor antigo |
| ContentDialogBackground | SaModalSurfaceBrush | Mantido, valor antigo |
| ContentDialogBorderBrush | SaBorderSubtleBrush | Mantido, valor antigo |
| ContentDialogCommandsBackground | SaModalSurfaceBrush | Mantido, valor antigo |
| ContentDialogPrimaryButtonBackground | Estilo DialogShell UI.2 com SaText/Selected/Surface; sem alias direto | Mantido, valor antigo |
| ContentDialogPrimaryButtonBackgroundPointerOver | Estilo DialogShell UI.2 com SaText/Selected/Surface; sem alias direto | Mantido, valor antigo |
| ContentDialogPrimaryButtonBackgroundPressed | Estilo DialogShell UI.2 com SaText/Selected/Surface; sem alias direto | Mantido, valor antigo |
| ContentDialogPrimaryButtonForeground | Estilo DialogShell UI.2 com SaText/Selected/Surface; sem alias direto | Mantido, valor antigo |
| ContentDialogPrimaryButtonForegroundPointerOver | Estilo DialogShell UI.2 com SaText/Selected/Surface; sem alias direto | Mantido, valor antigo |
| ContentDialogPrimaryButtonForegroundPressed | Estilo DialogShell UI.2 com SaText/Selected/Surface; sem alias direto | Mantido, valor antigo |
| ContentDialogSeparatorBorderBrush | SaBorderSubtleBrush | Mantido, valor antigo |
| ContentDialogSmokeFill | SaOverlaySmokeBrush | Mantido, valor antigo |
| GlassBorderBrush | SaGlassBorderBrush | Mantido, valor antigo |
| GlassBorderThickness | SaBorderThickness | Mantido, valor antigo |
| GlassCardPadding | Composição SaSpace* por primitiva; sem substituição automática | Mantido, valor antigo |
| GlassHighlightBrush | SaGlassHighlightBrush | Mantido, valor antigo |
| GlassSurfaceBrush | SaGlassSurfaceBrush | Mantido, valor antigo |
| ModalSmokeBrush | SaOverlaySmokeBrush | Mantido, valor antigo |
| MutedSurfaceBrush | SaInteriorBrush | Mantido, valor antigo |
| PagePadding | Composição SaSpace* por página; sem substituição automática | Mantido, valor antigo |
| ProgressBarPrimaryBrush | SaCpuBrush / SaMemoryBrush / SaDiskBrush conforme métrica | Mantido, valor antigo |
| ProgressRingPrimaryBrush | SaCpuBrush / SaMemoryBrush / SaDiskBrush conforme métrica | Mantido, valor antigo |
| RadiusLarge | SaRadiusCard (16 → 24) | Mantido, valor antigo |
| RadiusMedium | SaRadiusControl | Mantido, valor antigo |
| RadiusSmall | SaRadiusSmall (não equivalente: 8 → 4) | Mantido, valor antigo |
| RadiusXLarge | SaRadiusPanel (20 → 24) | Mantido, valor antigo |
| SpacingL | SaSpace16 | Mantido, valor antigo |
| SpacingM | SaSpace12 | Mantido, valor antigo |
| SpacingS | SaSpace8 | Mantido, valor antigo |
| SpacingXL | SaSpace24 | Mantido, valor antigo |
| SpacingXS | SaSpace4 | Mantido, valor antigo |
| SpacingXXL | SaSpace32 | Mantido, valor antigo |
| StatusCriticalBrush | SaErrorBrush | Mantido, valor antigo |
| StatusHealthyBrush | SaHealthyBrush | Mantido, valor antigo |
| StatusOfflineBrush | SaOfflineBrush | Mantido, valor antigo |
| StatusUnknownBrush | SaTextSecondaryBrush + estado explícito | Mantido, valor antigo |
| StatusWarningBrush | SaAttentionBrush | Mantido, valor antigo |
| SystemAccentColor | Retirar override após migração global; sem novo accent global | Mantido, valor antigo |
| SystemAccentColorDark1 | Retirar override após migração global; sem novo accent global | Mantido, valor antigo |
| SystemAccentColorDark2 | Retirar override após migração global; sem novo accent global | Mantido, valor antigo |
| SystemAccentColorDark3 | Retirar override após migração global; sem novo accent global | Mantido, valor antigo |
| SystemAccentColorLight1 | Retirar override após migração global; sem novo accent global | Mantido, valor antigo |
| SystemAccentColorLight2 | Retirar override após migração global; sem novo accent global | Mantido, valor antigo |
| SystemAccentColorLight3 | Retirar override após migração global; sem novo accent global | Mantido, valor antigo |
| TextControlBorderBrushFocused | SaFocusRingBrush | Mantido, valor antigo |
| TextControlBorderBrushFocusedPointerOver | SaFocusRingBrush | Mantido, valor antigo |
| TextPrimaryBrush | SaTextBrush | Mantido, valor antigo |
| TextSecondaryBrush | SaTextSecondaryBrush | Mantido, valor antigo |
| TextTertiaryBrush | SaTextSecondaryBrush (rever contraste) | Mantido, valor antigo |
| WindowBackdropTintBrush | SaSidebarMaterialBrush | Mantido, valor antigo |
| WorkloadDotCriticalBrush | SaErrorBrush | Mantido, valor antigo |
| WorkloadDotHealthyBrush | SaHealthyBrush | Mantido, valor antigo |
| WorkloadDotNeutralBrush | SaTextSecondaryBrush | Mantido, valor antigo |
| WorkloadDotWarningBrush | SaAttentionBrush | Mantido, valor antigo |
| WorkloadStateTextCriticalBrush | SaErrorBrush | Mantido, valor antigo |
| WorkloadStateTextWarningBrush | SaAttentionBrush | Mantido, valor antigo |

## 10. Revisão crítica da entrega anterior (continuação)

| # | Achado | Correção |
|---|---|---|
| 1 | `Color.Semantic.xaml` usava `{StaticResource SaColor*}` definidos num dicionário **irmão** (Color.Primitives) em App.xaml — lookup entre merged dictionaries irmãos durante o parse não é garantido em WinUI; como nada consome os brushes, a falha só surgiria em UI.2 | Color.Primitives passou a ser merged **dentro** de Color.Semantic (`ms-appx:///…`) e saiu de App.xaml |
| 2 | Motion usava `<Duration>`/`<KeySpline>` como recursos (formato não comprovado como recurso; o pulso usa KeyTime) | `x:String`, como o generic.xaml do WinUI; KeySplines renomeadas `Sa*KeySpline` |
| 3 | Cleanup mantido por "dúvida sobre reflexão", sem inspecionar os testes refletidos | Testes inspecionados (asserções negativas/allowlist); prova completa em §7.1; ficheiros removidos |
| 4 | App.xaml com terminações de linha mistas (CRLF + LF) | Normalizado para CRLF |
| 5 | Valores do Manual, prefixo, paridade de temas, ausência de estilos implícitos, DesignTokens intacto | Verificados, sem alteração |

Pendências: dicionário `Default` duplicado e indireções HC de DesignTokens continuam por tratar (UI.2, com contraprova); em HC o `SaOverlaySmokeBrush` é opaco (SystemColorWindowColor) — confirmar na galeria se é o comportamento desejado.

## 11. Correções da revisão UI.1 (Prism + Cortex, sobre c603efb)

| Achado | Resolução |
|---|---|
| Prism F-01 hover sem fonte | `SaColorHover*` = `#1E525252` / `#40FFFFFF` (Selected a metade do alfa), marcado "DERIVED – no Figma source, validate in UI.2 gallery"; citação enganadora removida (§3) |
| Prism F-02 contorno/highlight glass | `SaColorGlassBorder*`, `SaColorGlassHighlight*` + `SaGlassBorderBrush` (HC WindowText), `SaGlassHighlightBrush` (HC Transparent); mapa §9 atualizado |
| Prism F-03 line-heights | Inteiros 56/40/36/28/24/20/16/16/14/16, fonte por linha (§4) |
| Prism F-04 texto de controlo | `SaFontSizeControl` 13, `SaLineHeightControl` 18, `SaControlTextStyle` (Text, Medium) |
| Prism F-05 Divider = border | Chaves Divider mantidas; `SaBorderInsetBrush` alias com o mesmo valor; dois usos anotados |
| Prism F-06/F-08/F-09/F-10 | Correções de documentação (§3 DisabledText, §4 raios pill, §3 coleção antiga, §5 sombras medidas) |
| Prism F-07, Cortex F-1/F-3/F-4 | Gates explícitos de UI.2 (§7.2) |
| Cortex F-2 literais em Elevation | Primitivas nomeadas em Color.Primitives; Primitives merged em Elevation.xaml; só opacidades Acrylic literais |
| Cortex F-5 hover em HC | `SaHoverTextBrush` nos 3 temas (Dark/Light = Text; HC HighlightText) |
| Cortex F-7 sombra partilhada | Nota "nunca mutar" no XAML e §5 |

Zero drift mantido: só ficheiros `Styles/Tokens/**` e este documento mudaram; nenhuma chave existente nem consumidor XAML alterado. Runtime NÃO relançado nesta ronda (QA do Beacon em curso); §8 Runtime refere-se ao smoke anterior sobre a versão pré-revisão.
