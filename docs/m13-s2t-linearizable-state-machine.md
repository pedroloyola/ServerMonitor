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
   ⚠ RESSALVA CV-19, normativa: os eventos de CONCLUSÃO DE EFEITO capazes de ter criado
     uma afordância de shell — AddCompleted, SetVersionCompleted — NUNCA são descartados
     por geração obsoleta. São encaminhados para RECONCILIAÇÃO (§5.2). O passo 2 mantém-se
     para todos os outros eventos.
3. se houver episódio ativo e monotonicNow ≥ deadline → TERMINALIZAR como Lost AQUI,
   nesta execução, ANTES de processar o evento                                        (A)
4. só então despachar o evento
```

O passo 3 é o que torna a garantia do deadline **uma propriedade da função e não de um temporizador**.

> **CV-19 — porque a ressalva do passo 2 é obrigatória e não cosmética.** Um `AddCompleted` tardio traz
> **sempre** a geração antiga, porque o `Releasing` mudou a geração terminal. Lido à letra, o passo 2
> descartá-lo-ia **antes** de a reconciliação da §5.2 acontecer — que é exatamente o defeito que a §5.2
> existe para impedir, e um implementer que siga o preâmbulo literalmente escreve o ícone órfão. **O
> passo 2 não é removido**: continua a ser carga estrutural para todos os outros eventos e tem mutação
> própria.

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

### 5.1 Emissão — capacidade **opaca e não fabricável**

> **Uma vez comprometido `Releasing`, a `Transition` NUNCA emite um efeito `Add` novo.**

**A revisão anterior não cumpria isto.** `EpisodeToken` e `AddIcon.For` eram `internal`: qualquer código
do assembly podia fabricar token e efeito **sem passar pela `Transition`**. Esse `Add` não entrava no
contador `pending`, a máquina podia atingir `Released` com a guarda satisfeita, e o `Add` posterior
deixava um ícone real. **O token não era o mecanismo — era a convenção de ninguém o construir.**

**Mecanismo corrigido: os efeitos são tipos `private` aninhados na máquina, e o exterior só vê
comportamento.**

```csharp
internal interface IShellEffect                      // o que o executor vê. Não constrói nada.
{
    long Generation { get; }
    long Sequence { get; }
    bool MayCreateAffordance { get; }                // alimenta o contador `pending` (§6.4)
    ShellOpResult Execute(INativeTrayRegistration native);
}

internal sealed class TrayStateMachine
{
    // Tipos PRIVADOS ANINHADOS: não são nomeáveis fora desta classe, logo não são declaráveis
    // nem construíveis fora dela. Não há factory internal, não há token a circular.
    private sealed record AddIcon(long Generation, long Sequence) : IShellEffect { … }
    private sealed record DeleteIcon(long Generation, long Sequence) : IShellEffect { … }
    private sealed record FailSafeExitRequested(long Generation, long Sequence) : IShellEffect { … }

    // Únicos call sites de `new AddIcon(...)` em todo o programa: os ramos de emissão da Transition,
    // que o passo 1 do preâmbulo torna inalcançáveis em Releasing/Released.
}
```

**Porque não há via de fabricação, verificável só pela superfície do tipo:**

1. `AddIcon` é `private` numa classe `sealed` ⇒ **o nome não existe fora dela**. Nenhum outro ficheiro
   pode declarar a variável, quanto mais construir o valor. O compilador impõe-o.
2. **Não existe factory, `internal static`, token, nem qualquer outro produtor exportado.** O
   `EpisodeToken` **desaparece como mecanismo** — era uma capacidade a circular, e uma capacidade que
   circula é uma capacidade que se fabrica. O aninhamento privado torna-o desnecessário.
3. O executor recebe `IShellEffect` e **só chama `Execute`**; não tem construtor a que chegar, e o
   interface não expõe nenhum.
4. Dentro da máquina, os únicos `new AddIcon(...)` estão nos ramos de emissão, e o **passo 1 do
   preâmbulo** torna esses ramos inalcançáveis em estado terminal.

Um leitor conclui, **só da superfície do tipo**, que não existe produtor de efeitos de afordância fora
da `Transition`.

> **Limite honesto:** reflexão consegue construir qualquer coisa. Está fora da fronteira de confiança
> pela mesma razão do ponto 7 da CV-1 — um processo capaz de usar reflexão sobre o nosso assembly já
> tem controlo total, e a S2-T não lhe cria capacidade nova.

**`DeleteIcon` continua a não exigir token** — decisão mantida: compensar tem de ser sempre legal em
estado terminal. O aninhamento privado dá a não-fabricação **sem** reintroduzir uma restrição de
capacidade sobre a compensação.

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
| dirigida ao terminal (`Releasing`, `Released`) | **ENTREGUE** — é precisamente para isto que existem |
| **pedido fail-safe de saída** | **NÃO É UMA ENTREGA** — invocação direta do sink, §6.4; nunca passa por esta classificação |
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

#### O consumo é DIRETO e LIMITADO — não é uma entrega enfileirada

**Correção a um defeito real da versão anterior:** dizer apenas *"é um efeito entregue"* trocava a
reentrância por uma **entrega**, e a entrega herdava o problema noutra forma. Se ficasse enfileirada, o
`RequestExit` não corria, e **o watchdog de 10 s só é armado dentro do `RequestExit`** — ou seja, antes
disso **não há rede nenhuma** e o processo podia ficar vivo em `Releasing` indefinidamente.

**Mecanismo:** o pedido fail-safe **não é uma entrega**. É a **invocação síncrona e direta de um sink
registado**, executada pelo executor de efeitos **imediatamente após a libertação do lock, na própria
thread que correu a `Transition`**.

```csharp
// Injetado na construção. Null ⇒ ArgumentNullException na construção, nunca um drop silencioso.
internal sealed class TrayStateMachine(Action requestAuthoritativeExit, …);

// No executor, fora do lock, ANTES de qualquer outro efeito da mesma transição:
if (effects.HasFailSafeExit) { _failSafeOnce.RunOnce(requestAuthoritativeExit); }
```

**Porque não pode ficar pendente:**

1. **Fora do lock, sem fila, sem dispatcher, sem UI.** Não passa pelo mecanismo de entrega da §6.3, logo
   não pode ser classificado, adiado, coalescido nem suprimido.
2. **É o primeiro efeito executado** da transição que o produziu, portanto nada da própria S2-T se lhe
   pode interpor.
3. **Não depende de a thread de UI estar viva** — corre na thread da fonte de evento que terminalizou,
   que pode ser a do worker nativo ou a do temporizador.
4. **Sink obrigatório na construção.** Ausência é erro de construção, não uma condição de runtime que
   se descubra tarde.
5. **`RunOnce`** garante no máximo uma invocação, e uma exceção do sink não impede o resto da limpeza —
   mas também não é engolida silenciosamente: é registada.
6. Assim que o sink corre, o `RequestExit` da S2 arma o watchdog de 10 s logo após o seu CAS para
   `Exiting`. **A partir daí existe rede; antes, o único que garante progresso é este consumo direto.**

> **Só a NOTIFICAÇÃO (CV-17/CV-18) é fire-and-forget. O pedido de saída não é.**

**Sem autoridade de release recursiva dentro do tray**, e a reentrância continua inofensiva porque
**`ReleaseAsync` em `Releasing`/`Released` é um no-op que retorna imediatamente** — logo o
`ExitSequence.RemoveTrayIcon()` do caminho autoritativo da S2 **não pode bloquear** à espera do tray.

**`CleanupVerified = false` nunca autoriza continuação em background ou degradada** (CV-16). A S2 nunca
observa `Lost` com limpeza não verificada.

### 6.5 CV-17 / CV-18 — a notificação informativa antes da saída fail-safe

#### Condição de emissão *(Prism)*

> A notificação **só é emitida quando a chamada fail-safe ao `RequestExit` VENCE a transição para
> `Exiting`.**

Se o utilizador **já tinha pedido Sair** — pelo menu do tray, ou por X com o segundo plano desligado —
e a compensação falhar **durante essa saída**, **não há notificação**: o desfecho é exatamente o que
ele pediu, e *"abra o ServerAlyzer novamente para continuar"* **contradiria a sua própria ação**. Como
o `RequestExit` já é one-shot por CAS (`TryTransitionToExiting`), isto é uma **condição sobre o
resultado desse CAS no caminho existente**, e **não** um mecanismo novo: emite-se a notificação apenas
no ramo em que o nosso pedido foi o que ganhou.

#### Conteúdo

**Strings FINAIS já escritas pelo Prism** em `.boss/tmp/m13-s2-strings.md`, chaves
`TrayFailSafeExitNotificationTitle` e `TrayFailSafeExitNotificationBody`, em **pt-BR / pt-PT / en-US**.
**Referência, não cópia — não invento redação.** Instaladas nos `.resw` reais; sem strings de UI
hardcoded.

**Expiração:** **30 minutos**, valor **sugerido pelo Prism** por ainda cobrir quem regressa ao ecrã
alguns minutos depois, sem ficar indefinidamente no Centro de Notificações. Registado como sugestão
dele, não como número meu.

#### CV-18 — contrato da ação, normativo *(FECHADA)*

Tipo de ação **literal e fechado**, **zero parâmetros**, **sem payload arbitrário** — nada do episódio,
do shell ou da frota atravessa a notificação. **Fire-and-forget**; **não modal**; **sem** dados de
servidor/frota; **sem** terminologia técnica de Shell/`NIM`. **A falha da notificação é ignorada e
NUNCA pode atrasar nem impedir o Exit verdadeiro** — é o único efeito desta secção que é
fire-and-forget, ao contrário do pedido de saída (§6.4). Um **clique tardio**, já com o processo morto,
produz **apenas** o comportamento de lançamento que já está na allowlist — **sem capacidade especial e
sem laço**: não reabre o episódio, não reentra no tray, não transporta estado.

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
| CV-18 | contrato fechado da ação da notificação fail-safe | §6.5 | **ATIVA · FECHADA** |
| CV-19 | ressalva do passo 2 para eventos de conclusão de efeito | §1 (preâmbulo) | **ATIVA** |
| CI-1b | *(dívida do lado da S2)* grafias numéricas de enum em payloads hostis do contrato de ativação | §6.5 | **REFERENCIADA, não herdada em silêncio.** A ação `FailSafeExit` é acrescentada a esse mesmo contrato, logo a CI-1b **aplica-se-lhe**: vocabulário genuinamente fechado por `switch`/allowlist exata, e grafia numérica ou desconhecida ⇒ **fail closed**. A dívida continua a ser da S2; fica aqui registada porque a S2-T alarga o contrato. |
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
| T2 | `Release` durante: `Available` · debounce pendente · `Recovering` · atraso de retry · **chamada nativa em voo** · limpeza em curso ⇒ **`Releasing` vence em todos**. **Reforço obrigatório:** não basta observar que o estado é `Releasing` — o teste **CONTA os efeitos `Add` emitidos após o `Release` e exige ZERO**. Sem essa contagem, a M1 passa mutada. |
| T3 | admissão: entre `TryBeginEpisode` devolver `true` e o estado ser `Recovering` **não existe estado observável** |
| T4 | rejeição por B ⇒ sem episódio, sem deadline, sem `Lost`; sucesso-sempre adversarial converge para supressão |
| T5 | `Delete` falha 3× ⇒ `Releasing` + CV-17 + **efeito `FailSafeExitRequested`**, e **não** sessão degradada |
| T6 | efeitos de `Add`/`Delete` de transições consecutivas chegam ao shell **pela ordem de sequência** |
| T7 | temporizador atrasado: a **primeira** reentrada posterior terminaliza — a segurança não depende da promptidão |
| **T8** | **`Add` PENDENTE ainda não iniciado** — efeito emitido e ainda não entregue ao worker quando o `Release` é comprometido: o efeito é **cancelado antes de tocar no shell**, não há `Add`, e a reconciliação pendente resolve-se sem `Delete` |
| **T9** | **notificação de UI obsoleta** — entrega enfileirada em `Recovering`, atrasada, e entregue já em `Releasing`: é **suprimida** por classe de sessão, enquanto uma entrega dirigida ao terminal na mesma fila **é entregue** |
| **T10** | **reentrância completa do `RequestExit`, atravessando o caminho real** — `CleanupCompleted(false)` ⇒ o executor **invoca o sink registado** (§6.4), **não** o teste a chamar `RequestExit` diretamente ⇒ a S2 corre o `ExitSequence` **real**, cujo `RemoveTrayIcon()` chama `ReleaseAsync` de volta: **retorna imediatamente como no-op**, o `ExitSequence` completa, e **nada fica à espera do watchdog**. O teste **só conta se atravessar a invocação real do sink**. |
| **T11** | `Add` tardio pré-`Release` conclui com `ok:true` ⇒ **`Delete` compensatório emitido** e `Released` **inalcançável** até esse `Delete` verificar. **Reforço obrigatório, as duas asserções:** (i) o **estado** nunca é `Available`; (ii) **nenhuma entrega** de `Available` é produzida. Sem as duas, a M2 passa mutada. |

| **T12** | **o pedido fail-safe não pode ficar pendente** — com o despachante de UI **parado** e a fila de entregas **bloqueada**, `CleanupCompleted(false)` ⇒ o sink é invocado **na mesma pilha**, fora do lock, antes de qualquer outro efeito |

### Plano de mutação — obrigatório, **uma mutação de cada vez**

Formato exigido na entrega (§10 do `BOSS.md`): **baseline → mutação → invariante violado → testes que
falham com contagens → restauro provado → baseline PASS**. **Nesta volta declara-se o plano; a
evidência acompanha a entrega do código (CV-12).**

| # | Mutação (isolada) | Invariante violado | Teste determinístico que TEM de falhar |
|---|---|---|---|
| **M1** | permitir que a `Transition` emita `Add` durante `Releasing` | §5.1 — emissão não fabricável | **T2** pela **contagem de zero `Add`**, e **T8** |
| **M2** | um `Add` tardio pré-`Release` bem-sucedido publica `Available` | §5.2 — conclusão é obsoleta | **T11** pelas **duas** asserções (estado **e** entregas), e **T1** para a variante por deadline |
| **M3** | um `Add` tardio **não** recebe `Delete` compensatório | §5.2 + §6.4 — `Released` exige reconciliação | **T11** e **T5** |
| **M4** | remover a revalidação em tempo de entrega das notificações | §6.3 — validade no enqueue não basta | **T9** |

**Mutações herdadas, mantidas:** passo 1 do preâmbulo removido · passo 2 removido · **passo 3 removido**
· `TryBeginEpisode` movido para fora da transição · `Available` publicado sem revalidar o prazo · gate
adquirido dentro do domínio de decisão · `Transition` chamada de dentro do domínio · efeitos a mutar
estado · ordem de sequência removida · `CleanupCompleted(false)` a permanecer em `Lost` ·
**sink fail-safe convertido em entrega enfileirada** (deve falhar **T12**) ·
**`AddIcon`/`DeleteIcon` promovidos de `private` aninhado a `internal`** (deve falhar a asserção de
arquitetura que proíbe produtores de efeito fora da máquina) ·
**ressalva CV-19 removida do passo 2**, voltando a descartar `AddCompleted` obsoleto por geração
(deve falhar **T11**) ·
**guarda `pending == 0` removida da transição para `Released`** (deve falhar **T11**) · default-deny
removido em `Lost`/`Unverified` · cada validação da CV-6b isoladamente · `NIM_DELETE == false` ignorado
· B reposto por sucesso.

**Cada uma TEM de falhar testes. Nada entregue só com suite verde.**

## 13 — As cinco perguntas do Atlas

| | Pergunta | Resposta | Porquê, estruturalmente |
|---|---|---|---|
| 1 | Admissão e invalidação são **uma** operação linearizável? | **SIM** | §3 — `TryBeginEpisode`, geração, timestamp, deadline e `Recovering` são o mesmo corpo, sob o mesmo domínio, sem nada entre eles |
| — | *(por que a 1 e a 3 passam a SIM e a 5 perde a ressalva)* | — | a ressalva anterior era **a fabricação de efeitos fora da `Transition`** (§5.1). Com os efeitos como tipos `private` aninhados, **não existe nome nem produtor exportado**, logo nenhum caminho contorna a autoridade — e as respostas deixam de depender de convenção |
| 2 | Existe **exatamente uma** autoridade? | **SIM** | `Transition` é a única escritora de estado; efeitos nunca mutam estado; o gate não decide |
| 3 | `Release` é **estruturalmente** absorvente? | **SIM**, com a distinção clarificada | passo 1 do preâmbulo ∀ chamada; δ(`Releasing`,x)=`Releasing` ∀x. **Sem `Add` NOVO** depois de `Releasing` — imposto pelo tipo (§5.1); um `Add` pré-existente só pode completar **como trabalho obsoleto e compensado** (§5.2) |
| 4 | Algo **depois do deadline** pode publicar `Available`? | **NÃO** | passo 3 do preâmbulo terminaliza **antes** de o evento ser olhado; o ramo que publica `Available` é inalcançável em estado terminal |
| 5 | Algum resultado obsoleto pode **contornar** a função? | **NÃO**, sem ressalva | resultados não publicam: **reentram** por `Transition`. O passo 2 descarta geração obsoleta **exceto** conclusões de efeito, que são **reconciliadas** (CV-19). E **nenhum efeito é fabricável fora da máquina** (§5.1), pelo que já não existe a via que antes gerava a ressalva |

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
