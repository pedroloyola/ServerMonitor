# UI.8 — Modo compacto

**Estado: READY_TO_MERGE** (2026-10-08).
- **Branch:** `ui/ui8-compact-mode`, base `983994d`. PR #29. Código validado em `71642e9`; o commit deste documento acrescenta só docs.
- **CI do PR:** verde em `1411a0b` (run 37709848692), `c642464` (run 37714439705), `fccfada` (run 37723353648) `6a2eabd` (run 37727158663: attempts 1–2 vermelhos por flakes conhecidos fora do UI.8 — `FLAKE-WP-GUID` em WidgetProvider.Tests e `CI-FLAKE-WALLCLOCK-2` em Core.Tests, ambos com 0 linhas do UI.8 —, attempt 3 verde) e `71642e9` (run 37738582212).
- **Âmbito:** o Modo compacto foi reconstruído sobre as fundações UI.0–UI.7, segundo o Figma vivo (página 05 · secção 15), como **vista sobre o estado existente**. Não há segundo engine, polling, repositório, classificação de health nem persistência nova. Não foram tocados: MonitoringEngine, SSH, credenciais, trust, formatos persistidos, ordem de arranque, lifecycle do tray, notificações, WidgetProvider/contrato do snapshot (UI.9), Store/pacote e StartupTask (REL.0).
- **Execução:** um PR em três blocos: 8A (harness + lógica de apresentação), 8B (envelope da janela + correção da ativação; gate Cortex) e 8C (XAML/Figma, tokens, copy, a11y, remoção do legado, perf/fugas). Depois, as rondas c0–c4.

**Autoridade visual:** Figma `Qvk5dUFgsWf4UOYzfAkiDV`, página 05, secção 15 · Windows Modo compacto. Precedência: invariantes de produto/segurança > a11y/plataforma > Figma.

| Alvo | Escuro | Claro |
|---|---|---|
| Janela populada (normal + atenção + sem ligação) | `112:9397` | `112:9589` |
| Linhas saudável / atenção / sem ligação | `112:9439` / `112:9463` / `112:9559` | `112:9631` / `112:9655` / `112:9751` |
| Vazio (janela / bloco) | `112:9781` / `112:9818` | `112:9842` / `112:9879` |
| Barra de título / resumo / rodapé | `112:9398` / `112:9434` / `112:9580` | `112:9590` / `112:9626` / `112:9772` |

## 1. Arquitetura

**Antes (medido em `983994d`).** O Compact era uma segunda apresentação da MESMA `MainWindow`. Ligava-se ao mesmo `DashboardViewModel` singleton e aos mesmos `ServerCardViewModel` (`VisibleServers`), com um `ServerCompactCard` por servidor num `ItemsControl` não virtualizado e ~25 `{Binding}` reflexivos por cartão. A fonte de estado estava limpa. Os defeitos eram de apresentação e de shell:
- copy de health legada ("Offline") e leitura retida de um servidor offline sem marca;
- um único estado vazio para "a carregar", "nenhum", "todos ocultos" e "configuração indisponível";
- barras azuis de marca;
- `HealthToBrushConverter`, que não reagia à troca de tema;
- uma ativação (deep-link, Definições do tray, toast de background) feita com o Compact visível, ou num processo headless com `mode=Compact` persistido, navegava por trás do Compact.

O harness `--qa-compact` não arrancava (cast `IServerLoadStatusSource`).

**Depois.**
- `CompactPresentationViewModel` (singleton) constrói-se a partir de `VisibleServers`, reconstrói em `ServersReloaded` (nunca no Reset), reencaminha o `UpdatedAgoDisplay` já coalescido do Dashboard, deriva o estado do corpo (Loading > List > ConfigurationUnavailable > AllHidden > Empty) e faz `Dispose` das linhas. Não recebe `IWindowModeCoordinator`.
- `CompactServerRowViewModel` é uma vista read-only sobre o MESMO `ServerCardViewModel`. Guarda os valores **apresentados** e só notifica o que mudou: uma amostra idêntica gera 0 notificações.
- `ServerMetricPresentation` foi extraído da linha UI.4 e é a única fonte das regras de métrica: Offline → "—" (`RowHidesRetainedMetrics`), desconhecido → "—" (nunca 0), severidade pelos mesmos `MonitoringThresholds` do engine. As linhas UI.4 delegam nele, sem edição de asserções.
- `CompactShell` (UserControl dentro de `CompactRoot`): `x:Bind`, `ItemsRepeater` com `StackLayout Spacing=8` (virtualização nativa), lista com uma paragem de Tab e setas.
- Os nomes de contrato `CompactRoot/CompactCaptionColumn/CompactDragRegion/CompactBody` mantêm-se.

**Saídas do Compact** (`WindowModeViewModel`). Linha → Detalhe, vazio → Adicionar e todos ocultos → Gerir servidores ocultos chamam `RestoreAndActivateStandard()` e depois o comando existente. A guarda de saída do UI.7 corre uma vez, já em Standard. O id da linha chega por `CommandParameter="{x:Bind ServerId}"` (B-1: sob `ItemsRepeater` + `x:Bind` o `DataContext` da linha é `null`). Voltar do Detalhe → Visão geral em Standard (deliberado). `mode=Standard` fica persistido.

**Ativação** (`StandardSurfacing`): materializar → (navegar escondido, só no caso Background) → comutar para Standard → mostrar (numa janela minimizada: mostrar → comutar). Usam-na `ExecuteActivationIntent`, `OpenSettings` e `OpenBackgroundSettings`. Preservam o modo, inalterados: `RestoreAndActivate` (tray "Abrir", notificação `OpenDashboard`, `RestoreOnRedirect` da 2.ª instância) e `ToggleCompactMode`. Se a comutação for ignorada, `Run` devolve `false` (N-1).

**Janela.**
- Envelope do Compact em **DIP** por DPI do monitor alvo: default/máx. 432×704, mín. 320×340, área cliente medida (frame não-cliente compensado).
- Redimensionável dentro do envelope e não maximizável. A imposição manual de mín./máx. vale para o modo ativo; ao voltar a Standard repõem-se os limites Standard.
- Um Compact maximizado (Maximize da legenda, duplo clique no título, Win+↑) é reposto em `Restored` antes de qualquer captura, e o placement nunca grava um Compact maximizado.
- Sair de um Standard **maximizado** (B-7): `Restore()` antes de aplicar os bounds do Compact (dentro de `IsApplyingBounds`); uma janela maximizada nunca é capturada como bounds de um modo; ao voltar pelo "Expandir" o Standard volta a maximizar (estado só de sessão, nenhum campo persistido; depois de reiniciar abre no rect restaurado).
- O Standard continua em px físicos (backlog `WINDOW-STANDARD-DIP`). Formato persistido inalterado. O 348×420 legado mantém-se a 100 %; a 150 % é elevado ao mínimo.

**Seams UI.9 preservados:** `WidgetSnapshotRecorder`/`WidgetSnapshotMapper`/formato JSON e o contrato de ativação (`serveralyzer://server/{id}` → Standard → Detalhe) intactos. Nenhum tipo do Compact é referenciado pelo WidgetProvider.

## 2. Decisões (D-UI8-n, SPEC + revisões Cortex RC-1…RC-10 e Prism R-1…R-12)

| ID | Decisão |
|---|---|
| D-UI8-1 | Envelope 432×704 (Figma), Compact redimensionável no envelope (sem isso o 348×420 persistido ficaria preso) |
| D-UI8-2 | Tamanho não dirigido pelo conteúdo; estados centrados |
| D-UI8-3 | Legendas nativas; "Expandir" à esquerda da reserva; só-ícone por **medição** (limiar medido pt-PT 393/392, en-US 386/385 DIP) |
| D-UI8-4 | Regra da linha = `ServerStatusPresentation` (Offline → "—"; stale não-offline → valores atenuados + "Última atualização há N"); estado por `StatusKey` ("Sem ligação"), nunca `ServerHealth*` |
| D-UI8-5 | Barras/valores neutros; Warning → `SaAttentionTextBrush`, Critical → `SaDangerTextBrush` (valor e barra) só acima do limiar real; sufixo acessível "em atenção"/"crítico" |
| D-UI8-6 | Estados não desenhados derivados das flags existentes (a carregar, todos ocultos com "Gerir servidores ocultos", configuração indisponível sem ação); sem CTA falso |
| D-UI8-7/8 | Linha → Detalhe e "Adicionar servidor" implementados pelo caminho de ativação existente |
| D-UI8-9 | Entrar em Compact com o editor sujo: o editor fica escondido e intacto (comportamento anterior) |
| D-UI8-10 | Correção da ativação (`StandardSurfacing`) |
| D-UI8-11 | Rodapé "Sempre no topo" + switch = a mesma preferência das Definições |
| D-UI8-12 | Material do shell (`SaShellWindowBackgroundStyle`, fallback opaco/HC); tema ao vivo sem reiniciar |
| D-UI8-13 | Tokens `SaCompact*` (D/L/HC), zero literais de cor no XAML |
| D-UI8-14 | Copy pt-PT ("tu") / pt-BR / en-US |
| D-UI8-15 | Legado removido só com prova de zero-uso |
| Foco | Entrada → 1.ª linha **visível** (mantém o scroll), senão a ação real do estado, senão "Expandir"; anel só se a entrada foi por teclado (`FocusState.Pointer`, medido: `Programmatic` pintava o anel); nunca rouba foco a uma janela inativa; saída → título da página |

## 3. Desvios deliberados Figma ↔ produto (DV-n)

DV-1 legendas nativas sem r28/contorno/sombra; o glifo Maximize fica **visível e inerte** (o host de caption do WinAppSDK não segue `WS_MAXIMIZEBOX`; o gesto é neutralizado; backlog `COMPACT-CAPTION-MAXGLYPH`) · DV-2 "Expandir" abraça o conteúdo · DV-3 cores de estado do Manual · DV-4 cenário `figma` DISCO 88 (92 é Crítico pelo engine) · DV-5 hover/pressed/foco derivados · DV-6 ponto `SaStatusIndicator` partilhado + rótulo separado · DV-7 altura de linha 14 · DV-8 marca 16/22 · DV-9 raio do trilho 3 · DV-10 faixa stale extra · DV-11 estados derivados · DV-12 vazio centrado numa janela de 704 · DV-13 ícone 28 literal · DV-14 switch 44×26 · DV-15 scrollbar de sobreposição · DV-16 foco na 1.ª linha ao entrar · DV-17 "Atualizado…" alinhado à direita · DV-18 tooltip do nome só quando truncado.

## 4. Legado removido (prova de zero-uso)

`ServerCompactCard` (.xaml/.cs), `HealthToBrushConverter`, `CompactChromeToggleStyle`, `SaLegacyWindowBackgroundStyle`, `TitleBarSurfaceBrush`, `WindowBackdropTintBrush`, `HealthDisplayName` (o `ServerCardViewModel` emite 30 notificações por amostra em vez de 31), `ServerHealth*` e `CompactMetricsUnavailable` (resw). Ratchets baixados: FontSize 96 → 74; geometria CornerRadius 17 → 8, Padding 18 → 12. Recursos partilhados mantidos.

## 5. Harness `--qa-compact` (Debug-only, `Qa\**` excluído de Release)

`--qa-compact` + modificadores estritos (cada um uma vez): `--qa-compact-scenario <empty|one|many|mixed|figma|offline|stale|loading|all-hidden|config-unavailable|long-names|n20|n100|n200>`, `--qa-compact-start compact|standard`, `--qa-compact-ticker identical|varying`, `--qa-activation=dashboard|server:<n>`. Qualquer outro valor, duplicado ou modificador órfão → exit 3. A forma `--qa-compact:N` foi retirada. Usa relógio fixo, ids determinísticos e placement em memória (nunca o ficheiro real). O ticker publica no store real (`TimeProvider` injetado). `tools/qa/Start-QaApp.ps1` espelha as regras, e um corpus diferencial de 722 casos compara o launcher com o `LaunchRefusal` da app.

## 6. Medições (harness, 96 DPI)

| | legado (`32203dd`) | UI.8 |
|---|---|---|
| n200, ticker variável — CPU | 45,0 % de 1 núcleo | **5,0 %** (Beacon: 4,8 %) |
| n200 — elementos UIA / linhas realizadas | 2412 / todas | 28 / 13 |
| repouso — CPU | 0 | 0 |
| n200 — privado | 309 MB | 70 MB |

**Transições repetidas:** 60 ciclos Full↔Compact (Sentry) → +1,0 MB privados, handles/USER estáveis, 1 janela da app, 1 host de tray. Beacon: 55 ciclos (+5 MB), 20/20 e 10/10 ciclos (1 janela, 1 tray). Contagens de handlers por testes (K ciclos/rebuilds → constantes). Nenhum timer novo.

## 7. Testes e contraprovas

- **Canónico `ServerMonitor.slnx` @ `71642e9`:** Debug 5734 passed / 0 failed (App.Tests 3380), Release 5271 passed / 0 failed (App.Tests 2917), 1 skip pré-existente (Infrastructure). Baseline `983994d`: 5482.
- **Contraprovas do implementer (BOSS §10):** 86/86 mortas (8A/8B 25, 8C 14, c0 4, c1 8, c2 22, c3 6, c4 7), restauro byte-exato e rebuild `--no-incremental`. Evidência: `.boss/evidence/ui8/mutations-*.json`.
- **Atlas (independente, Codex) @ `6a2eabd`:** reproduziu Debug 5723/0/1 e Release 5260/0/1 (App.Tests 3369 / 2906). 21 mutações próprias contra componentes reais, 21 mortas e 0 sobreviventes (A-M01a…A-M14, incluindo 2 sobre o B-6). 10 corridas consecutivas das classes UI.8, 289/289 cada, 0 falhas e 0 skips. Varrimento de wall-clock limpo.

## 8. Reviews

| Reviewer | Resultado |
|---|---|
| Cortex (arquitetura) | SPEC APPROVED_WITH_CHANGES → gate 8B APPROVED_WITH_NITS (N-1/N-2/N-4 fechados, N-3 aceite pela medição) → c1 APPROVED_WITH_NITS (C1-N1/N2 opcionais, C1-N3 confirmado em runtime, C1-N4 = NOT_RUN abaixo) → c2 (delta c1–c3) APPROVED_WITH_NITS (C2-1 → verificação runtime → B-7) → c3 (delta c4) **APPROVED_WITH_NITS** (C3-1 Minor não bloqueante, abaixo) |
| Prism (Figma) | SPEC APPROVE_WITH_REQUIRED_CHANGES → c1 CHANGES_REQUIRED (P-1 linha 77, P-2 anel de foco, P-3…P-8) → c2 **APPROVED_WITH_NITS** |
| Beacon (runtime) | c1 CHANGES_REQUIRED (**B-1 linha não abria o Detalhe**, B-2 maximize) → c2 CHANGES_REQUIRED (B-6 foco com a lista deslocada) → c3 APPROVED_WITH_NITS → c4 CHANGES_REQUIRED (**B-7 Major: Standard maximizado → Compact fora do envelope e bounds corrompidos**) → c5 **APPROVED** |
| Atlas (testes) | fase 1 CHANGES_REQUIRED (A-1…A-4 → fechados no c2) → fase 2 **APPROVED_WITH_NITS** (N-1 glifo Maximize = DV-1/backlog; N-2 divergência launcher pré-UI.8 = backlog) |
| Vigil | não usado: o UI.8 não cruzou nenhuma fronteira de segurança/dados |

## 9. NOT_RUN (com razão)

- DPI 125/150/200 % real (S-10) e Alto Contraste real (S-11): definições globais do Windows; cobertos por testes unitários (envelope) e tokens HC sondados. → UI.12.
- Fallback opaco do material (X-7): o harness não o força sem alterar definições globais.
- Narrador real → UI.12.
- Tray "Definições" em Compact, toast de background com o Compact minimizado, 2.ª instância e snap Win+setas: não conduzíveis por automação no harness (a 2.ª instância exige o build normal). Cobertos por `StandardSurfacingTests` e pelos testes de ordem do `TrayAffordanceLifecycle`. Recomendação Cortex/Prism: passagem manual curta antes do release.
- Passagens de layout/frames e "depois de GC": sem instrumentação QA nova.

## 10. Dados reais, Firewall, processos

- Dados reais (servidores, routed, known-hosts, routed trust, Credential Manager, `~/.ssh`, settings, backups, window-placement): **0 diffs** em todos os snapshots (`realdata-before` vs após 8AB, 8C, c1, c2, c3). O placement real nunca foi tocado: harness em memória.
- Firewall: 4 regras "Query User" Inbound Allow do `testhost.exe` de `Infrastructure.Tests` do Floor UI.8 (Debug/Release, TCP/UDP). Names exatos em `.boss/evidence/ui8/firewall-ui8-floor-after-8ab.csv`. As regras de `%TEMP%` são pré-existentes e não foram tocadas.
- 0 processos residuais da app depois de cada janela de QA.

## 11. Backlog

`WINDOW-STANDARD-DIP` · `WINDOW-PRESENTER-LIMITS-DPI` (provar a unidade de `PreferredMinimum/Maximum*` num ecrã ≥ 125 %) · `COMPACT-CAPTION-MAXGLYPH` · Compact Home/End na lista (B-3) · Shift+Tab entra na última linha realizada (B-4, aceite) · launcher aceita `--qa-health` + `--qa-activation/--qa-start` (pré-UI.8, a app recusa) · `ServerCardViewModel` partilhado com 30 notificações incondicionais (não alterado sem pedido) · **COMPACT-EXIT-MAXIMIZE** (Cortex C3-1: saídas do Compact por `RestoreAndActivateStandard` — linha → Detalhe, Adicionar, Gerir ocultos, deep-link, Definições — provavelmente perdem o estado maximizado porque `ShowAndActivate` põe `WindowState = Normal`; rect correto, nada corrompido; melhor do que `main`, que nunca re-maximizava; correção de uma linha: Normal só se minimizado) · C2-3 anel após entrada pelo tray depois de input de teclado · C2-4 espera de foco armada até ao próximo passe se o Compact for escondido a meio · `FLAKE-WP-GUID` (recorreu no CI do PR) · **`CI-FLAKE-WALLCLOCK-2` REABERTO** (recorreu no CI do PR; decisão humana pendente) · Debug não byte-determinístico (B-5: proveniência = HEAD + árvore limpa + build canónico).

## 12. Oportunidades de motion (UI.11, nada implementado)

Transição Full↔Compact (hoje instantânea), entrada/saída do bloco de estado, interpolação da barra por amostra, aparecimento do cue stale, fade do véu de hover/pressed e troca texto↔só-ícone do "Expandir".
