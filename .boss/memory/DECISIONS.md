# Decisions — índice de ADRs + decisões operacionais

> ADRs formais vivem em `docs/decisions/`. Aqui: índice + decisões **operacionais** do Boss (routing, processo). Não duplicar o conteúdo dos ADRs.

## ADRs do produto (fonte: `docs/decisions/`)
- **ADR-001** — Direct SSH.
- **ADR-004** — Local server configuration.
- **ADR-005** — Standard/compact widget strategy (fronteira de apresentação; widget provider = integração futura separada).
- **ADR-006** — SSH transport & host trust.
- **ADR-007** — Windows Credential Manager.
- **ADR-008** — Linux metrics pipeline.
- **ADR-010** — macOS metrics pipeline.
- **ADR-011** — Monitoring engine & scheduling (M6).

## Decisões operacionais do Boss
- **D-001 (2026-08-25)** — Sistema Boss vive em `.boss/` no repo (legível por qualquer agente recrutado; versionável). Não commitado sem autorização.
- **D-002 (2026-08-25)** — Mapeamento de providers: "Gemini"→preset **Antigravity**; presets reais = Claude Code, Codex, Antigravity, OpenCode, Shell. Routing por capacidade (ver `ROUTING.md`).
- **D-003 (2026-08-25)** — Memória em duas camadas: ficheiros `.boss/` (fonte de verdade, cross-agent) + notes/fichários do Maestri no canvas (camada de superfície visual do Boss Workspace).
