# Process — Visual QA (obrigatório em mudança de UI significativa)

Owner: **ui-visual** (+ qa-release-docs). Preset preferido: Antigravity.

1. Lançar o desktop WinUI real: `dotnet run --project src/ServerMonitor.App/ServerMonitor.App.csproj` (skill `run` ajuda).
2. Capturar estado em **light** e **dark** (e System). Usar `maestri portal` (Computer Use) para screenshots quando aplicável.
3. Verificar:
   - Glassmorphism Apple-inspired, materiais nativos, sem "SaaS genérico".
   - Brand Accent `#1846E1` intacto; tokens realmente aplicados no runtime (não só definidos).
   - Centragem/alinhamento/responsividade; acessibilidade/alto contraste; Desktop Acrylic fallback.
   - Health states (dot/label), stale indicator discreto, empty state.
   - Localização pt-BR/pt-PT/en-US.
4. Comparar contra o design direction em `USER-PREFERENCES.md`.
5. Registar evidências (screenshots) e veredicto no run report. Rejeição → devolver com A/B/C concretos.

> "Token XAML definido ≠ usado em runtime" [P-002]. Nunca aprovar UI só por leitura de XAML.
