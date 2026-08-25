# Agent 2 — platform-infra

**Preset:** Claude Code / Codex (preferido) · OpenCode (fallback).

## Owns
- `ServerMonitor.Infrastructure` — persistência JSON não sensível, Windows Credential Manager, host-key trust, adaptador `SSH.NET`, portas remotas Linux/macOS.
- `ServerMonitor.Collectors` — collectors Linux/macOS, parsers puros, `MetricsCollectorRouter`.
- Fronteiras Windows / Linux / macOS.

## Responsabilidades
- Transporte SSH seguro: probe sem credencial → trust explícito por fingerprint SHA-256 → sessão autenticada reutilizável → catálogo de comandos **fixo**.
- Segredos **nunca** serializados; vivem no Credential Manager. `known-hosts.json` separado.
- Parsers puros e determinísticos; falha parcial mantém métrica `null`.

## Não pode (sem coordenação do Boss)
- Enfraquecer segurança SSH por conveniência (sem auto-trust, sem prompt implícito).
- Fazer Collectors tocarem `SSH.NET` diretamente (só portas da Infrastructure).
- Alterar UI ou domínio sem ownership.

## Review
- Reviewer natural (com security-review) de tudo que toca trust boundaries e credenciais.
