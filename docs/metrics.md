# Métricas Linux e macOS — Milestones 4 e 5

O M4 implementa recolha exclusivamente manual para servidores configurados como Linux; o M5 acrescenta macOS reutilizando o mesmo pipeline. Não existe polling, armazenamento histórico, gráficos ou execução remota fornecida pelo utilizador. Linux e macOS são apenas collectors diferentes a alimentar o mesmo `ServerMetricsSnapshot`; um `MetricsCollectorRouter` seleciona o collector por `Server.OperatingSystem` (resolvendo `Auto` via a deteção de host do M3) e é o único `IServerMetricsCollector` que a UI e o store consomem. Ver a secção macOS mais abaixo e a ADR-010.

## Pipeline

```text
ServerFullCard
  → IServerMetricsCollector
  → LinuxMetricsCollector
  → ILinuxMetricsRemoteSource
  → sessão SSH autenticada e validada do M3
  → catálogo fixo de comandos Linux
  → parsers puros
  → ServerMetricsSnapshot
```

`ServerMetricsSnapshot` é um modelo normalizado do Core. Memória e disco usam bytes (`long?`), percentagens usam `double?`, uptime usa `TimeSpan?` e o instante de recolha usa `DateTimeOffset`. `null` significa desconhecido; zero é um valor real e nunca é usado para mascarar falhas.

## Fontes controladas

| Dado | Comando fixo | Regra principal |
| --- | --- | --- |
| CPU | `cat /proc/stat` duas vezes | deltas entre total e `idle + iowait`, com intervalo local assíncrono de 500 ms |
| Memória | `cat /proc/meminfo` | `MemTotal - MemAvailable`, kB do kernel convertidos para bytes |
| Disco raiz | `LC_ALL=C df -P -B1 /` | apenas `/`, saída POSIX em bytes |
| Uptime | `cat /proc/uptime` | primeiro valor, parsing invariant |
| Hostname | `cat /proc/sys/kernel/hostname` | texto simples, limitado e sem decoração de shell |
| Sistema | `cat /etc/os-release` | allowlist de `NAME`, `VERSION`, `VERSION_ID` e `PRETTY_NAME`; nunca `source`/`eval` |

Nenhuma string de configuração, host, username ou UI é concatenada nestes comandos. A aplicação não publica uma API genérica `ExecuteCommandAsync(string)`.

## Falhas parciais

Um comando com exit status não-zero, output individual malformado ou campo opcional ausente torna apenas essa métrica indisponível. Grupos coerentes, como memória e disco, ficam integralmente `null` quando não podem ser validados. Um snapshot parcial é válido se contiver pelo menos um dado real.

Falhas de trust, autenticação, transporte, timeout ou cancelamento encerram a recolha e mantêm o erro SSH tipado do M3. Se a sessão funcionar mas nenhuma fonte produzir dados utilizáveis, o resultado é `NoMetricsAvailable` e não um snapshot de zeros.

## Limites e segurança

- uma recolha usa uma única sessão SSH autenticada depois do probe de host key;
- credenciais só são lidas depois de a fingerprint apresentada coincidir com a confiança persistida;
- o deadline cobre probe, autenticação, comandos e intervalo entre amostras;
- stdout é drenado concorrentemente com stderr e limitado por fonte; output excessivo fica indisponível;
- parsers aplicam limites adicionais de tamanho/linhas, parsing invariant e aritmética checked;
- outputs, stderr, segredos e paths de private key nunca são registados;
- o estado de métricas é transitório e não é persistido em `servers.json`.

## Apresentação

O `ServerFullCard` mostra apenas valores disponíveis de CPU, RAM, disco, uptime e sistema operativo, com refresh manual e timestamp. Antes da primeira recolha apresenta “Aguardando dados”. Durante a recolha o comando fica indisponível e um estado discreto é anunciado por acessibilidade. O último snapshot válido é preservado se uma atualização posterior falhar. A apresentação é idêntica para Linux e macOS: o card é agnóstico do OS de origem.

## macOS — Milestone 5

O macOS usa a userland BSD (sem GNU coreutils) e não expõe um contador cumulativo de ticks de CPU por CLI base equivalente a `/proc/stat`. O `MacOsMetricsCollector` encapsula estas diferenças e produz o mesmo `ServerMetricsSnapshot`.

| Dado | Comando fixo | Regra principal |
| --- | --- | --- |
| CPU | `top -l 2 -n 0` | duas amostras auto-geradas por `top` (~1s); última linha `CPU usage:`, `user + sys`; sem `sleep` remoto |
| Memória | `vm_stat` + `sysctl -n hw.memsize` | `used = (active + wired down + compressor) * pageSize`; total = `hw.memsize`; inactive/speculative/purgeable contam como disponíveis |
| Disco raiz | `df -P -k /` | apenas `/`; BSD df sem `-B`; blocos de 1024 bytes convertidos para bytes; percentagem da coluna Capacity |
| Uptime | `sysctl -n kern.boottime` | apenas o campo `sec` (epoch UTC); uptime derivado pelo `TimeProvider`, descartado se negativo |
| Hostname | `hostname` | parser partilhado e agnóstico de OS |
| Sistema | `sw_vers` | allowlist de `ProductName`, `ProductVersion`, `BuildVersion` |

O tamanho da página é lido do cabeçalho do `vm_stat` e nunca assumido como 4096 (Apple Silicon usa 16384). As regras de falha parcial, segurança, limites e não-persistência são as mesmas do Linux. O catálogo e as decisões constam da ADR-010.

## Workloads read-only — pipeline separado (Milestone 11)

O M11 (Docker + serviços systemd/launchd read-only) **não** faz parte deste pipeline de métricas de host. É uma **segunda fonte** completamente separada: produz um `ServerWorkloadSnapshot` próprio (nunca infla o `ServerMetricsSnapshot`), com o seu próprio catálogo fixo, cadência e store in-memory. Reutiliza os mesmos princípios — catálogo fechado sem interpolação, sem `ExecuteCommandAsync(string)`, uma sessão SSH autenticada por passagem, output não confiável com limites de tamanho/linhas, decode UTF-8 estrito, `unknown ≠ zero` e não-persistência — mas acrescenta sanitização de control-chars/ANSI/bidi porque os campos são texto influenciável pelo lado remoto. Ver a secção "Observabilidade de workloads read-only" em [architecture.md](architecture.md) e a ADR-016.
