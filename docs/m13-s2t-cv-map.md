# M13 S2-T — MAPA DE CONDIÇÕES CV

**Cumprimento da CV-15.** Condição → secção de desenho → **implementação de produção** → testes →
evidência de mutação → estado.

**Regra:** nenhuma condição desaparece em silêncio. Remover redação durante condensação **não** revoga
uma condição. Uma condição só sai marcada `SUPERSEDED BY <regra>`, com justificação.

**Fontes normativas:** `.boss/tmp/m13-s2t-vigil-conditions.md` (condições) ·
`docs/m13-s2t-linearizable-state-machine.md` (desenho) · `.boss/BOSS.md` §9 e §10.

**Base de medição:** worktree `ServerMonitor-m13-s2t`, ramo `agent/m13-s2t-tray`.
Baseline dos testes filtrados por `Tray|Theme|Flyout|FailSafe`: **143 passam, 0 falham**, em **10 corridas seguidas** com resultado idêntico.
Gates completos na árvore entregue: **Debug 1847/1847**, **Release 1812/1812**, zero abortos. A diferença de 35 vem de um
`ItemGroup Condition="'$(Configuration)' != 'Debug'"` no projeto de testes que remove `Qa\**\*.cs` —
condição pré-existente, não introduzida por esta entrega.

---

## Estado das condições

| CV | Assunto | Implementação de produção | Testes | Mutação | Estado |
|---|---|---|---|---|---|
| **CV-1** | modelo de confiança da `WndProc`, sete pontos | `Shell/Tray/TrayCallbackContract.cs` · roteamento em `TrayHostWindow.OnMessage` | `TrayCallbackContractTests` (9) | M15–M18 mortas | **FECHADA** para a função pura; a entrega da mensagem real é QA humana |
| **CV-2 / CV-2b** | dois orçamentos independentes | `EpisodeFrequencyLimiter` · `TrayStateMachine.Transition` | `T4` + convergência adversarial | M8 morta | **FECHADA** |
| **CV-3** | comportamento sob `TerminateProcess` | n/a — não há `NIM_DELETE` de um processo morto | — | — | **`NOT_RUN`** — S6, requer interação humana |
| **CV-4** | `Unavailable` no ordinal 0 · produtor único de `Available` | `TrayLifecycleState.cs` · `HandleAddCompleted` · valores **explícitos** em `TrayAffordanceState` | contrato de estados + valores fixados | M5 (7), M6 (6), **M45** | **FECHADA** |
| **CV-5** | `szTip`/`hIcon` estáticos | `NativeTrayRegistration` — resolvidos **uma vez** no construtor | `NativeTrayRegistrationTests` (6) | M23 morta | **FECHADA** na parte decidível |
| **CV-6** | mensagem forjada ignorada | — | — | — | `SUPERSEDED BY` **CV-6b** |
| **CV-6b** | quatro casos independentes de validação | `TrayCallbackContract.TryDecode` | quatro `[Fact]` A/B/C/D, cada um variando **um** campo | M15, M16 mortas | **FECHADA** |
| **CV-7** | topologia de thread | `TrayHostWindow` · **`TrayStateMachine.Schedule` marshala para a thread de UI** | `A_scheduled_recovery_attempt_is_marshalled_before_it_touches_the_shell` | **M50** | **CORRIGIDA.** O código divergia: os retries corriam no threadpool. Ver §5 |
| **CV-8** | custo nativo síncrono na thread de UI | idem | idem | **M50** | **MEDIDA · aceitável** — e agora **na topologia em que foi medida**: `NIM_ADD` mediana 3,16 ms / máx 4,36 ms, `NIM_DELETE` mediana 0,36 ms, contra 16,7 ms por frame a 60 Hz |
| **CV-9** | reentrância com flyout aberto | `Shell/Tray/FlyoutReentrancyGate.cs` · `OwnedTrayIconAdapter.ShowFlyout` | `FlyoutReentrancyGateTests` (6) | M26, M27 | **FECHADA** na decisão; a ativação da janela auxiliar é medida humana (matriz P, passo 5) |
| **CV-10** | acoplamento limitador ↔ custo de UI | `EpisodeFrequencyLimiter.DefaultCapacity = 5 / 60 s` | `T4` | M8 morta | **FECHADA** |
| **CV-11** | residual de admissão suprimida (LOW, aceite) | ordem das guardas em `Transition` | `T4` | — | **FECHADA · residual escrito** |
| **CV-12** | evidência de mutação na entrega | — | matriz da secção 3 | **65 entradas · 63 corridas** | **FECHADA com duas limitações declaradas** (M24, M25) |
| **CV-13** | só um episódio ADMITIDO por B pode expirar | `BeginEpisode`, só depois de `TryBeginEpisode` | `CV13` | M14 morta | **FECHADA** |
| **CV-14** | B não limita tentativas dentro de um episódio | `EpisodeFrequencyLimiter` com **um** método | `CV14` ×2 (inclui teste de arquitetura por reflexão) | M8 morta | **FECHADA** |
| **CV-15** | integridade do documento normativo | — | este ficheiro | — | **ATIVA · este mapa é o cumprimento.** Ver a **retratação** na secção 4 |
| **CV-16** | limpeza fail-closed, **REFINADA** | `CleanupDisposition` · `HandleCleanupCompleted` · `RecordFailedAdd` | `T5`, `QD1`–`QD6` | M10, M44, **M59–M64** | **FECHADA · refinada, não relaxada** (§7) |
| **CV-17** | notificação informativa antes da saída fail-safe | `Services/FailSafeExitNotice.cs` · `WindowsAppNotificationService.ShowFailSafeExitNotice` (30 min) · gancho no CAS de `AppLifecycleController` | `FailSafeExitNoticeTests` (16) · `WindowsAppNotificationServiceTests` (3) | M36, M37, M37b, M39, M39b, M40 | **FECHADA** |
| **CV-18** | contrato fechado da ação da notificação | `NotificationActivationContract` — **uma linha**: `("FailSafeExit", "OpenDashboard")` | quatro casos independentes + tabela de 9 pares | M38, M38b | **FECHADA** |
| **CV-19** | ressalva do passo 2 para conclusões de efeito | `Transition`, passo 2 | `T11` · **`CV19_a_stale_add_completion_in_a_live_episode_is_reconciled_and_compensated`** | **M13** | **FECHADA.** O estado é construído por teste; ver §3.1 |
| **CV-20** | canal de efeitos fechado por construção | tipos `private` aninhados em `TrayStateMachine`; capacidade retida só por `EffectExecutor._native` | `TrayCapabilityBoundaryTests` (T14a/b) · `TrayOwnershipCompletenessTests` (T14c) | M11, M19, M20, M22, **M34** | **FECHADA · a imprecisão do T14c foi corrigida** |
| **CV-21** | exceção do sink não consome o único disparo | **NÃO IMPLEMENTADA AQUI** | — | — | **com o Cortex** (`ServerMonitor-m13-s2`) |
| **CI-1b** | grafias numéricas de enum em payloads hostis | a ação `FailSafeExit` entra no mesmo `switch` de pares literais e **herda** o fail-closed | tabela de 9 pares, inclui `("FailSafeExit", "1")` e `("2", "1")` | M38b | **HERDADA · não agravada.** A dívida da S2 continua a ser da S2 |

---

## 1. O que está implementado e provado nesta entrega

1. **O núcleo de decisão.** `TrayStateMachine` é a máquina linearizável aprovada: um `Transition(evento,
   monotonicNow)` chamado direta e sincronamente por cada fonte de evento, com o preâmbulo de três
   guardas — absorção de Release · obsolescência de geração com a ressalva CV-19 · terminalização por
   prazo antes de o evento ser examinado. Os efeitos são **dados passivos** (`record struct` privado, sem
   `Execute`) e só o `EffectExecutor` recebe e retém `INativeTrayRegistration`.
2. **Os dois orçamentos.** A (3 tentativas, 250 ms + 1000 ms) e B (`EpisodeFrequencyLimiter`, 5 episódios
   / 60 s) são independentes **por construção**: B tem exatamente um método público, portanto nada no
   programa tem forma de lhe comunicar um sucesso.
3. **O contrato da `WndProc`.** Função pura, sete pontos, lista fechada de eventos v4, `uID == 1`, e o
   `wParam` tratado como âncora não fiável e **descartado** — não fixado — quando cai fora de todos os
   monitores.
4. **A fronteira nativa.** `NativeTrayRegistration` devolve o `BOOL` real de `Shell_NotifyIcon`, que é a
   razão de existir desta slice: o WinUIEx 2.9.3 deita-o fora. `NIM_SETVERSION` v4 é exigido sem recuo
   silencioso para v3, porque sob v3 os parâmetros do callback significam outra coisa e o contrato CV-1
   estaria a validar campos que não existem.
5. **A janela hospedeira.** `TrayHostWindow` é top-level, sem dono, nunca mostrada e **não**
   `HWND_MESSAGE` — a forma que a S-1(A) mediu como recebendo `TaskbarCreated` (id `0xC073`) nos casos
   headless e em primeiro plano, empacotados.
6. **A escalada `CS8509`.** Aplicada de facto à árvore, com prova diferencial (secção 3.2).

## 2. O que está por implementar, e porquê

**Não falta código.** A ligação em DI, o flyout e a notificação de saída fail-safe estão feitos. O que
resta é verificação que exige um desktop e um humano.

| Item | Estado |
|---|---|
| **CV-3** e o caso S6 `FORCED-TERMINATION TRAY CLEANUP` | **`NOT_RUN`.** Exigem terminar o processo à força. |
| Reinício real do Explorer → recuperação | **`NOT_RUN`.** Exige reiniciar o Explorer. |
| Ícone DPI após `WM_DPICHANGED` (M24) | **`NOT_RUN`.** Só é visível a olho. |
| Entrega real de `TaskbarCreated` (M25) | **`NOT_RUN`.** Exige reinício real do shell. |
| Aparência e clique da notificação de saída fail-safe | **`NOT_RUN`.** A emissão é provada; a entrega pelo Windows e o clique tardio são observação. |

**THEME-1** (persistência da preferência entre processos) continua **fora de âmbito**, por decisão. O que
esta entrega garante é que, dentro do processo, o Dashboard e o flyout resolvem a **mesma** preferência.

### A troca é completa — a prova

Não basta dizer que removi o caminho antigo. `TrayOwnershipCompletenessTests` afirma, contra o
**contentor real** construído por `App.ConfigureApplicationServices`:

1. o assembly declara **um** `ITrayIconAdapter` — `OwnedTrayIconAdapter`;
2. os únicos `ITrayAffordanceSource` são o adaptador e a própria `TrayStateMachine` a que ele reenvia,
   nomeados por identidade — um terceiro seria uma segunda resposta a «o utilizador tem saída?»;
3. **nenhum** tipo `WinUIExTray*` ou `PendingTrayAffordance*` sobrevive no assembly;
4. o contentor resolve `ITrayIconAdapter`, `ITrayAffordanceSource` e o tipo concreto para a **mesma
   instância** — verificado por resolução, não por leitura da forma do registo;
5. existe exatamente **um** descritor para cada um dos três.

M33 confirma que isto não é decorativo: registar uma segunda instância como fonte de afordância faz
falhar (4).


### 3.6 M41b: uma afirmação minha sobre testes de texto, também falsa

Escrevi que uma asserção de texto **positiva** não podia ser satisfeita por prosa, ao contrário da
negativa que partira o T14c antigo. A M41b mostrou que pode: **comentar a chamada** deixa o texto no
ficheiro, dentro do comentário, e a asserção continuava verde.

Corrigido tirando os comentários antes de procurar, com o mesmo `StripComments` que os
`TopmostMutationBoundaryTests` já usavam. A regra correta, sem a parte que inventei: **uma asserção sobre
texto tem de olhar para código, e o que distingue código de prosa é retirar os comentários primeiro.**

### 3.7 Um defeito encontrado por tornar o duplo honesto — QUESTÃO DEVOLVIDA

Ao fazer a `BlockingNativeTrayRegistration` modelar o que o `Shell_NotifyIcon` faz de verdade —
`NIM_DELETE` devolve **FALSE** quando a shell não tem o ícone — dois testes que passavam começaram a
falhar. Não era o duplo a mentir a favor: era o duplo a esconder um comportamento real.

**Cadeia:** `NIM_ADD` falha três vezes → `Lost` → o delete compensatório não tem nada para apagar e
devolve false → três tentativas de limpeza → `Unverified` → a CV-16 escala para a saída autoritativa.

**Consequência:** numa máquina onde o registo da tray falha, a aplicação **sai** em vez de degradar para
sessão em primeiro plano — que é exatamente o que o desenho aprovado diz que deve acontecer nesse caso.

**Não é obviamente errado.** O `Attempt()` marca `MayExist` **antes** da chamada de propósito, e um
resultado falso não é atribuível: `Add() && SetVersion()` também devolve false quando o ícone **ficou**
registado e só a versão falhou — e aí o ícone está mesmo lá e tem mesmo de ser removido. A máquina não
consegue distinguir «não havia nada para apagar» de «o apagar falhou», e escalar é a leitura fail-closed
da CV-16.

**Duas regras aprovadas discordam, e escolher entre elas não é meu.** Fica pinado em
`OPEN_QUESTION_a_registration_that_never_succeeded_escalates_instead_of_degrading`, que afirma o
comportamento de **hoje** para que a decisão seja visível no momento em que mudar. Não é um endosso.


## 5. Ordenação de efeitos, entrega e topologia de threads — ronda de correção

### 5.1 A sequência passou a ser respeitada (ATLAS-R1)

`DrainEffects` desenfileirava **fora** do `_nativeGate` e só depois o adquiria, portanto dois drenadores
podiam tirar A e B e correr para o gate — um DELETE posterior chegava à shell antes do ADD que compensa,
deixando o ícone vivo. O `Effect.Sequence` existia e **nunca era lido**: a pior forma que uma garantia
pode ter, escrita e não imposta.

Agora o gate é tomado à volta de todo o ciclo, o desenfileiramento incluído, e a sequência é **verificada**
— uma inversão lança em vez de produzir silenciosamente um ícone que ninguém pediu. Mutação **M46**,
morta pelo `Concurrent_drainers_never_invert_the_effect_order`.

### 5.2 Uma correção minha que tive de retirar

Tentei também garantir que os efeitos de uma transição nunca chegassem à shell antes da **publicação**
dessa transição: os efeitos ficavam em estágio e só eram comprometidos depois de publicar.

**Não sobreviveu ao primeiro teste.** Comprometer depois de publicar faz a ordem de compromisso divergir
da ordem de decisão entre threads: um Delete decidido em segundo lugar podia ser comprometido primeiro e
correr antes do Add que compensa — exatamente a inversão que estava a corrigir, reintroduzida pela
correção. A verificação de sequência apanhou-o na primeira corrida.

As duas propriedades não podem valer as duas com dois drenadores. Fica a que, ao ser violada, **corrompe
estado** em vez de atrasar uma notificação: um Delete invertido deixa um ícone vivo que ninguém removerá.

**Residual declarado:** com dois drenadores, um drenador já a correr pode executar o efeito de uma
transição antes de essa transição publicar. Depois da correção de topologia (5.3) o despacho e a drenagem
acontecem todos na thread de UI, portanto em produção não há um segundo drenador — mas o residual é
escrito, não presumido.

### 5.3 A topologia de threads passou a ser a que o desenho afirma (CV-8/CV-15-THREAD)

Os retries eram disparados por `TimeProvider.CreateTimer` e a continuação corria **onde o timer dispara**
— o threadpool. O `NIM_ADD` de recuperação corria assim fora da thread de UI, enquanto o desenho, as
linhas CV-7/CV-8 deste mapa e a própria justificação do `EpisodeFrequencyLimiter` afirmam o contrário, e
as medições de custo da CV-8 foram feitas na thread de UI.

Não era uma decisão a tomar: era o documento a afirmar uma coisa e o código a fazer outra — o defeito que
esta fatia tem andado a corrigir. O código passou a marshalar as continuações para a thread de UI.
Mutação **M50**.

### 5.4 Entrega: Release domina e o prazo é respeitado (ATLAS-R2)

A publicação largava o lock de decisão **entre** validar e invocar. Nessa janela um Release podia vencer
e a entrega sair à mesma, e — a que interessa — uma decisão tomada antes do prazo podia ser entregue como
`Available` **depois** dele, que é o invariante de raiz de oito voltas de desenho.

Fechado com três coisas: as entregas são serializadas entre si; cada uma leva um token monotónico, para
que uma que perdeu a corrida seja descartada em vez de aterrar depois de uma mais recente; e o estado é
revalidado **dentro** dessa serialização, contra os estados terminais e contra o prazo **da decisão** —
não contra o campo, que uma recuperação bem-sucedida já limpou.

Mutações **M48** e **M49**. A janela é entrada de propósito através de um probe de teste: uma guarda sobre
uma janela onde nenhum teste consegue entrar é uma guarda que nada falsifica, e esta fatia já enviou duas
dessas.

### 5.5 A atualização de DPI passou a ser serializada (ATLAS-R3)

`UpdateForDpi` troca e destrói um `HICON` e emite `NIM_MODIFY`, e era chamada do adaptador **fora** do
gate, podendo sobrepor-se a um `NIM_ADD` de recuperação: dois chamadores não sincronizados sobre um ícone,
um deles a libertar um handle que o outro pode estar a usar. Passa agora pelo mesmo gate, por delegado —
a capacidade continua onde a CV-20 a pôs. Mutação **M51**.

**Torna a M24 matável?** **Não.** Verifiquei. A M24 é a ordem *dentro* do `NativeTrayRegistration.UpdateForDpi`
— libertar o `HICON` antes do `NIM_MODIFY` em vez de depois — e continua a só ser observável num desktop
real. A serialização remove a sobreposição **entre** chamadores; não torna observável a ordem dentro de
uma delas. A **M25** não tem qualquer relação com o gate. Ambas continuam `NOT_RUN`.

---

## 6. Terceira ronda de correções — o vizinho de cada correção

O Atlas identificou um padrão em três rondas seguidas: **a correção fecha o caso apontado e deixa o
vizinho aberto**. Está certo, e as três correções da ronda anterior falharam todas do mesmo modo.

| Correção anterior | O vizinho que ficou aberto | Fechado agora por |
|---|---|---|
| ordem entre efeitos pelo gate e pela sequência | um drenador **já a correr** executava o efeito de outra transição antes de essa transição publicar | prontidão: os efeitos entram na fila em ordem no momento da decisão e só ficam **executáveis** depois de a sua transição publicar; o drenador **para** no primeiro não pronto, não salta |
| revalidação na entrega | a revalidação estava **antes** da invocação, e a janela entre elas continuava aberta | a verificação e a invocação são **uma só secção crítica**; a seam de teste passou para o ponto da invocação |
| marshalling para a thread de UI | o ramo de **fallback** executava inline na thread do timer | a continuação recusada é **largada** e registada; correr inline anulava a garantia que o caminho principal estabelece |

### 6.1 O que a minha asserção nova NÃO mata — escrito antes de ser perguntado

- **Prontidão (M53/M54):** provo que um drenador que chega durante a publicação não executa nada, e que
  **para** em vez de saltar. Não provo com **duas threads reais**: o drenador do teste é o mesmo fio, e o
  estado da fila que ele observa é o mesmo que outra thread veria. Uma mutação que quebrasse apenas a
  visibilidade entre threads — sem mudar a lógica de prontidão — não seria morta por este teste.
- **Secção crítica (M55):** provo que outra thread não consegue mudar o estado entre a última verificação
  e a invocação, com uma espera limitada de 2 s. É assimétrico — com a correção a outra thread **não pode**
  progredir, portanto nenhuma espera a veria — mas se alguém tornar o lock não exclusivo de uma forma que
  ainda bloqueie durante 2 s, o teste passaria.
- **Topologia (M56/M57):** provo que a máquina larga uma continuação recusada, e que o adaptador **não
  tem** ramo de fallback. O segundo é uma asserção sobre a **fonte**: tomar o ramo falso exige um
  `DispatcherQueue` real a encerrar, que nenhum teste produz.
- **Roteamento de DPI (M58):** provo que o handler chama o router e que o router usa o gate. Que o
  `OnDpiChanged` seja ligado ao evento certo do `TrayHostWindow` continua sem teste — exige uma janela
  real.

### 6.2 Duas armadilhas de ferramenta que me custaram esta ronda, e que ficam escritas

Descobri as duas a investigar uma queda de 709 para 678 testes que eu próprio provoquei:

1. **Nunca embrulhar `dotnet test` num `timeout` externo.** Matar o processo pai deixa o test host órfão,
   o órfão mantém um lock sobre `ServerMonitor.App.Tests.dll`, o build seguinte falha a cópia com
   `MSB3021` — e as corridas seguintes executam **silenciosamente o assembly antigo**. Usar
   `--blame-hang-timeout`.
2. **Uma linha verde `Passed!` não significa que a corrida terminou.** Uma corrida abortada imprime
   `Test host process crashed` **e** uma linha `Passed!` com o que tiver acabado antes. O procedimento de
   gates passa a procurar `Aborted` e erros de build, não só a contagem.

Isto explica também o que o Vigil viu: contagens diferentes entre corridas e falhas intermitentes,
incluindo a prova da CV-19. O teste probabilístico de 200 tentativas foi substituído por um determinístico
(§6), e a suite da fatia correu **10 vezes seguidas** com resultado idêntico e zero abortos.

---

## 7. Questão D — decidida pelo humano e implementada

**Texto autoritativo:** `.boss/tmp/m13-s2t-question-d-decision.md`. O comportamento de produto aprovado
prevalece: **uma falha de registo inicial DEGRADA, não termina a aplicação.**

### 7.1 A causa era o resultado colapsado

`Add() && SetVersion()` num único booleano destruía a distinção de que o ciclo de vida depende:
`NIM_ADD FALSE` significa que a shell nunca ficou com o ícone; `NIM_ADD TRUE` com `NIM_SETVERSION FALSE`
significa que o ícone pode existir e **tem** de ser removido. Ambos chegavam como `false`.

A fronteira passa a devolver um **`ShellOutcome`** tipado:

| valor | significado |
|---|---|
| `NotPerformed` | a operação não foi executada |
| `Succeeded` | todas as chamadas nativas reportaram sucesso |
| `FailedWithoutEffect` | falhou **sem deixar nada**: o `NIM_ADD` foi recusado |
| `FailedWithPossibleEffect` | falhou **depois** de uma chamada que pode ter criado o ícone |

O `TrayEvent` transporta o outcome, e `MayHaveCreatedAnEffect` é a pergunta que o ciclo de vida faz.

### 7.2 A disposição da limpeza

`CleanupDisposition`: **`NotRequired`** · **`Verified`** · **`Unverified`**. O nome é meu, como o humano
deixou explícito; a semântica é a da decisão.

| caso | classificação | desfecho |
|---|---|---|
| **1** — todos os `NIM_ADD` falsos e nada em voo | `NotRequired` · `ShellEffectState.NeverCreated` | sessão degradada; **nenhum `Delete` é sequer emitido** |
| **2** — `NIM_ADD TRUE` + `NIM_SETVERSION FALSE` | limpeza **REQUIRED** | `Delete` confirmado → `Verified` → degradada continua · `Delete` não confirmável → `Unverified` → CV-16 → saída fail-safe |
| **3** — `Add` em voo ou ambíguo | **REQUIRED** até reconciliar | fail-closed: `_shellMayHoldAnIcon` e `_reconciliationPending` impedem a desclassificação |

### 7.3 A CV-16 é refinada, não relaxada

`Unverified` só é alcançável a partir de `MayExist` — isto é, só quando a remoção **era** necessária — e
continua a não autorizar nada: vai à saída autoritativa. **`NotRequired` não é uma falha de limpeza e não
é mapeada para uma.** A **M63** (mapear `NotRequired` para `Unverified`) e a **M64** (deixar um `Delete`
acidental degradar a disposição) existem exatamente para que o Vigil possa verificar que o `NotRequired`
não se tornou um bypass — as duas morrem.

### 7.4 Testes e mutações exigidos

Os seis cenários da §5 da decisão, todos determinísticos e contra componentes de produção:
`QD1` (×2, incluindo *nenhum `Delete` emitido*), `QD2`, `QD3`, `QD4`, `QD5`, `QD6`.

As quatro mutações obrigatórias — colapsar o resultado (**M59**), classificar a falha inicial como
`Unverified` (**M60**), classificar sucesso-de-`Add`/falha-de-`SetVersion` como `NotRequired` (**M61**),
remover a compensação do `Add` tardio (**M62**) — mais duas minhas (**M63**, **M64**). Todas mortas.

### 7.5 O vizinho, e a mutação que não mato

- **Vizinho verificado:** três testes que provavam a escalada usavam `AddResult = false` — ou seja,
  provavam-na sobre o caso que agora **degrada**. Passaram a construir o CASO 2, que é uma limpeza
  genuinamente necessária. Sem isso teriam continuado verdes a provar a coisa errada.
- **O que não mato:** que o `NativeTrayRegistration` real devolva os `BOOL` certos por operação. A
  distinção é provada sobre o duplo; a fronteira nativa continua só observável num desktop, como as M24 e
  M25.

---

## 4. Retratação — uma afirmação minha que era falsa

Na entrega da ligação escrevi, sobre `App.xaml.cs`:

> «o diff é grande e a mudança é NULA — as linhas são as mesmas, re-indentadas.»

**É falso, e retiro-o.** O Cortex mediu: `git diff` 318/292, `git diff -w` 255/229, e o multiconjunto de
linhas sem espaços passou de 522 para 547. Há mudanças reais nesse ficheiro:

* o lambda do DI passou a `ConfigureApplicationServices(IServiceCollection)`;
* saíram `PendingTrayAffordanceSource` e `WinUIExTrayIconAdapter`;
* entrou `OwnedTrayIconAdapter`, registado uma vez e exposto nos dois papéis;
* (nesta ronda) entrou `EvaluateStartupAffordance` e a sua chamada no `OnLaunched`.

Cada uma dessas mudanças estava declarada noutro sítio do relatório, e nenhuma foi escondida. O problema
é a frase em si: **convida a não ler o diff**. Uma alegação falsa num relatório é do mesmo tecido que uma
mutação que não falsifica — descreve uma verificação que ninguém fez. É por isso que a CV-15 existe, e
por isso a retratação fica aqui e não só numa mensagem.

A afirmação correta: **o diff de `App.xaml.cs` é grande e contém mudanças reais e declaradas. Leiam-no.**

---

## 3. Matriz de mutação — CV-12

Uma mutação de cada vez, sempre contra o **código de produção**, com restauro e reconfirmação da baseline
entre cada uma. Filtro: `FullyQualifiedName~Tray` (M1–M25) e
`FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout` (M26–M35).
e `FullyQualifiedName~FailSafe|FullyQualifiedName~WindowsAppNotification|FullyQualifiedName~Notification`
(M36–M40). Baselines **95** e **82**, ambas 0 falhas.

**65 entradas · 63 corridas · 61 mortas · 2 sobrevivem.**

Duas não correm, com razão escrita: **M4** ficou `SUPERSEDED BY M55` e **M21** `SUPERSEDED BY M34`,
porque o código que atacavam foi reescrito e a propriedade passou para a mutação nova. A **M47** foi
retirada por mim (§5.2), e a **M46**, **M48** e **M50** foram retiradas quando a ronda 3 reformulou o
mesmo código — as suas propriedades são agora atacadas pela **M53**, **M55** e **M56**.

**Todas as outras âncoras foram reparadas em vez de deixadas mortas.** A Questão D reescreveu o executor e
a reconciliação, o que partiu a M3, a M5, a M6 e a M43; foram re-apontadas ao código como está e voltam a
matar. Uma matriz com âncoras mortas mente da mesma maneira que um mapa que cita ficheiros inexistentes.

As duas sobreviventes — **M24** e **M25** — continuam limitações declaradas, não alegações. A **M13**
deixou de ser uma delas: está morta (§3.1).

### Reprodução

**Os scripts estão na árvore**, em `tools/mutations/`, com um README. Antes estavam num diretório de
scratch e este mapa apontava para ficheiros que ninguém conseguia abrir — que é a CV-15 ao contrário: um
documento que invoca evidência inalcançável não é evidência.

```
# Núcleo da máquina e contrato do callback (M1–M18)
# a partir da raiz do repositório
python tools/mutations/mutate.py M1

# CV-20 e fronteira nativa (M19–M25)
python tools/mutations/mutate_t14.py M19

# Ligação em DI, flyout, CV-9 e tema (M26–M35)
python tools/mutations/mutate_wiring.py M26

# Notificação de saída fail-safe, CV-17/CV-18 (M36–M40)
python tools/mutations/mutate_notice.py M36

# Ronda de correções: arranque, ordem de publicação, ordinais (M41–M45)
python tools/mutations/mutate_round9.py M41

# Ronda Atlas/Vigil: ordem, entrega, topologia, DPI, cobertura (M46–M52)
python tools/mutations/mutate_round10.py M46

# Terceira ronda: prontidão, secção crítica da entrega, fallback, roteamento (M53–M58)
python tools/mutations/mutate_round11.py M53

# Questão D: resultado nativo tipado e disposição da limpeza (M59–M64)
python tools/mutations/mutate_questiond.py M59

# Prova diferencial da escalada CS8509
python tools/mutations/cs8509_differential.py

# Cada corrida aplica a mutação, executa
#   ~/.dotnet/dotnet.exe test tests/ServerMonitor.App.Tests/ServerMonitor.App.Tests.csproj \
#     -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Tray"
# e restaura o ficheiro.
```

### Matriz

| # | Mutação | Invariante violado | Falhas | Estado |
|---|---|---|---|---|
| M1 | `Transition` pode emitir `Add` durante `Releasing` | dominância do Release | 3 | **morta** |
| M2 | um `Add` tardio anterior ao Release publica `Available` | `Available` = provadamente disponível | 1 | **morta** |
| M3 | um `Add` tardio não recebe `Delete` compensatório | conclusão compensada | 1 | **morta** |
| M4 | revalidação na entrega das notificações removida | Release domina as continuações | — | `SUPERSEDED BY` **M48** (âncora reescrita) |
| M5 | um `Shell_NotifyIcon` falso é tratado como sucesso | a razão de ser da slice | 7 | **morta** |
| M6 | um `Shell_NotifyIcon` verdadeiro é tratado como falha | idem, direção oposta | 6 | **morta** |
| M7 | recuperação por `TaskbarCreated` removida | recuperação após reinício do Explorer | 1 | **morta** |
| M8 | um sucesso repõe o histórico de frequência | independência de A e B | 1 | **morta** |
| M9 | `Available` mantido após um `TaskbarCreated` admitido | `Recovering` em vez de mentir | 3 | **morta** |
| M10 | uma limpeza não verificável pode continuar a viver | CV-16 fail-closed | 1 | **morta** |
| M11 | acrescenta-se um braço `default` ao switch de efeitos | exaustividade do switch | 1 | **morta** |
| M12 | o RunOnce do fail-safe marca à entrada em vez de após retorno normal | uma exceção não consome o único disparo | 1 | **morta** |
| M13 | a ressalva CV-19 é removida | reconciliação de conclusões obsoletas | 1 | **morta** — ver 3.1 |
| M14 | o passo de prazo do preâmbulo é removido | terminalização pelo prazo | 1 | **morta** |
| M15 | a verificação da identidade da mensagem é removida | CV-6b caso B | 1 | **morta** |
| M16 | a verificação do `uID` é removida | CV-6b caso C | 1 | **morta** |
| M17 | a lista fechada de eventos v4 é aberta | CV-1 ponto 3 | 2 | **morta** |
| M18 | a sanitização da âncora é removida | CV-1 ponto 5 | 1 | **morta** |
| M19 | o executor deixa de ser `private`-aninhado | CV-20, fecho do canal | 1 | **morta** |
| M20 | a máquina retém a capacidade num campo próprio | CV-20, detentor único | 2 | **morta** |
| M21 | a capacidade é registada no composition root | CV-20, fora do contentor | — | `SUPERSEDED BY` **M34** (a âncora era a antiga forma em lambda) |
| M22 | um closure captura a capacidade num campo **gerado pelo compilador** | CV-20 sem exclusão por categoria | 2 | **morta** |
| M23 | o tooltip deixa de ser ajustado ao buffer `szTip` | CV-5 | 1 | **morta** |
| M24 | o `HICON` antigo é libertado **antes** de `NIM_MODIFY` | regra DPI do Prism | **0** | **SOBREVIVE — 3.3** |
| M25 | a janela hospedeira passa a `HWND_MESSAGE` | entrega de `TaskbarCreated` | **0** | **SOBREVIVE — 3.3** |
| M46 | o desenfileiramento volta para fora do gate | ordem sob drenagem concorrente | 1 | **morta** |
| M47 | — | — | — | **RETIRADA por mim** (§5.2) |
| M48 | `Release` deixa de dominar na entrega | dominância do Release | 2 | **morta** |
| M49 | uma decisão pré-prazo pode ser entregue como `Available` | invariante de raiz | 1 | **morta** |
| M50 | os retries correm onde o timer dispara | CV-7/CV-8, topologia | 2 | **morta** |
| M51 | a atualização de DPI vai direta à shell | serialização das chamadas nativas | 1 | **morta** |
| M52 | um `EffectKind` novo passa despercebido ao teste de cobertura | CV-12/T17 | 1 | **morta** |
| M26 | a porta CV-9 admite todos os pedidos | um só flyout de cada vez | 4 | **morta** |
| M27 | `Close` deixa de ser idempotente e dá um slot extra | CV-9 sob dupla notificação de fecho | 1 | **morta** |
| M28 | a ordem do menu põe `Sair` em primeiro | ordem fixada pelo produto | 4 | **morta** |
| M29 | um item desaparece do menu | idem | 3 | **morta** |
| M30 | um item resolve a chave de recurso errada | item vazio = app que não se fecha | 2 | **morta** |
| M31 | anexar um root de tema **substitui** o anterior | HIGH do Prism | 4 | **morta** |
| M32 | o tema é aplicado só ao root mais recente | idem | 1 | **morta** |
| M33 | a fonte de afordância é uma **segunda instância** | um só dono do ícone | 1 | **morta** |
| M34 | a capacidade é registada no contentor | CV-20 / T14c sobre descritores reais | 2 | **morta** |
| M35 | o adaptador reporta `Available` antes de haver registo | fail-closed antes do `Start()` | 1 | **morta** |
| M36 | a notificação é emitida quando o CAS foi **PERDIDO** | condição do Prism | 2 | **morta** |
| M37 | a guarda do controlador desaparece | a falha não pode impedir o Exit | 1 | **morta** |
| M37b | a guarda da própria notificação desaparece | idem, outra camada | 1 | **morta** |
| M38 | o vocabulário aceita qualquer ação sob `FailSafeExit` | CV-18, par literal | 2 | **morta** |
| M38b | o vocabulário aceita `OpenDashboard` sob qualquer kind | CV-18 / CI-1b | 3 | **morta** |
| M39 | a expiração passa a longa (30 dias) | CV-17, curta duração | 2 | **morta** |
| M39b | a expiração é removida | idem | 1 | **morta** |
| M40 | a fronteira deixa de ser fire-and-forget (devolve `Task`) | a saída não espera pela entrega | 5 | **morta** |
| M41 | a sessão degradada nunca é avaliada no arranque | o bloqueante do Prism | 2 | **morta** |
| M41b | a chamada é **comentada** no `OnLaunched` | idem, e ver 3.6 | 1 | **morta** |
| M41c | o arranque resolve a política mas nunca a avalia | idem | 1 | **morta** |
| M42 | a perda só chega ao lifecycle **depois** da I/O da shell | achado do Cortex | 2 | **morta** |
| M43 | um delete redundante é tratado como falha | a reordenação não pode fabricar escalada | 1 | **morta** |
| M44 | a regra do delete redundante engole uma falha **genuína** | CV-16 continua fail-closed | 4 | **morta** |
| M45 | os ordinais da afordância são deslocados | valores fixados, não só a ordem | 2 | **morta** |

### 3.1 M13: a ressalva CV-19 está agora **PROVADA**

Durante duas entregas a M13 sobreviveu, e eu argumentei que o estado protegido era inalcançável: o passo
1 do preâmbulo faz curto-circuito antes do passo 2, e todos os incrementos de geração exceto o de
`BeginEpisode` entram também num estado terminal.

**O argumento estava certo e era insuficiente.** A exigência era explícita: o passo 2 mantém-se **com
mutação própria**, e uma mutação que não mata não cumpre obrigação de prova nenhuma. A razão é a doutrina
desta fatia inteira: *uma guarda cuja remoção não muda nada pode ser apagada por um refactor sem que nada
falhe.*

O estado passou a ser **construído**. `InjectForTests` injeta um evento com uma geração escolhida, pela
mesma `Dispatch` que todos os eventos reais usam, e o
`CV19_a_stale_add_completion_in_a_live_episode_is_reconciled_and_compensated` põe um `AddCompleted` da
geração **anterior** num episódio vivo e não terminal, e afirma que a compensação sai. **M13 morta.**

### 3.2 Prova diferencial da escalada `CS8509`

A mesma mutação — apagar o braço `EffectKind.ScheduleDeadline` do switch exaustivo — compilada duas
vezes, mudando **apenas** a escalada no `.csproj`:

| Compilação | Escalada | `Build succeeded` | `error CS8509` | `warning CS8509` |
|---|---|---|---|---|
| **C1** | aplicada (a árvore tal como é entregue) | **não** | **2** | 0 |
| **C2** | removida | sim | 0 | 4 |
| baseline | aplicada, sem mutação | sim | 0 | 0 |

**`CS8524` não é escalado, deliberadamente.** Dispara na árvore como a análise previa (2 avisos, visíveis
em qualquer build). Escalá-lo obrigaria ao braço `default` que o desenho proíbe — que é exatamente a
mutação M11. A escolha está registada em comentário no `.csproj`, não apenas aqui.

### 3.3 M24 e M25 sobrevivem: limitações de cobertura, não invariantes por defender

Ambas são regras corretas cuja violação **só um ambiente gráfico real revela**:

- **M24** (libertar o `HICON` antes de `NIM_MODIFY`) entrega ao Explorer um handle morto. O sintoma é um
  ícone corrompido ou em falta, visível a olho.
- **M25** (`HWND_MESSAGE`) faz o `TaskbarCreated` deixar de chegar, porque o shell só transmite para
  janelas de topo. O sintoma é a tray não voltar depois de reiniciar o Explorer.

Nenhuma é observável num teste sem desktop. Ficam ligadas aos casos S6 de QA humana, e não são
apresentadas como provadas. Os passos exatos para as verificar estão no relatório, secção P, casos 6 e 4.

### 3.4 M37 e M37b: uma propriedade defendida duas vezes tem de ser provada duas vezes

Na primeira passagem **ambas sobreviveram**, e a razão é instrutiva: «a falha da notificação nunca impede
o Exit» está defendida em duas camadas — a própria notificação engole a exceção, e o `RunStep` do
controlador engole o que passar. Removendo **uma** das duas, a outra ainda apanhava, e o teste
ponta-a-ponta continuava verde.

Defesa em profundidade é o que torna a mutação de uma camada invisível. Cada camada passou a ter o seu
teste direto: `The_notice_itself_never_lets_a_platform_failure_escape` (a notificação, isolada) e
`The_exit_path_survives_a_committed_hook_that_throws` (o controlador, com um gancho cru que rebenta, para
que a guarda da notificação não possa substituir a dele). Ambas mortas com 1 falha cada.

### 3.5 Secção 1, atualizada pela ligação

Acrescenta-se ao que estava provado:

### 3.5 continuação

11. **A notificação de saída fail-safe.** Emitida do ramo que **venceu** o CAS existente para `Exiting`,
    e só para `ExitReason.TrayCleanupUnverified`. Uma linha no `switch` de pares literais, ação
    `OpenDashboard` que já estava na allowlist, zero parâmetros, 30 minutos, fire-and-forget com guarda
    em cada camada.

7. **Um dono do ícone.** `OwnedTrayIconAdapter` substitui o `WinUIExTrayIconAdapter`, que foi **apagado**
   junto com o `PendingTrayAffordanceSource` — não guardado como recurso. Provado contra o contentor real
   (secção 2).
8. **O composition root é invocável.** Era um lambda, e por isso a CV-20 só podia ser verificada por
   leitura de texto. A primeira versão do T14c falhou por causa de um **comentário de documentação** que
   nomeava a capacidade: o melhor argumento possível contra a técnica. Agora inspeciona os
   `ServiceDescriptor` que o root produz mesmo.
9. **CV-9 é um tipo, não um `bool`.** Uma flag dentro de uma chamada para o XAML só se exercita com
   desktop; `FlyoutReentrancyGate` prova-se com seis testes e morre a duas mutações.
10. **O tema chega a todos os roots.** O serviço guardava um `FrameworkElement`; anexar o flyout teria
    feito o Dashboard deixar de seguir a preferência em silêncio. `ThemeRootSet` leva a contabilidade
    sobre objetos simples, e por isso a regressão é uma asserção em vez de estado de UI não testável.
