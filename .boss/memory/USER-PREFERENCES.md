# User Preferences (não sensíveis)

> Preferências de UX/design/workflow/qualidade/Git/comunicação. Base do **User Quality Proxy** (só invocar quando fundamentado aqui).

## Comunicação
- **Português** na comunicação; **inglês** no código/identificadores; UI do Server Monitor com referência **pt-BR**.
- Reports consolidados e acionáveis, com referências de ficheiros.

## Design / UX
- **Apple-inspired glassmorphism**, minimal utility. Interface calma e compacta, materiais nativos.
- Brand Accent **`#1846E1`** — não alterar sem autorização.
- Rejeita dashboards "SaaS genéricos".

## Qualidade
- Ver `QUALITY_BAR.md`. Destaques: validação real (não "compila"), unknown≠zero, races estruturais, segurança SSH inegociável, sem scope futuro no milestone atual, QA real quando se reivindica plataforma.

## Git / workflow
- **Conservador.** Auto-commit = **false**. Auto-push = **false**. Nunca operações destrutivas em worktree sujo sem inspeção.
- Preservar trabalho existente antes de trocar de agente.
- Não commitar/avançar milestone sem pedido explícito.

## Segurança
- Nunca pedir credenciais em texto. Segredos no Windows Credential Manager.
