# M13 S2-T — ROOT STATE MACHINE REDESIGN

> ## ⚠️ HISTÓRICO — SUBSTITUÍDO
> O modelo de **actor FIFO** deste ficheiro foi **rejeitado**: separava a admissão do evento da
> transição de estado, e foi assim que o intervalo de `Available` falso voltou. O desenho vigente é
> **`docs/m13-s2t-linearizable-state-machine.md`**.
>
> **Nada aqui revoga uma condição CV** — a autoridade sobre o estado das condições é o **mapa CV** do
> documento vigente, e o ficheiro de condições do Vigil (CV-15).


**Autor:** Relay (platform-infra), implementer da S2-T / dono do Windows Shell.
**Branch:** `agent/m13-s2t-tray`, base `221eda4`. **DESENHO. Sem implementação.**
**Substitui** `docs/m13-s2t-architecture-review.md` como desenho normativo. Esse fica como histórico
das revisões 1–7; **nada nele revoga uma condição CV** — ver o mapa da secção 9.

---

## 0 — O diagnóstico, e o que muda

Três voltas seguidas em que corrigir um mecanismo desregulou outro, porque **três autoridades
decidiam o mesmo estado**: o caminho de sucesso, o temporizador do deadline, e o `Release`, árbitrados
por CAS, com um gate que às vezes também decidia. O CAS não estava errado — **a pluralidade de
autoridades é que estava**.

**A reestruturação:** uma **única autoridade de ciclo de vida**, num domínio de serialização próprio;
o **gate serializa apenas I/O nativo**; a **compensação é consequência** da máquina, não uma
autoridade. Menos autoridades, não necessariamente menos primitivas.

## 1 — A autoridade única

**`TrayLifecycle`** é um **actor de consumidor único**: uma fila de mensagens processada
sequencialmente por **uma** thread dedicada (`IsBackground = true`, para nunca manter o processo vivo).

> **Todo o estado de ciclo de vida é privado e só é escrito nesse laço.** Não há CAS, não há campo
> partilhado escrito por dois caminhos, não há decisão tomada fora do laço. A unicidade é
> **estrutural**: não existe segunda escrita para auditar.

**Porque não a thread de UI.** A chamada nativa corre na thread de UI (CV-7/CV-8) e **pode bloquear**.
Se o ciclo de vida vivesse lá, uma chamada nativa bloqueada voltaria a bloquear a decisão do deadline —
exatamente o defeito da revisão 6, apenas com uma fila em vez de um lock. **O laço de ciclo de vida
nunca executa I/O nativo**, logo nada do shell o pode bloquear.

### Entradas — tudo é mensagem para o laço

| Mensagem | Origem | Nota |
|---|---|---|
| `Establish` | arranque da S2 | episódio inicial |
| `Admit(now)` | `WndProc`, thread de UI | **só é enviada se o limitador B admitir** (secção 5) |
| `DebounceElapsed(gen)` | temporizador | dentro do episódio |
| `AttemptCompleted(gen, ok)` | worker nativo | resultado, nunca decisão |
| `DeadlineElapsed(gen)` | temporizador one-shot | **posta; não decide** |
| `CleanupCompleted(gen, verified)` | worker nativo | resultado, nunca decisão |
| `Release` | S2 (`ReleaseAsync`) | única operação terminal pública |

**Saídas do laço:** pedidos de I/O ao worker nativo, `State` autoritativo (campo volátil escrito só
pelo laço), e `StateChanged` despachado para a UI.

> **`State` autoritativo vs notificação.** O `State` muda no instante em que o laço processa a
> mensagem. A notificação é despachada para a thread de UI porque a S2 vive lá. **Com a UI encravada a
> notificação chega tarde; o `State` não.** Isto não é, e não é apresentado como, garantia de latência
> de notificação.

## 2 — A máquina

**Estados:** `Unavailable` · `Available` · `Recovering` · `Lost` · `Releasing` · `Released`.

```
              Establish / Admit(admitido)
Unavailable ─────────────────────────────►  Recovering
     ▲                                        │
     │  cleanup verificada                    │ AttemptCompleted(ok)  [gen atual, dentro do prazo]
     │                                        ▼
     │                                    Available
     │                                        │ Admit(admitido)  ⇒ prova invalidada AQUI
     │                                        ▼
     └──── Lost ◄── AttemptCompleted(!ok) & A esgotado ──── Recovering
             │      DeadlineElapsed(gen atual) ────────────┘
             │
             ├── CleanupCompleted(verified=true)  ⇒ permanece Lost  ⇒ S2 degrada (UX aprovada)
             └── CleanupCompleted(verified=false) ⇒ Releasing        (§4, fail-safe)

qualquer estado ── Release ──► Releasing ── limpeza terminada ──► Released
```

### 2.1 `Release` é absorvente — e a absorção é estrutural

Formalmente: a função de transição δ é total e, **para toda a entrada `x`**,
**δ(`Releasing`, `x`) = `Releasing`** e **δ(`Released`, `x`) = `Released`**, com a única exceção da
transição interna `Releasing → Released` quando a limpeza terminal acaba.

**Não é uma regra que cada handler tem de lembrar-se de respeitar.** O laço aplica uma guarda única
**antes** de despachar:

```csharp
// Ponto único de entrada do laço. Nenhum handler é alcançável em estado terminal.
if (_state is Releasing or Released) { HandleTerminalOnly(message); continue; }
Dispatch(message);
```

`HandleTerminalOnly` aceita **apenas** `CleanupCompleted` (para chegar a `Released`). Logo, uma vez em
`Releasing`: **sem** `Available` · **nenhum** `Lost` o supera · **não** começa recuperação · **nenhum**
retry continua · **sem** `NIM_ADD` · **sem** abertura de flyout · **nenhum** callback muta o ciclo de
vida · **nenhuma** continuação obsoleta ressuscita estado de tray.

### 2.2 O gate não é autoridade de ciclo de vida

`_nativeGate` serializa **exclusivamente** I/O nativo do shell, no worker. **Não decide** `Available`,
`Lost`, `Releasing`, desfecho de limpeza, nem terminalidade de episódio. Não é consultado pelo laço e
o laço nunca o adquire.

### 2.3 A compensação é consequência

Não existe autoridade de limpeza. Ao **entrar** em `Lost` (por qualquer causa) ou em `Releasing`, o
laço **emite um pedido** de limpeza ao worker e **espera a mensagem** `CleanupCompleted`. A decisão do
que fazer com o resultado é do laço, no laço.

### 2.4 Geração

Cada episódio tem uma geração atribuída pelo laço. Mensagens com geração diferente da atual são
**descartadas na entrada** — é o mesmo ponto único da guarda terminal. Uma continuação obsoleta não
tem caminho para o estado.

## 3 — Episódio de recuperação

```
TaskbarCreated na WndProc (thread de UI)
  ├─ pré-condição observada: State == Available
  ├─ EpisodeFrequencyLimiter.TryBeginEpisode(now)
  │     false ⇒ SUPRIMIDO: não se envia Admit. Sem episódio, sem deadline, SEM Lost.
  └─ true  ⇒ enviar Admit(now) ao laço
```

Ao processar `Admit`, o laço, **numa só passagem**: invalida imediatamente a prova anterior
(`Available → Recovering`), captura **uma** geração, **um** tempo de início monotónico, e
`deadline = início + 1500 ms`, e arma o temporizador one-shot. O **debounce ocorre DENTRO do
episódio** e apenas coalesce mensagens adicionais: não move o relógio, não cria geração, não preserva
`Available`.

> **Sem intervalo de `Available` falso.** A supressão por B acontece **antes** de existir episódio, e a
> admissão e a invalidação são a mesma passagem do laço. R1 das revisões anteriores mantém-se, agora
> sem depender de o bloco ser síncrono na `WndProc`: depende de o laço ser de consumidor único.

**`Lost` tem duas causas legítimas:** esgotamento do orçamento A com falha nativa observada, e
expiração do deadline. **Sucesso nativo tardio** — `AttemptCompleted(ok)` de uma geração já terminal ou
após o prazo — é **descartado na guarda de entrada** e **não pode publicar `Available`**.

## 4 — Falha de limpeza: escalada terminal fail-safe *(Opção A)*

> **`CleanupVerified = false` NÃO é condição de regime aceitável.**

```
Lost + CleanupVerified = true
  → permanece Lost → notificar a S2 → sessão FOREGROUND degradada aprovada

Lost + CleanupVerified = false após tentativas limitadas
  → o processo NÃO PODE continuar
  → transição ATÓMICA para Releasing (uma passagem do laço)
  → invocar o caminho autoritativo de saída verdadeira da S2 (RequestExit)
  → shutdown gracioso primeiro
  → o watchdog de 10 s de tempo de vida do processo já está armado
  → TerminateProcess apenas como escalada terminal do watchdog, se o gracioso falhar
```

**Não há segundo mecanismo de kill específico do tray.** Reutiliza-se o caminho de saída verdadeira que
a S2 já construiu; a S2-T apenas o invoca.

**Invariante central:** uma vez em `Lost`, **nenhum processo ServerAlyzer vivo pode reter
indefinidamente uma afordância de tray cuja remoção não possa ser positivamente estabelecida**. Se a
limpeza não puder ser estabelecida, **a terminação do processo é o fallback de segurança**.

**Política de limpeza, limitada:** até **3** `NIM_DELETE` consecutivos no worker, sem esperas
programadas (custo medido: mediana 0,36 ms, máx 0,74 ms ⇒ pior caso ≈2 ms). O efeito é registado como
`NotIssued` / `MayExist` / `Deleted` / `Unverified`, com `MayExist` marcado **antes** de emitir o
`NIM_ADD` — direção de erro deliberada: marcar depois deixaria uma interrupção convencer-nos de que não
há efeito quando há; marcar antes custa, no pior caso, um `NIM_DELETE` redundante.

> **`CleanupVerified=false` nunca autoriza continuação em background ou degradada.** Só conduz a
> shutdown terminal fail-safe. Se algum desenho futuro tentar usá-lo como permissão para continuar,
> **reabrir a review do Vigil** (CV-16).

## 5 — Independência A / B, por tipo

**A — orçamento de retry dentro de um episódio admitido.** 3 tentativas, atrasos 250 ms e 1000 ms
(~1250 ms programados) sob o deadline de 1500 ms. Um sucesso pode repô-lo para um episódio futuro.

**B — limitador de admissão.** Janela deslizante monotónica, **5 admissões / 60 s**, contadas
independentemente do desfecho.

```csharp
internal sealed class EpisodeFrequencyLimiter
{
    public bool TryBeginEpisode(long monotonicTimestamp);   // único método público
}
```

**Nos dois sentidos, por tipo e não por convenção:**
- **B não pode ser reposto por sucesso**, porque não existe `Reset()`, `OnSuccess()`, nem qualquer via
  para lhe comunicar um desfecho. B não limita retries de A, não decide `Lost`.
- **A não cria episódios** — `TryBeginEpisode` é chamado num único sítio, na `WndProc`, antes de existir
  episódio — **e não muta o histórico de B**, porque não tem referência ao limitador.

**Supressão por B não produz `Lost`**: sem episódio, não há causa terminal. Tráfego adversarial de
`TaskbarCreated` com **sucesso-sempre** bate na supressão de B, porque B conta admissões e não
desfechos.

## 6 — Encaminhamento de callbacks e default-deny *(CV-1 · reposto)*

**Reposto integralmente, e reforçado.** O contrato dos sete pontos da CV-1 mantém-se: default-deny ·
identidade da mensagem (o id é seletor, nunca prova de origem) · `lParam` low word na lista fechada
`NIN_SELECT`/`NIN_KEYSELECT`/`WM_CONTEXTMENU`/`WM_LBUTTONDBLCLK` · high word `uID == 1` · `wParam` é
coordenada não confiável, só âncora, saneada, **fora de todos os monitores ⇒ DESCARTAR** · nenhuma
mensagem transporta ou desreferencia ponteiro · teto de impacto de uma forja = incómodo de UI, sem
chegar ao `RequestExit` sem clique real.

> **Guarda de estado, e a razão pela qual passou a ser MAIS necessária.** Callbacks do tray são
> descartados por default-deny em **`Lost`**, em **`Releasing`/`Released`**, e **enquanto o efeito
> estiver por confirmar** (`MayExist`/`Unverified`). O estado `Unverified` torna o **ícone vivo mas
> repudiado** uma possibilidade explícita do desenho — é exatamente aí que um callback não pode ser
> aceite, porque o ícone que o originou já não é reconhecido como nosso.

**CV-6b — quatro casos independentes, reposta a tabela.** Restantes campos válidos em cada caso;
**B e C obrigatórios**; a mutação de **cada** validação isolada tem de falhar. **Sem prova só
conjuntiva.**

| | callback | `uID` | resultado |
|---|---|---|---|
| A | válido | válido | **aceite** |
| **B** | inválido | válido | **ignorado** |
| **C** | válido | inválido | **ignorado** |
| D | inválido | inválido | ignorado |

## 7 — Contrato público para a S2

```csharp
public enum TrayAffordanceState
{
    Unavailable = 0, Available = 1, Recovering = 2, Lost = 3, Releasing = 4, Released = 5
}

public interface ITrayAffordance
{
    TrayAffordanceState State { get; }                                 // autoritativo
    event EventHandler<TrayAffordanceChangedEventArgs> StateChanged;   // notificação, na UI
    Task<TrayAffordanceState> EstablishAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);            // ÚNICA operação terminal
}
```

`Available` ⇒ `BACKGROUND` legítimo · `Recovering` ⇒ segurar, não degradar, não tratar como disponível
· **`Lost` (sempre com limpeza verificada, por construção da §4)** ⇒ degradação obrigatória para a UX
aprovada · `Unavailable` no arranque `--background` ⇒ degradação obrigatória · `Releasing`/`Released`
⇒ a S2 não inicia nada e deixa a saída verdadeira concluir.

> **A S2 nunca observa `Lost` com limpeza não verificada.** Esse caso não é notificado como `Lost`
> degradável: converte-se em `Releasing` dentro do laço. Isto é o que impede a UX contraditória que o
> Prism assinalou — não há sessão degradada com um ícone vivo ao lado do relógio.

Menu inalterado: Abrir o ServerAlyzer · Modo compacto · Atualizar todos · Definições · Sair do
ServerAlyzer.

## 8 — As cinco perguntas do Atlas

| | Pergunta | Resposta | Porquê, estruturalmente |
|---|---|---|---|
| 1 | `Release` é absorvente? | **SIM** | δ(`Releasing`,x)=`Releasing` ∀x, imposto por **uma** guarda no ponto único de entrada do laço, não por disciplina em cada handler |
| 2 | Existe exatamente uma autoridade de ciclo de vida? | **SIM** | todo o estado é privado do actor e só escrito no seu laço de consumidor único; sem CAS, sem segundo escritor |
| 3 | Alguma continuação pode publicar estado depois de `Releasing`? | **NÃO** | continuações não publicam: **postam** mensagens, e a guarda de entrada descarta por estado terminal e por geração |
| 4 | `Lost` + falha de limpeza pode deixar processo vivo? | **NÃO** | `CleanupCompleted(false)` transita para `Releasing` e invoca o `RequestExit` autoritativo; o watchdog de 10 s da S2 escala se o gracioso falhar |
| 5 | A e B são independentes nos dois sentidos? | **SIM** | tipos separados, sem referência mútua; B não tem API para receber desfechos, A não tem referência ao limitador |

## 9 — Mapa de condições CV *(CV-15)*

**Regra de processo, agora fixada:** o documento de arquitetura é o desenho normativo; o ficheiro de
condições do Vigil é normativo para segurança. **Toda condição ativa é incorporada ou explicitamente
referenciada aqui. Remover redação durante condensação NÃO revoga uma condição.** Uma condição só
desaparece se marcada `SUPERSEDED BY <regra>` com justificação.

| CV | Assunto | Secção | Estado |
|---|---|---|---|
| CV-1 | modelo de confiança da `WndProc`, sete pontos | **§6** | **ATIVA · reposta integralmente** |
| CV-2 | `TaskbarCreated` coalescido e limitado | §3, §5 | ATIVA · fechada |
| CV-2b | dois orçamentos independentes | **§5** | ATIVA · fechada, agora por tipo |
| CV-3 | comportamento sob `TerminateProcess` | §10 | ATIVA · fechada |
| CV-4 | `Unavailable` no ordinal 0 · produtor único de `Available` | §7, §2 | ATIVA · fechada |
| CV-5 | `szTip`/`hIcon` estáticos | §7 | ATIVA · fechada |
| CV-6 | mensagem forjada ignorada | §6 | `SUPERSEDED BY` **CV-6b** — conjunção substituída por quatro casos |
| CV-6b | quatro casos independentes | **§6** | **ATIVA · tabela reposta** |
| CV-7 | gate de topologia de thread | §11 | ATIVA · **MEDIDA, PASSA** |
| CV-8 | custo nativo síncrono na thread de UI | §11 | ATIVA · **MEDIDA, aceitável** sob o envelope de B |
| CV-9 | reentrância com flyout aberto | §6 | ATIVA · fechada |
| CV-10 | acoplamento limitador ↔ custo de UI | **§11** | ATIVA · fechada, com números e lista de reabertura |
| CV-11 | residual de admissão suprimida | **§11** | ATIVA · **LOW aceite, redação corrigida** |
| CV-12 | evidência de mutação na entrega | §12 | **ABERTA** — fecha na entrega |
| CV-13 | *(fechada pelo Vigil na revisão 6)* | — | ATIVA · fechada |
| CV-14 | *(fechada pelo Vigil na revisão 6)* | — | ATIVA · fechada |
| CV-15 | integridade do documento normativo | **§9** | **ATIVA · este mapa é o cumprimento** |
| CV-16 | `CleanupVerified` fail-closed | **§4** | **ATIVA** — usar `false` como permissão para continuar reabre a review |

## 10 — `FORCED-TERMINATION TRAY CLEANUP` (S6) · CV-3

**`EMPIRICAL QA ACCEPTANCE WINDOW` = 120 s, fixada a priori.** `TerminateProcess` → 120 s sem interação
→ **uma** passagem do rato sobre a área de notificação → registar. Órfão transitório aceitável; **órfão
persistente = FAIL**; obsoleto não interativo; lançamento seguinte cria **exatamente um** ícone; sem
duplicados; sem consola/WER. **Não se exige `NIM_DELETE` do processo morto** — o kernel reclama os
handles USER/GDI na morte do processo, logo não há fuga.

## 11 — CV-7, CV-8, CV-10, CV-11

**CV-7 (medida, passa).** `TaskbarCreated` recebido num HWND **da thread de UI**, com
`WS_EX_TOOLWINDOW`, top-level, sem dono, nunca mostrado, em processo empacotado **headless**. Emissor:
`PostMessage(HWND_BROADCAST, …)` desta sessão, **não o Explorer**.

**CV-8 (medida, aceitável).** Frio ~10,06 ms; steady/churn máx < 4,7 ms; limiar de um frame a 60 Hz =
16,7 ms. **O `Shell_NotifyIcon` mantém-se na thread de UI sob o envelope de frequência aprovado —
dependência arquitetural de ~5 episódios/60 s.** **Estas medições não são garantia de desempenho do
Windows.**

**CV-10.** Adversarial ~5 ciclos/60 s ≈ 18,5 ms ≈ 0,031 % · critério ≤1 % ≈ ≤600 ms/60 s · folga ~7×
em episódios (5 admitidos contra ~37 para violar). **Reabrem a CV-8 e obrigam a re-medir:** subir o
teto de B · encurtar a janela de B · reduzir o debounce · aumentar as tentativas de A · permitir mais
do que **um** episódio em voo.

**CV-11 (LOW, aceite, com a redação corrigida).** Um atacante esgota B com 5 broadcasts forjados; se um
reinício legítimo do Explorer cair nessa janela, a mensagem verdadeira não é admitida e publica-se
`Available` sobre um ícone morto até a janela deslizar (≤60 s). O atacante escolhe o instante mas
**não provoca a perda sem já ter controlo sobre a sessão do utilizador** — só atrasa a deteção de uma
perda já ocorrida. Ligado ao item **Q** da matriz.

## 12 — Testes e mutações *(CV-12)*

**Harness.** O duplo de `INativeTrayRegistration` **bloqueia dentro de `Add`, `SetVersion` e `Delete`**
sob controlo do teste. O laço do actor é conduzido de forma determinística e o tempo por
`FakeTimeProvider`. **Um teste que não consiga parar o mundo dentro de uma chamada nativa não prova
nada disto.**

| Teste | Estaciona em | Prova |
|---|---|---|
| T1 | `Add` | tempo avança para lá dos 1500 ms com a chamada por retornar ⇒ **`State` já é `Lost`**; o laço não foi bloqueado pelo I/O |
| T2 | `Add` | `State` lido antes de qualquer despacho de UI ⇒ `Lost` |
| T3 | `Add` | `Add` devolve `true` depois do prazo ⇒ descartado na guarda ⇒ **sem `Available`**, `NIM_DELETE` emitido |
| T4 | `Add` | `Release` durante o estacionamento ⇒ `Releasing` vence; exatamente **um** `NIM_DELETE` eficaz |
| T5 | `Delete` | `Delete` falha 3× ⇒ **`Releasing` + `RequestExit`**, e **não** uma sessão degradada |
| T6 | `Add` | o `DeadlineElapsed` é processado pelo laço **sem** adquirir o gate |
| T7 | — | `Release` durante: `Available` · debounce pendente · `Recovering` · atraso de retry · **chamada nativa em voo** · limpeza em curso ⇒ **`Releasing` vence em todos** |

**Mutações** (contra a classe de produção): guarda terminal removida do ponto de entrada · guarda de
geração removida · `CleanupCompleted(false)` a permanecer em `Lost` em vez de escalar · `TryBeginEpisode`
consultado depois da invalidação · B reposto por sucesso · deadline decidido no worker em vez do laço ·
`Compensate` não emitido ao entrar em `Lost` · default-deny de callbacks removido em `Lost`/`Unverified`
· cada validação da CV-6b isoladamente · `NIM_DELETE == false` ignorado. **Cada uma TEM de falhar
testes. Nada entregue só com suite verde.**

## 13 — Matriz de plataforma real

| | Caso | Estado |
|---|---|---|
| A–C | registo inicial · tray headless · menu/flyout (teclado, tema, CV-9) | `NOT_RUN` |
| D–E | reinício real do Explorer em `FOREGROUND` / `BACKGROUND` | `NOT_RUN` — **autorização humana** |
| F | `TaskbarCreated` entregue **pelo próprio Explorer** | `NOT_RUN` — não promovível do sintético |
| G–K | restauro · sem duplicado · sem janela auxiliar/botão · degradação forçada · sem consola/WER | `NOT_RUN` |
| L | `FORCED-TERMINATION TRAY CLEANUP` | `NOT_RUN` |
| M | CV-7 — entrega na topologia do desenho | **MEDIDO · PASSA** (emissor sintético) |
| N | CV-8 — custo nativo na thread de UI | **MEDIDO · aceitável** |
| O | CV-8 pior caso com o shell a reiniciar | `NOT_RUN` — **autorização humana** |
| P | 1500 ms operacionalmente adequado num reinício real | `NOT_RUN` — **autorização humana** |
| Q | CV-11 — reinício do Explorer com o orçamento B esgotado | `NOT_RUN` — **autorização humana** |
| R | a compensação alguma vez falha num shell real (⇒ escalada da §4) | `NOT_RUN` |

**O Explorer não é reiniciado nesta volta.**
