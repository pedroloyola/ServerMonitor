# M13 S2-T — ARCHITECTURE REVIEW 6 (efeito nativo e serialização)

**Autor:** Relay (platform-infra), implementer da S2-T / dono do Windows Shell.
**Branch:** `agent/m13-s2t-tray`, base `221eda4`. **DESENHO. Sem implementação.**

> ## O invariante de raiz PASSOU — registado
> **Atlas, textualmente:** *"Não encontrei novo intervalo pré-episódio nem qualquer `Available` falso
> após a admissão aceite."* Admissão, relógio e `Recovering` no mesmo bloco síncrono; debounce dentro
> do episódio. **A condição de paragem que o humano fixou não se verificou, e a classe de defeito
> perseguida durante quatro voltas está fechada.**
>
> **A revisão 6 é de outra classe: efeitos nativos colaterais e serialização.** Os três ALTA são de
> **mecanismo, não de número.** Nada aqui ajusta temporização.

---

## 0 — Invariantes

**R1 (fechado).** Entre a **aceitação** de um `TaskbarCreated` e a publicação de `Recovering` não há
`await`, `TryEnqueue`, temporizador, lock nem chamada nativa: admissão, transição e captura do relógio
são um bloco síncrono único na `WndProc`.

**R2 (fechado).** `Available` só é publicado pelo caminho que leu `true` de `NIM_ADD` **e**
`NIM_SETVERSION` **e** revalidou o deadline **depois** dessas chamadas.

**R3 (novo — §1).** O deadline é um **temporizador one-shot monotónico independente** que
**terminaliza o episódio atomicamente no instante do prazo**, corra ou não a continuação.

**R4 (novo — §2).** **Nenhum ícone e nenhum callback sobrevive a um episódio `Lost`.** Todo efeito
nativo produzido por um episódio é revertido por **limpeza compensatória idempotente** na
terminalização.

**R5 (novo — §3).** Validação → chamada nativa → terminalização obedecem a **ordenação serializada**:
não existe intercalação possível entre verificar e chamar.

---

## 1 — R3: um deadline que terminaliza sozinho *(ALTA 1)*

**O defeito.** Até à revisão 5 o árbitro era uma *validação executada quando a continuação retomava*.
Se o dispatcher atrasasse, o `Recovering` podia ultrapassar 1500 ms **sem** `Lost`: as verificações
impediam o sucesso tardio, mas ninguém comprometia o episódio no prazo.

**A correção.** Na admissão, além de `episodeId`/`start`/`deadline`, cria-se **um temporizador
one-shot** — `TimeProvider.CreateTimer(dueTime: 1500 ms, period: Infinite)` — cujo callback é
**independente da continuação**:

```csharp
// Callback do temporizador. Não depende de a continuação estar a correr, bloqueada, ou nunca retomar.
void OnDeadline(long episodeId)
{
    lock (_nativeGate)                                   // R5: mesma serialização das chamadas nativas
    {
        if (!TryTerminalize(episodeId, Outcome.LostByExpiry)) return;   // one-shot, idempotente
        Compensate();                                    // R4
    }
    PublishOnUi(TrayAffordanceState.Lost);
}
```

- **A decisão de terminalizar é atómica e imediata** (`Interlocked.CompareExchange` sobre o desfecho do
  episódio), **não depende de dispatch**. Quando a continuação eventualmente retomar, encontra o
  episódio já terminal e não faz nada.
- **A publicação** do estado é despachada para a thread de UI, porque a S2 vive lá. Se a UI estiver
  encravada, o evento chega tarde — mas **o episódio já está comprometido a `Lost`** e **nenhum
  `Available` é possível a partir daí**. Digo isto explicitamente para não ser lido como garantia de
  latência de notificação, que não é.
- **Teste determinístico com a continuação bloqueada:** com `FakeTimeProvider`, suspender a
  continuação numa barreira, avançar o tempo para lá do deadline, e observar que o episódio
  terminaliza em `Lost` **enquanto a continuação continua bloqueada**. É exatamente a prova que a
  revisão 5 não conseguia dar.

## 2 — R4: limpeza compensatória do efeito nativo *(ALTA 2)*

**O defeito, ao qual o Atlas e o Prism chegaram por ângulos opostos.** Descartar o resultado tardio
**não desfaz o efeito no shell**. Um `NIM_ADD` que devolve `true` depois do deadline deixa **um ícone
real, vivo, com o nosso HWND como destino de callbacks**, num episódio já `Lost`. E o único
`NIM_DELETE` do desenho vivia no `ReleaseAsync`, isto é, na saída verdadeira. **Consequência visível
(Prism):** janela degradada aberta, InfoBar a dizer que não conseguiu colocar ou manter o ícone, **e um
ícone a funcionar ao lado do relógio ao mesmo tempo**.

**A correção.**

```csharp
_shellMayHoldIcon = true;      // marcado ANTES de emitir o NIM_ADD, nunca depois
ok = native.Add(...);
```

- **A marca é posta antes da chamada, e a direção do erro é deliberada.** Se fosse posta depois, uma
  interrupção entre a chamada e a marca deixaria-nos a acreditar que não há nada no shell quando há.
  Marcando antes, o pior caso é emitir um `NIM_DELETE` redundante — que é inofensivo, porque
  `NIM_DELETE` sobre um ícone inexistente devolve `false` e não faz nada.
- **`Compensate()` é idempotente:** emite `NIM_DELETE` para `uID = 1` contra o nosso HWND, ignora o
  resultado, e é seguro chamar repetidamente.
- **Corre em toda terminalização para `Lost`**, por qualquer das duas causas, e no `Release`.
- **Um `NIM_ADD` que regresse tarde compensa-se a si próprio:** ainda dentro da secção crítica, se a
  revalidação disser que o episódio é terminal, o próprio caminho que acabou de criar o ícone emite o
  `NIM_DELETE`. É este passo que impede o ícone-fantasma que o Prism descreveu.
- **Não viola a posse terminal única.** `ReleaseAsync` continua a ser a **única operação terminal
  pública**; `Compensate()` é interno ao árbitro de episódio e não é API.
- **Belt-and-braces no encaminhamento:** em `Lost` e `Releasing`, mensagens de callback do tray são
  descartadas por default-deny. O ícone já não existe, mas o caminho não depende disso.

**Prova exigida:** nenhum ícone e nenhum callback sobrevive a um episódio `Lost` — mutação 10 da
secção 6.

## 3 — R5: ordenação serializada entre validação, chamada e terminalização *(ALTA 3)*

**O defeito.** A dominância do `Release` estava **afirmada**, não serializada. Entre o `StillValid` e a
chamada nativa havia uma janela em que o `Release` podia começar e uma operação obsoleta ainda chegar
ao shell.

**A correção — uma secção crítica que cobre verificar, chamar e interpretar:**

```csharp
lock (_nativeGate)                       // tomado também pelo timer de deadline e pelo Release
{
    if (!StillValid(episodeId)) return;  // BARREIRA ANTES
    _shellMayHoldIcon = true;
    var ok = native.Add(...);            // chamada síncrona, SEM await dentro do gate
    if (!StillValid(episodeId))          // BARREIRA DEPOIS: relê o tempo monotónico
    {
        Compensate();                    // R4 — o efeito acabado de produzir é revertido aqui
        return;
    }
    RecordAttempt(ok);
}
// os atrasos do orçamento A são aguardados FORA do gate
```

- **Nenhum `await` dentro do gate.** Só a chamada síncrona.
- **Terminalização (timer e `Release`) toma o mesmo gate** antes de virar o desfecho e antes de
  compensar. Logo, ou a operação nativa completa e observa o estado terminal **dentro** do gate, ou a
  terminalização acontece primeiro e a operação encontra terminal na barreira de entrada.
  **Não existe intercalação possível** — é isto que R5 afirma e o que a mutação 11 testa.
- **Custo do gate, com número medido:** segura-se durante uma chamada síncrona cujo pior caso observado
  foi **10,06 ms** (frio) e **< 4,7 ms** em regime. O `Release` pode esperar isso, no máximo. Sem
  aquisição aninhada e sem reentrância, logo sem deadlock; o callback do timer corre em thread do pool
  e espera de forma limitada.
- **Gate disjunto do flyout.** O `_nativeGate` **nunca** é mantido através de um `ShowAt`; a guarda de
  flyout da CV-9 é outro mecanismo e os dois caminhos não se cruzam.

## 4 — Ciclo de vida do episódio (consolidado)

```
ADMISSÃO — bloco síncrono único na WndProc (R1)
  ├─ pré-condição: State == Available
  ├─ EpisodeFrequencyLimiter.TryBeginEpisode(now) → false ⇒ NÃO ACEITE; nada muda (CV-11)
  └─ true ⇒ atomicamente, sem await no meio:
        episodeId := ++_generation · start := now · deadline := start + 1500 ms
        armar o temporizador one-shot do deadline (R3)
        State := Recovering            ◄── Available deixa de ser publicado AQUI

EPISÓDIO — dentro de Recovering, dentro do deadline
  debounce 250 ms (coalesce; não atrasa entrada, não move o relógio, não cria geração)
  tentativas do orçamento A, cada uma na secção crítica de R5
  desfecho terminal, one-shot: Available | Lost causa A | Lost causa B (expiração)
  em qualquer Lost: Compensate() (R4)
```

**Pré-condições de admissão, exaustivas.** Só a partir de `Available`. De `Recovering` coalesce-se; de
`Unavailable`, `Lost` e `Releasing` **não há admissão** — em `Lost` a sessão já está degradada e o
desenho aprovado proíbe oscilar de volta.

**`Lost` tem duas causas terminais legítimas:** **A** falha nativa observada com esgotamento do
orçamento de retry; **B** expiração do deadline, mesmo que a última chamada nativa não tenha falhado.
A regra de que **exceder o limite de frequência não emite `Lost`** mantém-se e não é generalizada.

**Todo estabelecimento de disponibilidade é um episódio** — o de arranque e o desencadeado por
broadcast usam o mesmo árbitro, para não haver um segundo caminho onde um `Available` falso se esconda.

## 5 — Condições do Vigil, nomeadas

**CV-1 — modelo de confiança da `WndProc` (FECHADA).** Default-deny · identidade da mensagem (o id é
seletor, nunca prova de origem) · `lParam` low word na lista fechada
`NIN_SELECT`/`NIN_KEYSELECT`/`WM_CONTEXTMENU`/`WM_LBUTTONDBLCLK` · high word `uID == 1` · `wParam` é
coordenada não confiável, só âncora, saneada, **fora de todos os monitores ⇒ DESCARTAR** · nenhuma
mensagem transporta ou desreferencia ponteiro · teto de impacto de uma forja = incómodo de UI, sem
chegar ao `RequestExit` sem clique real.

**CV-2/CV-2b — dois orçamentos independentes (FECHADA).**
**A — retry de falhas, dentro de um episódio:** 3 tentativas, atrasos 250 ms e 1000 ms (~1250 ms
programados), sob o deadline de 1500 ms; um sucesso pode repô-lo para um episódio futuro.
**B — frequência de admissões por broadcast:** janela deslizante monotónica, 5 por 60 s, conta
admissões independentemente do desfecho; **nada além do tempo o repõe**; não conta o episódio de
arranque. Broadcasts adicionais **coalescem no episódio em curso e não consomem B**.

```csharp
internal sealed class EpisodeFrequencyLimiter
{
    public bool TryBeginEpisode(long monotonicTimestamp);   // único método
}
```

Não há `Reset()`, `OnSuccess()`, nem campo partilhado: **não pode ser reposto por sucesso porque nada
lhe consegue comunicar um sucesso**. **Exceder B não emite `Lost`** — a mensagem não é aceite.

**CV-6b — validação não conjuntiva (FECHADA).** Quatro casos independentes, restantes campos válidos;
**B e C obrigatórios**; mutação de **cada** validação isolada tem de falhar.

| | callback | `uID` | resultado |
|---|---|---|---|
| A | válido | válido | **aceite** |
| **B** | inválido | válido | **ignorado** |
| **C** | válido | inválido | **ignorado** |
| D | inválido | inválido | ignorado |

**CV-7 — topologia de thread (MEDIDA, PASSA).** `TaskbarCreated` recebido num HWND **da thread de UI**,
com `WS_EX_TOOLWINDOW`, top-level, sem dono, nunca mostrado, em processo empacotado **headless**.
Emissor: o `PostMessage(HWND_BROADCAST, …)` desta sessão, **não o Explorer**.

**CV-8 — custo nativo na thread de UI (MEDIDA, aceitável).** Frio ~10,06 ms; steady/churn máx
< 4,7 ms; limiar declarado de um frame a 60 Hz = 16,7 ms. A chamada fica na thread de UI. **Estas
medições não são garantia de desempenho do Windows.**

**CV-9 — reentrância com flyout aberto (FECHADA).** Com o flyout aberto, `WM_CONTEXTMENU` adicional ou
forjada é descartada por default-deny: sem segundo flyout, sem reposicionamento, sem mutação do estado
de episódio, sem alteração de visibilidade da janela auxiliar.

**CV-10 — acoplamento entre o limitador e o custo de UI: o número, o critério, e o que reabre a CV-8.**

| | |
|---|---|
| Custo bruto medido | 100 ciclos add+delete ≈ **372 ms** de thread de UI (~3,7 ms/ciclo) |
| **Caso adversarial sob o desenho** | **~5 ciclos/60 s ≈ 18,5 ms ≈ 0,031 % da thread de UI** |
| **Critério de aceitação** | **≤ 1 % do tempo de parede**, isto é **≤ 600 ms por 60 s** |
| **Folga** | **~7×** — em episódios: 5 admitidos contra ~37 para violar |
| **Ponto de violação** | **~37 episódios/60 s** (custeando o episódio no seu pior caso, ~16 ms) |

> **Alterações que REABREM a CV-8 e obrigam a re-medir antes de serem aceites:**
> **(a)** subir o teto de B · **(b)** encurtar a janela de B · **(c)** reduzir o debounce ·
> **(d)** aumentar as tentativas do orçamento A · **(e)** permitir mais do que **um** episódio em voo.
> Qualquer uma delas move o consumo de UI na direção do critério de 1 %.

**CV-11 — residual aceite, reposto no desenho (LOW).** A revisão 5 removeu este trade-off ao mudar o
limitador para governar a admissão; **o residual continua real e volta a ficar escrito, porque tinha
sido aceite por estar escrito**:

> Um atacante esgota o orçamento B com **5 broadcasts forjados**. Se um **reinício legítimo do
> Explorer** cair dentro dessa janela, a mensagem verdadeira **não é admitida**, e a S2-T publica
> `Available` **sobre um ícone morto** até a janela deslizar — até **60 s**.

**Variante adversarial:** o atacante pode escolher o instante, mas **não pode provocar a perda** — só
pode atrasar a deteção de uma perda que já aconteceu por outra causa. Não há travessia de privilégio e
não há caminho para `Lost` forçado (isso é a CV-2, fechada). **Aceite como LOW**, e é o preço
deliberado da correção estrutural da revisão 5: a alternativa — admitir sempre e limitar o trabalho
dentro do episódio — reabriria a CV-2 permitindo que input não autenticado comandasse a degradação da
sessão. **Ligado ao QA de reinício real:** item **Q** da matriz — reinício do Explorer **com o
orçamento B esgotado**, se reproduzível em segurança.

**CV-12 — obrigação de entrega.** Fecha quando a evidência de mutação acompanhar a entrega (secção 6).

**CV-13 e CV-14 — FECHADAS** pelo Vigil nesta volta.

## 6 — Evidência de mutação

Seam no limite nativo (`INativeTrayRegistration`); **máquina de estados sob teste é a de produção**;
tempo monotónico determinístico por `TimeProvider`/`FakeTimeProvider`.

| # | Mutação na produção | Teste que TEM de falhar |
|---|---|---|
| 1 | um sucesso repõe/limpa a janela de frequência | adversarial de sucesso-sempre ⇒ B converge para supressão |
| 2 | `Recovering` publicado depois do debounce em vez de na admissão | R1: nenhum instante com `Available` após aceitação |
| 3 | verificação de deadline pós-chamada nativa removida | sucesso tardio não publica `Available`; idem `NIM_SETVERSION` |
| 4 | expiração não produz `Lost` | deadline expirado ⇒ `Lost` sem falha nativa |
| 5 | verificação de geração pós-`await` removida | continuação obsoleta após `Release` não chama `NIM_ADD` |
| 6 | recuperação de `TaskbarCreated` removida | mensagem aceite ⇒ `Add` reinvocado e `Available` reemitido |
| 7 | encaminhamento de callback removido | callback v4 válido ⇒ `OpenRequested`/flyout exatamente uma vez |
| 8 | esgotamento de retries permanece em `BACKGROUND` | falhas observadas ⇒ `Lost` e o consumidor sai de background |
| 9 | cada validação da CV-6b, isoladamente | casos B e C falham cada um por si |
| **10** | **`Compensate()` removido da terminalização (R4)** | **`NIM_ADD` tardio bem-sucedido ⇒ `NIM_DELETE` emitido; nenhum ícone nem callback sobrevive ao `Lost`** |
| **11** | **secção crítica de R5 removida** | **`Release` durante a janela validação→chamada ⇒ nenhuma operação obsoleta chega ao shell** |
| **12** | **temporizador one-shot substituído por validação no retomar (R3)** | **com a continuação bloqueada numa barreira e o tempo avançado, o episódio terminaliza em `Lost`** |

Mais: `Unavailable` no ordinal 0 · produtor único de `Available` · `szTip`/`hIcon` estáticos ·
exatamente 3 tentativas · admissão impossível de `Lost`/`Releasing`/`Unavailable` · `Release`
idempotente · nenhum evento publicado após o release terminal · `Compensate()` idempotente sob
invocação repetida. **Nada entregue só com suite verde.**

## 7 — Contrato público para a S2 (inalterado)

```csharp
public enum TrayAffordanceState { Unavailable = 0, Available = 1, Recovering = 2, Lost = 3 }

public interface ITrayAffordance
{
    TrayAffordanceState State { get; }
    event EventHandler<TrayAffordanceChangedEventArgs> StateChanged;   // dispatcher de UI
    Task<TrayAffordanceState> EstablishAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);            // ÚNICA operação terminal
}
```

`Available` ⇒ `BACKGROUND` legítimo · `Recovering` ⇒ segurar, não degradar, não tratar como disponível
· `Lost` ⇒ degradação obrigatória · `Unavailable` no arranque `--background` ⇒ degradação obrigatória.
Menu preservado: Abrir o ServerAlyzer · Modo compacto · Atualizar todos · Definições · Sair do
ServerAlyzer.

## 8 — `FORCED-TERMINATION TRAY CLEANUP` (S6)

**`EMPIRICAL QA ACCEPTANCE WINDOW` = 120 s, fixada a priori.** `TerminateProcess` → 120 s sem
interação → **uma** passagem do rato sobre a área de notificação → registar. Órfão transitório
aceitável; **órfão persistente = FAIL**; obsoleto não interativo; lançamento seguinte cria **exatamente
um** ícone; sem duplicados; sem consola/WER. Não se exige `NIM_DELETE` do processo morto — o kernel
reclama os handles USER/GDI.

## 9 — Veredictos

**Atlas — PENDENTE.** Verificar **R3, R4 e R5**: o temporizador terminaliza sozinho com a continuação
bloqueada; nenhum efeito nativo sobrevive a um `Lost`; e a secção crítica torna impossível intercalar
`Release` entre a validação e a chamada.

**Prism — PASS COM RESSALVAS, endereçadas.** A ressalva do ícone vivo ao lado da InfoBar de degradação
está fechada por R4. Menu e âmbito de tema inalterados.

**Vigil — PENDENTE.** CV-10 completa com número, critério e lista de reabertura; CV-11 reposta com a
variante adversarial e ligada ao item Q; CV-12 fecha na entrega.

## 10 — Matriz de plataforma real

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
| **Q** | **CV-11 — reinício do Explorer com o orçamento B esgotado** | `NOT_RUN` — **autorização humana** |

**Nota para quem correr P e Q.** Se a recuperação real exceder ocasionalmente os 1500 ms, isso **não
justifica estender em silêncio a incerteza de `BACKGROUND`**: afina-se primeiro o calendário de
retries dentro do mesmo orçamento. E qualquer alteração da lista de reabertura da CV-10 obriga a
re-medir a CV-8 antes de ser aceite.

**O Explorer não é reiniciado nesta volta.**
