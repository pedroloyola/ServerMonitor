# Learnings — descobertas generalizáveis

> Regras que melhoram trabalho futuro (não erros concretos — esses vão para PITFALLS). Ligar a pitfalls com [P-00x].

- **L-001** — Concorrência: nunca estabelecer ownership de estado por timing do scheduler; usar single-flight com ordenação determinística. [P-001, P-004]
- **L-002** — Verificação: "compila"/"token definido"/"teste verde" não é aprovação. Validar comportamento real; UI na app viva. [P-002]
- **L-003** — Determinismo: toda a lógica temporal atrás de `TimeProvider` injetável; testes conduzem o tempo, não o esperam. [P-004]
- **L-004** — Semântica de dados: ausência ≠ zero; preservar `null`/`unknown` até à camada de apresentação. [P-005]
- **L-005** — Segurança como invariante: não negociar trust boundaries por conveniência; tradeoff → aprovação humana. [P-006]
- **L-006** — Materiais WinUI têm quirks de plataforma (backdrops de sistema, controllers nativos); validar no runtime real da versão-alvo. [P-003]
- **L-007** — Handoff: quota/limite de provider nunca justifica recomeçar; checkpoint + handoff + auditoria do worktree pelo próximo agente.
- **L-008** — Provider routing por capacidade, não hardcoded; "Gemini" no Maestri = preset **Antigravity**; Codex pode estar rate-limited → cair para Claude Code sem falhar a tarefa.
- **L-010** — Ownership de um recurso partilhado que outro caminho pode cancelar/remover deve ser estabelecido **sob o mesmo lock** que faz o cancel/remove (enqueue-under-the-gate), para ordenar Add-vs-Cancel deterministicamente. Review de concorrência: procurar "gate libertado antes de mutar estado partilhado". [P-007, L-001]
- **L-012** — **QA visual de desktop sem ferramenta de Computer Use:** quando não há tool de screenshot de desktop (só `maestri portal`=browser/Android), usar **PowerShell**: `System.Drawing.Graphics.CopyFromScreen` da rect da janela (via `user32 GetWindowRect`/`MoveWindow`) → PNG → **Read** (vê imagens); interação por `user32 SetCursorPos/mouse_event`. Desempenho por `Get-Process` (WS/threads/handles/CPU); cadência de rede por `Get-NetTCPConnection`. Requer sessão desktop **interativa** (confirmar com uma captura-smoke). Focar a captura na janela-alvo (privacidade). Helper: `qacap.ps1`.
- **L-013** — Avaliar leaks só na **janela completa** de observação: uma subida nos primeiros ~5–7 min pode ser aquecimento/heap-antes-de-GC + oscilação, não leak. Confirmado no M6: WS 160→183 e estabiliza; handles oscilam limitados. [P-002, QUALITY_BAR]
- **L-011** — Antes de confiar em "testes passaram", fazer **rebuild limpo** e confirmar descoberta (`--list-tests`) — o build incremental do `.slnx` pode deixar a dll de teste stale. NOT RUN ≠ PASS aplica-se também a "compilou o que eu editei?". [P-008, L-002]
- **L-009** — **Antigravity exige aceitação de ToS interativa na 1ª execução** antes de ser roteável (fica preso no ecrã de onboarding e um `ask` devolve a TUI, não trabalho). Tratar como "provider não-pronto" → fallback via `maestri recruit "Nome" --preset "Claude Code" --replace "Nome"` (mantém ligações/posição/routines). Não aceitar ToS de terceiros em nome do utilizador sem o consultar. (2026-08-25, audit M6)
- **L-014** — Em deployment Windows App SDK self-contained, `IsSupported()==true` não prova que `Register()` consegue carregar todos os payloads nativos. Exercitar a API real no binário final e verificar os redistributables efetivamente copiados; tornar payload obrigatório um gate de build. [P-009, L-002]
