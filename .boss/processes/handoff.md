# Process — Handoff (troca de agente / recuperação de quota)

> Objetivo: **nunca** perder trabalho ao trocar de agente ou quando um provider fica sem quota.

## Quando
- Provider perto do limite de tokens/quota.
- Troca planeada de especialista (ex.: arquitetura → visual).
- Fim de sessão com trabalho pendente.

## Fluxo
```
agente deteta limite / Boss deteta → termina em ponto seguro (build não partido)
→ escreve handoff (templates/handoff.md) num note do Maestri e/ou .boss/reports/
→ novo agente audita o worktree (git status/diff, testes) ANTES de continuar
→ continua a partir do checkpoint
```

## Regras
- **Nunca** `reset --hard`/`clean` para "recomeçar". Preservar o worktree.
- O handoff deve deixar claro: o que está feito, o que falta, ficheiros tocados, testes, riscos, próximo passo.
- Confirmar via `maestri check` que o agente anterior terminou antes de editar os mesmos ficheiros.
- Atualizar `STATE.md` com o ponto de handoff.
