# UI.4 — Visão geral + Servidores

Estado: **READY_TO_MERGE, à espera do GO humano** (2026-10-02). Branch `ui/ui4-overview-servers`, código fechado em `95b4daa` (este commit só toca em docs). Reviews finais: Prism r3 APPROVED, Cortex r3 APPROVED_WITH_NITS, Atlas r1 fechado, Beacon r2 PASS. Gates no §8. Não há merge.
Autoridade visual: Figma `Qvk5dUFgsWf4UOYzfAkiDV`, 05 Free secção 02 "Dashboard e servidores" (Visão geral D `112:930` / L `112:1141`; Servidores D `112:1353`, directory `112:1419`, tabela `112:1434`), §13 `112:14006` (estados vazios), §14 `112:15512` (carregamento). Precedência: invariantes/segurança > a11y/plataforma > Figma.

## 1. Decisões humanas (vinculativas)

- **D-UI4-NAV**:
  - "Ver todos" ao lado de "Servidores" na Visão geral.
  - O header mantém Modo compacto + Definições + Atualizar + Adicionar.
  - Breadcrumb "Visão geral" na página Servidores.
  - Tudo isto é temporário até ao UI.6.
- **D-UI4-DETAIL**:
  - Página interina com o `ServerFullCard` atual (sem redesign) + breadcrumb para a origem.
  - O deep-link do widget abre esta página (o retry pendente mantém-se; um servidor removido deixa a app na Visão geral).
  - Ocultar ou remover volta à origem.
  - Substituída no UI.5.
- **D-UI4-PRIORITY**:
  - `PriorityProblemSelector` puro usa a `MonitoringThresholds` do motor, ou seja, **a mesma instância** de `MonitoringOptions` registada uma única vez e passada ao motor.
  - Limites inclusivos.
  - Ordem: severidade → % → ordem da lista → métrica.
  - Offline, Unknown e servidores sem snapshot ficam excluídos.

## 2. Visão geral

- **Header** (`112:997`): "Atualizado há …" com a semântica D-UI3-9 sobre o `LastSuccessAt` mais recente (Prism h). Mostra "A recolher dados…" durante o loading (§14) e fica colapsado quando nunca houve leitura. O botão Atualizar usa o `IRefreshAllCoordinator` existente.
- **Saúde geral** (`112:1010`):
  - Contagens a partir de `ServerCardViewModel.Health` (o motor).
  - Barra de 213 px: até 12 servidores, um segmento por servidor (≤ 30); acima disso, um segmento proporcional por estado. Reserva 12 px para cada estado presente e reparte o resto (Prism a), por isso a soma é sempre 213; tooltip com a contagem.
  - Chips só para estados > 0.
  - Cartão focável cujo nome é a frase completa.
- **Prioritário** (`112:1031`):
  - Botão-cartão com ícone (HardDrive para disco, Computer para CPU/memória), rótulo na cor da severidade, valor 32 Light e barra de capacidade.
  - Sem candidato mostra "Sem problemas"; sem nenhuma leitura mostra "Sem leituras" (Prism e).
  - "Sem problemas" nunca aparece com Warning ou Critical > 0 (Cortex NIT-3).
- **Lista resumida** (`112:1057`):
  - Linhas `SaListRow` (nome + estado + chevron), até 8.
  - Rodapé explícito "Mais N servidores · Ver todos" (Prism b).
  - A pesquisa abrange todos os servidores.
  - Lista UIA nomeada, com um único tab stop.
- **mDNS**: secção própria abaixo da lista, só quando há sugestões. Sem servidores, a descoberta aparece dentro do `SaEmptyState`. Há um único template (já não existe a cópia inline).
- **Estados**:
  - loading: esqueletos §14;
  - vazio (Adicionar / Importar do SSH / descoberta);
  - sem leituras;
  - InfoBars de erro e de configuração bloqueada (M14.6);
  - pesquisa sem resultados.

## 3. Servidores

- Header com resumo "N servidores · N saudáveis · …" (a mesma fonte da Visão geral). Pesquisa por nome ou endereço (`OrdinalIgnoreCase`, parcial).
- **Tabela** `112:1434`: Wide ≥ 1040 com as 7 colunas do Figma; Mid 900–1039 sem SISTEMA e ação só com ícone; Stacked < 900 com 3 linhas por servidor.
  - **Um só** `ItemsRepeater` virtualizado; os estados trocam apenas o template, dentro do único ScrollViewer vertical da página.
  - Cada linha é um único alvo, com o nome "Ver detalhe de <servidor>" e a linha completa como HelpText.
  - Uma métrica desconhecida ou de um servidor Offline aparece como "—", nunca 0%.
  - Um valor acima do limite aparece âmbar ou vermelho (`112:1468`).
- **Nota dos ocultos** (Prism c):
  - aparece sempre debaixo de uma tabela com linhas;
  - aparece no estado vazio quando há servidores ocultos;
  - não aparece em "sem resultados" nem no vazio simples.
- Estados §13/§14: "Nenhum servidor encontrado" + query + Limpar pesquisa; "Ainda não há servidores"; "A carregar os teus servidores…" + 5 esqueletos.

## 4. Arquitetura e testabilidade

- **`ServersReloaded`**: disparado no fim de cada reconstrução da lista. Os consumidores ignoram o Reset da coleção, que dispara com a lista vazia.
- **Página interina**: hospeda o cartão vivo do dashboard.
  - Depois de cada reload volta a resolver por id.
  - Sai uma única vez, de forma diferida (NIT-1).
  - Ao fazer Dispose deixa de navegar.
- **`NavigationService`** (Cortex r1):
  - **MUST-1**: só abre a página interina para um servidor listado e atribui Content **antes** de `Load`. Tem uma costura interna (host + fábrica de páginas) para testar o serviço real.
  - **SHOULD-1**: `NavigatedAwayFromOverview` cancela o deep-link pendente. Navegar **para** a Visão geral não o cancela, o que protege o deep-link de arranque.
  - **SHOULD-3**: faz Dispose da página por-visita que é substituída.
- **SHOULD-2**:
  - O recálculo da Visão geral é coalescido num único passe por rajada (via dispatcher) e anuncia apenas o que mudou; chips e frase acessível ficam em cache.
  - Medido com um ciclo de 500 servidores que republicam o mesmo estado: **73,0 ms / 8500 + 500 notificações → 8,2 ms / 0 + 0**.
  - Teste com orçamento exato de 0 notificações.
- **`PresentationClock`**: relógio só da Visão geral. O harness congela-o; não há `TimeProvider` de contentor.

- **Beacon QA r1** (`.boss/tmp/ui4/beacon/qa-r1.md`):
  - **SHOULD-1 foco**:
    - a Visão geral põe o foco no conteúdo (cartão Saúde geral; no vazio, "Adicionar servidor"), nunca no "Modo compacto";
    - ao voltar da interina ou dos Servidores, o foco vai para a linha, o cartão prioritário ou o "Ver todos" de onde se saiu (`TakeReturnFocus`, consumido uma vez);
    - os Servidores voltam a focar a linha que abriu a interina (`TakeReturnFocusIndex`); sem origem, o foco vai para a pesquisa.
  - **SHOULD-2**: as linhas das duas listas dizem "x de N" sobre a lista inteira virtualizada (`SaRepeaterPosition`, partilhado com o `SaDataTableRow` do UI.3; `SaListRowAutomationPeer`; `ServerTableRowButton`).
  - **SHOULD-3**: a página interina mostra as InfoBars de erro e de configuração bloqueada. A fonte é a mesma do dashboard, e fechar lá fecha a origem.
  - **SHOULD-4 (decisão Boss)**:
    - o "voltar" e o "Ver servidor" do Histórico e o crumb dos Serviços levam à interina desse servidor (`ReturnToServerDetail`), com a origem com que foi aberta;
    - se o servidor já não estiver listado (ou não houver nenhum), vão para a Visão geral;
    - copy: "Voltar ao servidor" / "Back to server"; o "voltar" das Definições diz "Voltar à visão geral".
  - **SHOULD-5**: no reflow Wide/Mid/Stacked, a linha focada mantém o foco. Observa-se a troca do `ItemTemplate` do repeater, porque o `AdaptiveTrigger` não dispara os eventos do `VisualStateGroup`. A linha é registada pelo índice no `GotFocus`, porque as linhas x:Bind não têm o item como `DataContext`. Verificado na app real a 1440 → 1000 → 640 → 560 → 1440.
  - **Achado da QA r2 (corrigido)**: depois de trocar o tema em runtime, alguns pontos de estado (`SaStatusIndicator`, UI.2) ficavam com a cor do tema anterior. Causa: o WinUI não reavalia um `{ThemeResource}` no setter de um VisualState ativo. Correção: o primitivo reentra no estado quando o tema efetivo muda (`ActualThemeChanged` e `Loaded`, porque a Visão geral em cache está fora da árvore enquanto se muda o tema). Verificado em Light após Escuro → Claro.
  - **NITs**:
    - anel de foco do cartão prioritário com raio 24;
    - "sem resultados" da Visão geral com a mesma copy §13 dos Servidores.

## 5. QA harness (Debug-only)

- `--qa-overview` + `--qa-overview-scenario <healthy|mixed|attention|critical|offline|empty|loading|unavailable|discovery|many-100|many-500|vanishing>`, por defeito `mixed`.
- `vanishing` = `mixed` cujos servidores desaparecem (em memória, no thread de UI) 30 s depois do primeiro load. É a única forma de ver o vazio §13 dos Servidores sem diálogo, porque esse estado só existe com a página aberta.
- Registado no parser estrito e em `tools/qa/Start-QaApp.ps1`.
- O modificador é ordinal e é recusado sem `--qa-overview`; um cenário desconhecido termina com exit 3 antes da composição.
- Dados sintéticos: hosts `.local` ou RFC 5737; a saúde segue o `HealthEvaluator`.
- Diferença de dados (Prism g): o Figma mostra disco 92% "Atenção", mas pelos limites do motor 92% é Crítico, por isso o harness usa 88%.

## 6. Copy

- "Workloads" visível passou a "Serviços e containers" / "Services and containers" (as chaves ficam iguais).
- Estados de linha: "Sem ligação" / "Sem conexão" / "No connection". O `ServerFullCard` interino mantém "Offline" (dívida UI.5, Prism f).
- pt-PT em "tu":
  - nas chaves novas;
  - em `DashboardDiscoveryDescription`;
  - em `ServerOperationError.Message`.

## 7. Fidelidade, diferenças deliberadas e dívida
- **Corrigido na ronda de fidelidade (Prism fidelity-r1 + decisão Boss):**
  - contagem 36 Light (`SaHealthCountTextStyle`) e "de 6 saudáveis" 22 Regular (`SaHealthTotalTextStyle`);
  - títulos de secção UI.4 a 19 Semibold (estilo **novo** `SaSectionTitleTextStyle`; o partilhado `SaSectionHeaderTextStyle` = 20 fica como o UI.3 aceitou para os Serviços);
  - ponto "Saúde geral" neutro `#A6A6A6` nos dois temas (`SaHealthLabelDotBrush`);
  - segmentos logo após o texto (espaçador 75);
  - cartões de saúde/prioritário com 156;
  - estado da lista resumida a 13 e nome a 15 Medium (`SaListRowProminentStyle`; o `SaListRow` por omissão continua a 14);
  - "Adicionar" dos Servidores ao lado do título em todas as larguras;
  - chevron do prioritário, espaçamento estado→chevron e header 600–639.
- **Prism fidelity-r2:**
  - **R2-B1:** cartão de saúde com exatamente 156 (estava 159). A linha da contagem fica com 47 e o texto de 50 transborda 1,5 px em cima e em baixo (Margin −1,5), como o `112:1015`.
  - **R2-B2:** com 0 servidores, o resumo dos Servidores diz só "0 servidores" (`112:14077`).
  - **C-R2-1:** com 0 servidores, o foco inicial dos Servidores vai para "Adicionar servidor".
- **DERIVED aceite:** ícone CPU/Memória = `SaIconComputerData` (o Figma não tem ícone para essas métricas; o título por extenso desambigua).
- **Diferenças deliberadas que ficam:**
  - **Disco 92%:** o Figma mostra 92% como "Atenção", mas no motor 92% é Crítico (≥ 90). O harness usa 88% para manter os estados do frame (Prism g); a app não inventa nada.
  - **Títulos de secção:** a 19 Semibold só no UI.4 (`SaSectionTitleTextStyle`). Os Serviços e containers mantêm o 20 que o UI.3 aceitou (`SaSectionHeaderTextStyle`, decisão Boss).
  - **"Sem ligação":** é a copy nova das linhas UI.4. O `ServerFullCard` da página interina mantém "Offline" até ao UI.5.
  - **Temporários:** D-UI4-NAV (header com Modo compacto, Definições, Atualizar e Adicionar; "Ver todos"; breadcrumb, até ao UI.6) e D-UI4-DETAIL (página interina com o cartão atual, até ao UI.5).
  - sem sidebar (UI.6);
  - sombra projetada não reproduzida (desvio UI.2 existente);
  - cor dos segmentos teal (tokens do Manual, decisão UI.1).

## 8. Reviews e gates finais

| Review | Percurso | Final |
|---|---|---|
| Prism (fidelidade) | r1 (DERIVED + fidelidade, CHANGES) → r2 (2 B triviais) → r3 | **APPROVED**, sem A/B/C abertos |
| Cortex (arquitetura) | r1 CHANGES_REQUIRED (MUST-1 navegação, SHOULD-1/2/3) → r2 → r3 | **APPROVED_WITH_NITS**. O NIT-r3-1 (comparação inclusiva única + paridade com o Core) foi aplicado em `356017d`. |
| Atlas (testes) | r1 CHANGES_REQUIRED (coalescência com 2.ª rajada, load falhado, singletons, `Assert.Same` dos limites) → `234659a` | **Fechado**. O review foi feito por um subagente interno no papel do Atlas, porque o canvas Atlas estava indisponível. |
| Beacon (QA na app real) | r1 PASS_WITH_NITS (SHOULD-1..5) → `0e760dc`, `94c82ff`, `ad69155`, `95b4daa` → r2 | **PASS**, com 0 regressões. Ficam abertos o NIT-1 e o NIT-5 (backlog). |

Gates em `95b4daa`:
- **Suites:**
  - Debug: 4490 passam, 0 falham, 1 skip.
  - Release: 4243 passam, 0 falham, 1 skip.
  - Incluem os ratchets (geometria, dívida de UI, contrato XAML/recursos) e os testes de arquitetura UI.4.
- **`--qa-tokens` runtime** (via `Start-QaApp`): 649 PASS / 0 FAIL, exit 0 (medido em `ad69155`, antes do `95b4daa`, que não toca em tokens).
- **Contraprovas:** cada invariante nova tem uma mutação que faz falhar a suite, e o ficheiro é reposto byte a byte. Rondas Fase 1/2, Cortex, fidelidade, Atlas (A1–A4), Beacon (B1–B18) e Prism r2 (R2-B1/B2/C): todas apanhadas.
- **Dados reais:** o Beacon r2 comparou `%LOCALAPPDATA%\ServerMonitor` antes e depois. Resultado IDENTICAL: 17 ficheiros e 0 desvios.
- **Medição SHOULD-2** (Cortex): ciclo de 500 servidores 73,0 → 8,2 ms, com 0 notificações (teste de orçamento exato).

## 9. NOT_RUN

Estas verificações não foram corridas no UI.4 e ficam declaradas:
- deep-link real do widget até à interina (só testes de VM/navegação);
- sucesso real de Ocultar/Remover (o harness devolve falha; só o caminho de erro foi visto na app);
- Narrator;
- Alto Contraste real do sistema (só a pré-visualização HC do `--qa-tokens`);
- DPI 150% e 200%;
- cold start (tempo até à 1.ª pintura).

## 10. Backlog

- **Raiz QA partilhada** entre instâncias simultâneas: o placement e as definições são partilhados entre corridas paralelas (Prism r1 risco 1). Falta uma raiz isolada por lançamento.
- **Código morto** `IsFocusHighlighted`.
- **Listas em `tools/perf/*.ps1`** sem `--qa-overview`.
- **NIT-1:** a página interina não tem H1. O foco de chegada no breadcrumb anuncia o contexto. Fica para o UI.5 (página de servidor).
- **NIT-5:** a InfoBar de erro de operação tem nome UIA vazio (anterior ao UI.4).
- **Enter no foco inicial do header:** até o foco passar para o conteúdo, o primeiro foco da janela é o "Modo compacto" do header, e um Enter "solto" muda a janela de modo. O Beacon r2 mediu um Enter a 1,5 s do arranque e a app fica em Standard; o resto da janela não foi medido. Fica para o header do UI.6.
- **Ratchet automático de virtualização:** hoje é uma medição manual UIA (22 de 500 linhas realizadas, Atlas NIT-4).
- **`ServerHealthOffline` = "Offline"** no cartão interino (UI.5).
