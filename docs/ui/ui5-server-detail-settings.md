# UI.5 — Server Detail, Definições, Dados e servidores

**Estado: COMPLETE** (2026-10-04).
- **Merge:** PR #23 integrado por merge commit (GO humano), fixado ao head `b0a4130` → `main` = `4abed58` (pais `5168662` + `b0a4130`).
- **CI pós-merge:** verde à primeira tentativa (run 37162051830).
- **Branch:** `ui/ui5-server-detail-settings` (mantida), base `5168662`. HEAD de código validado `e9fcfab`; `b0a4130` é só de documentação.
- **Floor UI.5:** removido depois do merge (sem branch apagada). Confirmado nas três provas: `maestri floor list`, `git worktree list` e diretório inexistente.
- **Reviews finais:** Cortex c5 APPROVED, Prism c3 APPROVED, Beacon c3 APPROVED, Atlas c6 APPROVED.

**Autoridade visual:** Figma `Qvk5dUFgsWf4UOYzfAkiDV`. Precedência: invariantes/segurança > a11y/plataforma > Figma.

| Página | Escuro | Claro | Primeira leitura |
|---|---|---|---|
| Server Detail | `112:1785` | `112:2022` | `112:16007` / `112:16164` |
| Definições | `112:7909` | `112:8003` | — |
| Dados e servidores | `112:8230` | `112:8276` | — |

Os valores do Figma ficam nos comentários XAML junto de cada elemento. Este documento regista decisões, derivações e desvios.

## 1. Decisões humanas (vinculativas)

- **H-UI5-1 — Menu "…":** Ocultar e Remover ficam num menu "…" (DERIVED) junto de Editar. Ambos mantêm as confirmações existentes. Depois da ação própria, a app volta a Servidores com o aviso do Figma.
- **H-UI5-2 — Offline com leitura retida:** a última leitura continua visível, marcada como desatualizada e legível sem cor. Não há classificação de saúde nova.
- **H-UI5-3 — Pulso de CPU:** só amostras reais, do histórico local já gravado. Com menos de 30, mostram-se essas, alinhadas à direita; sem nenhuma, não há barras. Não há recolha nem persistência novas, nem enchimento com dados falsos.
- **H-UI5-4 — Backup/Restauro:** passa a ser um cartão em "Dados e servidores", entre Histórico e Sobre. A ligação "Sobre o ServerAlyzer" das Definições navega para Dados e dá o foco ao cartão Sobre. Não há página nova.

## 2. Decisões do Boss que mudaram comportamento

- **Escala do pulso:** teto de 25/50/75/100 %, o menor degrau ≥ ao máximo visível (`MetricVisualPresentation.PulseCeiling`).
  - Revê a escala absoluta 0–100 % da ronda 1.
  - O nome acessível diz os valores reais: "CPU: 24%. Pulso das últimas 12 amostras, escala até 25%".
  - Mínimo de 3 px para valores > 0 (DERIVED); um 0 medido não desenha barra.
- **Avisos transitórios** (aviso de Servidores e toast de Dados):
  - fecham sozinhos ao fim de **8 s** (DERIVED), ao fechar e ao sair da página;
  - nunca reaparecem numa visita posterior;
  - live polite;
  - ficam numa **linha própria** por baixo do conteúdo.
- **Erros com âmbito de servidor:** um Editar/Ocultar/Remover falhado regista o servidor (`DashboardViewModel.OperationErrorServerId`, camada App).
  - Só aparece no Detail desse servidor.
  - Os erros globais e a configuração bloqueada continuam em todo o lado (UI.4 SHOULD-3).
  - O âmbito é "o último ganha": há um único aviso partilhado.
- **Sem cor de severidade no Detail:** as cores das métricas são de identidade, estejam desatualizadas ou não. Não há propriedades `*Severity`.
- **Diálogos destrutivos:** `DestructiveConfirmDialog` = `SaDialogStyle` + `SaDialog.Kind="Destructive"`, com corpo, linha de dados afetados (564 × 42, r11) e nota.
  - Usado em Remover servidor (todos os pontos de entrada), Limpar histórico e Repor histórico.
  - Cancelar é o botão por defeito e recebe o primeiro foco, por isso Enter nunca apaga.
- **`--qa-backup` obrigatório em todos os cenários `--qa-overview`:** recusa no arranque (exit 3) e a composição lança exceção.
- **Relógio:** é um parâmetro obrigatório de `SettingsViewModel`, `ServersViewModel` e `TransientNoticeTimer`. A raiz faz `TryAddSingleton(PresentationClock.System)`; um harness regista antes e ganha.
  - **Guarda em runtime (a prova):** nos testes, `TransientNoticeTimer.Start` recusa o relógio de sistema. A recusa é registada por teste e falha-o; fora do corpo de um teste, faz `FailFast`.
  - A análise léxica (`SystemClockGuardTests`) é só consultiva. Aplicou-se o BOSS §16 (não convergência): o mecanismo foi substituído em vez de afinar mais o tokenizer.

## 3. Arquitetura e testabilidade

- **`ServerDetailViewModel`:**
  - lê o `ServerCardViewModel` do dashboard, sem recolha nova;
  - agrega as notificações num flush por rajada, sem alocações extra por tick;
  - lê o relógio uma vez por leitura (`ReadClock`);
  - pulso a partir do histórico local, com uma leitura por amostra persistida; uma leitura ultrapassada nunca escreve por cima (evento `PulseReadSettled`);
  - saída e aviso pela intenção própria (Ocultar/Remover), devolve o foco a "…" quando não sai, e trata o retorno de foco de Histórico/Serviços.
- **`ServerStatusPresentation`:** a fonte única do texto de estado e do valor acessível, partilhada pelas linhas e pelo Detail.
- **Definições divididas:**
  - `SettingsPage` (Geral) e `SettingsDataPage` (Dados e servidores), com o mesmo `SettingsViewModel` singleton;
  - carga em single-flight;
  - pedidos de secção (Segundo plano / Sobre) atendidos a frio e a quente, só com a página `Loaded`.
- **Navegação:**
  - `GoToServerDetail` com origem e breadcrumb (A-2);
  - `GoToSettings(General|Data|About)`;
  - `ServersReturnNotice` é um slot one-shot.
- **Primitivas:**
  - `SaPulseBars` e `SaSegmentMeter`: pista fixa de 288, cor por template part com `ThemeResource`;
  - `SaListHost`: lista UIA com nome;
  - `SaThemeRefresh`: remount na troca de tema ao vivo, com foco e scroll repostos;
  - `SaInlineNotice`: nome sem ponto duplicado;
  - `SaListRow`: o nome explícito ganha ao título.
- **Harness Debug `--qa-overview`:**
  - cenários UI.4, mais `detail`/`detail-failing` (um servidor por estado; operações com sucesso ou falha) e `data`/`data-failing` (ocultos, histórico disponível/indisponível com o diálogo real, doubles de backup);
  - relógio de apresentação fixo;
  - só no Debug.

## 4. Breakpoints

| Largura da janela | Detail | Definições / Dados |
|---|---|---|
| ≥ 1120 | 3 cartões de métrica (conteúdo ≥ 1040, cartão ≥ 336 = geometria Figma) | Controlo à direita da linha |
| 700–1119 | Métricas empilhadas, depois Ligação/Explorar empilhados; ações ao lado do nome | Controlo à direita |
| < 700 | Padding compacto; ações por baixo da identidade | Controlo por baixo do texto; 16 de ar até à linha seguinte |

## 5. Detalhes técnicos

- **Medidores:** pista fixa de **288 × 40**, alinhada à esquerda e nunca esticada.
  - Pulso: 30 × 5,73, gap 4.
  - Memória: 28 × 6,43, gap 4.
  - Disco: 14 × 15,93, gap 5.
- **Cabeçalho:** "{sistema}   ·   {endereço}" (`112:1802`); só o endereço quando não há sistema. O chip de leitura desatualizada diz sempre "Leitura desatualizada"; a idade aparece uma vez, em "Última atualização".
- **Primeira leitura:** cartão de 216; faixa de info em cartão sólido p24 / g32 com skeletons. O "Intervalo" fica visível (é configuração conhecida).
- **Ligação:**
  - 4 linhas h23 com gap 17.
  - A linha "Estado da ligação" só aparece quando a ligação não está verificada.
  - A linha "Rota" só aparece com jump host.
  - Um problema de ligação substitui o aviso genérico de recolha.
- **Teclado e leitores de ecrã:**
  - Seletor de tema: uma paragem de Tab, e as setas selecionam.
  - Sobre e Segundo plano: trazem a secção à vista e dão-lhe o foco.
  - Lista de ocultos: cada Restaurar é uma paragem de Tab e anuncia "n de N".
  - "A atualizar…" é live polite.
  - Depois de Limpar/Repor histórico, o foco volta ao botão que abriu o diálogo.
- **Tema ao vivo:**
  - Medido: a página aberta não recebia `ActualThemeChanged`, e trocar o estilo/brush no lugar dava outra mistura (Claro 223 vs 247).
  - O remount pela raiz iguala a reentrada (247 = 247, 37 = 37).

## 6. Diferenças deliberadas face ao Figma

| Diferença | Razão |
|---|---|
| Avisos numa linha própria por baixo do conteúdo (o Figma §3.2 põe-nos por cima); enquanto visíveis, a área de scroll fica ~128 px mais curta | a11y: nunca tapar ações (0 controlos tapados a 900×700 e 560×640) |
| Faixa da primeira leitura com ~97 px (Figma 86) | Funcional: o "Intervalo" mostra o valor real em vez de skeleton |
| Wide a partir de uma janela de 1120 (o Figma desenha 1040 de conteúdo) | Plataforma: sem a sidebar do UI.6, é a largura em que as pistas de 288 cabem em 3 colunas |
| Menu "…" com Ocultar/Remover (H-UI5-1); glifo Segoe `E712` | Funcionalidade preservada; o set de ícones não tem "more" |
| Cartão Backup/Restauro (H-UI5-4) e "Repor histórico" | Funcionalidade preservada, sem nó Figma |
| A ligação "Sobre" navega para Dados e foca o cartão Sobre (H-UI5-4) | Funcionalidade preservada |
| "Estado da ligação" (quando não verificado) e "Rota" (jump host) | Funcionalidade preservada |
| Escala do pulso por degraus e mínimo de 3 px | Funcional: derivação documentada (§2) |
| Botão voltar das Definições | Funcional: D-UI4-NAV, até ao UI.6 |
| Ordem dos botões dos diálogos à moda Windows | Plataforma: UI.2 B-10 |
| Endereço no formato A-13 (`host:porta`, `[IPv6]:porta`) | Funcional |
| Versão real no "Sobre" | Funcional: o número do Figma é ilustrativo |

## 7. Remoções com prova de zero usos

- **Prova:** grep em `src`, `tests` e `tools`, seguido das suites completas Debug e Release verdes.
- **Removidos:**
  - `ServerFullCard`, `ServerActionsButton` e `RemoveServerDialog` (substituído pelo diálogo destrutivo);
  - `IsFocusHighlighted`, `MoreOptionsAutomationName`, `RefreshMetricsAutomationName`;
  - as chaves de recurso mortas desses controlos e das Definições antigas;
  - `Cpu/Memory/DiskSeverity` do Detail;
  - o método `ConfigureDialog`, sem uso.
- **Mantidos com prova negativa:**
  - `ServerHealthOffline`/`ServerHealth*`, usados pelo Compact;
  - as chaves Ocultar/Remover, reutilizadas pelo menu "…";
  - `ServerMetricsRefreshingLabel`, usado pelo Compact e pelo Detail.

## 8. Reviews e rondas

| Review | Percurso | Final |
|---|---|---|
| Cortex (arquitetura) | B1 CHANGES_REQUIRED (M-1/M-2) → c1, c2 e c4 APPROVED_WITH_NITS | **c5 APPROVED** |
| Prism (fidelidade) | c1 CHANGES_REQUIRED (M-1..M-8) → c2 APPROVED_WITH_NITS | **c3 APPROVED** |
| Beacon (QA na app real) | c1 CHANGES_REQUIRED (M1–M5) → c2 CHANGES_REQUIRED (R2-M1) | **c3 APPROVED** |
| Atlas (testes) | c1 a c5 CHANGES_REQUIRED (barreiras, cultura, relógio real, guarda do relógio) | **c6 APPROVED** |

**Rondas de correções:**
1. **Ronda 1:** prontidão de navegação a frio, `--qa-backup` para todos, single-source da idade, pt-PT em "tu".
2. **Ronda 2:** decisões do Boss (pulso, avisos, âmbito do erro, sem severidade, diálogos), materiais de Prism/Beacon/Atlas e remount do tema.
3. **Ronda 3:** foco depois dos diálogos de histórico, relógio injetado nos testes, lista UIA com nome, corrida do callback em fila, scroll no remount.
4. **Ronda 4:** relógio como parâmetro obrigatório; TryAdd na raiz (achado do smoke).
5. **Ronda 5:** guarda em runtime no ponto de perigo, registada por teste; o lint passa a consultivo.
6. **Ronda 6:** `FailFast` para recusas fora do corpo de um teste; teste desarmado sem janela de 8 s.

## 9. Testes e contraprovas

- **Canónico em `e9fcfab`** (`ServerMonitor.slnx`, `--no-incremental`, `test --no-build`):
  - Debug: **4847 passam / 0 falham / 1 skip** (pré-existente).
  - Release: **4531 / 0 / 1**.
  - Incluem ratchets (geometria, dívida UI, contrato XAML), guards de tokens e de arquitetura.
- **Contraprovas P-008:** cada invariante nova tem uma mutação que faz falhar a suite, com restauro byte a byte e hash = HEAD.
  - Grupos A–J ao longo das rondas, todos KILLED, incluindo o tema ao vivo em runtime (build mutado: 223/56 vs 247/37).
  - Omitir o relógio dá erro de compilação CS7036.
  - As vias interpolação, `using static`, alias e helper real com o relógio de sistema fazem falhar o teste.
  - Uma recusa no construtor ou no `Dispose` aborta o host com a causa.
- **Sem relógio real nos testes:** FakeTimeProvider em todos os donos de avisos; os 30 s existentes servem só de proteção contra deadlocks.

## 10. NOT_RUN

- Narrator.
- Alto Contraste real do sistema.
- DPI 150 % e 200 %.
- Hover real das primitivas: só o disclosure em Claro registou hover com movimento sintético do ponteiro.
- Deep-link real do widget até ao Detail.
- Foco no "Segundo plano" em runtime: o gatilho é uma notificação do SO ou a degradação do tray.
- Foco depois de um Repor histórico com sucesso em runtime: coberto por teste.

## 11. Dados reais e Firewall

- **Dados reais:** em todas as corridas de QA, metadados e SHA-256 dos dados reais do utilizador antes e depois: **0 diferenças**. Nenhum caminho real foi passado à app nem aos testes.
- **Firewall:**
  - As 4 regras "Query User" do `testhost` deste Floor (Infrastructure.Tests, Debug e Release, TCP e UDP, em **Allow**) foram removidas depois do merge, com GO humano, por `Name` exato e com elevação: 716 → 712 regras, 0 removidas a mais, 0 novas.
  - Nenhum prompt foi respondido por agentes.
  - Não existe regra para a app.

## 12. Backlog

- **N-R4-2:** `DashboardViewModel` e `ServerDetailViewModel` com relógio obrigatório (hoje opcional, com fallback para System).
- **R-4 (UI.6):** acrílico desatualizado na troca de tema ao vivo nas páginas UI.4 (Visão geral, Servidores, Histórico, Serviços). A correção pode passar para o Frame ou a raiz do shell.
- **M14.6:** mensagens e diálogos de backup ainda em "você" (pt-PT).
- **Hover em Claro** das primitivas, na galeria (UI.6).
- **`TrayOwnershipCompletenessTests`:** o único uso de `TimeProvider.System` nos testes (tray, sem avisos), na allowlist do lint.
- **UI.6:**
  - sidebar/shell, que substitui o botão voltar das Definições (D-UI4-NAV);
  - área própria para avisos;
  - retorno de foco das Definições para a Visão geral (Beacon N7).
- **UI.7:** redesign do Editar e "Descartar alterações?".
