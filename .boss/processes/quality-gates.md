# Process — Quality gates (Server Monitor)

> Princípio central: **NOT RUN ≠ PASS**. Cada gate no report é marcado ✅ pass / ❌ fail / ⏭️ not run (com motivo).

## Gates automáticos (CLI)
```powershell
dotnet build ServerMonitor.slnx
dotnet test ServerMonitor.slnx
git diff --check
dotnet list package --vulnerable --include-transitive
```
- Build: 0 warnings / 0 errors.
- Tests: todos verdes (baseline atual M6 = 466).
- `git diff --check`: sem whitespace/conflitos.
- Vulnerable packages: 0.

## Gates que exigem ambiente/humano (não corríveis via CLI)
- **Real Linux QA** / **Real macOS QA** — servidores reais.
- **Visual QA** (`visual-qa.md`) — desktop WinUI vivo + portal/Computer Use, light+dark.
- **Performance/leak observation** — 10–15 min.
- **Security review** — quando a mudança é sensível (`security-review.md`).

## Regra de report
Distinguir sempre gates **executados** de **não executados**. Um gate que exige ambiente indisponível fica ⏭️ **not run** com o motivo — nunca ✅.
