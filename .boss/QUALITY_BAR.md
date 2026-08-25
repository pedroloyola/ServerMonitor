# QUALITY_BAR — Padrão de aprovação do utilizador (Pedro)

> O Boss usa isto para julgar trabalho e para o **User Quality Proxy**: só dizer "o Pedro não aprovaria isto" quando **fundamentado** por uma entrada real aqui ou em `USER-PREFERENCES.md`. Nunca inventar gostos.

## Invariantes de qualidade (aprendidos, não negociáveis)

1. **"Compila" ≠ "está correto".** Build verde não é aprovação. Funcionalidade tem de ser validada de facto.
2. **Token XAML definido ≠ usado em runtime.** Um recurso XAML alterado pode não estar ligado ao runtime — validar na aplicação real.
3. **UI valida-se na aplicação real** (desktop WinUI vivo + portal/Computer Use screenshots light/dark), não por leitura de XAML.
4. **Unknown ≠ zero.** Métrica desconhecida representa-se como `null`, nunca como `0`.
5. **Flaky não se ignora — investiga-se.** Testes intermitentes têm causa raiz.
6. **Races corrigem-se estruturalmente**, não com delays probabilísticos nem `Task.Yield`/`Task.Delay` de conveniência.
7. **Segurança SSH não se enfraquece por conveniência.** Sem auto-trust, sem prompt implícito; fail-closed em host desconhecido/mismatch.
8. **Não introduzir scope futuro** num milestone atual (ex.: não meter macOS/polling/compact mode fora do milestone).
9. **Preservar trabalho existente** antes de trocar de agente (handoff, nunca recomeçar do zero).
10. **Nunca pedir credenciais em texto.** Segredos vivem no Windows Credential Manager.
11. **Visual = Apple-inspired glassmorphism**, minimal utility. **Brand Accent = `#1846E1`** (não tocar sem autorização).
12. **Decisões funcionais exigem testes** (determinísticos).
13. **QA real Linux/macOS** tem valor adicional aos unit tests — obrigatório quando um milestone reivindica suporte real de plataforma.
14. **NOT RUN ≠ PASS** — um gate não executado não conta como passado.

## Padrões de rejeição a reconhecer

Quando o utilizador diz algo como: _"não gostei" · "isso não funciona" · "não está centrado" · "não parece glass" · "essa abordagem está errada"_ →
1. Investigar a causa concreta.
2. Verificar se existe aprendizagem generalizável.
3. Se sim → registar em `PITFALLS.md`/`LEARNINGS.md`/`USER-PREFERENCES.md`.
4. Recuperar automaticamente na próxima tarefa semelhante.

## Exemplos de veredicto do Boss (fundamentados)

- _"Rejeitado: dashboard SaaS genérico viola o design direction documentado — Apple-inspired glassmorphism, minimal utility (#1846E1)."_
- _"Rejeitado: representa métrica em falta como 0 — viola unknown≠zero."_
- _"Devolvido a review: race 'resolvida' com Task.Delay — exige single-flight/ordenação determinística."_
