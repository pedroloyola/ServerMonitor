# Process — Feature / Milestone development

1. **Arranque** (ritual §0 do BOSS.md): recuperar memória, contexto do repo, `git status`, disponibilidade de providers.
2. **Classificar** e escolher **nível** (SOLO / SMALL TEAM / FULL ORCHESTRA).
3. **Plano** curto: objetivo, subtasks, ownership por região (`OWNERSHIP.md`), critérios de aceitação ligados ao `QUALITY_BAR.md`.
4. **Registar run** em `STATE.md` (objetivo, agentes, pendente).
5. **Recrutar** especialistas conforme `ROUTING.md`: `maestri recruit "Nome" --preset "<preset>" --role "<Role>"` e `maestri connect` se preciso.
6. **Distribuir** subtasks com fronteiras claras; nunca dois agentes na mesma região sem coordenação.
7. **Executar & controlar**: `maestri ask`/`--batch`; `maestri check` para progresso (nunca interromper agente a trabalhar; nunca reenviar prompt em timeout — usar `check` e esperar).
8. **Receber handoffs** (`templates/handoff.md`). Consolidar.
9. **Review independente** (implementer ≠ reviewer): pode devolver trabalho abaixo do `QUALITY_BAR`.
10. **Fix → re-review**.
11. **Gates** (`quality-gates.md`) — NOT RUN ≠ PASS.
12. **QA real / visual** quando aplicável (`real-environment-qa.md`, `visual-qa.md`).
13. **Learning loop**: registar pitfalls/learnings reais.
14. **Report consolidado** ao utilizador (`templates/run-report.md`). Atualizar `STATE.md`/`JOURNAL.md`/`METRICS.md`.
15. **Não** commitar/push sem autorização.
