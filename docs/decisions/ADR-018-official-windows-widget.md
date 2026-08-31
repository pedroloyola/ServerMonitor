# ADR-018 — Widget oficial do Windows (M13)

Estado: **aceite** (2026-08-31). A arquitetura desta ADR está integralmente implementada no ramo
`feature/m13-windows-widget` (5 commits), revista de forma independente com **C0/H0/M0** por segurança
(Vigil), fiabilidade (Atlas) e visual (Prism), e **validada num host real de Widgets do Windows 11**
(descoberta, ativação COM, render Small/Medium/Large em claro+escuro, atualização ao vivo, ativação
cold/warm/rápida com instância única, isolamento de rede do provider com zero SSH, create/delete/
re-create). O pacote de produção (1.1.0.0) já transporta o widget e foi auditado extraído; o que falta é
ambiente — arte final do picker e QA do pacote de produção instalado (ver «Integração de release» e «Em
aberto» no fim).

O M13 integra o ServerAlyzer com o ecossistema oficial de **Widgets do Windows 11** (painel de
Widgets), distinto do Modo Compacto (ADR-014, uma janela WinUI própria). Constrói sobre M1–M12
e **não** adiciona monitorização nova nem inicia macOS desktop. Este documento é graduado a partir da
investigação da Fase 0 e é atualizado à medida que cada slice da Fase 1 se prova em runtime/review.

## Contexto

Os widgets do Windows 11 são servidos por um **provider COM out-of-process** (`IWidgetProvider`, Windows
App SDK `Microsoft.Windows.Widgets.Providers`) e renderizados como **Adaptive Cards**. O host de Widgets
ativa o provider **independentemente da app principal**, inclusive com a app fechada.

Restrições duras do produto: o widget **não** pode iniciar uma segunda pilha de SSH/monitorização
(o provider é um consumidor de estado, não uma segunda engine), **não** pode expor segredos/PII, **não**
pode desestabilizar o modelo single-instance/ativação/notificações estabilizado no M12 (1.0.x), e tem de
degradar honestamente quando a app está fechada (frescura explícita).

## Decisão

1. **Provider num executável dedicado** `ServerAlyzer.WidgetProvider.exe`, empacotado no mesmo MSIX,
   implementando `IWidgetProvider` e registado por **CLSID próprio** (GUID novo, distinto do CLSID de
   ativação de notificações do M12). Referencia **apenas** uma pequena biblioteca de contrato read-only e
   **não** pode referenciar Core/Infrastructure/Collectors/App (sem SSH, sem engine).
2. **Snapshot JSON sanitizado, versionado e escrito atomicamente** como única fonte de dados:
   `%LOCALAPPDATA%\ServerMonitor\widget-state.json` (nome de pasta interno mantido por compatibilidade).
   Escrito pela app a correr, sobre o seam do observador de ciclo de monitorização (padrão do ADR-011/
   ADR-015), com *throttle*, escritor único e substituição atómica (temp + rename). Lido como **não
   confiável** pelo provider (defesa em profundidade na leitura).
3. **Adaptive Cards** em small/medium/large; a semântica de health do produto é mapeada para as cores AC;
   o azul de marca é apenas acento. *(Rendering final: slices posteriores.)*
4. **Clique no widget → ativação normal da app** (deep-link) através da única `AppInstance` existente — o
   provider nunca aloja UI, tray, DI ou engine. *(Ativação: slice posterior.)*
5. **App fechada = último snapshot conhecido + frescura explícita** (fresh/stale/unavailable). Sem novo
   daemon residente.

## Compatibilidade (Opção A — escolhida)

Um único pacote com `TargetDeviceFamily MinVersion=10.0.22000.0`. Declarar `windows.comServer` +
`uap3:AppExtension com.microsoft.windows.widgets` é oficialmente válido muito abaixo desse piso; o SO não
consome a extensão — só um host app (o painel de Widgets) a enumera, e apenas em builds com widgets de
terceiros (Win11 22H2 / build **22621.1413+**). Assim: em **22000** a app instala/corre e a extensão fica
**inerte** (a fábrica COM nunca é ativada); em **22621+** o widget fica disponível. **Não** se eleva o
MinVersion nem se cria segundo pacote. O gate é feito pelo host, não por código artificial.

## Fase 1 — Slice 1 (contrato + escritor atómico do snapshot)

Estabelece **apenas a fronteira de dados**. Sem alteração de manifesto/COM/packaging.

- **Assembly de contrato:** `ServerMonitor.WidgetContract` (net10.0, só BCL, sem referências a Core/
  Infrastructure/Collectors). Contém os DTOs de fio, o enum de health, os limites, o sanitizador de nome,
  o validador de leitura, o serializador (source-gen) e a abstração `IWidgetStateWriter`. O futuro
  provider referencia **só** este assembly. O mapeador domínio→fio vive na camada App (o contrato
  permanece um limite puro de leitura); o Core fica intocado.
- **Schema v1** (`schemaVersion = 1`, JSON camelCase, enums como strings, números invariantes):
  `WidgetStateSnapshot { schemaVersion, generatedAtUtc, overallHealth, servers[] }` e
  `WidgetServerState { id (GUID opaco), displayName, health, cpuUsagePercent?, memoryUsagePercent?,
  diskUsagePercent?, lastUpdatedUtc? }`, com `WidgetHealth = Unknown|Healthy|Warning|Critical|Offline`.
- **Minimização de dados:** incluídos apenas id opaco, nome amigável **sanitizado**, health normalizado,
  três percentagens 0–100 **ou `null` = desconhecido** (nunca 0 para desconhecido) e timestamps de
  frescura. **Excluídos por construção** (não existe campo): host/IP, porta, utilizador SSH, nome/versão
  de SO, hostname, referência de credencial, caminho de chave privada, host-key, saída bruta, nomes de
  serviços/containers/processos, comandos, logs e séries de histórico.
- **Nome de exibição:** `WidgetDisplayName.Sanitize` remove controlo C0/C1, formatadores Unicode
  (overrides/isolates bidi, joiners), surrogates, uso privado e não atribuídos, colapsa espaços, limita a
  60 unidades UTF-16 e **nunca** recai para IP/hostname técnico.
- **Health agregado (OverallHealth), determinístico e puro** — precedência
  `Offline > Critical > Warning > Unknown > Healthy`; frota vazia = Unknown. `Unknown` ("informação
  insuficiente") supera `Healthy`, pelo que a frota só é reportada Healthy quando nada está
  Offline/Critical/Warning/Unknown (ex.: Healthy+Unknown → Unknown). A health por servidor é a do domínio,
  mapeada 1:1 (nunca recalculada), pelo que o widget não pode divergir do dashboard; esta regra agrega
  apenas a OverallHealth do widget. Valores indefinidos canonicalizam para Unknown.
- **Seam e cadência:** `WidgetSnapshotRecorder : IMonitoringCycleObserver` liga-se ao
  `CompositeMonitoringCycleObserver` existente. **Sem** novo timer/`PeriodicTimer`/`DispatcherTimer`/loop
  de polling/worker independente — a única origem de acordar é a conclusão do ciclo. Um *throttle* de
  bordo de subida sobre o relógio **monotónico** do `TimeProvider` (`GetTimestamp`/`GetElapsedTime`;
  `GetUtcNow` só para `generatedAtUtc`) limita as escritas a ≤1 por intervalo (por omissão 15 s, metade do
  ciclo de 30 s). Um *drain* de escritor único com coalescing (`_dirty`/`_writing`/`_lastWriteTimestamp`/
  `_disposed` sob um único lock) relê o estado vivo no momento da escrita.
- **Persistência atómica:** `AtomicWidgetStateWriter` serializa → ficheiro temp único na mesma pasta →
  `WriteThrough` + flush → verificação de cancelamento → `File.Move(overwrite:true)` (substituição atómica
  no mesmo volume). Um leitor nunca vê ficheiro meio-escrito; uma escrita falhada/cancelada preserva o
  último-bom-conhecido e limpa o temp.
- **Isolamento de falhas e shutdown:** toda a falha (incl. `OperationCanceledException` espúria não ligada
  ao shutdown) é registada de forma grosseira (nunca o payload/nomes) e engolida no *drain*; nada chega ao
  ciclo de monitorização. `DisposeAsync` é idempotente e *bounded* (`WaitAsync(timeout, TimeProvider)`).
- **Não confiável na leitura:** `WidgetStateSerializer.TryDeserialize` falha neutro (null) em qualquer
  input malformado; `WidgetStateValidator` valida versão de schema, intervalos de timestamp, contagem ≤
  `MaxServers` (100), id não-vazio, nome sanitizado, enums definidos e percentagens finitas em [0,100].

## Fase 1 — Slice 2 (fundação do provider COM out-of-process)

Estabelece o processo out-of-process que o host de Widgets ativa. Ainda **não** implementa o rendering
final S/M/L nem a ativação/deep-link da app (slices seguintes).

- **Projeto/executável:** `ServerMonitor.WidgetProvider` → `ServerAlyzer.WidgetProvider.exe`
  (`net10.0-windows`, self-contained no dev/unpackaged). Referencia **apenas**
  `ServerMonitor.WidgetContract` + o meta-package **Microsoft.WindowsAppSDK 2.3.1** (que resolve a
  composição oficial, incl. `Microsoft.WindowsAppSDK.Widgets 2.0.5`, `...Runtime 2.3.1` — **uma única
  linha de runtime**, decisão humana). **Não** referencia Core/Infrastructure/Collectors/App.
- **CLSID dedicado:** `78CFFBEF-7A95-4400-BB8B-A2376C6642C3` (distinto do CLSID de notificações
  `4B2E9C7A…`).
- **Reader não-confiável:** `WidgetSnapshotReader` impõe o **cap de tamanho ANTES de ler** (256 KB,
  `WidgetStateLocation.MaxFileBytes`), abre com `FileShare.ReadWrite|Delete` (não bloqueia o replace do
  writer), desserializa e valida via WidgetContract, e devolve sempre um estado neutro
  (Missing/Oversized/Corrupt/Invalid/IoError) — nunca lança.
- **Limpeza de temps órfãos:** `WidgetOrphanTempCleaner` apaga só o padrão de temp do writer, top-level,
  sem seguir reparse-points, best-effort, com exame limitado por varrimento.
- **Frescura (runtime, não persistida):** fresh/stale/unavailable a partir de `generatedAtUtc` vs relógio;
  limite de stale 90 s (≈3 ciclos). Stale ≠ unhealthy.
- **Lifetime COM (protocolo oficial):** `ComServerProcess` usa
  `CoAddRefServerProcess`/`CoReleaseServerProcess`/`CoSuspendClassObjects`. Cada objeto COM criado
  incrementa a referência do processo; ao chegar a zero, o COM suspende novas ativações atomicamente e o
  processo revoga (`CoRevokeClassObject`) e sai — o Windows relança na próxima ativação. O **registry de
  widgets NÃO decide o lifetime**. `GetWidgetInfos` reidrata no arranque (bounded), antes de registar a
  factory, com tombstones a impedir que um snapshot obsoleto ressuscite um widget eliminado.
  Neutral-on-exception em toda a fronteira COM (`WidgetProviderComAdapter`); a class factory devolve
  HRESULTs.
- **Template dev temporário:** Adaptive Card 1.5 mínimo e neutro (overall health + contagem + frescura;
  **sem nomes de servidores**), marcado "dev template" — o design final é uma slice posterior.
- **Manifesto:** extensões `com:ExeServer` (provider + CLSID) + `uap3:AppExtension`
  `com.microsoft.windows.widgets` (Activation `CreateInstance ClassId` + Definição small/medium/large)
  adicionadas ao **`Package.Dev.appxmanifest`** (QA DEV/local). MinVersion mantém-se **10.0.22000.0**
  (extensão inerte em 21H2, ativa em 22H2+). O manifesto de **produção/Store fica intocado** até à slice
  de release do widget; o fragmento de produção é idêntico (documentado). Sem nova capability
  (runFullTrust apenas). Sem submissão à Store.
- **QA:** smoke launch local confirma que o exe arranca, regista e auto-sai após o idle grace (code 0),
  exercitando o caminho `CoAddRefServerProcess`→`CoReleaseServerProcess`→0→`CoSuspendClassObjects`. A
  ativação COM real no board de Widgets é **NOT_RUN** — exige instalação packaged (dev-mode/admin) num
  board 22621.1413+; esta máquina é Windows 11 Home (build 26200/25H2 tem board, mas o install é gated).
- **Residuais aceites (documentados, para a slice de runtime-QA):** (1) o object-ref do processo é
  libertado num **finalizer** (não num hook determinístico do último `IUnknown::Release`), porque o COM
  gerido não expõe esse hook sem um `ComWrappers` custom; o GC-reclaim no idle checkpoint limita o
  zombie; o padrão documentado da Microsoft usa finalizer. (2) O drain do shutdown é **bounded** — uma
  chamada `host.Update` síncrona genuinamente presa além do timeout pode completar após o revoke
  (inofensivo, isolada por try/catch). Ambos exigem validação no board real (NOT_RUN aqui).

## Fase 1 — Slice 3 (UI final do widget / rendering Adaptive Card)

Constrói a experiência visual final para small/medium/large. Não implementa ainda ativação/deep-link da
app (Slice 4).

- **Pipeline (§24):** `WidgetSnapshot` → `WidgetViewModelBuilder` (puro) → `WidgetViewModel` →
  `WidgetCardRenderer` → Adaptive Card 1.5 (template self-contained, data `{}`). Ordenação em
  `WidgetOrdering`; localização em `WidgetStrings`.
- **Tamanhos (§6):** layouts distintos. **Small** = resumo (overall + counts + freshness, sem linhas de
  servidor). **Medium** = header + counts + freshness + até **3** servidores. **Large** = header + counts
  + freshness + até **6** servidores. Overflow = **"+N more"** (localizado). A linha de counts aparece em
  Medium E Large para nunca esconder uma severidade cortada pelo cap nem deixar o "hero" de estado ler
  como "app offline".
- **Ordenação (§10):** problemas primeiro — Offline > Critical > Warning > Unknown > Healthy — depois
  DisplayName ordinal, depois Id opaco (tiebreak total, estável entre updates).
- **Health (§4/§18/§39):** texto + cor, nunca só cor. AC semantic colours (host-themed light/dark, sem hex
  fixo): Healthy=`good`, Warning=`warning`, Critical/Offline=`attention` (distinguidos pelo LABEL), Unknown/
  neutral=`default`. Brand `#1846E1` = `accent` **só** no nome ServerAlyzer, nunca health.
- **Frescura (§12/§23):** texto relativo derivado de `generatedAtUtc` no render ("Updated 4 min ago"),
  distinto de health; stale nunca escala health. Sem timer novo — repinta no lifecycle existente.
- **Métricas (§19/§20):** percentagem inteira arredondada; null = "—" (nunca 0%); clamp [0,100]. Linha de
  métricas localizada (CPU/Memory/Disk).
- **Estados (§13/§14):** Empty (snapshot válido, 0 servidores) distinto de Unavailable (sem snapshot);
  ambos cards neutros válidos.
- **Privacidade (§15):** só nome amigável sanitizado (truncado em fronteira de rune; vazio → label neutra
  "Server"/"Servidor", nunca IP/host) + percents normalizados; Id opaco nunca renderizado.
- **Localização (§16/§17):** en-US/pt-BR/pt-PT via `WidgetStrings` leve (sem resx/stack da App); cultura
  de `CurrentUICulture`, default en; counts com concordância singular/plural. Segurança: card construído
  com `JsonNode` + `JavaScriptEncoder.Create(UnicodeRanges.All)` (acentos legíveis, structural/HTML chars
  escapados); formato de counts só recebe inteiros.
- **Reviews:** Prism visual **APROVADO** (S/M/L, l10n, semântica, a11y — 4 Medium corrigidos: labels de
  métrica localizados, "+N more", counts line no Medium, plural). Vigil security **C0/H0/M0/L0**. Atlas
  reliability **C0/H0/M0** (determinismo, sem estado mutável de tamanho, falha de render contida, sem
  timer). Runtime board = NOT_RUN; preview via Adaptive Cards designer (cards de teste válidos gerados).

## Fase 1 — Slice 4 (interação do widget / ativação + deep-link da app)

Torna o card clicável e converge a ação na **única** instância de UI do M12 — o widget nunca abre uma
segunda janela nem uma segunda pilha. Estritamente **read-only**: nenhum comando, escrita ou ação de
serviço; só navegação (Dashboard ou Dashboard com um servidor em foco).

- **Mecanismo escolhido (§36):** `selectAction = Action.Execute` como metadados no card (sem botões
  visíveis — o corpo renderizado fica byte-idêntico ao layout read-only da Slice 3). Card-root
  `openDashboard`; cada `Container` de linha de servidor `openServer` com `data = { serverId }`. O
  Adaptive Cards resolve o `selectAction` mais interno, portanto um toque numa linha dispara `openServer`
  e **não** também o `openDashboard` do card (precedência, não regiões sobrepostas); tudo fora de uma linha
  cai no `openDashboard`. Small/Empty/Unavailable = só ação de card.
- **Contrato de deep-link (`serveralyzer://`):** assemblies-folha BCL-only (`ServerMonitor.ActivationContract`).
  Gramática mínima: `serveralyzer://dashboard` e `serveralyzer://server/{guid}` (guid "D", exatamente 36
  chars, `!= Empty`). `ActivationUri.TryParse` é **total e fail-closed**: rejeita scheme errado, host fora
  do allowlist, segmento extra, QUALQUER query/fragment, userinfo/porta, slash codificado (`%2F`)/traversal
  (usa `Segments` crus + `Guid.TryParseExact`) e `> 256` chars; nunca lança, nunca executa input. A URI é
  construída SÓ a partir de um `ActivationIntent` (Kind + Guid) — o display name não tem lugar na gramática
  nem na `data` da ação, logo um nome hostil não pode chegar à URI. O Id opaco aparece só no `selectAction
  data`, nunca renderizado.
- **Modelo de input não-confiável:** o verbo+data do clique e a URI resultante são não-confiáveis.
  `WidgetActionHandler` faz allowlist do verbo, lê SÓ a chave `serverId` (ignora campos extra), e é
  neutral-on-exception (um clique nunca falha o provider — corre dentro do `Guard` COM). O lado da app
  **re-valida**: re-parseia a URI crua (`ProtocolActivationReader`) e resolve o guid contra o **próprio
  store** (`PendingServerFocus.TryResolve` só devolve o id se `VisibleServers` o contém) — um guid
  desconhecido/removido/adivinhado nunca resolve; no pior caso navega ao Dashboard e foca nada.
- **Convergência AppInstance (§4/§M-1/§M-2):** um único ponto de convergência. `PendingActivation` é o
  hand-off atómico através da fronteira de construção do `App` (o `Application.Current` é definido pelo ctor
  base ANTES de o router existir, logo **não** é flag de prontidão fiável). O intent cold e cada redirect
  são funilados por um só gate: entregue ao consumer se já ligado, ou bufferizado (latest-wins) até lá;
  `Attach` liga o `ActivationRouter.Route` e faz flush atómico do latest. Tanto `PendingActivation` como
  `ActivationRouter` usam o **mesmo padrão single-owner drain** (`_pending/_consumer|_ready/_draining` sob
  um `_gate`, consumer/executor invocado FORA do lock): um só pipeline, uma só ordenação — o clique mais
  recente ganha e nunca é ultrapassado na fronteira. `MarkReady` (em `OnLaunched`, após navegação pronta)
  drena o latest, uma vez.
- **Cold / warm:** cold launch (`serveralyzer://` que arranca o processo) e warm redirect (segundo launch
  reencaminhado para a instância primária) partilham o mesmo pipeline. O cold intent é entregue ANTES de
  subscrever `AppInstance.Activated`, para um redirect posterior (ação mais nova) o superar corretamente.
- **Foco do servidor (§H/§11):** `DashboardViewModel` levanta `ServerFocusRequested`; a `DashboardPage`
  (singleton, tal como o VM) mantém a subscrição por toda a vida da app (nunca dessubscreve em Unload) e
  faz scroll do card via `StartBringIntoView`. Se o servidor ainda não está carregado o pedido fica
  pendente e é reaplicado após o próximo load; um servidor removido simplesmente nunca resolve.
- **Compact / tray:** a ativação restaura e traz para a frente a janela na sua apresentação atual
  (Standard/Compact/tray) via `RestoreAndActivate` — nunca cria outra.
- **Manifesto:** `uap:Extension Category="windows.protocol"` com `uap:Protocol Name="serveralyzer"`
  (Package.Dev.appxmanifest, DEV-only); sem nova capability (`runFullTrust` inalterado).
- **Reviews:** Prism visual **APROVADO** (sem regressão S3; targets de clique inequívocos; sem elementos
  não suportados). Vigil security **C0/H0/M0/L0** (fronteira airtight, fail-closed, read-only, re-validação
  independente no lado da app). Atlas reliability **C0/H0/M0/L0** após duas rondas de correção — fechou
  H-1 (page singleton dessubscrita em Unload → foco+refresh mortos), M-1 (hand-off não atómico/ordenação na
  fronteira de construção → single-owner drain), L-1 (executor/sink que lançam encravavam o drain →
  isolamento + reset), L-2 (LoadAsync engole exceção → guard `IsOperationErrorOpen` nos testes de sucesso).
  Runtime Windows Widgets board = **NOT_RUN** (Win11 Home, sem dev-mode/admin) — nunca contado como PASS.

## Fase 1 — Redesign visual (instrument panel) + fix QA-1/QA-2

Depois do REAL BOARD QA (Gate 4.5), o resultado visual textual/lista foi **rejeitado pelo humano**. O widget
foi redesenhado (design do **Fable 5**, iterado no board real, **LOCKED pelo Prism**) para uma linguagem de
painel de instrumentos, e os dois findings de fiabilidade da QA de board (QA-1/QA-2) foram corrigidos.

- **Adaptive Cards 1.6 + `"header": null`:** a composição passa a ocupar a região de topo, eliminando o header
  de marca duplicado do host (o host desenha sempre a sua tira do menu `⋯`, que é chrome inamovível). O topo
  fica não-clicável, por isso o `selectAction` (openDashboard/openServer) vive no corpo clicável.
- **Direção instrument-panel, layouts por tamanho (genuinamente distintos):** kicker em caps (accent), herói
  numérico grande com split número+unidade ("39" + "%"), tudo top-aligned. **Small** = veredito da frota
  (kicker → N/N → label → barra de frota → frescura, sem linhas). **Medium** = telemetria compacta (herói +
  até 3 servidores). **Large** = o mais rico (herói + barra de frota 12px + até 6 servidores + rodapé de
  resumo da frota SAUDÁVEIS/ALERTA/CRÍTICO/OFFLINE + overflow "+N").
- **Meters segmentados nativos (magnitude-neutros):** ColumnSet de colunas `stretch` com `Container` de
  `style` + `minHeight` (o host honra minHeight), separadas por colunas-gap de pixel fixo (full-bleed). A
  **magnitude é a CONTAGEM de ticks preenchidos**; o preenchimento é `accent` (neutro), nunca a cor de saúde
  — a **saúde vive SÓ no chip `● Saudável`** (provado neutro mesmo em servidor Critical/Warning). A barra de
  frota SIM usa cores de saúde (mapa de saúde da frota).
- **Extensão de contrato (GB/uptime):** `WidgetServerState` ganhou `MemoryUsedGb/MemoryTotalGb/DiskUsedGb/
  DiskTotalGb` (double?) + `UptimeSeconds` (long?), mostrados **só no Large** (CPU→uptime, Mem/Disco→used/
  total GB). São **métricas de recurso de baixa sensibilidade**, da mesma classe dos percentuais — **NÃO PII**
  (§9 mantém-se). O `WidgetSnapshotMapper` popula-os a partir do `ServerMetricsSnapshot` mas **deliberadamente
  não mapeia Hostname nem OperatingSystemName** (que a fonte tem); o `WidgetServerState` não tem campo para
  eles → fuga impossível por construção. As duas allowlists de minimização (`WidgetContractSecurityTests`)
  foram atualizadas para incluir só os campos de métrica + um teste do mapper prova que nem o hostname nem a
  string de OS aparecem no JSON. **Vigil: C0/H0/M0** (1 Low informativo: capacidade/uptime é marginalmente
  mais fingerprintable, mas não cruza a fronteira §9 — nota de privacidade de produto, não blocker).
- **QA-1 (foco de servidor visível):** `openServer` resolvia o servidor certo e trazia a app para a frente,
  mas o "foco" era só `StartBringIntoView` — invisível quando o card já está no ecrã. Agora o card faz um
  **pulse de anel accent** (`ServerCardViewModel.IsFocusHighlighted` → `ServerFullCard` Storyboard), respeitando
  reduced-motion, one-shot (re-focus re-dispara). **Lifecycle-safe** para a Dashboard singleton: subscrição
  idempotente no `Loaded` (a página é reutilizada, o `DataContextChanged` não redispara) + consumo de um flag
  já-true no `Loaded`. **Atlas: C0/H0/M0/L1** (após fechar um M1 de re-subscrição no reuse da página singleton).
- **QA-2 (Compact):** uma ativação de widget em Compact ficava presa em Compact. Agora `ExecuteActivationIntent`
  força **Compact → Standard** antes de restaurar/navegar, preservando as invariantes de instância única.
- **Whitespace do Large — comportamento aceite (não bug):** a tira de topo é o chrome do menu `⋯` do host
  (inamovível sem voltar ao header default do host, rejeitado); o espaço inferior é consequência legítima de
  o layout suportar até 6 servidores com apenas 2 no ambiente real. **Decisão humana: aceitar** — não otimizar
  para exatamente 2 servidores nem degradar a escalabilidade.
- **Pitfall (UTF-8 BOM):** editar ficheiros de fonte com não-ASCII (acentos, `●`, `→`) via as tools de edição
  remove o BOM; o csc passa a lê-los como Windows-1252 → mojibake no output renderizado (os testes ainda passam
  porque os ficheiros de asserção são mal-lidos identicamente). Re-adicionar o BOM após editar.
- **Reviews:** Prism **LOCK** (Small/Medium/Large aprovados, reference-quality, sem C/H; M2/M3 = polish
  deferido). Vigil **C0/H0/M0** + 1 Low. Atlas **C0/H0/M0/L1**. Gates: Debug 1363/1363, Release verde,
  diff-check limpo, vuln limpo. **Re-verificação real-board do redesign + QA-1 pulse + QA-2 = NOT_RUN**
  (o provider no board é uma iteração anterior; um passe final de board é necessário e nunca contado como PASS).

## Modos de falha (leitura)

Cache ausente → estado indisponível. `schemaVersion` desconhecido / conteúdo corrupto / sobredimensionado
/ hostil → neutro, nunca a rebentar o host de Widgets. Servidor removido / todos offline → contagens
honestas. Falha na ativação do provider → no-op; o painel mostra o último conteúdo. Fail neutro em toda a
fronteira COM.

## Consequências

- ✅ Integração oficial de widget com zero exposição de SSH/engine/credenciais e sem segunda pilha de
  monitorização.
- ✅ Single-instance/ativação/notificações do M12 intocados (exe separado + CLSID separado).
- ✅ Comportamento honesto com a app fechada via frescura.
- ⚠️ Acrescenta um executável + CLSID COM + assets de widget ao pacote; a funcionalidade requer 22H2; QA
  de host real é provavelmente um passo humano/Store; os Adaptive Cards limitam a UI.

## Integração de release (produção) — FEITA

O pacote de produção passa a transportar o widget. `Package.appxmanifest` recebeu, idênticos ao que foi
validado no board com o pacote DEV: o `com:ExeServer` do provider (CLSID `78CFFBEF-…`), o
`uap3:AppExtension com.microsoft.windows.widgets` com `PublicFolder="Public"` e a definição
`ServerAlyzer_Widget` (small/medium/large), e o `uap:Extension windows.protocol` `serveralyzer`. O CLSID
de notificação de produção `4B2E9C7A-…` fica intacto e o CLSID DEV `206ACD0C-…` **nunca** entra no
pacote de produção (auditado no manifesto EXTRAÍDO: zero ocorrências). O piso
`TargetDeviceFamily MinVersion=10.0.22000.0` **não** subiu — o gate continua a ser o host (Opção A).

As duas hooks de packaging no `ServerMonitor.App.csproj` deixaram de depender de `DevIdentity`: passam a
correr em qualquer build com `Packaged == true`. DEV e produção partilham o mesmo payload de widget e
diferem apenas em identidade, CLSID de notificação e strings de apresentação — logo o que o board
exercitou é o que a Store leva.

**Uma só linha de runtime (invariante bloqueante).** Verificado no pacote EXTRAÍDO, não no csproj: o
manifesto declara `PackageDependency Microsoft.WindowsAppRuntime.2 MinVersion 2.3.1.0`; app e provider
têm `runtimeconfig.json` idênticos (`Microsoft.NETCore.App 10.0.0`, framework-dependent); existe um
único `Microsoft.Windows.Widgets.Projection.dll` na raiz e nenhuma pasta de runtime duplicada. O pacote
cresce ~158 KB face a 1.0.1.0 — exatamente os quatro ficheiros únicos do provider mais os assets do
widget, sem segundo payload self-contained.

**Identificadores congelados a partir desta release** (mudá-los órfão widgets afixados): CLSID do
provider `78CFFBEF-7A95-4400-BB8B-A2376C6642C3`, `AppExtension Id="ServerAlyzerWidgetProvider"`,
`Definition Id="ServerAlyzer_Widget"`.

**Versão.** Produto **1.1.0**, pacote da Store **1.1.0.0** (P-016: revisão = 0). `AssemblyVersion`
mantém-se em 1.0.0.0 por estabilidade de binding. Produto e pacote voltam a convergir.

## Em aberto

**Asset do widget picker.** `Public\ServerAlyzerWidgetScreenshot.png` é ainda o logo da app. O elemento
`Screenshot` é **obrigatório** e **visível ao utilizador** (diálogo *Adicionar widgets*), e a especificação
pede uma captura do tamanho **médio**, **300×304 px**, com cantos arredondados transparentes. Tem de ser
substituído por uma captura real com dados sintéticos antes de submeter. `DarkMode`/`LightMode` e as
variantes por locale são opcionais e ficam em aberto.

**QA do pacote de produção instalado.** Bloqueio de ambiente, não de arquitetura: a máquina tem a
1.0.1.0 instalada **pela Store** e o Windows recusa substituí-la por um registo local
(`0x80073CFB` — «the current user has already installed a packaged version of this app»); assinar o MSIX
com a identidade de publisher da Store exigiria confiar um certificado em `LocalMachine`, o que pede
elevação. Em aberto, portanto: primeira aparição do widget a partir do pacote de produção, QA de
ativação sobre produção e o teste de upgrade real 1.0.1.0 → 1.1.0.0. A via honesta é o próprio canal da
Store, ou uma máquina com elevação.

Restantes gates de runtime **NOT_RUN**, nunca contados como PASS: rehidratação do provider após reinício
natural, estados *unavailable*/*empty* no board (sem harness sintético seguro), frota de 6 servidores com
overflow em host real (só existem 2), servidor offline renderizado no board, Windows 11 21H2 real (a
máquina é 26200) e instalação em máquina limpa. Polish visual diferido por decisão humana: Prism M2/M3;
o *whitespace* do Large foi aceite (chrome do menu `⋯` do host + headroom de escalabilidade até 6
servidores).
