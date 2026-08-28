# ServerAlyzer 1.0.0 — Notas de versão (Release Candidate)

Primeira versão pública da ServerAlyzer: um painel de desktop **local-first** para monitorizar
servidores Linux e macOS a partir do teu PC.

## Destaques

- **Local-first, sem conta, sem telemetria.** A monitorização acontece por SSH direto entre o teu PC e
  os servidores; nada é enviado para servidores da aplicação.
- **Métricas Linux e macOS** — CPU, memória, disco e uptime, com monitorização automática e estado de
  saúde por servidor.
- **SSH seguro** — confiança de host key por fingerprint SHA-256 (sem auto-trust), *mismatch* bloqueia,
  segredos no Windows Credential Manager.
- **Histórico local** com gráficos temporais (SQLite, retenção 30 dias).
- **Descoberta local** passiva de servidores SSH (mDNS), sem varrer portas.
- **System tray + notificações locais** em transições reais de saúde.
- **Modo compacto** (widget) sempre-no-topo.
- **Workloads só-leitura** — containers Docker e serviços systemd/launchd (sem controlo remoto).

## Instalação

Distribuição pela **Microsoft Store** (trata da instalação, assinatura e atualizações). Requer
**Windows 11 x64**.

## Limitações conhecidas

- A observação de **Docker** requer que o utilizador SSH já tenha permissão de acesso ao Docker no
  servidor. A app é só-leitura e não usa `sudo`.
- No **macOS**, `launchd` reporta serviços a correr/parados, mas não distingue falhas reais de saídas
  de tarefas one-shot.
- Suporte oficial: **Windows 11 x64**. Servidores monitorizados: Linux (serviços via `systemd`) e
  macOS (serviços via `launchd`).

## Privacidade e segurança

Ver `PRIVACY.md` e `SECURITY.md`. Reporta vulnerabilidades via GitHub Security Advisories.
