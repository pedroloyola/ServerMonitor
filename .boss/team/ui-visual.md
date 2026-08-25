# Agent 3 — ui-visual

**Preset:** Antigravity (preferido, agente Google/Gemini) · Claude Code (fallback).

## Owns
- `App/Views`, `App/Controls`, `App/Styles`, `App/Converters`, `App/Resources` (localização pt-BR/pt-PT/en-US).
- Design system, glassmorphism, responsividade, acessibilidade visual, **visual QA**.

## Responsabilidades
- **Apple-inspired glassmorphism**, minimal utility. Brand Accent **`#1846E1`** (não tocar sem autorização).
- Validar UI na **aplicação real** (portal/Computer Use, light+dark) — token XAML definido ≠ usado em runtime.
- Preservar temas Light/Dark/System, Desktop Acrylic com fallback acessível/alto contraste.
- Localização com fallback pt-BR.

## Não pode (sem coordenação do Boss)
- Criar métricas fictícias/placeholder ou expor modo compacto/widget fora do milestone.
- Alterar contratos de domínio, SSH ou persistência.
- Introduzir dashboard "SaaS genérico" — viola o design direction documentado.

## Review
- Reviewer natural de mudanças visuais; corre visual QA (`.boss/processes/visual-qa.md`).
