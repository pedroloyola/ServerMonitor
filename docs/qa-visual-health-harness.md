# M6 — Visual Health QA Harness

Ferramenta **exclusiva de desenvolvimento/QA** para inspecionar visualmente todos os estados de saúde do M6 (Healthy, Warning, Critical, Offline, Stale, Unknown, Refreshing, Partial), em Linux e macOS, usando o **card real** (`ServerFullCard` / `DashboardPage`) — sem servidores reais, SSH, persistência ou credenciais.

## Como iniciar

```powershell
dotnet run -c Debug --project src/ServerMonitor.App/ServerMonitor.App.csproj -- --qa-health
```

A flag `--qa-health` ativa uma **composição DI exclusiva de Debug** que:

- substitui `IServerService`, `IServerMetricsStore`, `IServerMonitoringStateStore` e `IMonitoringEngine` por *doubles* in-memory (`ServerMonitor.App.Qa`);
- **não** regista o `MonitoringEngine` real nem o hosted service → nenhum SSH, scheduling, persistência ou acesso a credenciais corre;
- pré-carrega 16 cenários deterministas (8 estados × Linux/macOS) do `QaHealthCatalog`.

Sem a flag, a app arranca normalmente (engine real).

## Cenários

| Estado | CPU | RAM | Disk | Notas |
|---|---|---|---|---|
| Healthy | 22% | 41% | 52% | tudo dentro dos limites |
| Warning | 84% | 41% | 52% | CPU acima do warning |
| Critical | 20% | 52% | 93% | disco crítico |
| Offline | 30% | 40% | 50% | snapshot retido; Health=Offline; falhas consecutivas |
| Stale | 28% | 44% | 55% | último sucesso há ~2 h; indicador stale |
| Unknown | — | — | — | sem métricas (pending, não zero) |
| Refreshing | 35% | 48% | 60% | ProgressRing visível |
| Partial | 12% | **unknown** | 51% | RAM ausente, **nunca 0** (unknown ≠ zero) |

## Isolamento e Release-safety

- Toda a pasta `src/ServerMonitor.App/Qa/` está **excluída de builds Release** (`<Compile Remove>` condicional no `.csproj`). Verificado: build Release compila 0/0 sem o QA.
- Um único `#if DEBUG` em `App.xaml.cs` decide o caminho; em Release `qaHealth` é `const false`.
- Cobertura: `tests/ServerMonitor.App.Tests/Qa/QaHealthHarnessTests.cs` (16 testes) valida que o harness está desligado por omissão e que cada cenário mapeia para os flags corretos do card (incl. unknown ≠ zero).

## O que o harness NÃO faz

Não substitui a QA visual no ecrã (screenshots light/dark) nem a QA real com servidores. É o **mecanismo seguro** para produzir esses estados de forma determinística; a captura/observação visual continua a exigir o desktop real.
