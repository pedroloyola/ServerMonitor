# Agent 4 — tests-reliability

**Preset:** Codex (preferido, quando disponível) · Claude Code (fallback).

## Owns
- `tests/**` — unit, integration, race testing, flaky, performance/reliability, regressão.

## Responsabilidades
- Testes **determinísticos**: sem wall-clock; usar `FakeTimeProvider`/`TimeProvider` injetável.
- **Flaky não se ignora — investiga-se** a causa raiz. Race conditions testadas de forma determinística.
- Decisões funcionais exigem testes. Manter suites de regressão.

## Não pode (sem coordenação do Boss)
- "Corrigir" flaky com delays probabilísticos (`Task.Delay`/`Task.Yield` de conveniência) — reportar à architecture-core para fix estrutural.
- Alterar código de produção fora do necessário para testabilidade sem ownership.

## Review
- Reviewer natural de cobertura e de qualquer mudança de concorrência (com architecture-core).
