# ADR-018 — Widget oficial do Windows (M13)

Estado: **proposto**. O M13 integra o ServerAlyzer com o ecossistema oficial de **Widgets do Windows 11**
(painel de Widgets), distinto do Modo Compacto (ADR-014, uma janela WinUI própria). Constrói sobre M1–M12
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

## Em aberto (slices seguintes / humano)

Fundação do provider COM e manifesto (windows.comServer/uap3:AppExtension), CLSID dedicado, reader com
limite de tamanho de ficheiro e limpeza de temps órfãos, `GetWidgetInfos` no arranque, serialização
Create/Delete e barreira último-Delete/novo-Create, shutdown idempotente, contenção neutral-on-exception
em toda a fronteira COM, rendering final S/M/L, ativação/deep-link da app, e a versão de pacote da Store
para o release do widget (respeitando P-016, Revision=0).
