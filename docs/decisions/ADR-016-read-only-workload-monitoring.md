# ADR-016 — Read-Only Workload Monitoring: Docker + Serviços (M11)

Estado: **aceite**. Introduz observabilidade **read-only** de containers Docker e de serviços geridos
(systemd/launchd) por servidor. Princípio inalterável: **OBSERVAR, NUNCA ADMINISTRAR**. Esta ADR **não**
introduz qualquer ação remota (start/stop/restart/exec/rm), nem sudo, nem notificações, nem histórico,
nem presença no modo Compact. Constrói sobre a fundação SSH segura do M3 (ADR-006), os pipelines
Linux/macOS do M4/M5 (ADR-008/ADR-010), o motor de monitorização do M6 (ADR-011) e o padrão
observer→channel→consumer do histórico M10 (ADR-015).

## Contexto

Até ao M10 a aplicação apresenta métricas de **host** (CPU/RAM/Disco/uptime) e o seu histórico local. Não
existe visibilidade sobre o que **corre** no servidor: quais containers Docker existem e em que estado, e
que serviços do sistema estão ativos, parados ou falhados. Esta é a funcionalidade V2 do roadmap
(CONTEXT.md §21: "Docker", "estados de serviços"), estritamente na sua vertente de **leitura**.

O risco central de expor Docker/serviços é o *scope creep* para administração remota. O MVP exclui
explicitamente "gestão Docker", "restart remoto" e "execução arbitrária de comandos" (CONTEXT.md §20), e
as ações remotas seguras são V3 (§21). O M11 fica **inteiramente** do lado da observabilidade: uma segunda
fonte read-only, com o seu próprio ciclo de vida, que nunca toca no cálculo de saúde do host, nas
notificações, no histórico nem no Compact.

## Decisão

### 1. Fronteira read-only absoluta

O M11 **lê** estado; nunca o altera. O catálogo SSH é **fechado** e contém apenas comandos de observação.
Estão proibidos e **ausentes** do código:

- Docker: `start` / `stop` / `restart` / `kill` / `exec` / `rm` / `update` / `pause` / `unpause` /
  `compose`;
- systemd: `systemctl start` / `stop` / `restart` / `enable` / `disable` / `mask`;
- launchd: `launchctl bootstrap` / `bootout` / `kickstart` / `enable` / `disable`;
- qualquer `kill` / `pkill` / `sudo` / `su`.

Não existe — como nas ADR-008/ADR-010 — nenhuma API pública `ExecuteCommandAsync(string)`. A porta de
Infrastructure (`IWorkloadRemoteSource`) expõe apenas a recolha do catálogo fixo; nenhum texto de
utilizador, config, host, username ou UI é concatenado num comando.

### 2. Catálogo FECHADO — as famílias de comandos read-only autorizadas

O catálogo vive em constantes `internal const string` no código de Infrastructure, uma classe por
família, espelhando `LinuxMetricsCommandCatalog`/`MacOsMetricsCommandCatalog`. **Estas são as únicas seis
strings de comando que o M11 pode executar** (extraídas verbatim dos catálogos reais em
`src/ServerMonitor.Infrastructure/Collectors/{Docker,Systemd,Launchd}/`):

| Família | Constante | Comando literal | Papel |
| --- | --- | --- | --- |
| Docker | `DockerCommandCatalog.Version` | `docker version --format '{{.Server.Version}}'` | sonda de disponibilidade + versão do daemon |
| Docker | `DockerCommandCatalog.ContainerList` | `docker ps -a --no-trunc --format '{{json .}}'` | inventário de containers em NDJSON |
| systemd | `SystemdCommandCatalog.ListUnits` | `LC_ALL=C systemctl list-units --type=service --no-legend --no-pager --plain` | estado runtime dos serviços |
| systemd | `SystemdCommandCatalog.ListUnitFiles` | `LC_ALL=C systemctl list-unit-files --type=service --no-legend --no-pager` | enablement (enabled/disabled/static/masked) |
| launchd | `LaunchdCommandCatalog.PrintSystem` | `launchctl print system` | daemons do **domínio system** (macOS) |

Racional das escolhas:

- **`docker version --format '{{.Server.Version}}'`** é a sonda: o campo `Server` só resolve com daemon
  vivo, logo um único comando distingue *client-only* de *daemon-up*. Não se usa `docker info` (output
  pesado e caro).
- **`docker ps -a --no-trunc --format '{{json .}}'`** emite **NDJSON** (um objeto JSON completo por
  linha) → parsing robusto e determinístico com `System.Text.Json`, sem depender de posições de coluna
  nem de `jq`. `-a` inclui parados/exited (um monitor tem de ver o que **não** corre); `--no-trunc` deixa
  a truncação explícita ao nosso parser (§7).
- **`systemctl list-units …`** com `--plain --no-legend --no-pager` produz colunas limpas em **todo** o
  systemd suportado; deliberadamente **sem `--output=json`** (version-gated em systemd ≥ 246, e um
  catálogo fechado não pode ramificar por versão sem sonda extra) e **sem `--all`** (que inflaciona a
  lista com centenas de units mortas sem valor de monitorização).
- **`systemctl list-unit-files …`** dá enablement num único comando não-privilegiado, juntado por nome de
  unit no parser — em vez de `is-enabled <unit>` por serviço (O(n), e exigiria interpolar o nome da unit,
  proibido pelo catálogo fechado).
- **`launchctl print system`** alveja o domínio `system/` (daemons), **não** `gui/<uid>` nem `user/<uid>`
  — nunca se enumeram LaunchAgents por-utilizador nem sessões GUI. Rejeitado `launchctl list` (legacy;
  opera no domínio do chamador, i.e. o próprio user SSH, não os daemons do sistema).

`LC_ALL=C` estabiliza formatação e mensagens (determinismo, como o `df -P` do M4). Docker (Go) e
`launchctl` não são localizados; o matching de substrings de erro é estável sem `LC_ALL`. As chavetas
`{{…}}` vão dentro de aspas simples literais na constante; como a string é fixa (zero input do
utilizador), o quoting é controlado por nós e sobrevive ao shell remoto do `exec`, exatamente como já
acontece com o `LC_ALL=C df -P -B1 /`.

Toda a recolha corre numa **única sessão SSH autenticada** por passagem (§44), depois do probe de host-key
e da confiança fail-closed do M3. Zero comandos administrativos.

### 3. SEM sudo — permissão negada é um estado tipado

O M11 **nunca** escala privilégios. Um `permission denied` não dispara sudo; codifica-se num estado
**tipado** de disponibilidade (`PermissionDenied`). Isto vale para Docker (utilizador SSH fora do grupo
`docker`), systemd (polkit restrito) e launchd (`print system` pode ser root-only). A regra de
QUALITY_BAR (segurança SSH não se enfraquece por conveniência) e o invariante "sem auto-escalate" são
absolutos: preferimos um estado honesto de "sem permissão" a uma lista falsa ou a uma escalada.

### 4. Deteção tipada de disponibilidade do Docker

A disponibilidade deriva **inteiramente** do `ExitStatus` + substrings de stderr da sonda `docker
version` (sem comando extra). O mapeamento (implementado em `DockerWorkloadMapper` + `WorkloadStderrSignals`,
puros e testáveis) para `DockerAvailability` do Core:

| Sinal observado na sonda | `DockerAvailability` |
| --- | --- |
| `ExitStatus == 127` **ou** stderr contém `not found` | `NotInstalled` (binário ausente) |
| stderr contém `permission denied` | `PermissionDenied` (fora do grupo `docker`; **nunca** sudo) |
| stderr contém `Cannot connect to the Docker daemon` / `Is the docker daemon running` | `Unavailable` (instalado, daemon parado) |
| `ExitStatus == 0` e stdout = versão não-vazia | `Available` (só então corre `docker ps`) |
| output excede o cap, timeout, transporte, exit inesperado | `Error` (transitório) |

O `ExitStatus` é o sinal **primário**; o matching de substrings English é *defesa secundária* (Docker não
é localizado). `NotInstalled`/`PermissionDenied`/`Unavailable` são estados **distintos e finais**; `Error`
é transitório e preserva o snapshot anterior como stale (§8). Só com `Available` corre o `docker ps` —
poupa uma chamada e evita ruído quando não há daemon. Um `docker ps` que falhe **depois** de uma boa sonda
é `Error` (transitório), nunca dados fabricados.

**State e Health de container são campos separados** (`ContainerState` / `ContainerHealth` no Core). O
`.State` do JSON mapeia direto (`running`→`Running`, `exited`→`Exited`, …). O health **não** tem campo de
template próprio no `docker ps`; obtém-se parseando o parentético do `.Status` (`(healthy)`→`Healthy`,
`(unhealthy)`→`Unhealthy`, `(health: starting)`→`Starting`, sem parentético→`None`). `ContainerHealth.None`
(sem HEALTHCHECK) ≠ `Unknown` (não determinável) — distinção preservada.

**`docker stats` fica fora do M11 (§58).** `CpuPercent`/`MemoryUsedBytes`/`MemoryPercent` ficam `null`. O
`stats` não é single-shot barato: o daemon abre um *stream* de cgroup por container e amostra ~1–2 s antes
da primeira leitura, com latência a escalar com o nº de containers — viola §44 ("baixa utilização, evitar
esperas SSH"). É telemetria de recurso, conceptualmente sobreposta às métricas de host do M4–M6. O modelo
já **reserva** os campos `nullable` (`WorkloadRemoteRequest.IncludeContainerStats`, default `false`);
ativá-los fica para uma wave/milestone dedicada, com custo real medido. Até lá, `unknown ≠ zero`.

### 5. Serviços — systemd (Linux) e launchd (macOS)

O routing OS→manager é uma política pura única (`WorkloadManagerPolicy.Resolve`, Core §69): Linux com
systemd → `Systemd`; Linux sem systemd (SysV/OpenRC/runit) → `Unsupported`; macOS → `Launchd`; OS
desconhecido → `Unsupported`. **Docker é independente do service manager**: um Linux sem systemd pode ter
Docker → `Services.Manager=Unsupported` mas `Docker.Availability=Available`. Os dois nunca se contaminam.

**systemd** usa dois comandos. `list-units` dá `UNIT LOAD ACTIVE SUB DESCRIPTION`; o mapeamento
`ActiveState`(+`SubState`)→`ServiceState` cobre `active/running`→`Running`, `active/exited`→`Running`
(oneshot; `SubState` preserva o detalhe), `activating`→`Starting`, `deactivating`→`Stopping`,
`inactive`→`Stopped`, `failed`→`Failed`. `list-unit-files` dá enablement
(`enabled`→`Enabled`, `disabled`→`Disabled`, `static/indirect/generated/transient`→`Static`,
`masked`→`Masked`), juntado por nome de unit. Disponibilidade tipada a partir do `list-units`:

| Sinal em `list-units` | `WorkloadServiceAvailability` |
| --- | --- |
| `ExitStatus == 127` / `command not found` | `NotInstalled` → Manager `Unsupported` |
| stderr `has not been booted with systemd` / `Failed to connect to bus` | `Unavailable` (systemd não é PID 1) → Manager `Unsupported` |
| stderr `Access denied` / `Interactive authentication required` | `PermissionDenied` (nunca sudo) |
| `ExitStatus == 0` | `Available` (corre `list-unit-files`) |
| outro (timeout/transporte/output cap) | `Error` (transitório) |

**launchd** usa `launchctl print system` (domínio system apenas, §24). O bloco `services = { … }` traz
colunas `PID <último-exit> label`.

**Estado (H-04, finalizado contra o dump literal do mac-mini real — macOS 26.6, 428 serviços).** O estado
de runtime deriva **só da coluna PID**: PID numérico > 0 → `Running`; caso contrário (0 ou token não-PID)
→ `Stopped`. A 2ª coluna é o **último** exit token (valores reais observados `{-, 0, 1}`) e **não** é
mapeada para `Failed`: no host real, três loaders one-shot legítimos (`com.apple.wifiFirmwareLoader`,
`com.apple.iomfb_fdr_loader`, e um terceiro job custom) saem com `1` **por design**, e este
sumário não expõe o sinal de KeepAlive / estado pretendido necessário para distinguir uma falha real de
uma saída não-zero normal de um one-shot (os labels do host real foram **anonimizados** no fixture
versionado — sem PII). Distingui-las exigiria o detalhe por-serviço
`print system/<label>` (O(n), fora do catálogo fechado). Reportar um job parado como `Stopped` é o piso
honesto: uma falha real rara fica sub-reportada em vez de jobs saudáveis sobre-reportados como `Failed`
(`unknown ≠ fabricado`). **launchd nunca produz `Failed` a este nível.** O nome é o **label completo**
sanitizado (nunca colapsado para um segmento). launchd não expõe `Description`, enablement nem sub-state →
`DisplayName`, `StartupState`, `SubState` ficam **`null`** (§60/§61, sem portabilidade falsa);
`Starting`/`Stopping` nunca são fabricados. Fixture de regressão: o dump real sanitizado
(`launchd-print-system-macos26.txt`, 173 `Running` / 255 `Stopped` / 0 `Failed`).

**Risco validado — `print system` pode exigir root.** Em macOS moderno, ler o domínio system como
utilizador normal pode devolver erro de permissão (`Could not print domain: 5`, `Input/output error`,
EPERM). Como o SSH liga como user normal e **sudo é proibido**, este caso mapeia para
`PermissionDenied` (estado tipado, `unknown ≠ lista vazia`), **nunca** uma lista falsa nem escalada. O
comportamento real como user não-root no mac-mini (macOS 26.6) é **gate de QA de host** — não substituível
por unit tests (L-016: fronteira nativa atrás de fake não prova o comportamento real). Se `print system`
for sistematicamente root-only sem sudo, a decisão de fallback (ex.: sub-conjunto observável) sobe ao
Boss, sem enfraquecer para sudo.

### 6. Modelo e armazenamento — só memória, separado das métricas

O `ServerWorkloadSnapshot` (Core, `ServerMonitor.Core.Workloads`) é uma **segunda fonte**, completamente
separada do `ServerMetricsSnapshot` — o snapshot de métricas do M4–M6 (CPU/RAM/Disco/host) **não** é
inflado. Cada snapshot carrega `DockerSnapshot` e `ServiceSnapshot` sempre não-nulos, cada um com a sua
`Availability` própria, para que "Docker indisponível" e "serviços ilegíveis" sejam estados **distintos e
independentes**, não `null` ambíguo. Todos os records são imutáveis (`sealed record`, `required`/`init`),
todos os enums têm `Unknown = 0` (exceto `ServiceManager`, cujo default seguro é `Unsupported = 0`).

O store `IServerWorkloadStore` é **in-memory e transitório**, análogo ao `IServerMonitoringStateStore` do
M6: `Get`/`Set`/`Remove` + evento `WorkloadChanged`. **Sem SQLite, sem JSON, sem `servers.json`, sem
`%LOCALAPPDATA%`.** Reconstruído do zero a cada arranque; a entrada é removida quando o servidor sai do
reconcile (`ServersChanged`, paralelo ao `_stateStore.Remove` do M6). Ao contrário do M10, os workloads
**não** persistem — não há histórico de containers/serviços.

### 7. Limites e sanitização — output remoto é untrusted

Defesa em profundidade em três camadas, reutilizando o transporte SSH existente:

1. **Byte cap de transporte** por comando (como as métricas): output excessivo → o comando fica
   indisponível (`Error`/lista vazia tipada), **nunca** output cru exposto nem zero fabricado. O decode é
   **UTF-8 estrito**: um stream ilegível deixa a fonte inteira indisponível (padrão
   `SshNetSession.TryExecuteCommandAsync`).
2. **Count caps** no parser puro (`WorkloadLimits`): **≤ 512 containers**, **≤ 2048 serviços**. Ao atingir
   o cap, o parser marca `Truncated = true` (observável para a UI mostrar "a mostrar os primeiros N") em
   vez de dropar silenciosamente.
3. **Field caps + sanitização** por string (`WorkloadTextSanitizer`, Core, puro): clamp a **256 chars**
   (`WorkloadLimits.MaxTextLength`) por scalar Unicode/grapheme (nunca a meio de um par surrogate). Como
   estes campos são **texto influenciável por quem controla o container/unit** (nome, imagem, id, status,
   description, label), a sanitização vai além do strip de controlo dos parsers numéricos Linux/macOS:
   - **control chars** (C0/C1, `\n`/`\r`/`\t`, NUL) colapsam num espaço — campos são single-line;
   - **sequências ANSI/CSI e OSC/DCS/PM/APC/SOS** são removidas *como unidade* (evita spoof de
     terminal/logs, sem resíduo `[0m`);
   - **overrides/isolates bidi** (U+202A–202E, U+2066–2069, U+200E/200F, U+061C) são removidos (defesa
     contra Trojan-Source / spoofing de nome RTL);
   - surrogate desemparelhado é dropado; Unicode legítimo (acentos, CJK, emoji) é preservado.

**Logging** segue ADR-006/ADR-011: nunca se regista output remoto cru, nomes de container/serviço nem
stderr — só `ServerId`/estado/contagem/tipo de erro/duração. O store de workloads **nunca** contém
password, referência de credencial, path de chave, fingerprint SSH, username nem erro SSH cru.

### 8. Cadência — ride do ciclo M6, zero timers novos

A recolha de workloads **não cria timer nem loop por servidor**. Usa o sinal de conclusão de ciclo do M6
(`IMonitoringCycleObserver.OnCycleCompleted`) como *tick*, exatamente como o `HistoryRecorder` do M10. O
`MonitoringEngine` continua a ver **um único** observador: introduz-se em Core um
`CompositeMonitoringCycleObserver` que delega a `[HistoryRecorder, WorkloadCadenceObserver]`, isolando cada
membro num `try/catch` próprio — uma falha do workload observer **nunca** impede o history, e vice-versa
(§38). A engine thread e o contrato do observador (não-bloqueante, sem I/O, non-throwing) ficam intactos.

O `WorkloadCadenceObserver` (App) ignora ciclos `Cancelled` e aplica a política pura
`WorkloadCadencePolicy` — **due a cada 60 s por servidor** (default). Guarda `lastEnqueuedUtc` por
`ServerId` num `ConcurrentDictionary` (um loop por servidor ⇒ escritas por chave não corridas) e, quando
due, faz **um enqueue não-bloqueante** de um `WorkloadRequest{Reason=Scheduled}` num Channel bounded,
avançando o marcador **mesmo em drop** (um drop vira gap observável, não retry apertado). Consequência: os
workloads seguem a cadência do host, throttlada para ≥ 60 s — **nunca coletam mais depressa que o host nem
mais depressa que 60 s**; se o host poll for 5 min, os workloads seguem 5 min. Todo o tempo passa por
`TimeProvider` injetável (testes determinísticos).

### 9. Consumer e single-flight

O `WorkloadCollectorService` (App, `IHostedService`) drena o Channel **fora da engine thread**, espelhando
o `HistoryWriterService` do M10 mas com trabalho de **I/O SSH read-only** (via a porta de platform-infra)
em vez de SQLite:

- **Limiter global próprio** dos workloads (default **2** recolhas simultâneas), separado do limiter de
  host do M6, para que os workloads nunca roubem slots de host (§36).
- **Single-flight por servidor** (§37): nunca duas recolhas simultâneas para o mesmo servidor. O waiter é
  inscrito **sob o mesmo lock** que limpa o `InFlight` na conclusão — sincronamente, antes do primeiro
  `await` — pelo que um refresh manual junta-se deterministicamente a uma recolha em curso e nunca fica
  órfão (padrão `ServerMonitor` do M6; P-007/L-010). Requests `Scheduled` e `Manual` **coalescem** numa
  única recolha cujo resultado satisfaz todos os waiters.
- **Refresh manual** (`IWorkloadRefreshCoordinator.RefreshNowAsync`) **ignora o throttle de 60 s** e
  coalesce; o refresh manual da UI e o Refresh-All do M8 disparam-no em paralelo com o
  `IMonitoringEngine.RefreshNowAsync`. Um OS não suportado devolve um snapshot `Unsupported`/`Unknown` (o
  botão funciona, sem exceção).
- **Carry-over de freshness** na falha é do coordinator, não do collector: numa recolha falhada, as listas
  anteriores permanecem visíveis marcadas *stale* (`WorkloadFreshnessMerger`); `CapturedAtUtc` não recua;
  sem snapshot anterior → `Unknown`/pending. O collector devolve sempre uma tentativa fresca
  (`IsStale=false`). No shutdown, `Complete()` + drain bounded; waiters pendentes completam (nunca um
  refresh manual pendura o processo).

### 10. Isolamento de falhas

- **Workloads ⟂ host M6.** Se o `WorkloadCollectorService` falha, trava ou o Channel enche, o M6 continua
  a monitorizar host e a UI de métricas fica intacta (composite isola; enqueue non-blocking; drop bounded;
  consumer isolado).
- **Docker ⟂ Serviços.** São sub-passos independentes na mesma sessão, cada um com `Availability` própria.
  Docker em falha → só o `DockerSnapshot` reflete; `Services` continua a preencher, e vice-versa.
- **Nunca crash.** Parse/transporte em falha degrada para `Unknown`/`Error` + snapshot anterior mantido
  stale; o collector nunca lança para falhas remotas esperadas (P-012).

### 11. UI de workloads separada, e o que o M11 NÃO toca

A apresentação de Docker/Serviços é uma **secção separada** no detalhe do servidor (glass, read-only,
estados stale/unavailable/unsupported/permission-denied, localização pt-BR/pt-PT/en-US). O M11
explicitamente **não**:

- altera `ServerHealth`/`MonitoringThresholds` (§10) — workloads não entram no cálculo de saúde do host; a
  semântica do ponto de estado do card não muda;
- emite notificações (§11) — nenhum container/serviço dispara alerta no M11;
- toca no histórico (§12) — não grava workloads em SQLite nem no pipeline do M10;
- aparece no Compact (§55) — o widget permanece *glanceable*; workloads só no Standard Mode (paralelo à
  decisão "History só no Standard" do M10);
- administra — read-only absoluto (§1).

### 12. Ações remotas — EXPLICITAMENTE fora de escopo

Qualquer ação remota (restart/stop/start de serviços ou containers, `docker exec`, gestão Docker,
execução arbitrária) está **fora do M11** e permanece fora até um milestone dedicado. Corresponde a V3 do
roadmap ("ações remotas seguras", "restart de serviços", "restart de containers"; CONTEXT.md §21) e à
lista de exclusões do MVP ("gestão Docker", "restart remoto", "execução arbitrária de comandos"; §20).
Ativá-las exigirá o seu próprio ADR, modelo de autorização/confirmação e QA — o catálogo do M11 permanece
fechado e só de leitura.

## Consequências

- Observabilidade real de Docker + serviços por servidor, transitória e isolada; a monitorização de host
  do M6 é independente do path de workloads.
- **Zero dependências novas.** Os parsers são C# interno sobre a BCL (`System.Text.Json` para o NDJSON do
  `docker ps`; `System.Text` para a sanitização); `ServerMonitor.Collectors` referencia apenas Core +
  Infrastructure, sem `PackageReference`. **`THIRD-PARTY-NOTICES.md` não muda.**
- O catálogo fechado de seis comandos read-only é auditável num único sítio; nenhuma superfície de
  administração é adicionada.
- A cadência mínima (ride do ciclo M6, throttle 60 s, zero timers) mantém a utilização baixa e não contende
  com o host.
- A fronteira SSH/nativa real (docker/systemctl/launchctl num host verdadeiro) fica **NOT RUN** até QA de
  host — em particular o comportamento root-only do `launchctl print system` no mac-mini (L-016/P-010).
  Unit tests determinísticos cobrem cadence/single-flight/routing/isolamento/parsers, mas não substituem a
  recolha real.

## Alternativas rejeitadas

- **`docker info` como sonda** — output enorme e caro; a versão do servidor prova conectividade ao daemon
  num comando leve (§4).
- **`docker stats` para CPU/memória por container** — custo de amostragem ~1–2 s e latência O(n); telemetria
  sobreposta ao host; fora de escopo read-only (§4/§58). Campos ficam `nullable` reservados.
- **`systemctl --output=json`** — version-gated (systemd ≥ 246); um catálogo fechado não pode ramificar por
  versão sem sonda extra. A forma `--plain --no-legend` é universal (§2).
- **`systemctl is-enabled <unit>` por serviço** — O(n) e exigiria interpolar o nome da unit num comando; um
  único `list-unit-files` cobre todos (§2/§5).
- **`launchctl list`** — legacy; opera no domínio do chamador (o próprio user SSH), não nos daemons do
  sistema; contraria o alvo `system/` (§2).
- **Inflar o `ServerMetricsSnapshot` com workloads** — mistura duas fontes com ciclos de vida e freshness
  distintos; rejeitado a favor de um `ServerWorkloadSnapshot` separado (§6).
- **Persistir workloads (SQLite/JSON)** — o M11 é inventário transitório, não histórico; só memória (§6).
- **Um segundo observador diretamente no `MonitoringEngine` ou um novo timer** — roubaria o slot único do
  history ou duplicaria scheduling; o `CompositeMonitoringCycleObserver` + ride do ciclo resolve sem tocar
  na engine (§8).
- **sudo / auto-escalate em `permission denied`** — viola a segurança SSH do QUALITY_BAR; permissão negada
  é um estado tipado (§3).
