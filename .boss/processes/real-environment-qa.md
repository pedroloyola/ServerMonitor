# Process — Real-environment QA

> Obrigatório quando um milestone **reivindica suporte real de plataforma** (Linux/macOS). Valor adicional aos unit tests.

Owner: **qa-release-docs** (+ platform-infra).

## Checklist (M6 exemplo)
- Auto-refresh a correr sozinho por servidor.
- Intervalos (ex.: 10s / 30s) respeitados.
- Hidden-server: não recolhe/estado correto.
- Health states reais (healthy/attention/critical) via `ServerHealth`+`MonitoringThresholds`.
- Sleep/resume: catch-up ≤1 após salto de relógio.
- Performance/leak: observação 10–15 min (sem crescimento anómalo, sem leaks de subscrição).
- Métricas reais Linux/macOS coerentes; `unknown` fica `null`, não 0.
- Falhas de trust/auth/timeout preservam resultado tipado, sem snapshot.

## Registo
- Ambiente usado (OS/SDK/servidores) — sem segredos.
- Resultado por item: ✅/❌/⏭️(motivo).
- Report em `.boss/reports/`.
