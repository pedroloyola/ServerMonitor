# ADR-015 — Local History Storage + Charts (M10)

Estado: **aceite**. Introduz histórico **local** de métricas e a sua visualização temporal.
Princípio inalterável: **LOCAL-FIRST** — o histórico é mantido apenas na máquina do utilizador.
Esta ADR **não** introduz serviços remotos, contas, sincronização, dashboard web nem base de dados
externa.

## Contexto

Até ao M9 o pipeline é puramente transitório:

```
MonitoringEngine → snapshot atual (ServerMetricsStore / ServerMonitoringStateStore) → UI
```

Não existe memória temporal: ao reiniciar a app perde-se todo o passado. O M10 acrescenta um
histórico local persistente e a sua visualização (CPU / Memória / Disco ao longo do tempo),
**sem** interferir com o pipeline de estado atual e **sem** tornar a monitorização dependente da
base de dados.

## Decisão

### 1. History é um *side effect* assíncrono e degradável

O histórico observa o M6; nunca o bloqueia. Se a base de dados estiver locked, corrupta, lenta ou
indisponível, o `MonitoringEngine` continua a monitorizar normalmente. Uma query de histórico nunca
bloqueia o coletor SSH. Regra dura: **falha de base de dados ⇒ histórico degrada, monitorização
continua**.

```
MonitoringEngine.ApplyCycleResult (ciclo fresco concluído)
      ├─────────────→ estado atual → UI            (inalterado, M6)
      └─────────────→ IMonitoringCycleObserver
                            ↓  (HistoryRecorder: política de amostragem)
                        bounded Channel  (drop-newest em overflow, observável)
                            ↓  (HistoryWriterService: single writer, IHostedService)
                        SQLite (batch, transação)  ── retention (startup + diária)
                            ↓
                        IServerHistoryQueryService (off-UI-thread, cancelável, downsampling)
                            ↓
                        History UI (charts CPU/RAM/Disco)
```

### 2. Fresh vs. stale — o histórico nunca falsifica

A UI atual pode manter o último snapshot válido como *stale* quando uma recolha falha. O histórico
**não** grava esse snapshot antigo como se fosse uma medição nova. O seam é
`MonitoringEngine.ApplyCycleResult`, o único ponto onde um ciclo **fresco** é conhecido: ele já tem
o `result` fresco (cujo `Snapshot` é `null` quando a recolha falhou), o `outcome`, a `Health`
recalculada e `now = TimeProvider.GetUtcNow()`. O observador recebe exatamente esse resultado
fresco:

- **Success** → grava CPU/RAM/Disco reais (nullable preservado) + Health.
- **Retryable / NonRetryable / NoData** → grava CPU/RAM/Disco = `null` + Health (Offline/Unknown).
- **Cancelled** → **não** grava (estado intocado; shutdown/superseded).

Assim, `stale` (display) e `histórico` (persistência) são fontes conceptualmente distintas.
`null ≠ 0`: métrica desconhecida é `null`, nunca `0` (0 é uma medição válida de zero).

### 3. Seam de conclusão de ciclo — a menor abstração possível

Não se inventa heurística de timestamps. Introduz-se um contrato mínimo em `Core.Monitoring`:

- `MonitoringCycleCompletion` — `ServerId`, `CapturedAtUtc`, `Outcome`, `Health`,
  `ServerMetricsSnapshot? Snapshot`.
- `IMonitoringCycleObserver.OnCycleCompleted(MonitoringCycleCompletion)`.

O `MonitoringEngine` recebe o observador por injeção (default `NullMonitoringCycleObserver`) e
chama-o no fim de `ApplyCycleResult`. O M6 **não** ganha dependência de SQLite nem de I/O; o
observador é síncrono, não-bloqueante e não-lançante (o recorder faz `TryWrite` num Channel). Não se
alteram thresholds, retries, scheduler nem o intervalo de polling.

### 4. Biblioteca SQLite — `Microsoft.Data.Sqlite`

Investigadas: (A) `Microsoft.Data.Sqlite`, (B) EF Core SQLite, (C) Dapper+SQLite, (D) `sqlite-net`.

Escolha: **A — `Microsoft.Data.Sqlite` 10.0.x** (mesma linha de versão do restante stack MS),
provider ADO.NET fino, sem ORM. Justificação face aos critérios (§9):

- **Controlo total de SQL e migrations** — sem geração mágica; queries parametrizadas explícitas.
- **Dependency footprint pequeno** — traz `SQLitePCLRaw.*` + native `e_sqlite3`; sem grafo EF.
- **Self-contained / win-x64** — o payload nativo (`runtimes/win-x64/native/e_sqlite3.dll`) é
  copiado no build self-contained. **P-009/L-014:** a presença do DLL nativo no output Release é
  **gate obrigatório** (fake não valida fronteira nativa) — smoke launch real do binário Release a
  abrir o History e a executar uma query SQLite.
- **Async / cancellation / threading** — SQLite é síncrono; a app **detém** o threading (single
  writer em Task próprio; queries em `Task.Run` fora do UI thread; nunca `.Result`/`.Wait()` em
  paths de UI). Não se confia em falso-async de ORM.
- **Licença** — MIT.
- EF Core rejeitado (peso, migrations mágicas, async ilusório); Dapper desnecessário para um schema
  de uma tabela; `sqlite-net` menos mantido/menos idiomático em .NET moderno.

### 5. Schema e pragmas

```sql
PRAGMA user_version = 2;

CREATE TABLE history_samples (
    server_id       TEXT    NOT NULL,   -- Guid "D" (estável, §19); nunca hostname/IP/nome
    captured_at_utc INTEGER NOT NULL CHECK(typeof(captured_at_utc)='integer'),
    health          INTEGER NOT NULL CHECK(typeof(health)='integer' AND health BETWEEN 0 AND 4),
    cpu_percent     REAL    NULL CHECK(cpu_percent IS NULL OR (typeof(cpu_percent) IN ('real','integer') AND cpu_percent BETWEEN 0 AND 100)),
    memory_percent  REAL    NULL CHECK(memory_percent IS NULL OR (typeof(memory_percent) IN ('real','integer') AND memory_percent BETWEEN 0 AND 100)),
    disk_percent    REAL    NULL CHECK(disk_percent IS NULL OR (typeof(disk_percent) IN ('real','integer') AND disk_percent BETWEEN 0 AND 100)),
    PRIMARY KEY (server_id, captured_at_utc)
);
```

- **PK composta `(server_id, captured_at_utc)`** serve simultaneamente de índice de query (§18) e de
  chave de idempotência: a escrita usa `INSERT OR IGNORE` — um evento duplicado (mesmo
  server+timestamp) não duplica linha (§59). Sem índices adicionais (schema pequeno).
- **Tempo em UTC epoch ms (INTEGER)** — inequívoco, ordenável, imune a DST; a UI converte para
  timezone local (§17, §46). Nunca timestamps locais ambíguos.
- **Defesa em profundidade** — percentagens são sanitizadas no recorder, novamente no store ao
  escrever e ao materializar rows, e o schema v2 impõe `CHECK`. A migration v1→v2 é transacional,
  preserva dados válidos e converte métricas legadas inválidas em `NULL`; uma DB antiga/adulterada
  continua a ser tratada como input não confiável na leitura.
- **Pragmas** (§26): `journal_mode=WAL` (leituras concorrentes com o writer, típico desktop local),
  `synchronous=NORMAL` (durabilidade adequada com WAL; sem fsync por commit), `busy_timeout=5000`
  (absorve lock transitório sem retry-storm), `foreign_keys=OFF` (sem FKs — a config do servidor vive
  em `servers.json`, não na DB; o histórico é tolerante a órfãos §20).

### 6. Amostragem, retention, downsampling — três conceitos distintos

- **Amostragem (§16)** — política pura `HistorySamplingPolicy`: no máximo **1 amostra persistida a
  cada 30 s por servidor**. Poll a 10 s ⇒ ~1 em 3 ciclos é persistido; poll ≥ 30 s ⇒ cada ciclo útil
  é persistido. Bounded DB, boa resolução para 1h/6h, baixa write amplification.
- **Retention (§23)** — 30 dias. Cleanup assíncrono, bounded, cancelável, **não** por insert: corre
  no arranque do writer e uma vez por dia. `DELETE WHERE captured_at_utc < now - 30d`.
- **Downsampling (§37)** — decide **quantos pontos** devolver à UI (≠ retention). Alvo **~300
  pontos/série**. Algoritmo determinístico *gap-aware bucketing*: o range é dividido em N buckets de
  duração igual; cada bucket com ≥1 amostra de valor não-nulo emite **um** ponto no timestamp real da
  amostra com **o valor máximo** do bucket (representante *worst-case*: nunca oculta um pico, §37);
  bucket sem amostras ⇒ **nenhum** ponto (o gráfico quebra a linha = gap real, §38); bucket só com
  nulos ⇒ ponto com valor `null` (distingue "medido mas offline"). Se o nº de amostras cruas ≤ alvo,
  devolve-se cru (sem agregação) — ranges curtos mostram detalhe total. Ordem temporal preservada,
  `null` preservado, output bounded. Sem biblioteca pesada.
- **Bound de leitura** — uma query aceita no máximo 100 000 rows cruas (30 d/30 s ≈ 86 400);
  `LIMIT max+1` deteta uma DB sobredimensionada e falha de forma controlada, nunca devolvendo um
  prefixo parcial enganador.

### 7. Estimativa de tamanho (§25)

Amostragem 30 s ⇒ 2 880 amostras/servidor/dia. Linha ≈ 40 B (7 colunas compactas) + overhead de PK.
30 dias:

| Servidores | Amostras (30 d) | Ordem de grandeza |
|---|---|---|
| 2  | ~172 800 | ~7–10 MB |
| 10 | ~864 000 | ~35–50 MB |
| 50 | ~4 320 000 | ~180–260 MB |

Aceitável para desktop local; retention de 30 dias mantém a DB *bounded*. Reavaliar tiers agregados
apenas se e quando houver evidência (não implementado agora — §24).

### 8. Camadas e ownership

- **Core** (puro, testável, sem SQLite): modelos e políticas — `ServerHistorySample`,
  `HistoryTimeRange`, DTOs de query/chart, `IServerHistoryStore` (escrita), `IServerHistoryQueryService`
  (leitura), `IMonitoringCycleObserver`/`MonitoringCycleCompletion`, `HistorySamplingPolicy`,
  `HistoryDownsampler`, `HistorySampleValidator`.
- **Infrastructure**: `SqliteServerHistoryStore` (schema/migrations/pragmas/batch/query/retention),
  `HistoryStorageOptions.ForCurrentUser()` → `%LOCALAPPDATA%\ServerMonitor\history.db` (§11; nunca
  OneDrive/roaming/rede).
- **App**: `HistoryRecorder` (implementa `IMonitoringCycleObserver`; aplica amostragem; `TryWrite`
  para Channel bounded), `HistoryWriterService` (`IHostedService`; single writer; batch; retention),
  `ServerHistoryQueryService` (downsampling + off-UI-thread + cancelamento), wiring DI, hook no M6.
- **App/UI**: History view + charts custom + VM + entrada no menu de ações do servidor + Settings
  (Histórico: retention informativo + Limpar histórico) + localização pt-BR/pt-PT/en-US.

### 9. Isolamento de falhas

- **Corrupção (§32)** — a DB corrupta **não** crasha a app nem é apagada silenciosamente. O store
  entra em estado *unavailable*; a monitorização continua; a UI de History mostra "indisponível";
  Settings oferece **Repor histórico** explícito, confirmado e ordenado pelo single writer. Reset
  aguarda queries ativas através de um gate reader/writer e não apaga automaticamente.
- **Locked / disco cheio (§55, §56)** — writer com retry bounded (via `busy_timeout` + política);
  sem retry-storm; lock transitório no arranque recupera com backoff 5 s→1 min sem reiniciar a app;
  a monitorização continua; a fila bounded faz drop observável em vez de crescer.
- **Overflow da fila (§28)** — Channel bounded com `FullMode=DropWrite`: descarta a amostra **nova**
  de forma logada; nunca consome memória ilimitada; nunca bloqueia o SSH; sem popup por drop.
- **Shutdown (§30)** — o writer é `IHostedService` registado **antes** do `MonitoringEngine`, logo o
  seu `StopAsync` corre **depois** de o engine parar de produzir: fecha a entrada, drena a fila com
  bound, faz flush e fecha a conexão; se exceder o bound razoável, o shutdown prossegue (não se perde
  o processo por histórico), integrado no `AppShutdownCoordinator`.
- **Clear/Reset** — são control barriers FIFO no mesmo consumer: escritas aceites antes da barreira
  são resolvidas antes da operação; sucesso só é mostrado após o resultado real do store. Shutdown ou
  falha do consumer completa barriers pendentes como falha — nenhuma operação de Settings fica órfã.

### 10. Charts

WinUI não traz chart completo. Comparadas: Canvas/Path custom, Win2D, LiveCharts2, ScottPlot.
Escolha: **chart próprio pequeno** (geometry/`Path`/`PolyLine` sobre Canvas) — três *line charts*
simples de 0–100% não justificam dependência pesada (§43); melhor controlo de tema/glass,
acessibilidade e payload self-contained. Design: minimalista, brand line `#1846E1` para CPU/RAM/Disco,
sem 3D/neon/gradient/Grafana-look (§44). Y fixo 0–100% (§45); X = tempo local com poucos ticks (§46);
gaps desenhados como descontinuidade (§38, §91). Cada chart tem *accessible summary* calculado
(ex.: "CPU nas últimas 24 h. Atual 12%. Máximo 84%.") (§67). History só existe no Standard Mode; o
Compact permanece glanceable, sem charts (§40, §94).

### 11. Remoção e hidden

- **Servidor removido (§20)** → config removida, **histórico permanece** até retention/clear (evita
  surpresa destrutiva). Histórico órfão não aparece na UI principal. Política destrutiva diferente
  exigiria HUMAN CHECKPOINT.
- **Hidden (§21)** → continua monitorizado ⇒ continua a gerar histórico. Visibilidade
  Standard/Compact não altera a persistência.
- **Discovered-only (§22)** → **não** gera histórico (só servidores adicionados/monitorizados).

### 12. Segurança e privacidade

A DB de histórico contém **apenas métricas** (§85): server_id (Guid), timestamp, health, três
percentagens. **Nunca**: password, referência de credencial, path de chave privada, fingerprint SSH,
username, erro SSH cru, TXT de discovery, segredos. Todas as queries são parametrizadas (sem
concatenação; §100). Path da DB fixo em `%LOCALAPPDATA%` (sem path arbitrário/traversal). Sem
telemetria, sem upload, sem login.

## Consequências

- Persistência local real com restart-safety; monitorização independente da DB; falhas isoladas.
- Uma dependência nova (`Microsoft.Data.Sqlite`, MIT) + payload nativo `e_sqlite3` a validar em
  Release. THIRD-PARTY-NOTICES atualizado.
- Migrations versionadas (`user_version`) preparam evolução de schema sem apagar dados.
- Downsampling *max-per-bucket* privilegia visibilidade de picos sobre suavidade — decisão
  consciente e documentada; LTTB/tiers agregados ficam reservados para evidência futura.

## Alternativas rejeitadas

- **EF Core / Dapper / sqlite-net** — ver §4.
- **Escrever no pipeline de estado do M6 / reutilizar a última linha de histórico como estado atual**
  — viola fresh-vs-stale (§14, §47); rejeitado.
- **Gravar cada callback visual** — write amplification desnecessária; rejeitado a favor de
  amostragem 30 s.
- **Apagar histórico ao remover servidor** — surpresa destrutiva; rejeitado (§20).
- **Biblioteca de charts pesada** — desproporcionada para três linhas simples (§43); rejeitada.
