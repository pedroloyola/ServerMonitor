# ADR-010 — Pipeline macOS de métricas com comandos fechados

Estado: **aceite e implementado no Milestone 5**.

## Contexto

O M4 estabeleceu o pipeline Linux de métricas manuais com comandos fechados (ADR-008). O M5 acrescenta suporte a macOS reutilizando exatamente o mesmo domínio, o mesmo `ServerMetricsSnapshot`, o mesmo store transitório e o mesmo `ServerCardViewModel`. Não é criado um pipeline paralelo: macOS passa a ser apenas mais um collector a alimentar o mesmo modelo normalizado.

O macOS usa a userland BSD (não GNU coreutils) e expõe fontes de métricas diferentes do Linux. Em particular, não existe um contador cumulativo de ticks de CPU por CLI do sistema base equivalente a `/proc/stat`, pelo que a estratégia de deltas do Linux não se aplica.

## Decisão

O Core continua a expor apenas `IServerMetricsCollector`, `ServerMetricsCollectionResult` e `ServerMetricsSnapshot`.

O projeto Collectors passa a conter `MacOsMetricsCollector` e parsers puros de macOS. O collector depende de `IMacOsMetricsRemoteSource`, uma porta especializada da Infrastructure que, tal como a porta Linux, não aceita texto de comando. O adaptador SSH implementa apenas o catálogo literal documentado em `docs/metrics.md`.

O mesmo `SshConnectionService` serve agora `ISshConnectionService`, `ILinuxMetricsRemoteSource` e `IMacOsMetricsRemoteSource`, atravessando a sequência de trust/autenticação do M3 (ADR-006/ADR-007) sem alterações.

### Router

A UI e o store recebem um único `IServerMetricsCollector`: o `MetricsCollectorRouter`. Ele seleciona o collector por `Server.OperatingSystem`:

- `Linux` → `LinuxMetricsCollector`;
- `MacOS` → `MacOsMetricsCollector`;
- `Auto` → resolvido uma vez pela deteção de host do M3 (`uname -s`: `Darwin` → macOS, `Linux` → Linux) e depois encaminhado;
- `Unknown` e qualquer OS resolvido como desconhecido → falha `UnsupportedOperatingSystem`, sem qualquer comando remoto.

## Catálogo macOS

Todos os comandos pertencem ao sistema base do macOS (sem Homebrew, sem coreutils GNU):

| Dado | Comando fixo | Regra principal |
| --- | --- | --- |
| CPU | `top -l 2 -n 0` | `top` auto-amostra duas leituras a ~1s; usa a última linha `CPU usage:`, `user + sys`; sem `sleep` remoto |
| Memória | `vm_stat` + `sysctl -n hw.memsize` | `used = (active + wired down + compressor) * pageSize`; total = `hw.memsize` |
| Disco raiz | `df -P -k /` | BSD df sem `-B`; blocos de 1024 bytes convertidos para bytes; percentagem da coluna Capacity |
| Uptime | `sysctl -n kern.boottime` | apenas o campo `sec` (epoch UTC); uptime derivado pelo `TimeProvider` do collector |
| Hostname | `hostname` | texto simples, parser partilhado e agnóstico de OS |
| Sistema | `sw_vers` | allowlist de `ProductName`, `ProductVersion` e `BuildVersion` |

O tamanho da página é sempre lido do cabeçalho do `vm_stat` (`page size of N bytes`) e nunca assumido como 4096, porque Apple Silicon usa habitualmente 16384.

Inactive, speculative e purgeable são tratadas como recuperáveis (disponíveis), evitando um valor de "used" inflacionado. Um `used` que ultrapasse ligeiramente `hw.memsize` por páginas reservadas de firmware/GPU é limitado ao total.

## Robustez

- CPU usa a auto-amostragem do próprio `top`; não há `Task.Delay` nem `sleep` remoto adicional;
- stdout/stderr são drenados durante a execução e submetidos a caps por fonte;
- parsing é invariant, limitado em tamanho/linhas e usa aritmética checked;
- percentagens negativas ou não numéricas tornam a métrica desconhecida, não um valor forjado;
- falha de uma fonte produz `null` apenas nessa métrica/grupo; grupos coerentes (memória) ficam integralmente `null` quando não podem ser validados;
- falha de sessão (trust, autenticação, transporte, timeout, cancelamento) produz erro tipado sem snapshot;
- um uptime negativo (relógio do cliente atrás do boot) é descartado;
- métricas ficam apenas em memória e o refresh é manual/single-flight por servidor.

## Consequências

### Positivas

- macOS reutiliza domínio, store e ViewModels; a UI não conhece SSH.NET nem comandos shell;
- nenhuma entrada do utilizador pode alterar o comando remoto;
- zero e desconhecido continuam com semânticas diferentes;
- parsers, collector e router são testáveis sem servidores reais.

### Trade-offs

- o MVP mede apenas `/` e não representa APFS snapshots, mounts adicionais nem volumes de rede;
- não existe polling nem histórico;
- nomes comerciais de versão (Sonoma, Sequoia, …) não são mapeados neste milestone;
- Collectors conhece a porta especializada da Infrastructure, tal como no M4.

## Alternativas rejeitadas

- API pública `ExecuteCommandAsync(string)`: amplia a superfície de ataque e permite command injection;
- assumir página de 4096 bytes: incorreto em Apple Silicon;
- `iostat`/`powermetrics` para CPU: `powermetrics` exige privilégios; formatos menos estáveis;
- somar todas as páginas não livres como "used": inflaciona memória face à leitura de pressão do macOS;
- pipeline/domínio macOS separado: duplicaria estado e violaria a fronteira do CONTEXT.md.
