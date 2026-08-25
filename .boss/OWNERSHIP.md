# OWNERSHIP — Mapa de propriedade da codebase

> Ownership = **primeiro responsável + reviewer natural + contexto esperado**. Não é exclusividade absoluta.
> Se dois agentes precisam da mesma região → **o Boss coordena** e regista no run report.

## Mapa (Server Monitor)

| Região | Owner | Reviewer natural | Notas / invariantes |
|---|---|---|---|
| `src/ServerMonitor.Core/**` (domain, Models, Monitoring, Enums, validação) | **architecture-core** | security-review | Sem dependências de UI/SSH/persistência. `unknown≠zero`. Estado transitório. |
| `src/ServerMonitor.Core/Monitoring/**` | **architecture-core** | tests-reliability | Scheduling, health, thresholds, single-flight. Concorrência determinística via `TimeProvider`. |
| `src/ServerMonitor.Infrastructure/**` (SSH, persistência, Credential Manager, host-trust, portas remotas) | **platform-infra** | security-review | `SSH.NET` encapsulado. Fail-closed. Segredos nunca serializados. |
| `src/ServerMonitor.Collectors/**` (collectors + parsers puros + router) | **platform-infra** | tests-reliability | Conhece só portas da Infrastructure, nunca SSH.NET. Parsers puros/determinísticos. |
| `src/ServerMonitor.App/Views/**`, `Controls/**`, `Styles/**`, `Converters/**`, `Resources/**` | **ui-visual** | qa-release-docs | Glassmorphism Apple-inspired. `#1846E1`. Localização pt-BR/pt-PT/en-US. |
| `src/ServerMonitor.App/ViewModels/**`, `Services/**`, `App.xaml.cs` (DI/composição) | **architecture-core** (co-owned com ui-visual em VMs de apresentação) | ui-visual | MVVM simples. VM de apresentação não conhece largura da janela / widget mode. |
| `tests/**` | **tests-reliability** | owner da região testada | Testes determinísticos; sem wall-clock; race/flaky investigados. |
| `docs/**`, `docs/decisions/**` (ADRs), `README.md`, `CONTEXT.md` | **qa-release-docs** | architecture-core | Documentação honesta e sem drift. |
| `THIRD-PARTY-NOTICES.md`, licenças | **qa-release-docs** | security-review | Só deps de runtime nas notices; test-only não entra. |
| `.private/**` | **utilizador** | — | **Não ler automaticamente** por agentes comuns. Estratégico/local. |
| `.boss/**` | **Boss** | — | Sistema operativo do Boss. |

## Fronteiras críticas (não cruzar sem coordenação)

- UI **nunca** acede diretamente a persistência, Credential Manager ou SSH.NET — só contratos do Core.
- Collectors **nunca** tocam SSH.NET diretamente — só portas da Infrastructure.
- A UI recebe apenas `IServerMetricsCollector`/estado de monitorização — nunca executor ou texto shell; não conhece diferença Linux/macOS.
- Domínio/persistência/VMs **não** conhecem largura de janela nem "widget mode".
