# Team — 6 especialistas + Boss

As **roles não são providers**. Cada role é executada pelo preset que o `ROUTING.md` indicar conforme disponibilidade. O Boss mantém estes ficheiros atualizados quando o projeto evolui (evitar documentação abandonada).

| Role | Ficheiro | Owner de | Preset preferido |
|---|---|---|---|
| architecture-core | [architecture-core.md](architecture-core.md) | Core, Monitoring, concorrência, DI | Claude Code |
| platform-infra | [platform-infra.md](platform-infra.md) | SSH, Infrastructure, Collectors, OS | Claude Code / Codex |
| ui-visual | [ui-visual.md](ui-visual.md) | WinUI, XAML, design system, visual QA | Antigravity |
| tests-reliability | [tests-reliability.md](tests-reliability.md) | testes, race/flaky, perf | Codex |
| security-review | [security-review.md](security-review.md) | security review, trust boundaries | Codex |
| qa-release-docs | [qa-release-docs.md](qa-release-docs.md) | QA real, release, docs, packaging | Claude Code / Antigravity |

Cada agente, ao terminar uma subtask, produz um **handoff/report estruturado** (`.boss/templates/handoff.md`) que outro agente consome. Convenção do canvas: o role está registado como preset do Maestri (`maestri role list`), e é recrutado com `maestri recruit "Nome" --preset "<preset>" --role "<Role Maestri>"`.
