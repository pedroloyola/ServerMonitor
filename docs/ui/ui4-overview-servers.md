# UI.4 — Visão geral + Servidores

Estado: **EM CURSO** (2026-10-02): Fase 1 aceite para review; Cortex r1 corrigido (`3d42bfe`); Fase 2 com as Views no branch `ui/ui4-overview-servers`. Falta a QA visual completa (Light/Dark nas larguras do Prism, 3 línguas, 500 servidores realizados via UIA). Não há merge.
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

## 5. QA harness (Debug-only)

- `--qa-overview` + `--qa-overview-scenario <healthy|mixed|attention|critical|offline|empty|loading|unavailable|discovery|many-100|many-500>`, por defeito `mixed`.
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

## 7. Diferenças deliberadas / dívida

- Não há sidebar (UI.6).
- A sombra projetada das superfícies não é reproduzida (desvio UI.2 existente).
- Contagem "4" em 38 Light em vez de 36, e "de 6 saudáveis" em 20 Regular em vez de 22: não existe token para estes tamanhos e criar um exigiria Prism + manifesto.
- O ícone CPU/Memória é o `SaIconComputerData` existente (não há asset novo).
- O título "Servidores" da secção usa `SaSectionHeaderTextStyle` (20) em vez de 19.
- Backlog (Boss): código morto `IsFocusHighlighted`; listas de harness em `tools/perf/*.ps1`.
