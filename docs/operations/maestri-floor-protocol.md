# ServerAlyzer — Maestri Floor Protocol

Estado: normativo para trabalho orquestrado com Maestri ≥ 0.49. Âmbito: **mecânica** de isolamento Git e ciclo de PR.
Não altera autorização, fronteiras de segurança, gates de review nem regras de QA — essas continuam no `BOSS.md`
(local, não versionado), nos ADRs (`docs/decisions/`) e nos documentos de cada milestone.

**Precedência:** pedido explícito atual do utilizador → `BOSS.md` → ADRs → este protocolo. Em conflito, este protocolo cede.

## 1. Térreo (Ground)

- Normalmente `main` limpa, igual a `origin/main`.
- É o **plano de controlo**: orquestração, verificação de estado, observação de PR/CI, integração final.
- Implementação material **não** acontece no Térreo.

## 2. Floor principal

- **1 milestone/feature material = 1 Floor principal**, com branch própria (`maestri floor create "Nome" --branch <tipo>/<slug>`).
- **Um owner de implementação por superfície.** Outros agentes no mesmo Floor só leem.
- Floors nativos são o mecanismo preferido. `git worktree` manual é **fallback**, apenas quando um Floor não serve a
  tarefa com segurança (registar porquê). Mudança trivial pode continuar na branch atual sem Floor.

## 3. Reviews

O Git só permite uma branch num worktree: enquanto o Floor principal detém a branch do candidato, nem
`maestri floor create --pull-request <n>` (medido: recusado) nem outro Floor nessa branch são possíveis. Por isso:

- **Normativo:** o **orquestrador** (não o reviewer) cria um Floor de review numa branch local descartável
  `review/<slug>-rN` e aponta-a ao **SHA exato** do candidato (`maestri floor create "Review …" --branch
  review/<slug>-rN`, depois `git merge --ff-only <sha>` nesse Floor). Registar o SHA revisto; nova ronda = novo SHA.
  Nunca push, nunca commit.
- Se o `--ff-only` falhar (ex.: `main` avançou depois da base do candidato): **nunca** `reset --hard`; criar novo Floor
  `-rN+1` a partir de uma base de que o candidato descenda, ou reportar BLOCKED.
- `--pull-request <n>` só quando nenhum Floor detém a branch do PR.
- O reviewer **só lê**: não escreve no Floor principal nem em nenhuma branch. Findings voltam ao implementador, que
  corrige no Floor principal; segunda ronda quando houver findings materiais.
- Trabalho material: reviewer ≠ implementer.
- `review/*` é descartável: removida com o Floor de review (`floor delete` sem `--keep-branch`), com a autorização de
  cleanup aplicável (§10).

## 4. Paralelismo

- Paralelizar eixos **independentes** de leitura: review, investigação. Instâncias de QA da app ficam **serializadas** (§8).
- `dotnet test` concorrente em Floors diferentes partilha `%TEMP%`/`%LOCALAPPDATA%`: só em paralelo quando as suites
  não usarem raízes fixas partilhadas; em dúvida, serializar.
- Sem swarms. Nunca dois writers na mesma superfície, mesmo que haja Floors suficientes para isso.

## 5. Base e herança

Antes de criar um Floor, verificar e registar:

1. `git fetch`, depois SHA exato da base e `main == origin/main` (quando é o esperado);
2. working tree do Térreo limpa;
3. untracked/ignored relevantes no Térreo.

O Floor é um `git worktree` real (em `<pai do repo>\.maestri\floors\…`), criado a partir do `HEAD` atual, e
**copia** para ele o conteúdo untracked/ignored do Térreo, exceto saída de build (`bin/`, `obj/`, `dist/`,
`__pycache__/`, `artifacts/`…). Medido em 0.49, sem `--copy-ground` (que só copia o layout do canvas): chegaram
`.boss/`, `.private/`, `.claude/settings.local.json`, `CONTEXT.md`. Consequências:

- Base suja ⇒ decisão explícita antes de criar; nunca herdar alterações por omissão.
- Material local/privado é **duplicado** no Floor (caminho também sincronizado se o pai do repo o for): continua
  ignorado pelo Git, mas conta para o cleanup (§10) e para a verificação de histórico antes do primeiro push.
  Decidir por Floor se `.private/` é necessário; se não for, remover a cópia logo após a criação (com GO).
- Ficheiros locais não versionados (ex.: `.boss/`) chegam como **snapshot sem valor normativo**. Ver §12.
- Com árvores ignoradas grandes, `maestri floor create` pode expirar no CLI enquanto a criação continua (medido).
  **Não repetir** o comando (pode criar segundo worktree/branch): confirmar `git worktree list` + branch e esperar
  que `maestri floor list` mostre o Floor; se não aparecer em ~15 min, reportar BLOCKED.

## 6. Autoridade

Floor, PR, estado do Floor (`working`/`review`/`done`), checks verdes ou botão de merge/Land são **mecanismo, não
autorização**. Push, merge, Land, delete de Floor ou branch, tag, release, Store e qualquer ação destrutiva ou externa
continuam a exigir a autorização aplicável do utilizador. O estado do Floor não é evidência: verificar Git/CI/testes.

## 7. Isolamento de runtime

**Floor = isolamento Git/código. Não é isolamento de runtime.** Um Floor **não** isola:
`%LOCALAPPDATA%` · Credential Manager · `~/.ssh` · Firewall do Windows · raízes temporárias (`%TEMP%`) ·
sockets/portas · processos · app instalada da Store.

Todas as regras de QA/runtime continuam obrigatórias em qualquer Floor. Cada novo caminho de Floor que execute
testhost/app pode gerar regras de Firewall: no fim da milestone, listar regras pelo caminho exato do Floor e remover
apenas por `Name` exato, com GO humano.

## 8. QA

- A app é lançada por agentes **apenas** via `tools/qa/Start-QaApp.ps1`; nunca `dotnet run` nem o `.exe` diretamente.
- Floors não herdam `bin/`/`obj/`: primeiro build Debug x64 da `ServerMonitor.slnx` **do próprio Floor**; build e
  teste pela mesma `.slnx` (P-008); nunca um dll do Térreo ou de outro Floor. O `Start-QaApp` impõe
  `<worktree>\src\ServerMonitor.App\bin\x64\Debug\…` — é o guard, não o substitui.
- Parser `--qa*` estrito e fail-closed; só dados sintéticos; nunca dados reais.
- Enquanto a raiz de QA for partilhada (backlog): **uma instância QA de cada vez** em toda a máquina, não por Floor.
- Proveniência por lançamento: `HEAD` + árvore limpa + dll/output correto do Floor em causa.

## 9. Ciclo de PR e integração

- O owner do Floor pode fazer push e abrir PR **quando autorizado**; descrição segundo o template do repositório, se existir.
- Acompanhar checks, reviews e conflitos; correções voltam ao owner do Floor.
- **Integração só por merge do PR no GitHub**, com autorização aplicável; depois, no Térreo: `git fetch` +
  `git pull --ff-only` e verificação pós-merge (CI de `main`).
- **`maestri floor land` não é usado** até a sua semântica (merge local? push? delete de branch?) estar medida e
  documentada: um Land local em `main` contornaria PR/CI e quebraria `main == origin/main`.

## 10. Cleanup

Antes de remover um Floor (`maestri floor delete`/`git worktree remove`), provar:

- working tree limpa e trabalho preservado/reachable a partir de `main` ou de outro ref seguro;
- sem commits únicos que se percam;
- sem untracked/ignored valiosos (incluindo evidência sob cópias locais como `.boss/`);
- nenhum processo, harness ou sessão a usar o caminho;
- alvo exato (caminho do Floor confirmado em `maestri floor list` / `git worktree list`);
- sem `--force` por omissão.

`maestri floor delete` **apaga a branch** salvo `--keep-branch`: apagar branch é decisão separada, sujeita a autorização.

Depois de remover, provar: o caminho do Floor já não existe; ausente de `git worktree list` e `maestri floor list`;
`git worktree prune --dry-run` sem saída. Se o diretório ficar (ex.: cópias ignoradas como `.private/`), GO explícito
antes de o apagar. (Comportamento de `floor delete` sobre conteúdo ignorado: ainda não medido em 0.49.)

## 11. Relatório

`STATUS · WHAT CHANGED · EVIDENCE · RISKS/BLOCKERS · NEXT DECISION`. `NOT_RUN ≠ PASS`. Ao parar numa fronteira de
autorização, nomear a decisão exata necessária.

## 12. Ficheiros operacionais não versionados

`.boss/` é local e ignorado pelo Git: não viaja em PR. A cópia **canónica é a do Térreo**:

- Agentes num Floor leem `.boss/**` (incl. `BOSS.md`, `team/`, `OWNERSHIP.md`) pelo **caminho absoluto do Térreo**;
  a cópia do Floor é snapshot sem valor normativo.
- Alterações a `BOSS.md`/runbooks fazem-se na cópia do Térreo (são operação, não implementação de produto) e ficam
  registadas no relatório; nunca na cópia herdada por um Floor.
