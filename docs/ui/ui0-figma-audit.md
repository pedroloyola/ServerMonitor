# UI.0 — Auditoria Figma ↔ app atual

| | |
|---|---|
| **Estado** | **UI.0 COMPLETE** — auditoria aprovada; decisões D1–D8 resolvidas pelo utilizador (§0.3). Documento docs-only. |
| **Data** | 2026-09-30 |
| **Baseline do repo** | `origin/main` = `befb2cc` (M14 GLOBAL COMPLETE) |
| **Fonte de design** | Figma `Qvk5dUFgsWf4UOYzfAkiDV`, lido **ao vivo** em 2026-09-30 via Figma MCP (não a partir de screenshots/notas antigas) |
| **Autoridade visual** | Página `05 · ServerAlyzer Free` (19 secções) e `06 · ServerAlyzer Pro` (23 secções) |
| **Evidência de apoio** | Inventário de UI (Prism, read-only), auditoria de acoplamento/invariantes (Cortex, read-only), extração de texto/tokens/screenshots do Figma (Boss). Nada foi validado na app em execução: tudo o que diz "verificar em runtime" é **NOT_RUN**. |

---

## 0. Regras do rebuild (aprovadas com o UI.0, válidas para UI.1–UI.9)

### 0.1 Autoridades
Figma `Qvk5dUFgsWf4UOYzfAkiDV`, **sempre o ficheiro atual** (consultar diretamente em vez de screenshots, notas ou memória):
- `04 · Manual da marca` — autoridade de cor, tipografia, geometria, material, ícones e voz;
- `05 · ServerAlyzer Free` e `06 · ServerAlyzer Pro` — **autoridade visual** da nova presentation layer.

### 0.2 Regra global — fidelidade ao Figma
Tudo o que está explicitamente desenhado em 05/06 é para **reproduzir com alta fidelidade**, não para interpretar, "inspirar" ou modernizar por preferência do implementador: layout, dimensões, proporções, grelhas, padding, gaps, alinhamentos, hierarquia, tipografia (tamanho, peso, altura de linha), raios, bordas, separadores, materiais, transparência, sombras/elevação, cores, ícones, indicadores de estado, tabelas, cards, botões, inputs, sidebar, navegação, estados vazios/carregamento/erro/sucesso/aviso, diálogos, formulários, feedback, Light e Dark, e estados de interação (hover, pressed, focus, disabled, selected), bem como os fluxos de navegação mostrados. "Aproximadamente parecido" não é aceite quando o WinUI permite reproduzir o desenho.

**Exceções deliberadas (as únicas gerais):**
1. **Tipografia** — o Figma usa SF Pro; no Windows usa-se **Segoe UI Variable** (o próprio Manual, pág. 06, manda adaptar à tipografia nativa). Calibrar tamanho, peso, altura de linha e espaçamento para preservar a hierarquia. SF Pro **nunca** é distribuído no Windows.
2. **Liquid Glass** — sem hacks frágeis; usar Mica, Acrylic, transparência suportada, composition quando apropriado e **fallback opaco**, convergindo o máximo possível com o Figma.
3. **Acessibilidade/plataforma** — acessibilidade, navegação por teclado, High Contrast e limites reais da plataforma podem exigir alterações discretas, sem mudar desnecessariamente a linguagem visual.
4. **Responsivo/localização** — larguras, strings longas, pt-BR, en-US, HC e conteúdo extremo não representados no Figma: comportamento robusto mantendo a intenção visual.
5. **Funcionalidades reais sem design** — nunca desaparecem por falta de frame; recebem desenho coerente com a mesma linguagem visual.
6. **FUTURE_PRO** — elementos M15–M19 não são ativados sem autorização.

### 0.3 Ordem de autoridade em conflitos
1. Invariantes funcionais / segurança do produto (§5);
2. Acessibilidade e limitações sustentáveis da plataforma;
3. Figma visual.

Fora desses conflitos, **o Figma é a referência**. Qualquer desvio de UX desenhada é **reportado antes** de ser feito.

### 0.4 Decisões D1–D8 (resolvidas)
| ID | Decisão |
|---|---|
| **D1 Tipografia** | Windows: **Segoe UI Variable**. SF Pro = referência de design/macOS. Inter só onde já é aproximação deliberada de layout nos widgets. |
| **D2 Funcionalidades M14** | **Todas sobrevivem.** Figma incompleto não autoriza remover comportamento: SSH direto, import de ssh-config, ProxyJump, confiança SSH, known-hosts, auto-deteção de chaves, checklist do teste de ligação, diagnósticos, helper de preparação, mDNS/descoberta, backup/restore cifrado, settings existentes, Modo compacto, Windows Widgets. |
| **D3 Navegação** | Adotar a nova IA/sidebar do Figma, **por milestones**, sem mega-rewrite. |
| **D4 Free → Pro** | **Não apresentar** "Free · Conhecer Pro", paywalls, compra, ativação, licenciamento ou gating Pro. M15 não autorizado. Só é permitido um extension point técnico invisível/inativo, se necessário. |
| **D5 Editor** | Destino final: Adicionar/Editar como **página inteira**, conforme Figma — **só no UI.7**; não antecipar. |
| **D6 Copy pt-PT** | pt-PT usa **"tu"**. Uniformizar gradualmente a copy antiga durante o rebuild; sem alteração global fora de âmbito. |
| **D7 Liquid Glass** | Abordagem nativa/sustentável do Windows, com fallback **opaco**, **High Contrast** e para ambientes sem material disponível. |
| **D8 Cor** | O **Manual da marca é autoridade**. Base neutra; cor **só quando comunica informação**. `#1846E1` deixa de ser accent global da nova presentation layer (fica apenas como alias de compatibilidade durante a migração; nenhuma dependência visual nova no azul antigo). Valores oficiais em §3.1. |

Classificação usada: **KEEP_CORE** (lógica correta, preservar) · **RESTYLE** (só apresentação) · **REBUILD_UI** (UI refeita, lógica reutilizada) · **GAP** (comportamento desenhado que ainda não existe) · **FUTURE_PRO** (M15+, não implementar) · **OUT_OF_SCOPE** (ex.: macOS).

---

## 1. Executive summary

1. **O Figma não é um restyle, é uma nova arquitetura de informação.** A app atual é *uma* página (Dashboard com cards) + Settings, com History/Workloads acessíveis pelo menu de cada card e o editor num modal. O Figma 05 define um **shell com sidebar** (Visão geral · Servidores · Histórico · Definições), uma **página de lista de servidores** (tabela), uma **página de detalhe do servidor**, **Serviços e containers como sub-página do detalhe**, **Adicionar/Editar servidor como página inteira**, e um **onboarding de 3 passos**. Estes são **REBUILD_UI/GAP** de navegação, não de estilo.
2. **O core está pronto para isto.** Os ViewModels já não dependem de WinUI (só 2 fugas: `InfoBarSeverity` e `Visibility`), Core/Infrastructure nunca tocam XAML, e todas as invariantes de M14 (trust SSH, ProxyJump, import fail-closed, backup/write-gate, entitlement) estão garantidas **abaixo** da UI com testes fortes. O rebuild pode ficar confinado a `Views/`, `Controls/`, `Styles/`, `MainWindow.xaml`, `Resources/*.resw` e a um novo shell.
3. **O Figma está atrás do produto em várias áreas M14.** Não há design para: **Backup/Restore cifrado (M14.6)**, **descoberta mDNS (M14.5)**, **auto-deteção de chaves**, **helper "Como preparo o servidor?"**, **checklist de 4 passos para ligação direta** (o Figma só desenha a checklist para ProxyJump), nem **seletor de idioma com pt-BR/en-US**. Estes comportamentos **não podem desaparecer** no rebuild → resolvido por **D2**: todos sobrevivem (§0.4).
4. **O Figma tem inconsistências internas** que têm de ser resolvidas antes de tokenizar: ecrãs em **SF Pro** mas estilos de texto em **Manrope**; variáveis em coleções de **modo único** (Dark e Liquid Glass como coleções separadas, não modos); muitos ecrãs usam hex crus não ligados a variáveis; tom mistura **"tu"** (onboarding, formulários) e **"você"/imperativo formal** (validação, importação SSH, Pro).
5. **Página 06 = página 05 + extensões Pro.** Todas as secções partilhadas diferem apenas no badge da sidebar (`Free · Conhecer Pro` ↔ `Pro · Licença ativa`) e nos separadores extra do detalhe (Métricas, Hardware, Docker, Proxmox, Processos, Alertas, Disponibilidade). Consequência arquitetural: **uma única presentation layer**, com dois pontos de extensão (slot de tier na sidebar, slot de separadores no detalhe). Tudo o que é M15–M19 é **FUTURE_PRO**.
6. **Tamanho/risco:** grande no total (≈ 8–10 PRs), mas decomponível. Risco **ALTO** concentrado em 2 áreas: `ServerFormControl`/editor (segurança, 791 linhas XAML, testes que leem o XAML) e `MainWindow`/shell/compacto (code-behind de janela, DPI, tray, deep-link). Tudo o resto é MÉDIO/BAIXO se o core não for tocado.
7. **Primeiro milestone recomendado: UI.1 — Fundações de tokens (sem mudança visual)**. Pequeno, reversível, desbloqueia tudo.

---

## 2. Matriz Figma ↔ implementação atual (página 05 · Free)

Legenda de risco de regressão: **B** baixo · **M** médio · **A** alto.

| # | Secção Figma → fluxo/ecrã | Implementação atual | Views / VMs / componentes | Serviços/core existentes | Diferença visual | Diferença funcional | Risco | Classificação → ação |
|---|---|---|---|---|---|---|---|---|
| 01 | **Onboarding** 1 Bem-vindo · 2 Ligação local · 3 Primeiro servidor ("Adicionar manualmente" / "Importar de SSH" / "Explorar primeiro"; "Agora não") | **Não existe** wizard. Existe empty state no Dashboard com "Adicionar" + "Importar do SSH" + descoberta mDNS (M14.5) | `DashboardPage`, `EmptyStateControl`, `DashboardViewModel` | `ServerDialogService`, `ISshConfigImportSource`, discovery | Total (ecrã inteiro, paginação 1/2/3) | **GAP**: 3 passos, flag "onboarding visto" | M | **GAP** → UI.6. Precisa de 1 preferência persistida (não-sensível). Reutiliza comandos Add/Import existentes. |
| 01 | **Primeira utilização · Dashboard vazia** | Empty state do Dashboard | `EmptyStateControl` | discovery (mDNS) | Figma sem secção de descoberta | Figma **omite** descoberta mDNS | M | **RESTYLE** + manter descoberta (decisão D2) |
| 02 | **Visão geral** (Minimal Dark / Liquid Glass): "Saúde geral 4 de 6 saudáveis" com segmentos, card de alerta ("Disco quase cheio 92% prod-db-01"), lista simples de servidores com estado + chevron, "Procurar" | Dashboard = lista de `ServerFullCard` densos com métricas, menu "…" por card | `DashboardPage`, `ServerFullCard`, `ServerActionsButton`, `ServerCardViewModel` | `IServerMetricsStore`, `IServerMonitoringStateStore`, `IMonitoringEngine` | Muito diferente: sumário agregado + lista leve | **GAP**: agregado de saúde, card "atenção" (pior métrica), pesquisa. Dados já existem nos stores | M | **REBUILD_UI** (VM de agregação nova, puramente derivada) → UI.4 |
| 02 | **Servidores** (tabela SERVIDOR/SISTEMA/ESTADO/CPU/RAM/DISCO, "Ver detalhe", pesquisa, contagens, "servidores ocultos restauram-se nas Definições") | **Não existe** página de lista | — | mesmos stores | Nova página | **GAP**: página + pesquisa/filtro | M | **GAP/REBUILD_UI** → UI.4. Reusa `ServerCardViewModel` (unknown≠zero, stale) |
| 03 | **Detalhe do servidor** (breadcrumb, cabeçalho com SO/IP/estado, Atualizar/Editar, 3 cards CPU/Mem/Disco com barras, uptime/última atualização/intervalo, card Ligação, card Explorar → Histórico / Serviços e containers) | **Não existe**; parte da info está no `ServerFullCard` | `ServerFullCard`, `ServerCardViewModel` | stores, `IRefreshAllCoordinator` | Nova página | **GAP**: página de detalhe; ações Editar/Atualizar já existem | M | **GAP/REBUILD_UI** → UI.5 |
| 03 | **Histórico** (seletor de servidor, 1h/6h/24h/7d/30d, 3 gráficos com eixo Y 0/50/100, eixo temporal, "Pico no período", legenda de período, "Guardado neste dispositivo durante 30 dias") | `HistoryPage` por servidor (via menu do card) com 3 `GlassCard` + `HistoryChart` sem eixos | `HistoryPage`, `HistoryViewModel`, `HistoryChart`, `HistoryChartGeometry` | `IServerHistoryQueryService` (M10) | Eixos, labels, pico, legenda | **GAP pequeno**: "pico no período" (derivável dos dados carregados); sidebar "Histórico" precisa de seletor de servidor | B-M | **RESTYLE** + pequeno GAP → UI.3 |
| 03 | **Serviços e containers** (+ "Com problemas"): pesquisa, filtro Todos/Com problemas, tabelas NOME/IMAGEM · ESTADO/SAÚDE e NOME/DESCRIÇÃO · ESTADO/ARRANQUE, rodapé "modo de leitura" | `WorkloadsPage` (Docker + serviços, pesquisa, filtro todos/a correr/falhados, 5 estados de mensagem) | `WorkloadsPage`, `WorkloadsViewModel`, `ContainerRowViewModel`, `ServiceRowViewModel` | workload collectors (M11), read-only | Tabela vs lista; densidade | Filtro "Com problemas" ≈ "falhados"; coluna "Arranque" (enabled/static) — verificar se o coletor já expõe | B-M | **RESTYLE** → UI.3 (verificar coluna Arranque; se não existir → GAP pequeno, não inventar) |
| 04 | **Adicionar servidor** (página inteira: "O teu servidor" + "Como te queres ligar?" segmentado Chave/Palavra-passe + Porta/SO/Intervalo + checkbox jump host + Testar ligação + Cancelar/Adicionar; nota Credential Manager) | `ServerEditorModal` + `ServerFormControl` (modal overlay, formulário único empilhado) | `ServerEditorModal`, `ServerFormControl`, `ServerEditorViewModel` (1634 linhas) | `ISshConnectionService`, `IServerValidator`, Credential Manager | Modal → página; layout 2 colunas; segmentado | Figma **omite** seletor de chaves auto-detetadas e helper "Como preparo" (M14.5) | **A** | **REBUILD_UI** (VM intacto) → UI.7. Manter auto-deteção/prep (D2) |
| 04 | **Importar de SSH** (painel com perfis, estado Disponível/Selecionado, "Usar perfil", preenche só campos vazios) | Painel de import dentro do `ServerFormControl` (M14.4a/c) | `ServerFormControl`, `SshConfigHostOptionViewModel` | `SshConfigResolver` (fail-closed), `ISshConfigImportSource` | Painel lateral/overlay vs inline | Igual em regra ("preenche só campos vazios" — **confirmar** que o VM atual respeita esta semântica; se não, é GAP e não um restyle) | **A** | **RESTYLE** + verificação → UI.7 |
| 04 | **ProxyJump · Adicionar / Importação e dados preservados** (secção Jump host, rota "Este dispositivo → bastion → 10.0.0.5") | Secção ProxyJump no formulário (M14.4b/c) | `ServerFormControl`, `ServerEditorViewModel` (jump fields) | `routed-servers.json`, `known-hosts.routes.json`, túnel loopback | Diagrama de rota textual | Nenhuma (rota = texto derivado) | **A** | **KEEP_CORE + RESTYLE** → UI.7 |
| 05 | **Editar servidor** (+ mudar para palavra-passe, alterações por guardar, remover servidor…) | Mesmo modal em modo edição; segredo guardado com checkbox "remover" | idem | idem | Página; "Voltar ao detalhe" | **GAP**: deteção de "alterações por guardar" + confirmação "Descartar alterações?" (sec. 11) | **A** | **REBUILD_UI** + GAP dirty-tracking → UI.7 |
| 06 | **Validação dos formulários** (obrigatórios, endereço/porta inválidos, palavra-passe em falta, chave incompatível, **servidor duplicado**, nova credencial em falta, **falha ao guardar**; ProxyJump) | InfoBar de validação Assertive + erros por campo via `IServerValidator` | `ServerFormControl`, `ServerEditorViewModel.TryCreateResult` | `IServerValidator` | Erro inline por campo + banner | **Verificar** cobertura: "servidor duplicado" e "chave incompatível" podem não existir como validações dedicadas | M-A | **RESTYLE** + possíveis GAPs → UI.7 (auditar códigos de validação vs Figma antes) |
| 07 | **Importação SSH — problemas** (sem perfis, ficheiro não encontrado, falha de leitura, config inválida c/ linha, perfis não suportados, alguns indisponíveis, já adicionado, Include não verificado; ProxyCommand / vários jumps / ambíguo / não resolvido) | Classificação do resolver M14.4a/c já existe (bloqueios fail-closed) | `SshConfigHostOptionViewModel`, `ServerEditorViewModel` | `SshConfigResolver`, `SshConfigModels` | Cartões de estado dedicados | **Verificar** "Servidor já adicionado" (dedupe vs lista) e "Linha 12" (nº de linha do erro) | **A** (segurança) | **KEEP_CORE + RESTYLE**; os casos não cobertos são GAP → UI.7. **Nunca** relaxar um bloqueio para caber no design |
| 08 | **Teste de ligação** direto: A testar / Verificada / Não foi possível autenticar / Cancelado (painel modal simples) · ProxyJump: checklist 6 passos (jump acessível → identidade jump → auth jump → final acessível → identidade final → auth final) | Checklist de **4 passos** (M14.5) com `SshConnectionStage`, `ConnectionHint{code}`, detalhe copiável; ProxyJump integrado | `ConnectionChecklistViewModel`, `ConnectionStepViewModel`, `ConnectionDiagnosis` | `ISshConnectionService` (stages), 27 códigos × 3 culturas | Figma direto é **mais pobre** que o atual | Figma perde diagnóstico por passo e hints no caminho direto | **A** | **KEEP_CORE + RESTYLE** usando o padrão de checklist do Figma ProxyJump também no direto (D2) → UI.7 |
| 09 | **Confiança SSH** (confirmar identidade, identidade mudou → bloqueado; ProxyJump passo 1/2 jump + 2/2 final, chave do jump mudou, chave do servidor mudou) | Painéis host-key desconhecida / mismatch (fail-closed, sem override); trust routed separado | `ServerFormControl` (painéis), `ServerEditorViewModel.TrustAndConnectAsync` (**único escritor de trust**) | `IHostKeyTrustStore`, `IRoutedHostKeyTrustStore`, SSH.NET `CanTrust=false` | Modal dedicado, fingerprint destacado | Nenhuma de segurança — Figma confirma o modelo atual (sem override em mismatch) | **A** | **KEEP_CORE + RESTYLE** → UI.7 (+ Vigil obrigatório) |
| 10 | **Definições** (Aparência/Tema, Idioma "usar idioma do sistema", Monitorização: segundo plano + alertas, Janela: modo compacto + sempre no topo, "Guardado automaticamente") · **Dados e servidores** (ocultos/restaurar, dispositivos ignorados/repor, limpar histórico, Sobre/GitHub/versão/MIT) | `SettingsPage` = 1 card com 9 secções + 13 InfoBars | `SettingsPage`, `SettingsViewModel`, `BackupRestoreViewModel`, `WindowModeViewModel` | theme/notifications/background/history maintenance/discovery services | 2 sub-páginas agrupadas | Figma **omite Backup/Restore** e "repor histórico"; idioma sem lista pt-BR/en-US | M-A | **REBUILD_UI** (layout em 2 sub-páginas, VMs intactos) → UI.5. Backup mantém-se com primitivas Figma (D2) |
| 11 | **Confirmações** (remover servidor, limpar histórico, descartar alterações) | `RemoveServerDialog` (Premium), diálogos de histórico **sem estilo** (`HistoryMaintenanceService`) | dialogs + services | — | Estilo unificado | "Descartar alterações?" não existe (liga a dirty-tracking de 05) | B-M | **RESTYLE** (+ GAP descartar) → UI.2 primitiva `DialogShell`, aplicada por página |
| 12 | **Feedback das ações** (toasts in-app: adicionado, guardado, perfil importado, restaurado, removido, histórico limpo, dispositivos repostos, ocultado) | InfoBars de sucesso/erro dispersos (Settings) — não há padrão de toast in-app | vários | — | Padrão de "toast" in-app com ✓ e × | **GAP**: serviço de feedback transitório | M | **GAP** → UI.2 primitiva `InlineNotice`/`ToastHost`; adoção página a página |
| 13 | **Estados vazios e indisponíveis** (servidores: nenhum / pesquisa sem resultados; histórico: sem dados / período sem dados; serviços: nenhum / indisponível / pesquisa) | Cobertos em History/Workloads/Dashboard; "Período sem dados → Ver últimos 30 dias" não existe | `EmptyStateControl` + empties ad hoc | — | Uniformizar | Pequeno GAP (ação "ver 30 dias") | B | **RESTYLE** → primitiva `EmptyState` (UI.2) |
| 14 | **Carregamento e primeira leitura** (dashboard, servidores, detalhe "A ligar… / A recolher métricas…", histórico, serviços) | History/Workloads/cards têm loading; **Dashboard não** (`LoadAsync` não expõe `IsLoading` → empty state pode piscar) | `DashboardViewModel` | — | Skeleton/placeholder | **GAP**: `IsLoading` no Dashboard | M | **GAP pequeno** (VM aditivo, null-safe p/ testes `GetUninitializedObject`) → UI.4 |
| 15 | **Modo compacto** (painel ~300 px, "Expandir", N servidores + "Atualizado há", linhas com CPU/RAM/DISCO + barras, "Sempre no topo" toggle no rodapé; vazio "Adicionar servidor") | `MainWindow` root Compact (320–400 px) + `ServerCompactCard` | `MainWindow`, `WindowModeViewModel`, `ServerCompactCard` | `WindowModeCoordinator`, `WindowSizeConstraints` | Próximo; toggle move-se para rodapé; barras coloridas | Nenhuma | **A** (code-behind de janela/DPI) | **RESTYLE** → UI.8 (respeitar 320–400 px; medir 100/150/200% DPI) |
| 16 | **Windows Widgets — V3 "família macOS"** (S/M/L × saudável / ocorrências / sem dados recentes / vazio; anel N/N, barras coloridas por métrica, blocos por servidor, "+ Adicionar servidor") | Adaptive Card 1.6 S/M/L (só `TextBlock`/`ColumnSet`/`Container`, medidores `▮▯`, accent = accent do sistema) | `WidgetProvider/Rendering/*`, `WidgetViewModelBuilder` | `WidgetSnapshotRecorder` (JSON) | Anel e barras coloridas não são primitivas AC | Ação "Adicionar servidor" no vazio (hoje só deep-link) | **A** (o host corta silenciosamente; lição M13) | **REBUILD_UI com spike de viabilidade** (imagens SVG/data-URI no host? medir no board real) → UI.9, por último |
| 17 | macOS — Monitor, Notch, Barra de menus | — | — | — | — | — | — | **OUT_OF_SCOPE** (e "Limites por servidor · Pré-visualização Pro" = FUTURE_PRO) |
| 18 | **Free → Pro — compra e ativação** (plano, código, a verificar, inválido, entrega por email, Store, privacidade) | — | — | M14.2 seam (`IEntitlementProvider`) | — | — | — | **FUTURE_PRO (M15)** — não apresentar (D4) |
| 19 | **Ativação — preenchimento e recuperação** (Store procurar/não encontrada/sem ligação/restaurada, primeiro acesso Pro) | — | — | — | — | — | — | **FUTURE_PRO (M15)** |
| — | **Sidebar**: Visão geral · Servidores · Histórico · *Free · Conhecer Pro* · Definições | Sem navegação persistente: header do Dashboard + botões "voltar" + `Frame.Content =` sem back stack | `MainWindow`, `NavigationService` | — | Shell novo | **GAP**: navegação persistente, back stack, estado selecionado. "Conhecer Pro" = FUTURE_PRO | **A** | **REBUILD_UI** → UI.6 (shell) |

### 2.1 Página 06 · Pro

| Secção Figma | Diferença vs 05 | Classificação |
|---|---|---|
| 01–16 partilhadas | Só badge da sidebar (`Pro · Licença ativa`) + separadores extra no detalhe (Métricas, Hardware, Docker, Proxmox, Processos, Alertas, Disponibilidade) | Presentation layer **partilhada**; os slots de extensão existem, conteúdo = FUTURE_PRO |
| 17 macOS | + "Limites de consumo" editáveis | OUT_OF_SCOPE / FUTURE_PRO |
| M15 · Licença e privacidade (licença ativa, Pro ativado, privacidade com opt-in **Aptabase**, compra restaurada) | — | **FUTURE_PRO**. Nota: a telemetria opcional desenhada **não está autorizada**; nada disto entra no rebuild |
| M16 · Métricas avançadas e hardware (rede RX/TX, load, I/O, temperatura, SMART/NVMe, sensores indisponíveis) | — | **FUTURE_PRO** |
| M17 · Docker (CPU/RAM por container, logs), Proxmox | — | **FUTURE_PRO** |
| M18 · Processos | — | **FUTURE_PRO** |
| M19 · Histórico 90 d, limiares por servidor, destinos de alertas (ntfy/webhook), disponibilidade (endpoints) | — | **FUTURE_PRO** (webhooks/destinos = saída de rede → reclassificar como SECURITY quando chegar) |
| UX · Preferências, validação e feedback (Pro) | Mesmos padrões de feedback/validação do 05 | Padrões **reutilizáveis** (validam as primitivas `FormField`/`InlineNotice`); conteúdo FUTURE_PRO |

**Componentes partilháveis Free/Pro identificados:** sidebar + slot de tier; cabeçalho de página com breadcrumb e ações; card de métrica (valor grande + barra/sparkline); tabela com pesquisa + filtro segmentado; toast de feedback; diálogo de confirmação; formulário (FormField + segmentado + toggle "usar X"); empty/loading/error state; sub-página de definições com "‹ Voltar". Todos devem nascer em Community (MIT) sem conhecimento de Pro — Pro apenas os consome via a seam M14.2 (dependência privado → público, nunca o inverso).

---

## 3. Inventário de componentes (atual → destino)

| Componente atual | Veredicto | Destino |
|---|---|---|
| `Styles/DesignTokens.xaml` (paleta, semântica, HC) | KEEP base + reorganizar | Dividir em `Tokens/Color.Primitives` + `Color.Semantic` (Light/Dark/HC); manter **todos os nomes de brush atuais como aliases**; eliminar dicionário `Default` (cópia do Dark) |
| `Styles/Controls.xaml` | RESTYLE | Botões com visual states (hover/pressed), um raio de controlo (12 no Figma), estilos para TextBox/ComboBox/PasswordBox/ToggleSwitch/segmentado |
| `GlassCard` | RESTYLE → `Surface` | `SystemBackdropElement` **sem backdrop atribuído** (o "acrylic por card" não existe; o vidro real é o `AcrylicBrush` in-app) — decidir material e corrigir |
| `MainWindow` (Standard) | REBUILD_UI (shell) | `AppShell` com sidebar + `ContentFrame`; preservar title bar, DPI, backdrop fallback, min size, tray, startup recovery |
| `MainWindow` (Compact) | RESTYLE | `CompactShell`; mesmos nomes (`CompactRoot`, `CompactBody`, `CompactDragRegion`, `CompactCaptionColumn`) |
| `DashboardPage` | REBUILD_UI | Visão geral (agregado) — lista detalhada passa para página Servidores |
| `ServerFullCard` | REPLACE (por linha de tabela + página de detalhe) | A lógica de formatação no `ServerCardViewModel` sobrevive; `FocusRing`/pulso de deep-link migra para a linha/detalhe |
| `ServerCompactCard` | RESTYLE | Barras por métrica com cores semânticas do Figma |
| `ServerActionsButton` | KEEP (transitório) | Na IA nova as ações passam para o detalhe; manter até UI.5 |
| `DiscoveredServerCard` | KEEP + desduplicar | Remover cópia inline em `DashboardPage.xaml:116-162` |
| `EmptyStateControl` | RESTYLE → `EmptyState` | Generalizar para todas as páginas e compacto |
| `HistoryChart` (+`HistoryChartGeometry`) | RESTYLE | Adicionar DPs de espessura/opacidade, eixos/labels fora do Canvas; **não reescrever** a geometria (testada) |
| `HistoryPage` | RESTYLE | + seletor de servidor, pico no período |
| `WorkloadsPage` | RESTYLE | Tabela; mesmo VM |
| `SettingsPage` | REBUILD_UI (layout) | 2 sub-páginas (Definições / Dados e servidores) + secção Backup com primitivas Figma |
| `ServerEditorModal` | REBUILD_UI | Página de editor (D5, só no UI.7); manter contrato Esc/Enter/foco |
| `ServerFormControl` (791 linhas) | REBUILD_UI (VM intacto) | Secções: Servidor · Autenticação · Ligação · Jump host · Teste · Confiança. **Último** a migrar |
| `AddServerDialog`, `EditServerDialog` | REPLACE → apagar | Código morto; manter as chaves resw que o modal lê |
| `RemoveServerDialog`, `RestoreConfirmDialog`, `BackupCreateDialog`, `RestoreOpenDialog` | KEEP + restyle via `DialogShell` | Higiene de passphrase e recusa de fecho durante busy são comportamento |
| Diálogos em C# (`BackupRestoreDialogService`, `HistoryMaintenanceService`) | RESTYLE | Passar a XAML, `ConfigureDialog` único (corrige guard de `XamlRoot` nulo em falta) |
| `ConnectionStateToBrushConverter` | apagar | Registado, nunca usado |
| `HealthToBrushConverter` | substituir | Congela o tema (lê `Application.Current.Resources`); usar padrão Style (como os converters de workload) |
| Tray (menu nativo Win32), toasts | KEEP | Fora do XAML; só strings |
| Widget Adaptive Cards | KEEP até UI.9 | Spike de viabilidade V3 antes de qualquer alteração |
| `Resources/{pt-PT,pt-BR,en-US}` (582 × 3) | KEEP | Paridade obrigatória em cada PR |

### 3.1 Tokens/primitives que o Figma implica

**Valores oficiais (D8 — Manual da marca, págs. 04–08; prevalecem sobre as coleções abaixo):**

| Papel | Dark | Light |
|---|---|---|
| Fundo (`canvas`) | `#141414` | `#E7E7E7` |
| Superfície (`panel`) | `#242424` | `#F7F7F7` |
| Interior (`inset`) | `#313131` | `#E8E8E8` |
| Texto | `#F5F5F5` | `#202020` |
| Secundário | `#ADADAD` | `#626262` |
| CPU | `#B69AF8` | `#8668CA` |
| Memória | `#7DB8FF` | `#427EC5` |
| Disco / atenção | `#FFC16E` | `#B87B2F` |
| Saudável / sucesso | `#53DAB1` | `#299985` |
| Erro / sem ligação | `#FF858D` | `#BD5766` |

- **Estado**: texto **e** indicador; a cor nunca comunica sozinha. Sem leitura: "—", nunca 0%.
- **Tipografia (Manual pág. 06)**: 44 Semibold (título de página) · 28 Semibold (métrica) · 20 Semibold (título de secção) · 14 Regular (corpo) · 12 Regular (legenda) — calibrar para Segoe UI Variable.
- **Geometria (Manual pág. 07)**: painéis raio 24, controlos 12; ritmo 4, 8, 12, 16, 24, 32.
- **Material (pág. 07)**: vidro subtil, contorno fino, sombra suave, **sem vidro dentro de vidro**; se perder contraste, aumentar a opacidade.
- **Ícones (pág. 08)**: **Hugeicons** de contorno, 20–24 px na navegação, 16–20 px em ações/campos. A app usa hoje Segoe Fluent Icons → decisão de fonte de ícones (licença/embutir) a tratar no UI.2, não no UI.1.

Coleções de variáveis encontradas no ficheiro (referência; os valores de neutros e "saudável" diferem dos oficiais acima):

- **Cor — Dark (`Primitives` → `ServerAlyzer · Dark`)**: canvas `#0C0E11`, sidebar `#101216`, surface `#171A20`, raised `#20242B`, border `#2B3039`, text `#F4F6F8`, muted `#9CA5B3`, subtle `#727D8D`; semânticas green `#B7F76B`, mint `#53DAB1`, purple `#B69AF8` (CPU), blue `#7DB8FF` (RAM), amber `#FFC16E` (atenção), red `#FF858D` (sem ligação); fundos tonais greenDark/amberDark/redDark.
- **Cor — Light (`ServerAlyzer · Liquid Glass`)**: canvas `#E7EFF4`, surface/sidebar/border `#FFFFFF`, raised `#DCE5EC`, text `#243347`, muted `#607185`; green `#288E70`, mint `#299985`, purple `#8668CA`, blue `#427EC5`, amber `#B87B2F`, red `#BD5766`.
- **Mapeamento de métricas**: CPU = purple, Memória = blue, Disco = amber (Manual pág. 05; a coleção macOS companion usa mint para disco — prevalece o Manual).
- **Espaço**: 8 / 16 / 24 / 32 (+4/12 na coleção companion). **Raio**: card 24, controlo 12, pill 99.
- **Tipografia (estilos de texto)**: Display 44 · Heading 30 · Title 18 · Body 14 · Caption 12 · Micro 10, altura de linha 140%. **Família**: Manrope nos estilos, **SF Pro nos ecrãs** — inconsistência (D1).
- **Material**: efeito `GLASS` (raio 10–24) + inner shadow 1 px (highlight de bordo) + drop shadow 10–44. WinUI 3 não tem refração por elemento: aproximação = `AcrylicBrush` in-app / Mica na janela + borda de highlight + `ThemeShadow`. Abordagem nativa com fallback opaco/HC (D7).
- **Comparação com hoje**: semânticas atuais (`#34D399/#FBBF24/#F87171`, marca `#1846E1`) **não coincidem** com o Figma; o Figma usa botão primário neutro (quase-preto/branco), sem azul de marca na UI. Resolvido por D8: Manual da marca é autoridade; `#1846E1` só como alias de compatibilidade.
- **Contraste**: os tons Dark são claros sobre fundo escuro (OK para texto); verificar AA para amber/red em Light (`#B87B2F` sobre `#FFFFFF` ≈ limite) no UI.1.

---

## 4. Gaps e inconsistências

### 4.1 Figma atrás do produto (comportamento M14 sem design)
| Comportamento existente | Onde vive hoje | Risco se o rebuild seguir o Figma à letra |
|---|---|---|
| Backup & Restore cifrado (M14.6) | Settings + 6 diálogos | **Perda de feature** e de fixes de foco/scroll (7195a40) |
| Descoberta mDNS + "Encontrados na rede" + ignorados (M14.5) | Dashboard | Perda de onboarding zero-friction (o Figma 01·Dashboard antigo tinha "2 servidores encontrados na rede local"; o 05 não) |
| Auto-deteção de chaves locais (metadata-only) | Formulário | Regressão de UX; a política de segurança fica intacta no core |
| Helper "Como preparo o servidor?" (comandos sem sudo) | Formulário (flyout) | Perda de feature |
| Checklist de 4 passos + `ConnectionHint{code}` no caminho **direto** | Formulário | Figma direto só tem 4 estados globais → perda de diagnóstico |
| Idiomas pt-BR/en-US no seletor | Settings | Figma só mostra "usar idioma do sistema" |
| "Repor histórico" (além de "Limpar") | Settings | Figma só "Limpar histórico" |

### 4.2 Produto atrás do Figma (GAP real, Community)
Onboarding 3 passos · shell com sidebar e back stack · página Servidores (tabela + pesquisa + filtros) · página Detalhe · Visão geral agregada (saúde N/M, card de pior métrica) · toasts de feedback in-app · "alterações por guardar" + "Descartar alterações?" · loading do Dashboard · Histórico: pico no período, eixos, seletor de servidor, "ver últimos 30 dias" · possivelmente: validação "servidor duplicado", "chave incompatível", import "já adicionado" e nº de linha em erro de config (**verificar antes de declarar GAP**) · widget V3 (anel, barras, ação adicionar).

### 4.3 Inconsistências internas do Figma
1. Fonte: SF Pro (ecrãs) vs Manrope (estilos) vs Segoe UI Variable (app). SF Pro não é licenciável para distribuição em Windows.
2. Variáveis em coleções separadas de modo único (Dark / Liquid Glass) em vez de uma coleção com modos Light/Dark → não mapeia 1:1 para `ThemeDictionaries`; muitos ecrãs usam hex crus (`#292929`, `#636363`, `#B8B8B8@13%`) não ligados a variáveis.
3. Nomes de tema: "Dark"/"White"/"Liquid Glass"/"light"/"dark" misturados; "Minimal · Liquid Glass" é na verdade Light.
4. Tom: "tu" (onboarding, formulários, definições) vs "você"/formal (validação: "Verifique", "Introduza"; importação: "Adicione", "Corrija"; Pro). Liga à decisão aberta de tom pt-PT.
5. Versão exibida "1.1.1"/"v1.1.0" nos mockups vs produto atual — ilustrativo, não usar.
6. Sem estados de **High Contrast**, **foco de teclado**, **larguras estreitas** (todos os ecrãs são 1440×1012) nem **escala de texto** — o rebuild tem de os definir (a app atual tem HC completo e mínimo 560×640).
7. Página 01/02 (Dashboard antigo, Manrope, marca própria) contradiz 05 — **05/06 prevalecem** conforme instrução.

### 4.4 Dívida da app atual encontrada na auditoria (não-funcional)
`SystemBackdropElement` inerte (GlassCard, modal) · tokens definidos e não usados (`GlassHighlightBrush`, `BrandAccentHover/Pressed`, `RadiusXLarge`, todos os `Spacing*`) · 184 `FontSize=` literais, 165 `Spacing=`, 40 `Padding=`, 14 `CornerRadius=` literais · 0 `VisualStateManager`/`AdaptiveTrigger` · sem `KeyboardAccelerator` (F5, Ctrl+N, Ctrl+,) · sem back stack (Alt+←) · HC mapeia Healthy/Warning/Critical para a mesma cor (texto compensa) · alt-text do widget no manifesto hardcoded em pt-PT · `DashboardViewModel.OnConnectionStateChanged` não marshalled (seguro hoje; registar, não mexer no restyle).

---

## 5. No-regression boundaries (não podem regredir)

**Fronteira de ownership do rebuild:** `Views/`, `Controls/`, `Styles/`, `MainWindow.xaml(.cs)` (só shell), `Resources/*.resw`, novo `Shell/`/`Controls/Primitives/`. **Fora de limites:** `ServerMonitor.Core`, `.Infrastructure`, `.Features`, `.Collectors`, persistência, trust stores, write gate, `App.ComposeFeatures`. Edições de VM limitadas a: (a) `SettingsViewModel` deixar de devolver `Visibility`; (b) `BackupRestoreViewModel` deixar de expor `InfoBarSeverity`; (c) propriedades **aditivas** e null-safe (ex.: `IsLoading`, agregados de saúde, dirty-tracking) — cada uma com teste.

| # | Invariante | Guardado por |
|---|---|---|
| 1 | Entitlement **nunca** condiciona Community; nenhum ecrã Community lê entitlement; o slot "Conhecer Pro" não entra antes de M15 | `FeatureCompositionTests`, `FeatureCompositionRootTests`, `CommercialCompositionSeamTests`, `CommunityBoundaryGuardTests`, `CommercialVocabularyBoundaryTests` |
| 2 | Trust SSH: host key **antes** da auth; sem auto-TOFU (direto e routed); mismatch **sem override**; único escritor = `TrustAndConnectAsync` acionado pelo utilizador | `SshConnectionServiceTests`, `SshConnectionServiceRoutedTests`, `ServerEditorRouteTests`, `ServerEditorViewModelTests` |
| 3 | Formatos: `servers.json` array legado (só diretos), `routed-servers.json` {schemaVersion 1}, `known-hosts.json` + `known-hosts.routes.json` | `JsonServerRepositoryRoutingTests`, `JsonRoutedHostKeyTrustStoreTests`, `CommercialVocabularyBoundaryTests` |
| 4 | Import ssh-config fail-closed (ProxyCommand, multi-jump, Match/Include não verificáveis, canonicalização) — a UI nunca oferece "usar mesmo assim" | `SshConfigResolverTests`, `SshConfigProxyJumpTests`, `SshConfigIncludeTests`, `ServerEditorSshConfigImportTests` |
| 5 | Deteção de chaves metadata-only | `LocalSshKeyDiscoveryTests`, `LocalKeyPathPolicyTests`, `ServerEditorLocalKeyTests` |
| 6 | `ConnectionHint{code}` × 3 culturas; hints nunca sugerem aceitar chave mudada nem sudo/root | `OnboardingLocalizationTests`, `ConnectionChecklistViewModelTests`, `ConnectionErrorLocalizationTests` |
| 7 | Backup: write gate (lease/hold/seal), restore re-keyed, recuperação journaled; botões de backup **enabled** durante execução (foco); Enter não atravessa o diálogo de restore | Core/Infra backup tests, `StartupRestoreRecoveryTests`, `ConfigurationLockedMappingTests` + **QA manual** (foco não tem teste) |
| 8 | Testes runtime-free (`GetUninitializedObject`): nomes de campos privados do `DashboardViewModel` e inicializadores null-safe | `DashboardDiscovery*Tests`, `CompactServerSourceTests`, `ConfigurationLockedMappingTests` |
| 9 | QA harness Debug (`--qa-*`), incl. isolamento `--qa-proxyjump --qa-proxyjump-dir` | `Qa*PolicyTests`, `*HarnessTests` (sem testes para `--qa-discovery/--qa-history/--qa-store-screenshot` → adicionar) |
| 10 | Contratos de texto XAML/resw: `x:Uid` (229 usos), `{Binding Preview}` em `ServerFormControl.xaml`, listas `XamlKeys`; boundary tests que varrem `*.xaml.cs` (topmost/terminate) | `SshConfigHostOptionViewModelTests:105`, `OnboardingLocalizationTests`, `*LocalizationTests`, `WatchdogOwnershipBoundaryTests`, `TopmostMutationBoundaryTests` |
| 11 | Segredos: `PasswordBox` **nunca** faz binding; capturado e limpo (`CaptureSecret`); passphrase de backup limpa em `Closed` | **sem teste** → QA + revisão Vigil |
| 12 | Unknown ≠ zero ("—"), stale ≠ health, "nunca mostrar uma pesquisa falsa" na descoberta | testes de VM de cards/discovery + QA visual |
| 13 | Compacto 320–400 × 168–560; standard mínimo 560×640; deep-link de widget/toast foca o servidor; pulso respeita `AnimationsEnabled` | `WindowSizeConstraints` tests + QA |
| 14 | Acessibilidade existente: `HeadingLevel`, `LiveSetting` (17 usos), `AutomationSummary`, foco no título "host desconhecida" | QA (Narrator) — sem teste automático |

---

## 6. Proposta de arquitetura da nova presentation layer

```
Styles/Tokens/        Color.Primitives.xaml   (Figma primitives, sem tema)
                      Color.Semantic.xaml     (ThemeDictionaries Light/Dark/HighContrast → aliases dos nomes atuais)
                      Typography.xaml         (Display/Heading/Title/Body/Caption/Micro/Mono)
                      Spacing.xaml · Radius.xaml · Elevation.xaml · Motion.xaml
Controls/Primitives/  Surface · StatusDot/StatusBadge · MetricBar · MetricCard · SectionHeader
                      EmptyState · InlineNotice · ToastHost · KeyValueRow · FormField
                      SegmentedControl · DataTable(row template) · CopyableCommand
                      DialogShell · ModalShell · Chart (HistoryChart evoluído)
Shell/                AppShell (sidebar + ContentFrame + ModalOverlayHost + ToastHost)
                      PageScaffold (breadcrumb, título, ações, ScrollHost nomeado)
                      CompactShell
Views/                Overview · Servers · ServerDetail · History · Workloads(sub de Detail)
                      Settings · SettingsData · ServerEditor · Onboarding
ViewModels/           inalterados + VMs de apresentação finos e derivados (ex.: OverviewSummary)
```

Princípios:
1. **Figma → tokens → primitivas → shells → páginas.** Cada camada só depende da anterior; primitivas não conhecem VMs (só DPs).
2. **VMs são o contrato estável.** Bindings mantêm nomes; novas propriedades são aditivas, null-safe e testadas.
3. **Cores por estado via Style/VisualState**, nunca via `Application.Current.Resources` (corrige o congelamento de tema).
4. **Navegação:** `INavigationService` evolui para `Frame.Navigate` com back stack e parâmetros tipados (serverId), mantendo `GoToDashboard/GoToSettings/History/Workloads` como fachada para os chamadores atuais (tray, toast, widget deep-link).
5. **Extensão Free/Pro:** `AppShell` expõe um slot de tier e `ServerDetail` um slot de separadores, preenchidos **só** por composição explícita (seam M14.2). Community compila sem ambos preenchidos. Nenhum descoberta dinâmica.
6. **Responsivo:** `AdaptiveTrigger` em 3 larguras (≥1200 sidebar expandida · 760–1199 sidebar compacta/ícones · 560–759 sidebar colapsável, colunas empilhadas). O Figma só desenha 1440 → os breakpoints são decisão de implementação, validados por QA.
7. **Liquid Glass com contenção:** Mica/DesktopAcrylic na janela; `AcrylicBrush` in-app para superfícies; highlight de 1 px + `ThemeShadow`; **nunca glass-on-glass**; fallback opaco quando transparência está desligada ou em HC.
8. **Galeria Debug** `--qa-gallery` (mesmo padrão `#if DEBUG` + `Compile Remove`) para verificar primitivas em Light/Dark/HC × pt-PT/pt-BR/en-US.
9. **Teste de contrato XAML** (XDocument) para os elementos nomeados de que o code-behind depende (`ServersItemsControl`, `FocusRing`, `BackupStatusBar`, `BackgroundSection`, `UnknownHostHeading`, `PassphraseBox`, `Compact*`, partes do chart).

---

## 7. Milestones recomendadas

| Milestone | Âmbito | Tamanho | Risco |
|---|---|---|---|
| **UI.1 — Fundações de tokens** | Tokens de cor (Manual, Light/Dark/HC), tipografia (Segoe UI Variable calibrada), espaço, raio, bordas, elevação/material (incl. fallback opaco/HC) e motion, com **aliases** de todos os nomes atuais e **sem aplicar** às páginas (zero visual drift); cleanup de código morto **só com prova** de zero uso; guard de contrato XAML; testes para `--qa-discovery/--qa-history/--qa-store-screenshot` | S (1 PR) | B |
| **UI.2 — Primitivas + galeria** | `Surface`, `StatusDot`, `MetricBar`, `MetricCard`, `EmptyState`, `InlineNotice`, `ToastHost`, `FormField`, `SegmentedControl`, `DialogShell`, `KeyValueRow`; `--qa-gallery`; substituir `HealthToBrushConverter` por Style; decidir material glass | M (1–2 PRs) | B-M |
| **UI.3 — History + Workloads** | Restyle das 2 páginas com primitivas; eixos/pico/legenda; tabela de serviços | M (2 PRs) | B-M |
| **UI.4 — Servers + Visão geral** | Página Servidores (tabela, pesquisa, filtros) e Visão geral agregada; `IsLoading` no Dashboard; descoberta mantida (D2); deep-link foca linha | M-L (2 PRs) | M |
| **UI.5 — Detalhe do servidor + Settings** | Página Detalhe (Workloads como sub-página); Settings em 2 sub-páginas com Backup em primitivas; diálogos em `DialogShell` | M-L (2 PRs) | M-A (backup) |
| **UI.6 — AppShell + Onboarding** | Sidebar, back stack, atalhos de teclado, `AdaptiveTrigger`; onboarding 3 passos com flag persistida | L (2 PRs) | A |
| **UI.7 — Editor de servidor** | Add/Edit como página inteira (D5); secções; import/ProxyJump/checklist/trust restyled; dirty-tracking + "Descartar alterações?"; feedback toasts; auditoria de validações vs sec. 06/07 | L (2–3 PRs) | **A** (Vigil obrigatório) |
| **UI.8 — Modo compacto** | `CompactShell` conforme sec. 15 | S-M | A (janela/DPI) |
| **UI.9 — Windows Widget V3** | Spike de viabilidade (imagens/anel no host real) → só depois rebuild dos templates | spike + M | A |
| M15–M19 | Licenciamento, métricas avançadas, Docker/Proxmox/logs, processos, alertas/disponibilidade, telemetria opcional | — | **FUTURE_PRO — não iniciar** |
| macOS (sec. 17) | — | — | **OUT_OF_SCOPE** |

## 8. Ordem de implementação

`UI.1 → UI.2 → UI.3 → UI.4 → UI.5 → UI.6 → UI.7 → UI.8 → UI.9`

Racional: risco crescente; cada página prova as primitivas antes da seguinte; o shell (UI.6) chega depois de as páginas já serem `PageScaffold`, para que a mudança de navegação seja só de host; o editor (UI.7, maior risco de segurança) vem com todas as primitivas já estáveis; compacto e widget no fim porque dependem de medição no host real e estão estáveis hoje. Nota: até UI.6, as páginas novas (Servers, Detalhe) são alcançáveis pela navegação atual (header + menus), sem sidebar.

## 9. Critérios de conclusão (por milestone)

**QA visual (a partir das milestones visuais, UI.2+)** — ciclo obrigatório: **Figma → implementação WinUI → screenshot real da aplicação → comparação visual lado a lado → refinamento → nova comparação**, até que aplicação e Figma lado a lado sejam praticamente o mesmo produto. Cada comparação regista: nó Figma (id), screenshot da app (harness `--qa-*`, tema, cultura, largura, DPI), diferenças encontradas e se cada uma é (a) corrigida, (b) exceção deliberada da §0.2 (com motivo) ou (c) desvio reportado. Light e Dark são ambos comparados; estados hover/pressed/focus/disabled/selected são verificados nos componentes que os têm no Figma. No **UI.1** (fundação) o critério é o inverso: **zero visual drift** face ao baseline anterior.

Comum a **todas**: build + testes completos pela mesma `.slnx` (Debug e Release, P-008) · 0 alterações em Core/Infrastructure/Features · paridade resw 3 culturas · QA visual `--qa-*` em Light/Dark/HC × pt-PT/pt-BR/en-US com PASS/FAIL/NOT_RUN explícito · 560×640 sem overflow · Narrator smoke nos ecrãs tocados · um PR por página.

| Milestone | Critérios específicos |
|---|---|
| UI.1 | Screenshots `--qa-*` **idênticos** antes/depois (sem mudança visual) · todos os nomes de brush antigos resolvem · teste de contrato XAML verde e **contraprovado** (renomear um elemento → falha) · testes dos 3 harnesses sem cobertura · código morto removido sem alterar chaves resw usadas |
| UI.2 | Galeria com todas as primitivas em 3 temas; contraste AA medido para texto e estados (incl. amber/red Light); primitivas sem referência a VMs (teste de arquitetura); troca de tema em runtime reflete-se nos status dots |
| UI.3 | Estados empty/loading/error/offline/stale de History e Workloads comparados com sec. 13/14; geometria do chart inalterada (testes existentes); VMs transient continuam a fazer `Dispose` |
| UI.4 | Unknown≠zero e stale visíveis na tabela; descoberta mDNS preservada; sem flash de empty state (IsLoading testado); deep-link de widget/toast foca o servidor certo; testes `GetUninitializedObject` verdes |
| UI.5 | Backup/restore: QA M14.6 (foco/scroll, busy, passphrase limpa, Enter não atravessa) repetida PASS · Settings persistem como antes · Workloads acessível a partir do Detalhe |
| UI.6 | Navegação por teclado completa (Tab/Alt+←/atalhos), back stack correto, tray/toast/widget aterram na página certa, onboarding só na 1.ª execução e dispensável ("Agora não"), 3 breakpoints verificados |
| UI.7 | Revisão **Vigil APPROVED** · Atlas PASS · invariantes 2/4/5/6/11 com testes verdes · `--qa-proxyjump` isolado 9/9 PASS repetido · nenhum bloqueio de import relaxado · PasswordBox nunca em binding (revisão) |
| UI.8 | 320/360/400 px × 100/150/200% DPI sem corte · always-on-top e expandir funcionam · `TopmostMutationBoundaryTests` verde |
| UI.9 | Spike documentado com medição no board real (Win+W) para S/M/L; só avança se o host renderizar sem corte; senão, restyle limitado ao que AC 1.6 garante |

---

## 10. Decisões

Todas as decisões D1–D8 levantadas nesta auditoria foram **resolvidas pelo utilizador** em 2026-09-30 — ver §0.4. Novas decisões que surjam durante UI.1–UI.9 são reportadas no checkpoint da milestone respetiva (ex.: fonte de ícones Hugeicons vs Segoe Fluent Icons, a decidir no UI.2).

---|---|---|
| **D1** | Fonte: Figma usa SF Pro (ecrãs) / Manrope (estilos); app usa Segoe UI Variable | **Segoe UI Variable** (nativa, sem licença, melhor hinting em Windows). SF Pro não é licenciável aqui. Manrope (OFL) só se a marca o exigir — implica embutir a fonte |
| **D2** | Features M14 sem design (backup/restore, descoberta mDNS, auto-deteção de chaves, helper de preparação, checklist direto de 4 passos, idiomas explícitos, repor histórico) | **Manter todas**, desenhadas com as primitivas do Figma; idealmente pedir ao design que adicione estes ecrãs às páginas 05/06 antes de UI.4/UI.5/UI.7 |
| **D3** | Adotar a nova IA (sidebar; Workloads como sub-página do Detalhe; lista de servidores como página) | **Sim**, faseado conforme §7 |
| **D4** | Badge "Free · Conhecer Pro" na sidebar Community | **Não incluir** até M15 estar autorizado (slot existe, vazio) |
| **D5** | Editor: página inteira (Figma) vs modal grande | **Página** conforme Figma, mas só em UI.7; até lá, modal atual restyled |
| **D6** | Tom pt-PT: "tu" vs "você" (o próprio Figma mistura) | Decidir **antes de UI.3** (afeta strings novas); recomendação: "tu" (maioria do Figma 05 e onboarding M14.5) |
| **D7** | Fidelidade Liquid Glass (WinUI não refrata por elemento) | Aceitar aproximação Acrylic/Mica + highlight + sombra, com fallback opaco |
| **D8** | Accent: marca `#1846E1` atual vs UI neutra do Figma (botão primário quase-preto/branco) + nova paleta semântica | Seguir Figma 05 (neutro + semânticas novas); confirmar com o Manual da marca (página 04) |

---

*Fontes locais de trabalho (não versionadas): `.boss/tmp/ui0/prism-ui-inventory.md`, `.boss/tmp/ui0/cortex-coupling.md`.*
