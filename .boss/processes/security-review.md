# Process — Security review

Owner: **security-review** (independente do implementer). Preset: Codex → Claude Code.

## Checklist
- **Trust boundaries SSH**: fail-closed em host desconhecido/mismatch; sem auto-trust; sem prompt implícito.
- **Segredos**: nunca serializados em `servers.json`; nunca logados; fonte = Windows Credential Manager. Referência GUID opaca apenas.
- **Comandos remotos**: catálogo **fixo** (sem injeção de shell); limites de output; timeout/cancellation.
- **Dependências**: `dotnet list package --vulnerable --include-transitive` = 0. Só deps de runtime em notices.
- **Logs**: sem dados sensíveis.
- **Falha**: não-transitória não é retried agressivamente.

## Escalada
- Qualquer **tradeoff** de segurança → **aprovação humana** (Boss para para o utilizador). Nunca aprovado só pelo agente.
- Usar a skill `security-review` do Claude Code para o diff pendente quando útil.
