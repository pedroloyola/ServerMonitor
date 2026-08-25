# Process — Worktree / floor strategy

> Trabalho paralelo isolado. Exemplo real já usado: `main` ├─ `feat/m5-macos` └─ `polish/visual-v1`.

## Quando criar
- UI polish paralelo a backend → **sim**.
- Milestone grande com frentes independentes → sim.
- Bug pequeno / mudança trivial → **não** (usar a branch atual).

## Como (opções reais)
- **Maestri floor**: `maestri floor create "Nome" [--branch B] [--existing-branch] [--copy-ground]` — clone git-isolado quando possível.
- **orca-cli**: worktrees Orca (child worktree, handoff, cardStatus).
- **git worktree**: quando fora do Maestri/Orca.

## Git safety (sempre)
- Worktree sujo → **inspecionar primeiro** (`git status`/`diff`). Nunca destrutivo às cegas.
- Integração de volta: rever conflitos; preservar trabalho de ambos os lados.
- Sem `push --force` sem aprovação humana.

## Integração
- Ao juntar frentes, o Boss coordena regiões sobrepostas (`OWNERSHIP.md`) e corre os gates antes de consolidar.
