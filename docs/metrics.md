# Métricas Linux — Milestone 4

O M4 implementa recolha exclusivamente manual para servidores configurados como Linux. Não existe polling, armazenamento histórico, gráficos, métricas macOS ou execução remota fornecida pelo utilizador.

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

O `ServerFullCard` mostra apenas valores disponíveis de CPU, RAM, disco, uptime e sistema operativo, com refresh manual e timestamp. Antes da primeira recolha apresenta “Aguardando dados”. Durante a recolha o comando fica indisponível e um estado discreto é anunciado por acessibilidade. O último snapshot válido é preservado se uma atualização posterior falhar.
