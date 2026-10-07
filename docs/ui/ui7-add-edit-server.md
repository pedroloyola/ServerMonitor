# UI.7 — Adicionar / Editar servidor

**Estado: READY_TO_MERGE** (2026-10-07).
- **PR:** #27, branch `ui/ui7-add-edit-server`, base `4ffbb1b`. Código validado em `c25ffb9`; o commit que acrescenta este documento é só de documentação.
- **CI do PR:** verde em `a5915a5` (run 37554757487), `ace95a5` (run 37593466055) `7ca4d86` (run 37606201930) e `c25ffb9` (ver PR).
- **Âmbito:** o modal Add/Edit foi substituído pela página inteira do Figma dentro do shell do UI.6, preservando todas as invariantes M14: SSH config import, credenciais, ProxyJump, trust, key discovery e o teste de ligação em 4 etapas. É um redesenho de apresentação; não há migração de armazenamento.
- **Execução:** um PR em três blocos internos. UI.7A (IA, página, sessão, guard, ciclo de vida, harness), UI.7B (SSH, credenciais, ProxyJump, trust, import, key discovery; gate Vigil obrigatório antes do 7C) e UI.7C (validação, teste de ligação, feedback, fidelidade, limpeza do legado).

**Autoridade visual:** Figma `Qvk5dUFgsWf4UOYzfAkiDV`, página 05: secções 04 Adicionar e importar SSH, 05 Editar servidor, 06 Validação, 07 Importação (problemas), 08 Teste de ligação, 09 Confiança SSH, 11 Confirmações, 12 Feedback. Precedência: invariantes de segurança/produto > a11y/plataforma > Figma.

| Alvo | Escuro | Claro |
|---|---|---|
| Adicionar (inicial) | `112:3252` | `112:3412` |
| Editar (populado) | `112:4613` | `112:4779` |
| Validação (campos obrigatórios) | `112:16837` | `112:17000` |
| Importar de SSH | `112:3888` | `112:4088` |
| Teste — a testar / verificada / falha | `112:5597` / `112:5962` / `112:6329` | `112:5779` / `112:6145` / `112:6515` |
| Confiança — desconhecida / mismatch / PJ passo 1 | `112:7078` / `112:7457` / `159:241` | `112:7267` / `112:7648` / `159:1037` |
| Descartar alterações? | `112:9018` | `112:9203` |

## 1. Decisões humanas (vinculativas)

- **H-UI7-1 — regras novas só na escrita.** São recusados apenas em `ValidateDraft` (editor) e em `Validate(ServerInput)` (Add/Update do `ServerService`):
  - host com esquema, caminho ou espaço;
  - chave `.pub` escolhida como privada (alvo e jump);
  - jump host == servidor final (endpoints normalizados).

  `Validate(Server)` não as aplica. É o overload do load/quarentena (`ServerService.cs:234`) e do export/restore de backup (`BackupPayloadSerializer`), por isso servidores e backups existentes continuam a carregar. Um servidor antigo que viole uma regra abre no editor e só mostra o erro ao guardar; esconder e restaurar continuam a funcionar.
- **H-UI7-2 — duplicado = aviso não-bloqueante.**
  - Comparação em memória contra visíveis + ocultos, lida uma vez por visita (índice pré-calculado).
  - Identidade = `SshEndpoint` normalizado + utilizador (ordinal) + via. Um Edit exclui-se a si próprio.
  - Mostra o aviso Figma 06 com "Abrir servidor", que passa pelo guard. Guardar continua permitido. Sem persistência nem índice persistido.
  - No import, as linhas com a mesma identidade dizem "Já adicionado" e continuam selecionáveis.
- **H-UI7-3 — um único guard de saída.**
  - Cobre sidebar, Voltar, Cancelar, Esc, "Abrir servidor" e ativação externa (notificação/widget/tray/deep-link).
  - Limpo → sai. Sujo → "Descartar alterações?", com "Continuar a editar" por omissão.
  - Nunca guarda implicitamente; nunca descarta em silêncio.
  - Fechar a janela para a tray não é navegação: o editor fica.

## 2. Decisões do Boss (dentro das regras do UI.0)

| ID | Decisão |
|---|---|
| B-1/B-2 | `ServerEditorPage` por visita (`IDisposable`, sem cache). `NavigationDestination.ServerEditor` → Servidores na sidebar. `ServerEditorViewModel` continua a ser o único VM do formulário; não há editor paralelo. |
| B-3 | `IServerEditorSession` (singleton) é o dono único do ciclo de vida: no máximo um editor vivo e Dispose em todas as saídas. `IServerDialogService` ficou só com `ConfirmRemoveAsync`. |
| B-4/B-5 | O Save continua `DashboardViewModel` → `IServerProfileService`; o caminho de escrita não mudou. Sucesso → Detalhe + toast "Servidor adicionado" / "Alterações guardadas". Falha → fica na página com o aviso Figma 06 e "Tentar guardar" (oculto quando a configuração está bloqueada por um restauro). Cancelar → origem, com o foco no controlo que abriu o editor (incluindo a linha de uma sugestão de rede). |
| B-6 (F-1) | **Trust inalterada.** Uma chave aceite explicitamente é gravada logo por `TrustAndConnectAsync` e o Cancel não a reverte. É o status quo do M14.4b-2, decidido a partir do pedido humano de seguir a semântica existente. Um teste fixa esta retenção. |
| B-7 (F-2) | Nada corre depois do Dispose do editor: sem re-teste SSH e sem escrita de estado. Corrigido no VM com verificações de tempo de vida depois de cada await. |
| B-8 (F-3) | Os testes no editor já não escrevem o estado de ligação do servidor guardado; esse estado só muda depois do Save. |
| B-9/B-10/B-11 | Teste, confiança e import vivem numa **camada modal dentro da página**, não num ContentDialog: o WinUI só permite um por XamlRoot, e "Descartar alterações?" tem de poder abrir. As 4 etapas são as reais (`SshConnectionStage`), nunca as 6 linhas PJ do Figma, e o estado vem de `ReachedStage`/família de `ErrorCode`/`ConnectionHint`, nunca de texto. O accept só acontece quando o prompt no ecrã == pendente; o mismatch não tem botão de aceitar. Com a camada aberta, o foco entra nela (botão seguro) e a página por trás fica inerte. |
| B-12 | Seletor de chave: nome do ficheiro e flyout com as chaves descobertas (metadata-only) + "Procurar ficheiro…". O caminho completo fica em tooltip/HelpText. Não há caixa de caminho editável. |
| B-16 | Erros por campo só depois de uma tentativa de Test/Save; depois são atualizados por campo, sem tempestades de validação. O resumo é o subtítulo do H1 com `LiveSetting=Assertive`, e o foco vai para o primeiro campo inválido. |
| m-2 | Ativação externa com a pergunta de descarte aberta: só a última é guardada, e só corre se o utilizador escolher "Descartar". "Continuar a editar" larga-a. Cliques durante a pergunta são largados, porque o diálogo é modal. |
| L-1 | Um re-teste que arranque com a pergunta de descarte aberta é cancelado de imediato. |

## 3. Escritas (write boundary)

| Ficheiro | Quando é escrito |
|---|---|
| `servers.json` / `routed-servers.json` | Só no Save (Add/Update via `ServerProfileService`). |
| Credential Manager | Só no Save (stage → JSON → retirar o antigo). Um Test sem override pode **ler**, mas nunca escreve uma credencial nova (prova R-15 com um store gravador). |
| `known-hosts.json` / `known-hosts.routes.json` | Só no accept explícito de uma chave ("Confiar e …"), no hop certo: direct/jump → store direto; target → store routed. |
| Estado de ligação (memória) | Só depois do Save (B-8). |
| Cancel / Esc / sidebar / ativação | Zero escritas de servidor e de credencial. Zero trust, salvo uma chave já aceite explicitamente (B-6). |

Não há mudança de formato persistido. O diff do Core é só `ServerValidator.cs` + códigos acrescentados no fim de `ServerValidationError.cs`. Infrastructure, Collectors, Compact e Widget não mudaram. O legado foi removido: `ServerEditorModal`, `DialogReturnFocus`, os `ShowEditor*` e 71 chaves resw mortas por cultura. O `ModalOverlayHost` mantém-se porque continua em uso.

## 4. Diferenças deliberadas face ao Figma

| # | Área | Figma | App (`c25ffb9`) | Razão |
|---|---|---|---|---|
| DV-1 | Moldura da janela | janela flutuante r30 com sombra, sem botões de legenda, pílula "Free · Conhecer Pro" | janela nativa do Windows com botões de legenda, sidebar 216, sem pílula Pro | plataforma (UI.6 G-1/G-7) · D4 |
| DV-2 | Tipografia | SF Pro; títulos de diálogo 22/23/24; subtítulo do H1 Regular 13 | Segoe UI Variable; título de diálogo `SaTitle` 20/28; `SaPageSubtitle` | D1 · G-25 (sem tokens novos) |
| DV-3 | Disponibilidade dos botões | Testar/Adicionar desativados (.58 inicial, .45 com erros) | sempre ativos; os erros aparecem na tentativa | B-14 / G-10 (sem gating pelo teste) |
| DV-4 | Ênfase primária | nenhuma na página; ênfase .38 nos diálogos | nenhuma na página (default = ação à direita); `SaDialogCommandButtonStyle` uniforme (.14) | G-2 · G-16 (coerência com os diálogos do UI.2) |
| DV-5 | Seletor de chave | "Escolher ficheiro…" | chave descoberta pré-selecionada ("Chave encontrada em .ssh"), flyout das chaves + "Procurar ficheiro…", sem caminho editável; caminho completo em tooltip/HelpText | funcionalidade M14.5 preservada (G-3/G-4, B-12) |
| DV-6 | Ajuda de preparação | não desenhada | link "Como preparar o meu servidor?" sob o subtítulo da autenticação e na etapa falhada | funcionalidade preservada (G-5, B-13) |
| DV-7 | Teste de ligação | direto sem linhas; ProxyJump com 6 linhas de glifos e "Concluir" | 4 etapas `SshConnectionStage` reais para direto e routed (jump no título da etapa 1); `SaIcon` + anel + **coluna de estado em texto**; dica + comando copiável + link de preparação sob a etapa falhada; "Voltar ao formulário" | nunca fingir etapas (B-9, G-6/G-7) · a11y |
| DV-8 | Sujeito da confiança | "prod-web-01 · 192.168.1.10:22 · monitor"; "Chave guardada · ED25519" | `HostKeySubjectDisplay` (hop exato), algoritmo tal como apresentado ("ssh-ed25519") | segurança B-10 (hop exato, só dados reais) |
| DV-9 | Impressão digital | SF Pro Regular 12 | `SaMonoTextStyle`, selecionável | legibilidade (B-10) |
| DV-10 | Camada modal | backdrop sobre a janela inteira, incluindo a sidebar | teste/confiança/import com véu só sobre a página (camada na página); "Descartar alterações?" é um ContentDialog sobre tudo | arquitetura (um ContentDialog por XamlRoot) |
| DV-11 | Ordem dos botões em "Descartar?" | [Continuar a editar] [Descartar alterações] | [Descartar alterações] [Continuar a editar], foco e default em "Continuar a editar" | contrato `SaDialogStyle` Destructive (UI.2: Enter = ação segura) |
| DV-12 | Cor do texto de erro | `#FF858D` / `#BD5766` | texto `SaDangerTextBrush` (L `#A83E36`), contornos `SaErrorBrush` | a11y (contraste ≥ 4.5) |
| DV-13 | Resumo da validação | subtítulo em "você" | mesmo sítio, em "tu", `LiveSetting=Assertive`, foco no primeiro campo inválido | D6 · a11y (B-16) |
| DV-14 | Feedback do import | toast "Perfil SSH importado" | só a linha de dica | D-C4 (sem anúncio duplo) |
| DV-15 | Estados do import | "Linha 12" + botões de recuperação 07 | classificação só do resolver/VM, texto genérico, sem número de linha | B-11 (sem alteração ao SshConfig; um BLOCK nunca é "corrigível") |
| DV-16 | Posição do toast | flutua sobre o conteúdo | linha própria no fundo do Detalhe (40/36 das margens) | padrão UI.5 |
| DV-17 | "Remover servidor…" | no Editar | ausente | fora de âmbito (G-12, BACKLOG) |
| DV-18 | Segredos guardados | password do jump mostrada como "••••" | campo vazio + "Palavra-passe · guardada" / "Deixa em branco para manter a atual" | segurança G-13 (segredos nunca desenhados) |
| DV-19 | Opção do SO | "Detetar automaticamente" | "Automático" (chave existente) | B-18 (reutilização) |
| DV-20 | Foco à entrada | — | o H1 recebe o foco programático | a11y B-20 (coerente com UI.3–UI.6) |
| DV-21 | Responsivo | sem frames < 1440 | duas colunas com ≥ ~760 de conteúdo, empilhado abaixo; padding compacto < 700; botão do cabeçalho sob o H1; opções e barra de ações quebram; diálogos 640/720 limitados à janela | B-19 |
| DV-22 | Texto | 06/07 em "você", "Fingerprint" | pt-PT em "tu", "Impressão digital apresentada", vocabulário "jump host"; pt-BR/en-US escritos | D6 · B-18 |
| DV-23 | Vidro em Light | contorno branco + sombra suave | materiais Acrylic nativos `Sa*` (borda mais fraca) | D7 / UI.1 aceite |
| DV-24 | Comandos dos diálogos em janela estreita | dois botões lado a lado | com conteúdo < 404 px (janela ≈ < 620), empilham a toda a largura; o botão seguro/default tem o foco inicial | responsivo B-19 · a11y |
| DV-25 | Cartão de opções em janela estreita | Porta 160 · SO 400 · Atualizar 400 numa linha | 420–760 px de formulário: Porta+SO / Atualizar; < 420: um campo por linha | responsivo B-19 |
| DV-26 | Ordem de Tab do botão do cabeçalho | botão desenhado em cima à direita | "Importar de SSH" / "Voltar ao detalhe" é o **último** Tab stop; a entrada foca o H1 e o primeiro Tab é Nome | a11y B-20 (ordem centrada no formulário) |
| DV-27 | Foco com a camada aberta | — | o foco entra na camada (botão seguro/lista) e a página por trás fica inerte; ao fechar volta a "Testar ligação" / "Importar de SSH"; depois de "Continuar a editar" volta ao controlo anterior | a11y (focus trap e retorno) |
| DV-28 | Estados das linhas do import | "Disponível" / "Selecionado" | idem, mais "Bloqueado" (cor de atenção, com o motivo em texto) e "Já adicionado" | B-11 / H-UI7-2 / G-15 |

Nota de comportamento (Cortex n-2/n-3): a lista de servidores conhecidos para o aviso de duplicado é um retrato por visita; qualquer mutação concorrente obriga a sair da página. Uma ativação externa largada com "Continuar a editar" não é repetida.

## 5. Reviews

| Reviewer | Veredicto final | Notas |
|---|---|---|
| Cortex (arquitetura) | **APPROVED_WITH_NITS** (7A: CHANGES_REQUIRED → M-1 corrigido; c1 e c3) | Nits não bloqueantes (n-2/n-3 documentados acima). Classificou o defeito `--qa-discovery` como BACKLOG P3. |
| Vigil (segurança, obrigatório) | **APPROVED** (gate 7B após 1 correção de teste; c1; deltas c2 e c3) | 0 achados C/H/M. Mutações próprias V1–V11 todas mortas, incluindo a separação H-UI7-1 (V7: 16 testes de load/restore partem). Info-1/Info-2 aceites. |
| Prism (fidelidade Figma) | **APPROVED** (c1 e c2 CHANGES_REQUIRED → corrigidos; c3) | Comparação Figma ao vivo ↔ screenshots isolados D/L/estreito; DV-1…DV-28. |
| Beacon (runtime/a11y/l10n) | **APPROVED** (c1 CHANGES_REQUIRED: foco B-1…B-4 → corrigidos; c3) | ~65 lançamentos isolados no total; teclado real com verificação do PID em primeiro plano. |
| Atlas (testes) | **APPROVED_WITH_NITS** | Feito por um subagente interno Claude independente, porque o Atlas Codex estava sem quota até 2026-10-12. UI.7 corrido 20× seguidas (8800 execuções) sem falhas; delta c3: 457 testes ×10 sem falhas; c3-L1 corrigido em `c25ffb9`. |

## 6. Testes e contraprovas

- **Contagens no candidato `c25ffb9`** (`--no-incremental`, via `ServerMonitor.slnx`):
  - Debug **5483**: 5482 passaram, 1 skip pré-existente em Infra, 0 falhas.
  - Release **5089**: 5088 passaram, 1 skip, 0 falhas.
  - Base `4ffbb1b`: 5040 Debug.
- **Contraprovas (BOSS §10), todas em código de produção, com restauro verificado por SHA-256:**
  - 7A: 19;
  - 7B: 20 + 13 (fix c1 e camada);
  - 7C: 26 + 14 mutações do 7A re-corridas sobre o código final (n-6);
  - final c1: 20; final c2: 19; final c3: 2 (D08b, E01);
  - Vigil: V1–V11; Atlas: X1–X6.
  - Sobreviventes iniciais: M03, M16, C14, D08 e D08b (Atlas c3-L1: o Importar de SSH durante um Save só era travado pelo botão; o controller passou a recusar e há teste comportamental). Os testes foram reforçados e as mutações re-corridas por nome; agora matam. X5 é um mutante equivalente (duplicação pré-UI.7) e C07 também é equivalente, justificado.
- **Provas obrigatórias (cada uma com teste e mutação):**
  - Test ≠ Save;
  - Cancel sem escritas (Add e Edit, todas as saídas);
  - sem resíduo de credencial ou de trust (salvo B-6);
  - identidade direct/routed;
  - a troca de autenticação não guarda o segredo errado;
  - SSH config bloqueado continua bloqueado;
  - a UI não contorna o Core (H-UI7-1);
  - a ativação externa com o editor sujo nunca descarta em silêncio;
  - o duplicado não bloqueia o Save;
  - configs e backups antigos continuam a carregar.
- **Determinismo:** barreiras/TCS, `FakeTimeProvider`, sinais normativos, sem `Task.Delay`/`Yield`/relógio. Um teste intermitente encontrado pelo Vigil (gate 7B, amostrava `IsTestingConnection`) passou a usar o token de cancelamento: 25/25 e 10/10 com a mutação.

## 7. NOT_RUN

- Narrator e High Contrast real.
- `LiveSetting`/`DescribedBy` em runtime: os clientes UIA geridos não expõem estas propriedades. Estão declarados no XAML e verificados por guarda estática.
- Retorno de foco à linha de uma sugestão de rede em runtime: o harness `--qa-discovery` sai sem janela por um defeito **pré-existente** do UI.6 (BACKLOG). Coberto por testes unitários e pela mutação C24.
- Ativação externa com o editor sujo em runtime: o harness recusa `--qa-activation` com `--qa-editor` por desenho. Coberto pelos testes L-1/H-UI7-3/m-2.
- Hover do link com o rato (só foco/sublinhado verificado).
- Light na localização (só Dark nas 3 línguas; Light verificado em pt-PT).
- Servidor SSH real: todo o teste de ligação foi QA com resultados scriptados no harness isolado.

## 8. Dados reais, Firewall, resíduos

- Antes e depois de cada janela de QA (7A ×3, 7B, 7C ×2, Beacon c1 e c3), o snapshot de metadata + SHA-256 de `%LOCALAPPDATA%\ServerMonitor`, `~\.ssh`, as pastas do pacote e a contagem de alvos do Credential Manager mostrou **0 diferenças**. O `known-hosts.json` real ficou inalterado e o `known-hosts.routes.json` real não existe.
- Todos os lançamentos foram feitos via `tools/qa/Start-QaApp.ps1`, com paragem por PID exato e 0 processos residuais. Não houve prompt de Firewall durante a QA.
- O Firewall ganhou 4 regras "Query User" Inbound Allow para o `testhost.exe` de `Infrastructure.Tests` (Debug/Release, TCP/UDP) do Floor UI.7. A remoção por Name exato está autorizada e fica registada no fecho.

## 9. Motion (UI.11)

Oportunidades, nenhuma implementada:
- abrir/fechar a camada (fade + subida 8 px);
- troca Teste ↔ Confiança (cross-fade);
- mudança de estado das etapas (desenho do visto);
- aparecimento de erros de campo (altura + fade);
- avisos de duplicado/falha (deslizar);
- toast (já no padrão UI.5);
- segmentado de autenticação (deslizar a pílula);
- cartões do jump host (expandir).

## 10. Backlog

- **`--qa-discovery` e outros harnesses de dados** (health, notifications, compact, history, workloads, screenshot) saem com exit 0 e sem janela.
  - Causa: o cast `IServerLoadStatusSource` introduzido no UI.6 (`bcf340c`/`2e6d1cd`) falha com os seus doubles.
  - Correção: os doubles implementam a interface, mais um teste que resolve o shell para cada composição.
  - Classificação: P3, só QA/Debug.
- `OnServersChanged` (Dashboard/Settings) sem marshalling para o UI thread. Seguro hoje; corrigir antes de qualquer escritor em background (Cortex m-5).
- "Remover servidor…" no editor (G-12).
- Estados 07 do import: frase "Os dados já introduzidos…" e esconder "Usar perfil" quando nada é importável, mais uma ação de recarregar (Prism N-1).
- Regra de formato do host aplicada também ao jump host (hoje só ao alvo, como na SPEC).
- Revelar a password (olho do Figma): só com prova UIA de que o valor revelado não fica exposto (D-B1).
- Avisos do analisador xUnit em testes UI.7 (9).
- Prova de restore com um ficheiro de backup gravado antes do UI.7 (Atlas N-5).
- Auditoria de chaves resw dinâmicas noutras famílias (Workload*, History*, Navigation*).
- L-2: fechar para a tray mantém o editor e um segredo já capturado até ao Dispose (comportamento de produto aceite com H-UI7-3).
