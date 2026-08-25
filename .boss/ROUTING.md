# ROUTING — Capability-based provider routing

> **Não** `role = provider` hardcoded. É: `role → capacidades exigidas → preset preferido → disponibilidade → fallback`.
> Presets reais (`maestri preset list`): `Claude Code`, `Codex`, `Antigravity`, `OpenCode`, `Shell`.

## Mapa conceptual → preset real

| Conceito do modelo | Preset real do Maestri | Notas |
|---|---|---|
| "Claude" | **Claude Code** | raciocínio profundo, arquitetura, docs, review |
| "Gemini" | **Antigravity** | agente da Google (Gemini) — UI/visual, browser QA |
| "Codex" | **Codex** | precisão, testes, security review; pode estar rate-limited |
| flexível/extra | **OpenCode** | fallback aberto, multi-modelo |
| não-agente | **Shell** | comandos crus, sem raciocínio de agente |

## Disponibilidade (DINÂMICA — atualizar a cada run)

| Provider | Estado | Última verificação |
|---|---|---|
| Claude Code | ✅ available | 2026-08-25 |
| Antigravity | ⚠️ instalado, mas precisa de aceitação de ToS interativa na 1ª execução (ver L-009) | 2026-08-25 |
| Codex | ⚠️ potencialmente rate/token-limited | 2026-08-25 |
| OpenCode | ✅ available (fallback) | 2026-08-25 |
| Shell | ✅ available | 2026-08-25 |

> Verificar sempre com `maestri preset list` antes de rotear. Se Codex indisponível, **cair para Claude Code** — nunca falhar a tarefa por causa disso.

## Tabela de routing por role

| Role | Capacidades exigidas | Preferido | Fallback 1 | Fallback 2 |
|---|---|---|---|---|
| architecture-core | domínio, concorrência, design, raciocínio | Claude Code | Codex | OpenCode |
| platform-infra | SSH, OS, persistência, redes | Claude Code | Codex | OpenCode |
| ui-visual | WinUI/XAML, design, visual QA | Antigravity | Claude Code | OpenCode |
| tests-reliability | testes, race/flaky, perf | Codex | Claude Code | OpenCode |
| security-review | adversarial, trust boundaries | Codex | Claude Code | — |
| qa-release-docs | QA real, portal, docs, packaging | Claude Code / Antigravity | conforme tarefa | — |

## Overrides do utilizador

- `Boss, usa Antigravity` → força preset neste run.
- `Boss, não uses Codex` → exclui preset; usar o fallback seguinte.
- Registar o override no run report (não persiste entre runs a menos que peça).

## Regras de decisão de harness

- Arquitetura / concorrência / raciocínio longo → **Claude Code**.
- UI / XAML / glassmorphism / visual QA → **Antigravity** (fallback Claude Code).
- Review preciso / adversarial / testes → **Codex** (fallback Claude Code).
- Considerar sempre: tarefa · contexto · saúde do provider · budget de tokens · ferramentas disponíveis.
