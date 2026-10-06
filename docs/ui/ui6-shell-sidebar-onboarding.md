# UI.6 — Shell, Sidebar e Onboarding

**Estado: READY_TO_MERGE** (2026-10-06). O merge aguarda GO humano.
- **Branch:** `ui/ui6-shell-sidebar-onboarding`, base `4f5bfaa`.
- **PR:** #25.
- **Candidato:** `006e4ec` (código validado; este commit de documentação vem por cima).
- **CI:** verde no `006e4ec` (run 37396058509: Debug build + tests, Release build + vulnerability scan).

**Autoridade visual:** Figma `Qvk5dUFgsWf4UOYzfAkiDV`, página 05 (principal), 04 (marca) e 06 (só extensibilidade). Precedência: invariantes/segurança > a11y/plataforma > Figma.

| Alvo | Escuro | Claro |
|---|---|---|
| Shell + sidebar | `112:1358` / `112:1359` | `112:1539` / `112:1540` |
| Onboarding 1 · 2 · 3 | `112:242` · `112:456` · `112:614` | `112:349` · `112:535` · `112:685` |
| Primeira utilização · Dashboard vazia | `112:756` | `112:841` |
| Segmentado de período do Histórico | `112:2278` | — |

## 1. Decisões humanas (vinculativas)

- **H-UI6-1 (D-2) — estado de carga aditivo.** O carregamento dos servidores passa a expor `NotFound` / `Loaded` / `Unavailable`, em vez de um `[]` simples.
  - `IServerLoadStatusSource` (Core) e `JsonServerRepository.GetLoadStatusAsync` (Infra) são só de leitura.
  - Não há mudança de formato nem do caminho de escrita.
  - O contrato e os corpos de `GetAllAsync`/`SaveAllAsync` ficam inalterados (provado em texto contra a base).
  - `Unavailable` cobre: erro de IO ou de acesso, JSON inválido, schema routed futuro, ou zero entradas carregáveis com quarentena.
  - A lista em cache manda sobre o diagnóstico: lista com servidores → `Loaded`; lista vazia com entradas legíveis em disco → `Unavailable`.
  - Revisão: Cortex e Vigil (fronteira de dados), APPROVED_WITH_NITS.
- **H-UI6-2 (D-1) — gatilho derivado, sem persistência nova.** O onboarding só aparece quando estão reunidas todas estas condições:
  - estado `NotFound`;
  - arranque normal (sem intenção de ativação nem `--background`);
  - destino atual Visão geral.
  - "Agora não", "Explorar primeiro" e os CTAs do passo 3 dispensam-no **só para o processo**.
  - Não há chave nem ficheiro novos.

## 2. Decisões do Boss (dentro das regras do UI.0)

| ID | Decisão |
|---|---|
| D-3 | O Histórico pela sidebar abre o último servidor visto no Detail (em memória). Sem nenhum, abre o primeiro visível (CreatedAt); com zero servidores, o estado vazio. Uma leitura falhada mostra `Unavailable`, nunca "sem servidores". A partir do Detail, aparece "‹ {servidor}" numa linha própria de 22 px acima do H1, com o nome acessível "Voltar para {servidor}" |
| D-4 | Seleção: Visão geral → Visão geral; Servidores, Detail, Serviços → **Servidores**; Histórico → Histórico; Definições e Dados → **Definições** (Figma §2.5). Diálogos e toasts não mudam a seleção |
| D-5 | Sidebar própria (`SaSidebar`), não `NavigationView`: landmark de navegação com nome, lista com nome, `IsSelected`/posição no conjunto, uma paragem de Tab, setas, Enter e Espaço |
| Fonte única | `NavigationService.Show` é o único ponto que muda o conteúdo e publica `CurrentDestination`/`Navigated` (por último, depois de libertar a página anterior). O `ShellViewModel` é só de leitura. `IsChecked` é aplicado de forma imperativa (`SidebarSelectionSync`), nunca por binding OneWay (que o clique apagaria) |
| Arranque a frio | `EnsureInitialNavigation` só vai para a Visão geral quando não há conteúdo. Ativações e `--qa-start` preservam o destino |
| G-1 | Sem a banda de 52 px: `ExtendsContentIntoTitleBar`, região de arrasto de 32 DIP, botões de legenda do sistema. Os headers começam abaixo da banda das legendas em todas as larguras |
| G-2/G-17 | Espaçador flexível. Definições ancorado a 40 px do fundo. Slot Pro vazio e invisível |
| G-6 | `SaBrandMark`: vetor oficial da marca, `Fill` por tema e HC. É um ativo de marca, não uma biblioteca de ícones |
| G-8 | Onboarding na mesma janela, em ecrã inteiro sem sidebar, com o painel de 1040×780 centrado |
| G-10 | Os CTAs do passo 3 e do estado vazio usam **só** os comandos existentes (`AddServerCommand`/`ImportFromSshCommand`); o editor continua o modal atual (UI.7) |
| G-11 | A descoberta mDNS mantém-se no estado vazio B |
| G-14 | Variante de botão h46 r15 com tokens, guardas e contorno de 1 px |
| G-15 / D | Estado C (todos ocultos): mesma linguagem do B, com ação para Definições › Dados e servidores. Estado D (indisponível): aviso de erro polite, sem onboarding e sem o texto "primeiro servidor"; esconde-se no próximo `ServersChanged` |
| G-16 | Responsivo sem design: sidebar de 208 com área cliente ≥ W1 = 1040 DIP; abaixo disso, rail de 80 só com ícones (nomes e tooltip no rail). As páginas usam `SaContentWidthTrigger` (largura de **conteúdo**) em vez de `AdaptiveTrigger` de janela |
| G-19 | Duas camadas: o material da janela e um véu `SaSidebarMaterialBrush` num `Border` (um `UserControl` não pinta `Background`), mais uma aresta de 1 px `SaSidebarEdgeBrush`. Em HC não há véu. O R-4 (remount do tema) passa para o conteúdo do shell, sem remount duplo |
| Navegação temporária | Removidos: "Ver todos" como navegação global ("Mais N" passa a link de conteúdo), o botão Definições do header, o Voltar das Definições e a breadcrumb "Visão geral › Servidores". Mantidos: a breadcrumb do Detail (origem), a de Serviços, o "‹" de Dados e o "‹ {servidor}" do Histórico vindo do Detail |
| Foco | Arranque → item selecionado da sidebar. Ativação da sidebar por teclado → o foco fica na sidebar. Navegação pelo conteúdo, deep-link ou saída do onboarding → H1 (`SaHeadingHost`). Os slots de retorno existentes têm prioridade. Diálogo → controlo de origem, ou o H1 se a origem saiu (`DialogReturnFocus`, ao nível do chamador). No passo 3, o H1 da Visão geral recebe o foco antes de o editor abrir (melhor esforço: falhas registadas, o editor abre sempre) |
| Arranque Compacto | Com `NotFound`, o onboarding fica pendente e aparece ao expandir para Standard |
| Segmentado do Histórico | 5 itens iguais de 82,4×36, lacuna de 4, pista de 436. Com área útil < 436, `Width = min(436, útil)` (78,4 a 560) |

## 3. Decisões de plataforma

- **Foco no H1 (BOSS §14/§16):** duas correções do mecanismo "TextBlock selecionável" falharam (foco e repaint, depois offset obsoleto após resize). O gate de decisão levou à substituição por `SaHeadingHost : ContentControl`:
  - template nativo;
  - `UseSystemFocusVisuals`;
  - `HeadingLevel=1` no host;
  - `IsTabStop` só durante a visita programática;
  - texto simples, não selecionável.
  - **Fontes:** `UIElement.Focus`, `Control` (condições de foco), `UIElement.IsTabStop`, `AutomationProperties.HeadingLevel`, WinUI Gallery `PageHeader`.
  - **Documentado:** as APIs. **Derivado:** a composição. **Empírico:** medido em runtime pelo Beacon.
- **Crash ao abrir o Detail** (encontrado no QA c1, 4/4, `E_NOINTERFACE`):
  - medido com uma sonda first-chance temporária, só Debug, removida depois;
  - causa: o `connectionId` do compilador XAML era inserido no `SaHeadingHost` quando o `<TextBlock x:Name>` abria na mesma linha, enquanto `Connect(25)` faz o cast para `TextBlock`;
  - correção: o filho passa para uma linha própria;
  - guarda: `Ui6XamlConnectorTests` percorre **todos** os `*.g.cs` gerados (25 ficheiros, 468 casts, incluindo x:Bind) e falha fechado perante formas desconhecidas;
  - rever o guarda ao atualizar o WindowsAppSDK.

## 4. Breakpoints (a 100 %, moldura de 16 px)

| Janela exterior | Cliente | Sidebar | Conteúdo |
|---:|---:|---:|---:|
| 1440 | 1424 | 208 | 1216 |
| 1120 | 1104 | 208 | 896 |
| 1040 | 1024 | 80 | 944 |
| 900 | 884 | 80 | 804 |
| 700 | 684 | 80 | 604 |
| 640 | 624 | 80 | 544 |
| 560 | 544 | 80 | 464 |

Estados por largura de conteúdo (intervalos sem sobreposição):

| Página | Estados |
|---|---|
| Visão geral | 0 / 600 / 640 / 900 |
| Servidores | 0 / 640 / 900 / 1040 |
| Detail | 0 / 700 / 1120 |
| Histórico, Serviços | 0 / 640 / 900 |
| Definições, Dados | 0 / 700 |

Os estados vazios B/C só centram num bloco de 620 quando a altura útil é ≥ 700; abaixo disso, o CTA fica acima da dobra.

## 5. Diferenças deliberadas face ao Figma

| Diferença | Razão |
|---|---|
| Sem r30 nem sombra desenhada; janela nativa e legendas do sistema | Plataforma (G-1, G-7) |
| Segoe UI Variable | Exceção do UI.0 |
| Rail de 80 e empilhamentos abaixo de W1 | Responsivo sem design (G-16) |
| Item selecionado em Medium | Pista não cromática (G-3) |
| Texto secundário pelo token do Manual | Manual prevalece (G-4) |
| Cor da marca `SaTextBrush` | Manual prevalece (N-7) |
| Painel do onboarding sem superfície própria | Consequência do G-8 |
| Botão Modo compacto mantido no header dos estados B/C/D | Funcionalidade existente (N-10), nunca primeiro foco |
| Estados C e D | DERIVED, sem nó Figma |
| "‹ {servidor}" no Histórico vindo do Detail | DERIVED (D-3) |
| `ModalOverlayHost` na raiz (também cobre o Compacto) | DERIVED (Cortex N-6) |
| Anel de foco sobre a pílula no arranque | Política de foco a11y (SPEC §3) |
| Segmentado a 78,4 com área útil < 436 | Exceção estreita (Prism §3.3) |
| Botões de legenda com nomes em inglês | Plataforma |

## 6. Reviews

| Review | Percurso | Final |
|---|---|---|
| Cortex (arquitetura) | B1 CHANGES_REQUIRED (M-1…M-4) → r2 APPROVED_WITH_NITS; c1 CHANGES_REQUIRED (seleção OneWay) → c2 e c3 APPROVED_WITH_NITS | **c4 APPROVED** |
| Vigil (fronteira de dados, H-UI6-1) | b1 e r2 APPROVED_WITH_NITS (Core/Infra de produção inalterados desde `107f3ac`) | **APPROVED_WITH_NITS** |
| Prism (fidelidade Figma) | c1 CHANGES_REQUIRED (centragem B, véu, alinhamento do H1) → c2 CHANGES_REQUIRED (véu, breadcrumb do Histórico) → c3 CHANGES_REQUIRED (segmentado) | **c4 APPROVED_WITH_NITS** |
| Beacon (QA na app real) | c1 CHANGES_REQUIRED (crash no Detail, seleção, Enter, legendas, ícone B, foco de diálogo) → c2 CHANGES_REQUIRED (Histórico a 560, foco no passo 3) → c3 CHANGES_REQUIRED (regressão do segmentado) | **c4 APPROVED** |
| Atlas (testes; subagente interno) | c1 CHANGES_REQUIRED (composition root, âncoras mortas) → c2 CHANGES_REQUIRED (cobertura do guarda de conectores) → c3 APPROVED_WITH_NITS | **c4 APPROVED_WITH_NITS** |

**Substituições de papel (registo):**
- **Atlas:** subagente interno Claude, só leitura. O Atlas no canvas corre Codex, com quota partilhada.
- **Implementação do c3:** concluída por um subagente interno Claude depois de a Sentry (Codex) esgotar a quota a meio da ronda. O trabalho foi preservado, não refeito.

## 7. Testes e contraprovas

- **Canónico no `006e4ec`** (`ServerMonitor.slnx`, `--no-incremental`, `test --no-build`, `--disable-build-servers`):
  - Debug: **5039 passam / 0 falham / 1 skip**;
  - Release: **4686 / 0 / 1**;
  - focado: 628 / 0 / 0.
  - O skip é o teste opt-in real do Credential Manager (pré-existente).
- **Contraprovas (BOSS §10):** todas KILLED e restauradas byte a byte, com rebuild não incremental e o total de testes invariante.
  - B1: 14 + 17 mutações (router, seleção, arranque a frio, gatilho, estado de carga, quarentena, ACL, latch de ativação).
  - B2: 16 (estrutura do shell, W1, breakpoints, T-8, remount, `SaHeadingHost`, FUTURE_PRO).
  - c1: lotes A, B e C, mais as âncoras B1 reancoradas.
  - c2: A e B (x:Bind isolado).
  - c3: A1–A3, B1–B2, C1–C2 (incluindo um `.g.cs` gerado real).
  - Evidência em `.boss/tmp/ui6/` e `.boss/evidence/ui6/` (local).
- **Determinismo:** não há relógio de parede nos testes novos, e só se usam diretórios temporários (nunca caminhos reais).
- **Achado de CI corrigido:** o teste ACL comparava o SDDL exato, e o runner acrescenta `AI`. Passou a comparar as regras semanticamente, ignorando só o auto-inherit. Não era um flake.

## 8. NOT_RUN

- Onboarding em Light: inalcançável sem mudar o tema do SO.
- Alto contraste real, DPI 150/200 % e escala de texto: ambiente a 100 %.
- Narrator com voz.
- Workloads com dados: o harness `detail` só produz "A carregar…", tal como no UI.5.
- Redirect real entre instâncias e deep-link real do widget empacotado: o QA Debug não tem instância única.

## 9. Dados reais, Firewall, resíduos

- **Dados reais:** metadados e SHA-256 de 22 ficheiros (`%LOCALAPPDATA%\ServerMonitor`, `~/.ssh`, `Settings`/`LocalState` do pacote), mais a contagem de alvos do Credential Manager, antes e depois de **todas** as vagas de QA: **0 diferenças**. Não se passou nenhum caminho real à app nem aos testes.
- **Firewall:** 718 regras no total, sem alteração desde a linha de base.
  - Existem 4 regras "Query User" **Inbound Allow** do `testhost` deste Floor: Infrastructure.Tests, Debug e Release, TCP e UDP. Os nomes exatos estão em `.boss/evidence/ui6/firewall-after.csv`.
  - **Não foram removidas** (precisam de GO humano). Nenhum prompt foi respondido por agentes.
- **Resíduo:** o diretório temporário `%TEMP%\ui6-load-23eb0e25-8ca0-4753-942b-0d7f9731e94d` (um `servers.json` sintético de 2 B, com a regra Deny já removida) ficou de uma contraprova ACL inicial.
  - A ferramenta do implementador recusou apagá-lo, e o pedido para o Boss o apagar foi recusado (não se contorna uma recusa por outro agente).
  - Fica para decisão humana. O `finally` corrigido impede que se repita.

## 10. Backlog

- **Oportunidades de motion para o UI.11:**
  - transição entre passos do onboarding;
  - entrada e saída do rail;
  - crossfade entre páginas;
  - apresentação dos passos com VisualStates (Cortex N-5).
- **Vigil:**
  - N2: TOCTOU residual entre a sonda de atributos e a leitura (aceite);
  - N5: aviso de quarentena registado em duplicado.
- **Cortex:** N-2 c2: o inset das legendas reserva só a altura (não a largura); ler `TitleBar.RightInset` se um header subir.
- **Cortex N-9:** `WorkloadsPage` sem `IDisposable`.
- **Harness:**
  - cenário de Workloads com dados;
  - raiz QA por lançamento (hoje partilhada: uma instância de cada vez).
- **Compacto:** o foco ao sair do modo compacto fica na raiz (Beacon N-5, pré-existente).
- **Servidores sem servidores:** o estado vazio do UI.4 não foi redesenhado (G-13).
- **Segmentado:** abaixo de 394 de área útil não há fallback de quebra (inalcançável hoje).
- **CI-FLAKE-WALLCLOCK-2:** mantém-se no backlog, não recorreu.

**UI.7 NÃO iniciado.**
