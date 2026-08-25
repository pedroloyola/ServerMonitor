# ADR-011 — Motor de monitorização e agendamento automático

Estado: **aceite e implementado no Milestone 6**.

## Contexto

Os milestones anteriores estabeleceram a recolha de métricas *manual* (M4 Linux, M5 macOS) através de um `IServerMetricsCollector` e de um store transitório (`IServerMetricsStore`). Cada `ServerCardViewModel` disparava a sua própria recolha ao clicar no botão de atualizar. Não existia recolha automática, nem noção normalizada de *saúde*, *stale* ou *offline* ao longo do tempo.

O M6 acrescenta um motor de monitorização automática que agenda recolhas por servidor, aplica a política de retry/saúde e publica o estado observável que a UI consome. O CONTEXT.md exige: atualização automática (§15, intervalos 10 s/30 s/60 s/5 min), refresh manual coexistente (§19), um servidor lento não bloquear os restantes (§44/§45), a UI nunca bloquear (§18/§44), dados stale distinguíveis (§46), e estados de saúde Healthy/Warning/Critical/Offline/Unknown (§16/§17).

## Decisão

### Um loop assíncrono por servidor

Cada servidor monitorizável tem o seu próprio loop `async` (`ServerMonitor.RunLoopAsync`), lançado via `Task.Run` — **nunca uma thread dedicada**. O loop é: recolher → aplicar estado → esperar o intervalo → repetir. Um servidor lento ou pendurado só atrasa o seu próprio loop; os restantes continuam. Toda a espera é cancelável.

### `TimeProvider` injetável

Todo o tempo (delays de intervalo, delays de retry, timeout de drain no shutdown, timestamps de estado) passa por um `TimeProvider` injetado. Em produção usa-se `TimeProvider.System`; nos testes usa-se `FakeTimeProvider`, tornando o agendamento determinístico e testável sem depender do relógio real. É a razão pela qual o M6 tem cobertura de agendamento sem testes lentos ou frágeis.

### Intervalos

O intervalo é por servidor (`Server.RefreshIntervalSeconds`), normalizado por `RefreshIntervalPolicy` para o catálogo `{10, 30, 60, 300}` s (default 30 s). Valores ausentes/0 (configurações pré-M6) normalizam para 30 s. Nada é recolhido mais depressa do que 10 s. O formulário Add/Edit expõe estas quatro opções; a persistência é a mesma `servers.json` (ver Migração).

### Limite global de concorrência

Um `SemaphoreSlim` global (`MonitoringOptions.MaxConcurrentCollections`, default 4) limita quantas recolhas correm em simultâneo em todos os servidores. Protege a máquina e a rede quando há muitos servidores, sem serializar servidores independentes. Há ainda um *stagger* de arranque para não disparar todos os primeiros ciclos ao mesmo tempo.

### Single-flight e refresh manual

O refresh manual da UI é encaminhado por `IMonitoringEngine.RefreshNowAsync`, que **não** faz uma recolha paralela: enfileira um pedido e acorda o loop do servidor através do seu sinal de espera. O pedido manual e qualquer ciclo agendado concorrente convergem numa única recolha (single-flight por servidor), e o intervalo **reinicia a contar a partir do refresh manual** — evitando o padrão "manual em t=20, automático desnecessário em t=30" com intervalo de 30 s. Servidores não monitorizados (OS não suportado) recebem uma recolha pontual para o botão continuar a funcionar.

### Retries

Dentro de um ciclo, apenas falhas *transitórias* são repetidas, segundo `MonitoringOptions.RetryDelays` (default `[1 s, 3 s]` → 3 tentativas). Falhas *não transitórias* (autenticação, host-key desconhecida/alterada, configuração inválida, OS não suportado) **não** são repetidas de forma agressiva — quebram o ciclo imediatamente e agendam a próxima tentativa no `AttentionInterval` mais longo (default 5 min), permitindo recuperação eventual sem martelar um servidor mal configurado. A classificação vive num único sítio testável (`MonitoringOutcomeClassifier`).

### Execução em background, não interativa

O motor corre enquanto o processo da app está vivo (é um `IHostedService` ligado ao host de DI); **não** é um serviço Windows. A monitorização em background é estritamente não interativa: nunca abre o diálogo de confiança de host-key, nunca pede password. Uma host-key desconhecida ou alterada, ou credencial em falta, resultam em estado de atenção (`Unknown`), não em tentativas repetidas nem prompts — a resolução acontece no fluxo explícito de Add/Edit/Test Connection.

### Saúde (health)

`ServerHealth` (Unknown/Healthy/Warning/Critical/Offline) é distinto de `ServerConnectionState`. A saúde baseada em métricas é derivada por `HealthEvaluator` a partir do snapshot e de `MonitoringThresholds` (CPU 80/95, RAM 80/95, Disco 80/90 — CONTEXT §17), tomando a severidade máxima entre métricas; métricas desconhecidas são ignoradas, nunca tratadas como zero. `Offline` é decidido pela *acessibilidade* (falha transitória esgotada), nunca pelos valores. A UI mostra a saúde no ponto de estado do card com cores semânticas (verde/âmbar/vermelho/offline/neutro); o azul da marca (#1846E1) fica reservado a interação e às micro-barras.

### Stale

`StalePolicy` marca a última leitura como stale quando a idade excede ~2× o intervalo (com um piso). Numa falha com snapshot anterior, as métricas **permanecem visíveis** (não vão a zero), `LastSuccessAt` **não** recua, e a UI mostra uma indicação discreta ("última atualização há N min"). Sem snapshot anterior, mostra-se estado de erro/pendente.

### Servidores ocultos (hidden)

Ocultar **não** para a monitorização: o motor reconcilia a partir de todos os servidores monitorizáveis de `IServerService`, independentemente de `IsHidden`. Um servidor oculto continua a ser recolhido e o seu estado/snapshot continuam a avançar; apenas não aparece no dashboard principal. Ao restaurar, o card pode aparecer já com um snapshot recente recolhido enquanto esteve oculto.

### Ciclo de vida (lifecycle)

`StartAsync`/`StopAsync` do `IHostedService` iniciam/param o motor com o host. `StopMonitoringAsync` cancela os loops e faz *drain* com um timeout limitado (via `TimeProvider`) para nunca bloquear o shutdown. A reconciliação reage a `IServerService.ServersChanged`: Add inicia um loop, Remove cancela o loop e limpa o estado, Edit de host/auth/OS/intervalo acorda o loop para usar a nova configuração sem duplicar loops.

### Estratégia de sleep/resume

O agendamento usa **um único delay one-shot por ciclo** (via `TimeProvider`), não um timer de taxa fixa que acumula ticks. Após uma suspensão do sistema, o relógio salta para a frente e esse único delay dispara **uma** vez → uma recolha oportuna → agendamento normal retomado. Não há "tempestade" de ticks perdidos replicados. Está coberto por teste (`LongClockJump_ProducesAtMostOneCatchUpCycle`). Não foi adicionado lifecycle de energia complexo por não ser necessário.

## Integração com a UI

A UI observa, nunca agenda. `IServerMonitoringStateStore` (in-memory, transitório, sem valores de métrica nem segredos) emite `StateChanged`; o `DashboardViewModel` subscreve, faz *marshal* para a UI thread (o evento vem de loops de background) e empurra o estado para o `ServerCardViewModel` correspondente via `ApplyMonitoringState`, que relê o snapshot atual. O card não tem timers. As subscrições do dashboard são removidas em `Dispose` para evitar leaks.

## Logging

- **Debug**: início/fim de cada ciclo de recolha (com o resultado classificado).
- **Information**: motor iniciado/parado, e cada transição de saúde (inclui offline → recuperado).
- **Error**: exceções inesperadas em loop/reconcile/recolha.

Nunca se regista output remoto em bruto, credenciais nem segredos. Deliberadamente **não** há logs de nível Information por ciclo (a cada 10 s), para não poluir.

## Dependência de teste

`Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) foi adicionada **apenas** ao projeto de testes `ServerMonitor.App.Tests` (licença MIT, Microsoft). Não é referenciada pela app nem entra na distribuição, pelo que — seguindo a política atual do `THIRD-PARTY-NOTICES.md`, que lista apenas dependências de runtime distribuídas (SSH.NET/Bouncy Castle) — não é ali adicionada.

## Consequências

- A UI reflete recolhas automáticas sem intervenção do utilizador; o refresh manual coexiste e reinicia o agendamento.
- O domínio de saúde/stale/retry é puro e testável (Core), separado do motor (App).
- Um servidor lento ou mal configurado não afeta os restantes nem a UI.
- Não há persistência de estado de runtime nem de métricas; tudo é in-memory e reconstruído a cada arranque.
- Fora de scope (mantém-se): notificações, system tray, arranque com o Windows, histórico, discovery — nada disto é introduzido pelo M6.
