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

- Review independente pode usar um Floor próprio (ex.: `maestri floor create --pull-request <n>`, ou Floor na branch
  do candidato em modo leitura).
- O reviewer **não escreve** no Floor principal nem na sua branch. Findings voltam ao implementador, que corrige no
  Floor principal; segunda ronda quando houver findings materiais.
- Trabalho material: reviewer ≠ implementer.

## 4. Paralelismo

- Paralelizar eixos **independentes**: review, QA, investigação.
- Sem swarms. Nunca dois writers na mesma superfície, mesmo que haja Floors suficientes para isso.

## 5. Base e herança

Antes de criar um Floor, verificar e registar:

1. SHA exato da base e `main == origin/main` (quando é o esperado);
2. working tree do Térreo limpa;
3. untracked/ignored relevantes no Térreo.

O Floor é um `git worktree` real (em `<pai do repo>\.maestri\floors\…`), criado a partir do `HEAD` atual, e
**copia** para ele o conteúdo untracked/ignored do Térreo, exceto saída de build (`bin/`, `obj/`, `dist/`,
`__pycache__/`, `artifacts/`…). Medido em 0.49: chegaram `.boss/`, `.private/`, `.claude/settings.local.json`,
`CONTEXT.md`. Consequências:

- Base suja ⇒ decisão explícita antes de criar; nunca herdar alterações por omissão.
- Material local/privado é **duplicado** no Floor: continua ignorado pelo Git, mas conta para o cleanup (§10) e para a
  verificação de histórico antes do primeiro push.
- Ficheiros locais não versionados (ex.: `.boss/`) chegam como **cópia divergente**. A canónica fica no Térreo; editar
  a cópia do Floor **não** altera a canónica. Ver §12.
- Com árvores ignoradas grandes, `maestri floor create` pode expirar no CLI enquanto a criação continua. **Não repetir**
  o comando: esperar até `maestri floor list` mostrar o Floor e só então trabalhar nele.

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

- A app é lançada por agentes **apenas** via `tools/qa/Start-QaApp.ps1` (exige o executável Debug x64 do próprio
  Floor/worktree); nunca `dotnet run` nem o `.exe` diretamente.
- Parser `--qa*` estrito e fail-closed; só dados sintéticos; nunca dados reais.
- Enquanto a raiz de QA for partilhada (backlog): **uma instância QA de cada vez** em toda a máquina, não por Floor.
- Proveniência por lançamento: `HEAD` + árvore limpa + dll/output correto do Floor em causa.

## 9. Ciclo de PR

- O owner do Floor pode fazer push e abrir PR **quando autorizado**; descrição segundo o template do repositório, se existir.
- Acompanhar checks, reviews e conflitos; correções voltam ao owner do Floor.
- Merge/Land apenas com autorização aplicável. Landing não substitui a verificação pós-merge (CI de `main`).

## 10. Cleanup

Antes de remover um Floor (`maestri floor delete`/Land/`git worktree remove`), provar:

- working tree limpa e trabalho preservado/reachable a partir de `main` ou de outro ref seguro;
- sem commits únicos que se percam;
- sem untracked/ignored valiosos (incluindo evidência sob cópias locais como `.boss/`);
- nenhum processo, harness ou sessão a usar o caminho;
- alvo exato (caminho do Floor confirmado em `maestri floor list` / `git worktree list`);
- sem `--force` por omissão.

`maestri floor delete` **apaga a branch** salvo `--keep-branch`: apagar branch é decisão separada, sujeita a autorização.

## 11. Relatório

`STATUS · WHAT CHANGED · EVIDENCE · RISKS/BLOCKERS · NEXT DECISION`. `NOT_RUN ≠ PASS`. Ao parar numa fronteira de
autorização, nomear a decisão exata necessária.

## 12. Ficheiros operacionais não versionados

`.boss/` é local e ignorado pelo Git: não viaja em PR. Alterações a `BOSS.md`/runbooks são feitas na cópia canónica do
Térreo (são operação, não implementação de produto) e registadas no relatório; nunca na cópia herdada por um Floor.
