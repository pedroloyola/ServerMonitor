# M13 S2-T — ARCHITECTURE REVIEW 7 (ordem entre deadline, gate e compensação)

> ## ⚠️ HISTÓRICO — SUBSTITUÍDO
> Este ficheiro é o registo das revisões 1–7 e **já não é o desenho normativo**. O desenho vigente é
> **`docs/m13-s2t-root-state-machine-redesign.md`**.
>
> **Nada aqui revoga uma condição CV.** Se uma condição parecer ausente do desenho novo, a leitura
> correta é o **mapa CV** da secção 9 desse documento, e o ficheiro de condições do Vigil — nunca
> inferir revogação a partir de redação removida (CV-15).


**Autor:** Relay (platform-infra), implementer da S2-T / dono do Windows Shell.
**Branch:** `agent/m13-s2t-tray`, base `221eda4`. **DESENHO. Sem implementação.**

> **Estado herdado, registado.** Invariante de raiz **fechado e confirmado duas vezes** — sem intervalo
> pré-episódio e sem `Available` falso após admissão aceite. **Vigil:** CV-10 e CV-11 **fechadas**;
> resta **só a CV-12**, que fecha na entrega. **Prism: PASS**, sem mudança de redação.
>
> **Esta volta corrige a ORDEM entre três mecanismos que introduzi ao mesmo tempo na revisão 6 —
> deadline autónomo, secção crítica e compensação. Não acrescenta mecanismo novo.**

---

## 0 — O que a revisão 6 partiu, e como

Ao serializar tudo através do mesmo `_nativeGate`, **o temporizador do deadline passou a adquirir o
mesmo gate da chamada nativa antes de terminalizar**. Se a chamada nativa bloquear, **o timer bloqueia
com ela**: o `Recovering` pode ultrapassar os 1500 ms e o `Lost` só aparece quando o dispatcher
retomar. **A atomicidade que a correção do ALTA-3 ganhou custou a independência do deadline, que era
exatamente a propriedade do ALTA-1.** Um mecanismo anulou o outro por ordem errada.

**A correção é de ordem: o deadline DECIDE fora do gate; a limpeza SEGUE dentro do gate.**

## 1 — Invariantes

**R1 (fechado).** Entre a aceitação de um `TaskbarCreated` e a publicação de `Recovering` não há
`await`, `TryEnqueue`, temporizador, lock nem chamada nativa — bloco síncrono único na `WndProc`.

**R2 (fechado).** `Available` só é publicado pelo caminho que leu `true` de `NIM_ADD` **e**
`NIM_SETVERSION` **e** venceu a decisão terminal (R3′).

**R3′ (corrigido).** A **decisão terminal e o `State` autoritativo mudam NO PRAZO, fora do gate e sem o
adquirir**, por uma única operação atómica sem lock. Nenhuma chamada nativa bloqueada pode atrasá-la.

**R4′ (corrigido).** A compensação é **verificável e limitada**, não "best-effort silencioso": se a
limpeza não puder ser confirmada, isso é **estado observável**, não silêncio.

**R5′ (refinado).** Uma chamada nativa obsoleta pode ainda ser **emitida**, mas **nunca sobrevive**:
quem a emitiu é quem revalida e compensa **antes de largar o gate**, e ninguém consegue observar o
shell nesse intervalo sem o gate.

## 2 — R3′: o deadline decide fora do gate *(ALTA 1)*

**A terminalização parte-se em duas fases com donos e sincronização diferentes.**

```csharp
// FASE 1 — DECISÃO. No prazo. Sem gate, sem lock, sem dispatch.
void OnDeadline(long episodeId)
{
    if (!TryTerminalize(episodeId, Outcome.LostByExpiry)) return;   // CAS one-shot; perdeu ⇒ já terminal
    // o State autoritativo passa a Lost AQUI, e nada o pode atrasar

    // FASE 2 — LIMPEZA. Segue. Pode esperar pelo gate.
    ScheduleCompensation(episodeId);   // adquire _nativeGate; pode esperar por uma chamada em voo
    PublishOnUi(TrayAffordanceState.Lost);   // notificação; não é a decisão
}
```

**O slot de desfecho é único e lock-free.** `TryTerminalize` é um `Interlocked.CompareExchange` sobre
um slot one-shot. **Competem por ele quatro produtores** — sucesso, esgotamento do orçamento A,
expiração do deadline, e `Release`. Quem vence, decide; quem perde, não publica nada e limita-se a
compensar se tiver produzido efeito.

**É isto que fecha a race que o gate escondia.** Antes, o caminho de sucesso podia validar, ser
ultrapassado pelo timer, e mesmo assim publicar `Available`. Agora o caminho de sucesso **também tem de
vencer o CAS**: se o timer já lá pôs `LostByExpiry`, o CAS falha e **`Available` não é publicado** —
sem precisar de lock nenhum.

**Duas leituras que separo de propósito:**
- **`State` autoritativo** — muda no prazo, é o que qualquer leitor observa, e **não depende do gate
  nem do dispatcher**.
- **Notificação `StateChanged`** — é despachada para a thread de UI porque a S2 vive lá. Com a UI
  encravada chega tarde. **Isto não é garantia de latência de notificação, e não a apresento como tal**
  — a garantia é que a partir do prazo **`Available` é impossível**.

**O gate deixa de proteger o desfecho.** Passa a proteger **apenas** a sequência de chamadas nativas e
a compensação. **O temporizador nunca o adquire para decidir.**

## 3 — R4′: compensação verificável e limitada *(ALTA 2)*

**O defeito.** Ignorar `NIM_DELETE == false` torna a chamada repetível, mas **não garante que o ícone
real e os callbacks desapareceram** — que era todo o objetivo da compensação.

**Distinguir `false` benigno de `false` que importa.** `NIM_DELETE` devolve `false` legitimamente
quando não existe ícone (nunca foi adicionado, ou já foi removido). Só é falha quando **sabemos que
pode existir efeito nosso**:

```csharp
enum ShellEffect { NotIssued, MayExist, Deleted, Unverified }
```

- `MayExist` é escrito **antes** de emitir o `NIM_ADD` — direção de erro deliberada: marcar depois
  deixaria uma interrupção convencer-nos de que não há efeito quando há; marcar antes custa, no pior
  caso, um `NIM_DELETE` redundante.
- `Deleted` só quando o `NIM_DELETE` devolveu `true`.
- **`Unverified`** quando o `NIM_DELETE` devolveu `false` estando em `MayExist`, esgotada a política.

**Política de compensação — limitada, dentro do gate, sem atrasos programados.** Até **3** tentativas
consecutivas de `NIM_DELETE`, sem espera entre elas: o `NIM_DELETE` medido custa mediana 0,36 ms e
máximo 0,74 ms, logo o pior caso da política é **≈2 ms** dentro do gate. Sem retry infinito, sem ciclo
escondido, sem busy spin.

**Se terminar em `Unverified`:** o estado **não é engolido**. É registado, é exposto nos argumentos do
`StateChanged` (`CleanupVerified = false`), e a marca **fica pegajosa** — a próxima compensação (num
episódio seguinte ou no `Release`) volta a tentar. Isso dá um ponto de retry natural **sem criar
mecanismo novo nem ciclo oculto**. É também a resposta honesta ao Prism: se não conseguimos confirmar
que o ícone saiu, o sistema **diz que não conseguiu** em vez de afirmar limpeza que não tem.

**As três provas exigidas:**

| Caso | O que tem de ficar provado |
|---|---|
| **`NIM_ADD`/`NIM_SETVERSION` tardios** | quem emitiu revalida dentro do gate, perde o CAS, compensa, e o resultado é `Deleted`: **sem ícone e sem callback após `Lost`** |
| **`Release` concorrente** | o `Release` vence ou perde o CAS, mas a compensação é idempotente: **exatamente um `NIM_DELETE` eficaz**, sem dupla limpeza reportada como erro |
| **`NIM_DELETE` falhado** | 3 tentativas, depois `Unverified` **reportado** em `CleanupVerified=false`, marca pegajosa, e o `Release` posterior volta a tentar |

## 4 — R5′: o gate, com o alcance certo *(refinamento do ALTA 3)*

O gate serializa **a sequência de chamadas nativas e a compensação**. Não serializa a decisão terminal.

```csharp
lock (_nativeGate)
{
    if (!StillCurrent(episodeId)) return;       // barreira antes; lê o slot lock-free
    _effect = ShellEffect.MayExist;
    var ok = native.Add(...);                   // síncrona; SEM await dentro do gate
    if (!TryTerminalize(episodeId, ok ? Outcome.Available : Outcome.AttemptFailed))
    {
        Compensate();                           // perdeu o CAS: o efeito acabado de criar é revertido AQUI
        return;
    }
    // venceu o CAS: só então o sucesso conta
}
// os atrasos do orçamento A são aguardados FORA do gate
```

**O que R5′ afirma, e é mais honesto do que o que a revisão 6 afirmava.** Impedir *totalmente* que uma
chamada obsoleta seja emitida exigiria segurar a decisão terminal dentro do gate — que é precisamente o
que partiu o ALTA-1. Então: **a chamada pode ser emitida, mas não pode sobreviver.** Quem a emitiu é
quem revalida e compensa **antes de largar o gate**, e **ninguém consegue observar o shell nesse
intervalo sem adquirir o gate**. O efeito líquido para qualquer outro observador é o mesmo que se a
chamada nunca tivesse acontecido.

**Custo, com número medido:** o gate é segurado durante uma chamada cujo pior caso observado foi
**10,06 ms** (frio) e **< 4,7 ms** em regime, mais ≈2 ms de compensação no pior caso. O `Release`
espera isso, no máximo — e **o deadline já não espera nada**. Gate disjunto do caminho do flyout, que
continua a nunca ser mantido através de um `ShowAt`.

## 5 — Ciclo de vida do episódio (consolidado)

```
ADMISSÃO — bloco síncrono único na WndProc (R1)
  ├─ pré-condição: State == Available
  ├─ EpisodeFrequencyLimiter.TryBeginEpisode(now) → false ⇒ NÃO ACEITE; nada muda (CV-11)
  └─ true ⇒ atomicamente: episodeId · start · deadline := start + 1500 ms
            armar o temporizador one-shot (R3′)
            State := Recovering        ◄── Available deixa de ser publicado AQUI

EPISÓDIO
  debounce 250 ms (coalesce; não move o relógio, não cria geração)
  tentativas do orçamento A, cada uma na secção crítica de R5′
  desfecho: CAS one-shot entre Available | Lost(A) | Lost(B, expiração) | Releasing
  em qualquer Lost ou Release: compensação verificável (R4′)
```

Admissão **só** a partir de `Available`; de `Recovering` coalesce; de `Unavailable`/`Lost`/`Releasing`
não há admissão. **`Lost` tem duas causas legítimas** — falha nativa observada com o orçamento A
esgotado, e expiração do deadline. **Exceder o limite de frequência não emite `Lost`.**

## 6 — Testes que param dentro da chamada nativa

**O Atlas tem razão: os testes que planeei não detetariam nada disto.** Um teste que não consegue parar
o mundo **no meio de uma chamada nativa** não prova nem o timer bloqueado pelo gate, nem o `State`
atrasado, nem a falha de compensação.

**Requisito de harness:** o duplo de `INativeTrayRegistration` tem de **poder bloquear dentro de
`Add`, `SetVersion` e `Delete`**, sob controlo do teste (uma barreira que o teste abre quando quer). A
máquina de estados é conduzida com um dispatcher determinístico e `FakeTimeProvider`, sem thread de UI
real, para o estacionamento ser possível e sem deadlock do harness.

| Teste | Estaciona em | Prova |
|---|---|---|
| **T1 — independência do deadline** | dentro de `Add` | avançar o tempo para lá dos 1500 ms **enquanto está estacionado** ⇒ **`State` autoritativo já é `Lost`**, com a chamada nativa ainda por retornar e o gate ainda tomado |
| **T2 — `State` não depende do dispatcher** | dentro de `Add` | ler a propriedade `State` **antes** de libertar a barreira e **antes** de correr qualquer despacho ⇒ `Lost` |
| **T3 — sucesso tardio não ressuscita** | dentro de `Add` | libertar com `true` depois do prazo ⇒ CAS perdido ⇒ **sem `Available`**, e `NIM_DELETE` emitido |
| **T4 — `Release` concorrente** | dentro de `Add` | `Release` durante o estacionamento ⇒ exatamente **um** `NIM_DELETE` eficaz, sem dupla limpeza reportada como erro |
| **T5 — compensação falhada** | dentro de `Delete` | `Delete` devolve `false` 3× ⇒ `CleanupVerified = false` reportado, marca pegajosa, `Release` posterior volta a tentar |
| **T6 — timer não é bloqueado pelo gate** | dentro de `Add`, gate tomado | o callback do deadline **completa a decisão** sem adquirir o gate; a compensação é que fica à espera |

### Mutações

| # | Mutação na produção | Teste que TEM de falhar |
|---|---|---|
| 1 | sucesso repõe/limpa a janela de frequência | adversarial de sucesso-sempre ⇒ B converge para supressão |
| 2 | `Recovering` publicado depois do debounce | R1: nenhum instante com `Available` após aceitação |
| 3 | decisão terminal do timer movida para **dentro** do gate | **T1/T6** |
| 4 | expiração não produz `Lost` | deadline expirado ⇒ `Lost` sem falha nativa |
| 5 | verificação de geração pós-`await` removida | continuação obsoleta após `Release` não chama `NIM_ADD` |
| 6 | recuperação de `TaskbarCreated` removida | mensagem aceite ⇒ `Add` reinvocado e `Available` reemitido |
| 7 | encaminhamento de callback removido | callback v4 válido ⇒ `OpenRequested`/flyout exatamente uma vez |
| 8 | esgotamento de retries permanece em `BACKGROUND` | falhas observadas ⇒ `Lost` e o consumidor sai de background |
| 9 | cada validação da CV-6b, isoladamente | casos B e C falham cada um por si |
| 10 | `Compensate()` removido da terminalização | **T3** |
| 11 | secção crítica de R5′ removida | operação obsoleta observável fora do gate |
| 12 | publicação de `Available` deixa de exigir o CAS | **T3** |
| 13 | `NIM_DELETE == false` volta a ser ignorado | **T5** |

**Nada entregue só com suite verde** — a evidência de mutação acompanha a entrega (**CV-12**).

## 7 — Contrato público para a S2

```csharp
public enum TrayAffordanceState { Unavailable = 0, Available = 1, Recovering = 2, Lost = 3 }

public interface ITrayAffordance
{
    TrayAffordanceState State { get; }                                 // autoritativo; muda no prazo
    event EventHandler<TrayAffordanceChangedEventArgs> StateChanged;   // notificação, na UI
    Task<TrayAffordanceState> EstablishAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);            // ÚNICA operação terminal
}
```

`TrayAffordanceChangedEventArgs` transporta **`CleanupVerified`** (R4′). `Available` ⇒ `BACKGROUND`
legítimo · `Recovering` ⇒ segurar, não degradar · `Lost` ⇒ degradação obrigatória · `Unavailable` no
arranque `--background` ⇒ degradação obrigatória. Menu inalterado: Abrir o ServerAlyzer · Modo compacto
· Atualizar todos · Definições · Sair do ServerAlyzer.

## 8 — Condições do Vigil

**CV-1, CV-2/2b, CV-6b, CV-9, CV-13, CV-14 — FECHADAS.** **CV-7** e **CV-8** — medidas
(entrega na topologia do desenho; frio ~10,06 ms, steady/churn < 4,7 ms, limiar de 16,7 ms; **não são
garantia de desempenho do Windows**). **CV-10 e CV-11 — FECHADAS pelo Vigil**, e mantidas no desenho:

- **CV-10:** adversarial ~5 ciclos/60 s ≈ 18,5 ms ≈ 0,031 % · critério ≤1 % ≈ ≤600 ms/60 s · folga
  ~7× em episódios (5 admitidos contra ~37 para violar). **Reabrem a CV-8:** subir o teto de B ·
  encurtar a janela de B · reduzir o debounce · aumentar as tentativas de A · permitir mais do que um
  episódio em voo.
- **CV-11 (LOW, aceite e escrito):** um atacante esgota B com 5 broadcasts forjados; se um reinício
  legítimo do Explorer cair nessa janela, a mensagem verdadeira não é admitida e publica-se `Available`
  sobre um ícone morto até a janela deslizar (≤60 s). O atacante escolhe o instante mas **não provoca a
  perda** — só atrasa a deteção de uma perda já ocorrida. Ligado ao item **Q** da matriz.

**CV-12 — única aberta; fecha com a evidência de mutação na entrega.**

## 9 — `FORCED-TERMINATION TRAY CLEANUP` (S6)

**`EMPIRICAL QA ACCEPTANCE WINDOW` = 120 s, fixada a priori.** `TerminateProcess` → 120 s sem interação
→ **uma** passagem do rato sobre a área de notificação → registar. Órfão transitório aceitável; **órfão
persistente = FAIL**; obsoleto não interativo; lançamento seguinte cria **exatamente um** ícone; sem
duplicados; sem consola/WER. Não se exige `NIM_DELETE` do processo morto.

## 10 — Veredictos

**Atlas — PENDENTE.** O pedido: **R3′** (a decisão terminal acontece no prazo, fora do gate, e nenhuma
chamada nativa bloqueada a atrasa), **R4′** (a compensação é verificável e a falha é observável), e
**R5′** (uma chamada obsoleta pode ser emitida mas não sobrevive, e ninguém a observa sem o gate). Mais
os testes T1–T6, que estacionam dentro da chamada nativa.

**Prism — PASS.** A ressalva do ícone vivo ao lado da InfoBar fecha por R4′, e `CleanupVerified` dá-lhe
o sinal para o caso em que a limpeza não pôde ser confirmada.

**Vigil — PENDENTE apenas na CV-12** (evidência de mutação na entrega).

## 11 — Matriz de plataforma real

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
| P | 1500 ms é operacionalmente adequado num reinício real | `NOT_RUN` — **autorização humana** |
| Q | CV-11 — reinício do Explorer com o orçamento B esgotado | `NOT_RUN` — **autorização humana** |
| **R** | **`CleanupVerified=false` num shell real** — a compensação alguma vez falha? | `NOT_RUN` |

**O Explorer não é reiniciado nesta volta.**
