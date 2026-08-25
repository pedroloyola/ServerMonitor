# Project Handbook — Server Monitor

> Arquitetura atual, convenções, invariantes críticos. Fonte de verdade operacional para o Boss e agentes. Manter sem drift.

## O que é
App **desktop WinUI 3** (Windows) — painel pessoal de monitorização de servidores. Interface calma, compacta, materiais nativos. **Local-first**; cloud opcional ainda **não** implementado.

## Stack / bootstrap
- **.NET 10**, Windows App SDK **2.3.1**, app **unpackaged x64**.
- MVVM simples (sem framework MVVM extra). `Microsoft.Extensions.Hosting` para DI/lifecycle. `Logging.Debug` (sem ficheiros/dados sensíveis).
- Recursos RESW externos; idiomas **pt-BR** (padrão/fallback), **pt-PT**, **en-US**.
- `DesktopAcrylicBackdrop` para o backdrop da janela; Acrylic interno com tint subtil e fallback opaco/alto contraste.
- Build/test: `dotnet build ServerMonitor.slnx` · `dotnet test ServerMonitor.slnx`.

## Estrutura de projetos (dependências)
```
App ──→ Core ; App ──→ Infrastructure ; App ──→ Collectors
Infrastructure ──→ Core
Collectors ──→ Core ; Collectors ──→ Infrastructure
```
- **App** — DI, shell WinUI, Views/ViewModels, diálogos, serviços de apresentação, localização, design tokens. Depende de contratos do Core; não acede diretamente a persistência/Credential Manager/SSH.
- **Core** — `Server`, snapshot normalizado, validação, estados de conexão, host identities, contratos, workflows de perfil, `Monitoring/`, `Enums/ServerHealth`.
- **Infrastructure** — persistência JSON não sensível, Credential Manager, host-key trust, adaptador `SSH.NET`, portas remotas Linux/macOS de comandos fechados.
- **Collectors** — collectors Linux/macOS, parsers puros, `MetricsCollectorRouter`; conhece só portas da Infrastructure, nunca `SSH.NET`.

## Invariantes críticos
1. Segredos (passwords/passphrases/chaves) **nunca** serializados — só Windows Credential Manager. `servers.json` guarda config não sensível + referência GUID opaca.
2. SSH: probe sem credencial → host desconhecido exige **aprovação explícita** (fingerprint SHA-256) → mismatch **bloqueia** → só depois autentica. Trust em `known-hosts.json` separado.
3. `unknown ≠ zero`: falha de parsing mantém métrica `null`.
4. Refresh manual protegido por **single-flight por `ServerId`**. Snapshot/estado/erro vivem em store **transitório** em memória; não em `servers.json`.
5. UI recebe só `IServerMetricsCollector`/estado de monitorização — nunca executor/texto shell; não conhece diferença Linux/macOS.
6. Domínio/persistência/VMs não conhecem largura de janela nem "widget mode" (fronteira de apresentação compacta escolhida no XAML).
7. Concorrência via `TimeProvider` injetável; testes determinísticos.

## Estado dos milestones (2026-08-25)
- **M1–M3** — base visual, gestão manual de servidores, **SSH seguro** (ADR-001/004/005/006/007). Committed.
- **M4** — métricas Linux (ADR-008). Verde no histórico; ver memória Claude Code.
- **M5** — métricas macOS (ADR-010). Verde; correção de race no store single-flight.
- **M6** — **monitoring engine automático** (ADR-011): `MonitoringEngine` (`IHostedService`), health via `ServerHealth`+`MonitoringThresholds`, estado em `IServerMonitoringStateStore` transitório, UI observa (sem timers no card), refresh manual via `IMonitoringEngine.RefreshNowAsync`. **457→466 testes verdes**, branch `feat/m6-monitoring-engine`, **não commitado**. QA real (auto-refresh, intervalos, hidden, health visual, perf/leak, screenshots light/dark) **pendente** — exige desktop WinUI vivo + servidores reais + Computer Use.
- Ainda **não** existe: descoberta de rede, notificações, system tray, cloud.

## Documentos-fonte
`CONTEXT.md` (spec completa) · `README.md` · `docs/architecture.md` · `docs/metrics.md` · `docs/decisions/ADR-*.md`. **Não** ler `.private/` automaticamente.
