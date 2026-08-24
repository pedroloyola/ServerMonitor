# ADR-008 — Pipeline Linux de métricas com comandos fechados

Estado: **aceite e implementado no Milestone 4**.

## Contexto

O primeiro pipeline de monitorização precisa de recolher CPU, memória, filesystem raiz, uptime, hostname e identificação do sistema num Ubuntu por SSH, sem criar uma superfície de execução remota arbitrária nem enfraquecer a confiança de host e o armazenamento de credenciais do M3.

Outputs remotos não são confiáveis: podem estar malformados, ser demasiado grandes ou conter texto concebido para enganar logs/UI. Uma falha parcial também não pode ser representada como zero, porque `0%` é um valor válido.

## Decisão

O Core expõe apenas `IServerMetricsCollector`, `ServerMetricsCollectionResult` e `ServerMetricsSnapshot`. Campos potencialmente indisponíveis são nullable e os valores de memória/disco permanecem em bytes.

O projeto Collectors contém `LinuxMetricsCollector` e parsers puros. O collector depende de `ILinuxMetricsRemoteSource`, uma porta especializada da Infrastructure que não aceita texto de comando. O adaptador SSH implementa apenas o catálogo literal documentado em `docs/metrics.md`.

O mesmo `SshConnectionService` serve `ISshConnectionService` e `ILinuxMetricsRemoteSource`. Assim, M3 e M4 atravessam exatamente a mesma sequência:

1. validação local;
2. leitura da host key confiada;
3. probe sem credencial;
4. comparação fail-closed da fingerprint;
5. leitura do Credential Manager;
6. autenticação com a política criptográfica moderna;
7. operação autorizada numa única sessão;
8. disposal de sessão e segredo.

`ISshSession`, a factory e tipos que recebem passwords permanecem internos à Infrastructure; apenas os testes da própria assembly recebem acesso por `InternalsVisibleTo`.

## Robustez

- CPU usa duas amostras e `Task.Delay` cancelável; não usa `sleep` remoto;
- stdout/stderr são drenados durante a execução e submetidos a caps;
- parsing é invariant, limitado e usa aritmética checked;
- falha de uma fonte produz `null` apenas nessa métrica/grupo;
- falha da sessão produz erro tipado sem snapshot;
- métricas ficam apenas em memória e refresh é manual/single-flight por servidor.

## Consequências

### Positivas

- a UI não conhece SSH.NET nem comandos shell;
- nenhuma entrada do utilizador pode alterar o comando remoto;
- zero e desconhecido têm semânticas diferentes;
- parsers e collector são testáveis sem servidores reais;
- o último snapshot válido pode permanecer visível após uma falha posterior.

### Trade-offs

- o MVP mede apenas `/` e não representa mounts adicionais;
- não existe polling ou histórico;
- servidores `Auto`, `MacOS` e `Unknown` não recebem comandos Linux neste milestone;
- Collectors conhece a porta especializada da Infrastructure; esta dependência é intencional e será reavaliada se surgir um segundo transporte.

## Alternativas rejeitadas

- API pública `ExecuteCommandAsync(string)`: amplia a superfície de ataque e permite command injection;
- `top`, `free`, `mpstat` ou ferramentas opcionais: formatos e disponibilidade variam;
- converter falhas em zero: confunde indisponibilidade com uma medição real;
- abrir uma sessão por comando: repete autenticação, aumenta latência e complica deadlines;
- persistir snapshots em JSON: introduziria histórico/persistência fora do âmbito do M4.
