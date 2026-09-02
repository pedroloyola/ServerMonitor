# M13 S2-T — LINEARIZABLE STATE MACHINE

**Autor:** Relay (platform-infra), implementer da S2-T / dono do Windows Shell.
**Branch:** `agent/m13-s2t-tray`, base `221eda4`. **DESENHO. Sem implementação.**
**Desenho normativo vigente.** Substitui `m13-s2t-root-state-machine-redesign.md` (actor FIFO) e
`m13-s2t-architecture-review.md` (revisões 1–7). **Nenhum deles revoga uma condição CV** — o mapa da
secção 8 é a autoridade sobre o estado das condições (CV-15).

---

## 0 — Porque o modelo de actor estava errado

O actor FIFO **separava a admissão do evento da transição de estado**:

```
WndProc admite TaskbarCreated → enfileira Admit → o actor ainda não consumiu
                                                → o Available antigo continua publicado  ✗
```

Foi assim que o intervalo voltou, pela terceira estrutura diferente. A causa não era a fila, o CAS ou
o lock: era **haver um passo entre aceitar o evento e mudar o estado**. A correção é eliminar o passo,
não encurtá-lo.

## 1 — A função de transição

**Existe uma e uma só autoridade de ciclo de vida: `Transition`.**

```csharp
// A ÚNICA escritora de estado de ciclo de vida em todo o sistema.
TransitionResult Transition(TrayEvent e, long monotonicNow);
```

- **Chamada DIRETAMENTE pelas fontes de evento**, de forma síncrona, na thread da própria fonte.
  **Não há fila, não há despacho, não há passo intermédio.**
- Corpo executado sob **um** domínio de exclusão mútua que protege **apenas a decisão**. Dentro dele:
  **nenhum I/O nativo, nenhum `await`, nenhum despacho para a UI, nenhum `ShowAt`, nenhuma aquisição
  do gate.** É aritmética sobre campos privados.
- Devolve **efeitos**. Os efeitos correm **depois** de o domínio ser libertado, **nunca** mutam estado,
  e os seus resultados **reentram por `Transition`**.

### Preâmbulo normativo — corre em toda a chamada, antes de olhar para o evento

```
1. se o estado for Releasing/Released  → só eventos terminalmente legais prosseguem  (D)
2. se o evento trouxer geração ≠ geração atual → DESCARTAR                            (obsolescência)
3. se houver episódio ativo e monotonicNow ≥ deadline → TERMINALIZAR como Lost AQUI,
   nesta execução, ANTES de processar o evento                                        (A)
4. só então despachar o evento
```

O passo 3 é o que torna a garantia do deadline **uma propriedade da função e não de um temporizador**.

### Fontes de evento

Admissão de `TaskbarCreated` · `Release` · continuação de retry · resultado de registo nativo ·
resultado de `NIM_SETVERSION` · observação do deadline · resultado da limpeza · callbacks do tray.

**Todas chamam a mesma função. Nenhuma escreve estado por outra via.**

## 2 — Semântica do deadline, reformulada *(A)*

**Não se exige transição em tempo real em T+1500 ms.** O Windows não oferece escalonamento de tempo
real e a aplicação não o pode garantir. **1500 ms não é, e não é descrito como, garantia de
escalonamento do Windows.**

> **Garantia normativa (segurança).** Depois do deadline monotónico, **nenhuma** observação,
> continuação, callback, retry ou resultado nativo pode publicar `Available`.
> **A terminalização ocorre na primeira execução agendada que observa o deadline expirado.**

Isto é **segurança**; a promptidão é **vivacidade** e é dada, sem ser garantida, pelo temporizador
one-shot — que passa a ser **apenas mais uma fonte de evento** cuja única função é assegurar que
*alguma* execução acontece mesmo que nada mais dispare. Se o temporizador atrasar, a segurança
mantém-se: quem chegar primeiro terminaliza.

**Exemplo canónico, e é literalmente o passo 3 do preâmbulo:**

```
NIM_ADD começa antes do deadline
→ devolve SUCESSO depois do deadline
→ o resultado reentra por Transition(NativeAddCompleted(gen, ok:true), now)
→ preâmbulo relê o tempo monotónico e vê o deadline expirado
→ terminaliza como Lost NESTA execução, antes de olhar para o ok:true
→ o sucesso é obsoleto e NÃO publica Available
→ o efeito emitido é a compensação, não a promoção
```

O deadline é **monotónico · capturado uma vez · nunca reiniciado**, e é verificado **depois de cada
`await`, depois de cada chamada nativa síncrona, e antes de publicar `Available`** — as três, porque
todas essas são reentradas em `Transition`.

## 3 — Admissão: uma só operação linearizável *(C)*

```csharp
// Na WndProc, thread de UI. Síncrono. Sem fila.
case TrayEvent.TaskbarCreated:
    if (_state != Available) { /* coalesce ou descartar, conforme o estado */ break; }
    if (!_frequencyLimiter.TryBeginEpisode(monotonicNow)) break;   // B: sem episódio, sem deadline, sem Lost
    _generation++;                       //  ┐
    _episodeStart = monotonicNow;        //  │ MESMA transição, mesmo domínio,
    _deadline = monotonicNow + 1500ms;   //  │ sem nada entre elas
    _state = Recovering;                 //  ┘ ◄── Available deixa de estar publicado AQUI
    effects += StartDebounce(_generation);
    break;
```

**A consulta a B acontece DENTRO da transição.** É por isso que não existe janela entre "admitido" e
"invalidado": não há duas operações a ordenar. **Zero intervalo entre aceitar o `TaskbarCreated` e
deixar de publicar `Available`** — e agora sem depender de a `WndProc` ser síncrona, porque a
propriedade é da função de transição.

**Debounce, retry e I/O nativo só começam como efeitos, depois da decisão.** O debounce vive **dentro**
do episódio e do deadline; coalesce mensagens adicionais e **não** move o relógio, **não** cria
geração, **não** preserva `Available`.

## 4 — Estados e transições

`Unavailable(0)` · `Available(1)` · `Recovering(2)` · `Lost(3)` · `Releasing(4)` · `Released(5)`

| Estado | Evento | Novo estado | Efeitos |
|---|---|---|---|
| `Available` | `TaskbarCreated` admitido por B | **`Recovering`** | iniciar debounce |
| `Unavailable` | `Establish` | `Recovering` | iniciar tentativa |
| `Recovering` | `NativeAddCompleted(ok)` **dentro do prazo**, `SetVersion` ok | `Available` | publicar; limpar efeito |
| `Recovering` | falha nativa com orçamento A esgotado | `Lost` | compensar |
| `Recovering` | **deadline expirado** (preâmbulo) | `Lost` | compensar |
| `Lost` | `CleanupCompleted(verified: true)` | `Lost` | notificar a S2 ⇒ UX degradada aprovada |
| `Lost` | `CleanupCompleted(verified: false)` | **`Releasing`** | CV-17 + **`FailSafeExitRequested`** (§6.4) |
| **qualquer** | `Release` | **`Releasing`** | compensar |
| `Releasing` | `AddCompleted`/`SetVersionCompleted` obsoletos | `Releasing` | **`DeleteIcon` compensatório** (§5.2) |
| `Releasing` | `CleanupCompleted(verified: true)` **e** nenhuma reconciliação pendente | **`Released`** | — |
| `Releasing` | `CleanupCompleted(verified: false)` | `Releasing` | **`FailSafeExitRequested`** (§6.4) |

**`Lost` tem duas causas legítimas:** esgotamento do orçamento A com falha nativa **observada**, e
expiração do deadline. **Supressão por B não produz `Lost`** — sem episódio não há causa terminal.

## 5 — `Release` absorvente: **emissão** vs **conclusão** *(D · decisão OPÇÃO A)*

A regra literal anterior — *"nenhum `NIM_ADD` pode executar depois do `Release`"* — **está substituída**
pelo invariante causal abaixo. Satisfazê-la à letra exigiria que o `Release` esperasse pelo gate de I/O
nativo, **o que reintroduziria o defeito de timer/deadlock provado na revisão 6**. O gate **não** volta
a ser acoplado ao ciclo de vida.

### 5.1 Emissão — garantia estrita, imposta pelo TIPO

> **Uma vez comprometido `Releasing`, a `Transition` NUNCA emite um efeito `Add` novo.**

Isto **não é uma convenção que cada ramo tem de respeitar**: é **imposto pelo tipo**, e por isso
impossível de violar sem alterar a estrutura.

```csharp
// Só existe enquanto houver episódio ativo. Releasing/Released põem _episode a null.
internal readonly record struct EpisodeToken(long Generation, long Deadline);

internal abstract record ShellEffect
{
    // O construtor de AddIcon é privado; a ÚNICA via de construção exige um EpisodeToken.
    internal sealed record AddIcon : ShellEffect
    {
        private AddIcon(EpisodeToken token) { … }
        internal static AddIcon For(EpisodeToken token) => new(token);
    }
    internal sealed record DeleteIcon : ShellEffect;          // não exige token: compensar é sempre legal
    internal sealed record FailSafeExitRequested : ShellEffect;
}
```

**Não é possível construir um `AddIcon` sem um `EpisodeToken`, e não é possível obter um `EpisodeToken`
em `Releasing`/`Released`**, porque nesses estados `_episode` é `null` e não há outro produtor do token.
A garantia de emissão é, portanto, verificável por inspeção do tipo — não por revisão de cada ramo.
`DeleteIcon` **não** exige token de propósito: compensar tem de continuar legal em estado terminal.

### 5.2 Conclusão — o que pode acontecer fisicamente depois

Um `Add` **legitimamente emitido e linearizado ANTES** de `Releasing` **pode completar fisicamente
depois**. Esse resultado é **trabalho obsoleto e compensado**:

- reentra na **mesma** `Transition`;
- observa `Releasing` / geração obsoleta;
- **não publica `Available`** · **não reabre recuperação** · **não muta o ciclo de vida para fora de
  `Releasing`**;
- **se pode ter recriado o ícone no shell ⇒ `Delete` compensatório é OBRIGATÓRIO.**

> **O passo 1 do preâmbulo deixa de significar "ignorar".** Em estado terminal, `AddCompleted` e
> `SetVersionCompleted` **não são descartados em silêncio: são RECONCILIADOS.** Descartá-los seria
> exatamente o defeito — o resultado é obsoleto para o *ciclo de vida*, mas não para o *shell*.

### 5.3 Absorção, tal como se mantém

`Releasing` **+** `TaskbarCreated` · `AddCompleted` · `SetVersionCompleted` · `Retry` · `Deadline` ·
`Lost` · callback · continuação de notificação ⇒ **nunca** volta a `Available` nem a `Recovering`.
δ(`Releasing`, x) = `Releasing` ∀x, salvo a transição interna para `Released` da §6.
**Nenhum resultado obsoleto contorna a `Transition`.**

## 6 — Efeitos, gate de I/O, entrega de notificações, e a saída fail-safe

### 6.1 Efeitos

Um efeito **nunca** muta estado. Os efeitos de uma transição correm **depois** de libertar o domínio de
decisão, e cada um carrega a **geração** e o **número de sequência** da transição que o produziu.

> **Ordem:** os efeitos que tocam o shell são executados **pela ordem de sequência em que foram
> produzidos**, serializados pelo gate. Sem isto, um `Add` e um `Delete` de transições consecutivas
> poderiam chegar ao shell trocados.

**É proibido chamar `Transition` de dentro do domínio de decisão.** Os efeitos correm fora e os seus
resultados reentram como eventos novos.

### 6.2 Gate

`_nativeGate` serializa **exclusivamente** I/O nativo do shell. **Não decide** admissão, `Available`,
`Recovering`, `Lost`, `Releasing`, nem terminalidade da limpeza. **Nunca é adquirido dentro do domínio
de decisão, e o `Release` nunca espera por ele** — é isso que impede o defeito de timer/deadlock da
revisão 6.

### 6.3 Entrega de notificações — revalidação **no momento da entrega** *(§7 da decisão)*

> **Um evento ser válido quando foi enfileirado NÃO É SUFICIENTE.**

Toda entrega enfileirada ou atrasada — `StateChanged` para a S2, notificações de UI, a notificação
CV-17/CV-18 — **revalida geração e estado de ciclo de vida no instante da ENTREGA**, e não apenas no
instante em que foi enfileirada.

```csharp
// Avaliado NA ENTREGA, contra o estado atómico atual. Não é um snapshot capturado no enqueue.
bool ShouldDeliver(Delivery d) => d.Generation == CurrentGeneration && d.Class.IsLegalIn(CurrentState);
```

**Classificação, porque "suprimir" não é uniforme:**

| Classe de entrega | Em `Releasing`/`Released` |
|---|---|
| semântica de sessão (`Available`, `Recovering`, `Lost` degradável) | **SUPRIMIDA** — não pode dizer à S2 para degradar nem para confiar numa afordância |
| dirigida ao terminal (`Releasing`, `Released`, `FailSafeExitRequested`) | **ENTREGUE** — é precisamente para isto que existem |
| notificação CV-17/CV-18 | entregue **uma vez**, fire-and-forget, nunca aguardada |

### 6.4 Limpeza, `Released`, e escalada fail-safe **sem reentrância**

Efeito registado como `NotIssued` / `MayExist` / `Deleted` / `Unverified`, com **`MayExist` marcado
antes de emitir o `NIM_ADD`** — direção de erro deliberada: marcar depois deixaria uma interrupção
convencer-nos de que não há efeito quando há; marcar antes custa, no pior caso, um `NIM_DELETE`
redundante. Política limitada: até **3** `NIM_DELETE` consecutivos, sem esperas (custo medido: mediana
0,36 ms, máx 0,74 ms ⇒ pior caso ≈2 ms).

#### `Released` exige compensação positivamente resolvida

> **`Released` NÃO significa "o `ReleaseAsync` retornou".** Significa: nenhum efeito de shell novo pode
> ser emitido · nenhum efeito obsoleto pode publicar estado de ciclo de vida · **todo** efeito em voo
> conhecido capaz de deixar uma afordância de tray foi **reconciliado** · e a compensação exigida
> **completou positivamente** — **ou** a terminação do processo já está a ser **irreversivelmente
> imposta** pelo caminho fail-safe/watchdog.

Estruturalmente, `Releasing` mantém um **contador de reconciliações pendentes**, incrementado ao emitir
qualquer efeito capaz de criar afordância e decrementado quando o seu resultado reentra e é
reconciliado. **A transição `Releasing → Released` é guardada por `pending == 0 && effect ∈ {Deleted,
NotIssued}`.** Enquanto houver um `Add` em voo, `Released` é **inalcançável** — não por disciplina, mas
porque a guarda o proíbe.

#### Falha de limpeza: efeito, não chamada

**NÃO** se implementa a cadeia que se auto-bloqueia:

```
CleanupCompleted(false) → RequestExit → ExitSequence.RemoveTrayIcon() → ReleaseAsync
→ reentra na mesma maquinaria de release → bloqueia até o watchdog matar o processo   ✗
```

Substituída por:

```
CleanupCompleted(false) → Transition → Releasing MANTÉM-SE terminal
                        → emite o efeito FailSafeExitRequested
                        → o ciclo de vida EXTERIOR da S2 consome o efeito
                        → inicia o Exit verdadeiro autoritativo (RequestExit)
                        → watchdog de 10 s mantém-se autoritativo
```

**Sem autoridade de release recursiva dentro do tray.** Duas propriedades tornam a reentrância
inofensiva mesmo que o caminho exterior volte a passar pelo tray:

1. `FailSafeExitRequested` é **um efeito entregue**, nunca uma chamada síncrona feita de dentro da
   `Transition` nem do caminho de limpeza;
2. **`ReleaseAsync` em `Releasing`/`Released` é um no-op que retorna imediatamente** — logo o
   `ExitSequence.RemoveTrayIcon()` do caminho autoritativo da S2 **não pode bloquear** à espera do tray.

**`CleanupVerified = false` nunca autoriza continuação em background ou degradada** (CV-16). A S2 nunca
observa `Lost` com limpeza não verificada.

### 6.5 CV-17 / CV-18 — a notificação informativa antes da saída fail-safe

**Uma** tentativa, emitida como efeito com o Exit verdadeiro **já comprometido**, e **nunca aguardada**.

**Slot, não texto.** O desenho define as chaves; **o Prism escreve e localiza pt-BR / pt-PT / en-US, e
eu não invento redação.**

| Chave | Conceito (redação final do Prism) |
|---|---|
| `TrayFailSafeExitNotificationTitle` | *"ServerAlyzer was closed"* |
| `TrayFailSafeExitNotificationBody` | *"We couldn't safely restore the notification-area icon. Open ServerAlyzer again to continue monitoring."* |

**CV-18 — contrato da ação, normativo.** Tipo de ação **literal e fechado**, **zero parâmetros**,
**sem payload arbitrário** — nada do episódio, do shell ou da frota atravessa a notificação.
Expiração **curta**; **fire-and-forget**; **sem** dados de servidor/frota; **sem** terminologia técnica
de Shell/`NIM`; **não modal**. **A falha da notificação é ignorada e NUNCA pode atrasar nem impedir o
Exit verdadeiro.** Um **clique tardio**, já com o processo morto, produz **apenas** o comportamento de
lançamento que já está na allowlist — **sem capacidade especial e sem laço**: não reabre o episódio,
não reentra no tray, não transporta estado.

## 7 — Independência A / B, por tipo *(G)*

**A** — retry dentro de um episódio admitido: 3 tentativas, atrasos 250 ms e 1000 ms (~1250 ms
programados) sob o deadline de 1500 ms; um sucesso pode repô-lo para um episódio futuro.
**B** — admissão: janela deslizante monotónica, **5 admissões / 60 s**, contadas independentemente do
desfecho.

```csharp
internal sealed class EpisodeFrequencyLimiter
{
    public bool TryBeginEpisode(long monotonicTimestamp);   // único método público
}
```

**Nos dois sentidos:** B **não tem API** para receber desfechos, logo o sucesso não o repõe e não pode
esfomear os retries de A; A **não tem referência** ao limitador, logo não cria episódios nem muta o
histórico de B. **Rejeição por B ⇒ sem episódio, sem deadline, sem `Lost`.** Tráfego adversarial com
**sucesso-sempre** bate na supressão de B, porque B conta admissões e não desfechos.

## 8 — Mapa de condições CV *(CV-15)*

**Remover redação durante condensação NÃO revoga uma condição.** Uma condição só desaparece marcada
`SUPERSEDED BY <regra>` com justificação.

| CV | Assunto | Secção | Estado |
|---|---|---|---|
| CV-1 | modelo de confiança da `WndProc`, sete pontos | §9 | **ATIVA** |
| CV-2 | `TaskbarCreated` coalescido e limitado | §3, §7 | ATIVA · fechada |
| CV-2b | dois orçamentos independentes | §7 | ATIVA · fechada, por tipo |
| CV-3 | comportamento sob `TerminateProcess` | §10 | ATIVA · fechada |
| CV-4 | `Unavailable` no ordinal 0 · produtor único de `Available` | §4, §1 | ATIVA · fechada |
| CV-5 | `szTip`/`hIcon` estáticos | §9 | ATIVA · fechada |
| CV-6 | mensagem forjada ignorada | §9 | `SUPERSEDED BY` **CV-6b** — conjunção substituída por quatro casos |
| CV-6b | quatro casos independentes | §9 | **ATIVA** |
| CV-7 | topologia de thread | §11 | ATIVA · **MEDIDA, PASSA** |
| CV-8 | custo nativo síncrono na thread de UI | §11 | ATIVA · **MEDIDA, aceitável** sob o envelope de B |
| CV-9 | reentrância com flyout aberto | §9 | ATIVA · fechada |
| CV-10 | acoplamento limitador ↔ custo de UI | §11 | ATIVA · fechada |
| CV-11 | residual de admissão suprimida | §11 | ATIVA · LOW aceite, redação corrigida |
| CV-12 | evidência de mutação na entrega | §12 | **ABERTA** — fecha na entrega |
| CV-13 · CV-14 | *(fechadas pelo Vigil na revisão 6)* | — | ATIVA · fechadas |
| CV-15 | integridade do documento normativo | §8 | **ATIVA** — este mapa é o cumprimento |
| CV-16 | `CleanupVerified` fail-closed | §6 | **ATIVA** |
| CV-17 | notificação informativa antes da saída fail-safe | §6.5 | **ATIVA** — slot definido, redação do Prism |
| CV-18 | contrato fechado da ação da notificação fail-safe | §6.5 | **ATIVA** |
| — | regra literal *"nenhum `NIM_ADD` pode executar depois do `Release`"* | §5 | **`SUPERSEDED BY` o invariante normativo do `Release` (§5.1 emissão + §5.2 conclusão compensada).** Justificação: satisfazê-la à letra obrigaria o `Release` a esperar pelo gate de I/O nativo, reacoplando ciclo de vida e I/O e reintroduzindo o defeito de timer/deadlock provado na revisão 6. O invariante substituto é **causalmente mais forte**: proíbe a *emissão* pelo tipo e obriga a *compensação* da conclusão tardia, o que a regra literal não fazia. |

## 9 — `WndProc`: confiança e default-deny *(CV-1 · CV-6b · CV-9)*

Sete pontos, integrais: default-deny · identidade da mensagem (o id é seletor, **nunca prova de
origem**) · `lParam` low word na lista fechada `NIN_SELECT`/`NIN_KEYSELECT`/`WM_CONTEXTMENU`/
`WM_LBUTTONDBLCLK` · high word `uID == 1` · `wParam` é coordenada não confiável, só âncora, saneada,
**fora de todos os monitores ⇒ DESCARTAR** · nenhuma mensagem transporta ou desreferencia ponteiro ·
teto de impacto de uma forja = incómodo de UI, sem chegar ao `RequestExit` sem clique real.

**Guarda de estado, e porque é mais necessária e não menos:** callbacks são descartados em **`Lost`**,
**`Releasing`/`Released`**, e **enquanto o efeito estiver por confirmar** (`MayExist`/`Unverified`) —
o estado `Unverified` torna o **ícone vivo mas repudiado** uma possibilidade explícita do desenho, e é
exatamente aí que um callback não pode ser aceite. Os callbacks entram por `Transition` como qualquer
outro evento, logo o preâmbulo aplica-lhes as mesmas guardas.

**CV-6b — quatro casos independentes, sem prova só conjuntiva.** Restantes campos válidos em cada
caso; **B e C obrigatórios**; a mutação de **cada** validação isolada tem de falhar.

| | callback | `uID` | resultado |
|---|---|---|---|
| A | válido | válido | **aceite** |
| **B** | inválido | válido | **ignorado** |
| **C** | válido | inválido | **ignorado** |
| D | inválido | inválido | ignorado |

**CV-9:** com o flyout aberto, `WM_CONTEXTMENU` adicional ou forjada é descartada — sem segundo
flyout, sem reposicionamento, sem mutação de episódio, sem alteração de visibilidade da janela
auxiliar.

## 10 — `FORCED-TERMINATION TRAY CLEANUP` (S6 · CV-3)

**`EMPIRICAL QA ACCEPTANCE WINDOW` = 120 s, fixada a priori.** `TerminateProcess` → 120 s sem interação
→ **uma** passagem do rato sobre a área de notificação → registar. Órfão transitório aceitável; **órfão
persistente = FAIL**; obsoleto não interativo; lançamento seguinte cria **exatamente um** ícone; sem
duplicados; sem consola/WER. Não se exige `NIM_DELETE` do processo morto — o kernel reclama os handles
USER/GDI.

## 11 — CV-7, CV-8, CV-10, CV-11

**CV-7 (medida, passa).** `TaskbarCreated` recebido num HWND da thread de UI, com `WS_EX_TOOLWINDOW`,
top-level, sem dono, nunca mostrado, em processo empacotado **headless**. Emissor:
`PostMessage(HWND_BROADCAST, …)` desta sessão, **não o Explorer**.

**CV-8 (medida, aceitável).** Frio ~10,06 ms; steady/churn máx < 4,7 ms; limiar de um frame a 60 Hz =
16,7 ms. O `Shell_NotifyIcon` mantém-se na thread de UI **sob o envelope de frequência aprovado —
dependência arquitetural de ~5 episódios/60 s**. **Estas medições não são garantia de desempenho do
Windows.**

**CV-10.** Adversarial ~5 ciclos/60 s ≈ 18,5 ms ≈ 0,031 % · critério ≤1 % ≈ ≤600 ms/60 s · folga ~7×
em episódios. **Reabrem a CV-8 e obrigam a re-medir:** subir o teto de B · encurtar a janela de B ·
reduzir o debounce · aumentar as tentativas de A · permitir mais do que um episódio em voo.

**CV-11 (LOW, aceite).** Um atacante esgota B com 5 broadcasts forjados; se um reinício legítimo do
Explorer cair nessa janela, a mensagem verdadeira não é admitida e publica-se `Available` sobre um
ícone morto até a janela deslizar (≤60 s). O atacante escolhe o instante mas **não provoca a perda sem
já ter controlo sobre a sessão do utilizador** — só atrasa a deteção de uma perda já ocorrida. Item
**Q** da matriz.

## 12 — Testes e plano de mutação *(CV-12)*

**Harness.** O duplo de `INativeTrayRegistration` **bloqueia dentro de `Add`, `SetVersion` e `Delete`**
sob controlo do teste; tempo por `FakeTimeProvider`; entregas conduzidas por um despachante
determinístico que permite **atrasar uma entrega já enfileirada**. **Um teste que não consiga parar o
mundo dentro de uma chamada nativa não prova nada disto.**

### Casos determinísticos

| Teste | Prova |
|---|---|
| T1 | estacionado em `Add`, avançar o tempo além do prazo; ao libertar com `ok:true`, a reentrada terminaliza `Lost` **na própria execução** e **não publica `Available`** |
| T2 | `Release` durante: `Available` · debounce pendente · `Recovering` · atraso de retry · **chamada nativa em voo** · limpeza em curso ⇒ **`Releasing` vence em todos** |
| T3 | admissão: entre `TryBeginEpisode` devolver `true` e o estado ser `Recovering` **não existe estado observável** |
| T4 | rejeição por B ⇒ sem episódio, sem deadline, sem `Lost`; sucesso-sempre adversarial converge para supressão |
| T5 | `Delete` falha 3× ⇒ `Releasing` + CV-17 + **efeito `FailSafeExitRequested`**, e **não** sessão degradada |
| T6 | efeitos de `Add`/`Delete` de transições consecutivas chegam ao shell **pela ordem de sequência** |
| T7 | temporizador atrasado: a **primeira** reentrada posterior terminaliza — a segurança não depende da promptidão |
| **T8** | **`Add` PENDENTE ainda não iniciado** — efeito emitido e ainda não entregue ao worker quando o `Release` é comprometido: o efeito é **cancelado antes de tocar no shell**, não há `Add`, e a reconciliação pendente resolve-se sem `Delete` |
| **T9** | **notificação de UI obsoleta** — entrega enfileirada em `Recovering`, atrasada, e entregue já em `Releasing`: é **suprimida** por classe de sessão, enquanto uma entrega dirigida ao terminal na mesma fila **é entregue** |
| **T10** | **reentrância completa do `RequestExit`** — `CleanupCompleted(false)` ⇒ `FailSafeExitRequested` ⇒ a S2 corre o `ExitSequence` real, cujo `RemoveTrayIcon()` chama `ReleaseAsync` de volta: **retorna imediatamente como no-op**, o `ExitSequence` completa, e **nada fica à espera do watchdog** |
| **T11** | `Add` tardio pré-`Release` conclui com `ok:true` ⇒ **`Delete` compensatório emitido** e `Released` **inalcançável** até esse `Delete` verificar |

### Plano de mutação — obrigatório, **uma mutação de cada vez**

Formato exigido na entrega (§10 do `BOSS.md`): **baseline → mutação → invariante violado → testes que
falham com contagens → restauro provado → baseline PASS**. **Nesta volta declara-se o plano; a
evidência acompanha a entrega do código (CV-12).**

| # | Mutação (isolada) | Invariante violado | Teste determinístico que TEM de falhar |
|---|---|---|---|
| **M1** | permitir que a `Transition` emita `Add` durante `Releasing` | §5.1 — emissão imposta pelo tipo | **T2** (variante "`Release` com retry pendente") e **T8** |
| **M2** | um `Add` tardio pré-`Release` bem-sucedido publica `Available` | §5.2 — conclusão é obsoleta | **T11**, e **T1** para a variante por deadline |
| **M3** | um `Add` tardio **não** recebe `Delete` compensatório | §5.2 + §6.4 — `Released` exige reconciliação | **T11** e **T5** |
| **M4** | remover a revalidação em tempo de entrega das notificações | §6.3 — validade no enqueue não basta | **T9** |

**Mutações herdadas, mantidas:** passo 1 do preâmbulo removido · passo 2 removido · **passo 3 removido**
· `TryBeginEpisode` movido para fora da transição · `Available` publicado sem revalidar o prazo · gate
adquirido dentro do domínio de decisão · `Transition` chamada de dentro do domínio · efeitos a mutar
estado · ordem de sequência removida · `CleanupCompleted(false)` a permanecer em `Lost` ·
**`FailSafeExitRequested` substituído por chamada síncrona a `RequestExit`** (deve falhar **T10**) ·
**guarda `pending == 0` removida da transição para `Released`** (deve falhar **T11**) · default-deny
removido em `Lost`/`Unverified` · cada validação da CV-6b isoladamente · `NIM_DELETE == false` ignorado
· B reposto por sucesso.

**Cada uma TEM de falhar testes. Nada entregue só com suite verde.**

## 13 — As cinco perguntas do Atlas

| | Pergunta | Resposta | Porquê, estruturalmente |
|---|---|---|---|
| 1 | Admissão e invalidação são **uma** operação linearizável? | **SIM** | §3 — `TryBeginEpisode`, geração, timestamp, deadline e `Recovering` são o mesmo corpo, sob o mesmo domínio, sem nada entre eles |
| 2 | Existe **exatamente uma** autoridade? | **SIM** | `Transition` é a única escritora de estado; efeitos nunca mutam estado; o gate não decide |
| 3 | `Release` é **estruturalmente** absorvente? | **SIM**, com a distinção clarificada | passo 1 do preâmbulo ∀ chamada; δ(`Releasing`,x)=`Releasing` ∀x. **Sem `Add` NOVO** depois de `Releasing` — imposto pelo tipo (§5.1); um `Add` pré-existente só pode completar **como trabalho obsoleto e compensado** (§5.2) |
| 4 | Algo **depois do deadline** pode publicar `Available`? | **NÃO** | passo 3 do preâmbulo terminaliza **antes** de o evento ser olhado; o ramo que publica `Available` é inalcançável em estado terminal |
| 5 | Algum resultado obsoleto pode **contornar** a função? | **NÃO** | resultados não publicam: **reentram** por `Transition`, e o passo 2 descarta geração obsoleta |

## 14 — Matriz de plataforma real

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
| R | a compensação alguma vez falha num shell real (⇒ escalada da §6) | `NOT_RUN` |
| **S** | **CV-17 — a notificação aparece e não atrasa a saída** | `NOT_RUN` |

**O Explorer não é reiniciado nesta volta.**
