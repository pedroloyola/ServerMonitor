# M13 S2-T — ARCHITECTURE REVIEW 5 (invariante de raiz)

**Autor:** Relay (platform-infra), implementer da S2-T / dono do Windows Shell.
**Branch:** `agent/m13-s2t-tray`, base `221eda4`. **DESENHO. Sem implementação.**
**Revisão 5** não ajusta temporização. Reescreve a máquina de estados para que o intervalo de
`Available` sem prova **não possa existir por construção**.

---

## 0 — O INVARIANTE DE RAIZ

> **R1.** Entre a **aceitação** de um `TaskbarCreated` e a publicação de `Recovering` **não existe
> nenhuma fronteira em que o controlo saia do bloco**: nenhum `await`, nenhum `TryEnqueue`, nenhum
> temporizador, nenhuma aquisição de lock, nenhuma chamada nativa. A admissão, a transição de estado e
> a captura do relógio são **um único bloco síncrono na `WndProc`, na thread de UI**.
>
> **R2.** `Available` só é publicado por **um** caminho: o que leu `true` de `NIM_ADD` **e** de
> `NIM_SETVERSION`, **e** revalidou o deadline **depois** dessas chamadas.
>
> **Corolário (é isto que o Atlas deve verificar):** para qualquer mensagem aceite no instante `t`,
> não existe **nenhum** escalonamento do dispatcher que produza `State == Available` para um instante
> `> t` antes do fim do episódio. Não é um intervalo pequeno — **não há instante nenhum**, porque
> entre `t` e a transição não há ponto onde o controlo possa ser suspenso.

As três revisões anteriores estreitaram o intervalo. Esta remove o ponto de suspensão que o criava.

---

## 1 — Contradição que herdei, e como a resolvo *(§3 do texto autoritativo)*

O desenho anterior exigia `Lost` na expiração do deadline **e** proibia `Lost` sem falha nativa
observada. Eram duas regras corretas de revisores diferentes, e colidiam. Resolução adotada:

**`Lost` tem exatamente DUAS causas terminais legítimas:**

| Causa | Origem |
|---|---|
| **A** — falha nativa **observada** de `NIM_ADD`/`NIM_SETVERSION` com esgotamento do orçamento de retry | o shell recusou |
| **B** — **expiração do deadline monotónico global do episódio** | o tempo acabou sem prova positiva |

**Deadline expirado emite `Lost` mesmo que a última chamada nativa não tenha devolvido falha.**

A regra do Vigil — *"o esgotamento do limite de frequência não emite `Lost`"* — **mantém-se verdadeira
e não é generalizada** para "só falha nativa emite `Lost`".

## 2 — Um problema na §4 que reporto em vez de transcrever

A §4 diz que, se o rate limiting impedir trabalho nativo imediato, se permanece em `Recovering`, o
deadline continua, e a expiração produz `Lost`. **Lida à letra, essa frase reabre a CV-2 numa forma
nova:** um processo local qualquer faz broadcast → episódio admitido → o limitador esfomeia o trabalho
nativo → o deadline expira → **`Lost` → a S2 degrada a sessão**. Ou seja, **input não autenticado
voltaria a comandar a transição de sessão**, que é precisamente o que a CV-2 existe para impedir. É a
mesma classe de colisão que a §3 já reconheceu ter acontecido uma vez.

**Resolução estrutural, e é a única leitura que satisfaz Atlas e Vigil ao mesmo tempo:**

> **O limitador de frequência governa a ADMISSÃO de episódios novos. NUNCA esfomeia um episódio já
> admitido.**

- Mensagem que o limitador **suprime** → **não é aceite** → não há admissão, não há `Recovering`, não
  há relógio, não há `Lost`. É **exatamente equivalente a uma mensagem que nunca chegou** — a resposta
  correta para input não autenticado, e mantém intacta a invariante do Vigil.
- Mensagem **aceite** → o episódio tem **garantido** o seu trabalho nativo dentro dos 1500 ms. O
  limitador não volta a intervir dentro dele. **A situação de esfomeamento da §4 não pode ocorrer.**
- Logo `Lost` só é alcançável a partir de um episódio **admitido**, e a admissão está limitada pelo
  teto. Um atacante dentro do teto força no máximo 5 episódios/60 s que, contra um shell saudável,
  sucedem em ~3 ms e restauram `Available` muito antes do deadline. **`Lost` continua a exigir uma
  perda real.**

E a exigência da §4 que **não** é enfraquecida: *"nunca pode permitir que a prova `Available` antiga
sobreviva a um broadcast recém-aceite"* — satisfeita por R1, porque a aceitação e a saída de
`Available` são o mesmo bloco síncrono.

## 3 — Ciclo de vida do episódio

Um **episódio** é a única unidade de recuperação. **Todo** estabelecimento de disponibilidade positiva
é um episódio — o de arranque (`EstablishAsync`) e o desencadeado por broadcast usam **o mesmo
árbitro**, para não existir um segundo caminho onde um `Available` falso se possa esconder.

```
ADMISSÃO (bloco síncrono único na WndProc — R1)
  ├─ pré-condição: State == Available            (só se invalida uma prova que existe)
  ├─ EpisodeFrequencyLimiter.TryBeginEpisode(now) → false ⇒ NÃO ACEITE, retorna, nada muda
  └─ true ⇒ ATOMICAMENTE, sem qualquer await no meio:
        episodeId  := ++_generation
        start      := TimeProvider.GetTimestamp()
        deadline   := start + 1500 ms
        State      := Recovering            ◄── Available deixa de ser publicado AQUI
     ...só depois disto o controlo sai do bloco.

EPISÓDIO (dentro de Recovering, dentro do deadline)
  debounce 250 ms → tentativas do orçamento A → cada uma com revalidação (secção 5)
  desfecho: Available (prova positiva)  |  Lost causa A  |  Lost causa B
```

**Pré-condições de admissão, exaustivas.** Admite-se **só** a partir de `Available`. De `Recovering`
coalesce-se no episódio em curso; de `Unavailable`, `Lost` e `Releasing` **não há admissão** — em
`Lost` a sessão já está degradada e o desenho aprovado proíbe oscilar de volta, e só uma transição de
ciclo de vida revista em separado pode abrir uma nova sessão de tray.

## 4 — O debounce, redefinido

O debounce de 250 ms fica **DENTRO** do `Recovering` e **DENTRO** dos 1500 ms, e serve **apenas** para
coalescer mensagens adicionais. Explicitamente, **não**:

- atrasa a entrada em `Recovering`;
- atrasa o início do deadline;
- preserva `Available`;
- reinicia o `startTimestamp`;
- estende o deadline;
- cria geração nova.

Broadcasts adicionais durante o episódio **juntam-se ao episódio existente**. A incerteza nominal é
**1500 ms e não 1750 ms**, porque o relógio arranca na admissão e não depois do debounce.

## 5 — Árbitro monotónico não reiniciável *(§5 · §6 · §7)*

O deadline pertence a **um** árbitro de episódio, monotónico e não reiniciável. **Não pode depender
apenas** de verificações antes dos `await`, de aritmética de atrasos, nem do momento em que o
dispatcher decide retomar.

```csharp
// Falha se a geração mudou, se há release/terminal, ou se o deadline passou.
bool StillValid(long episodeId);
```

- **Depois de CADA fronteira assíncrona:** revalidar geração → estado terminal/release → deadline.
- **Depois de CADA chamada nativa síncrona ao shell:** **reler o tempo monotónico e revalidar o
  deadline ANTES de interpretar o sucesso como `Available`.** Uma chamada síncrona pode atravessar o
  prazo mesmo tendo começado dentro dele.
- `NIM_ADD` **só** pode ser invocado a partir de um caminho que chamou `StillValid` imediatamente
  antes, **sem qualquer `await` entre a verificação e a chamada**.

### A race que isto existe para matar

```
t0        NIM_ADD começa          (dentro do deadline)
t0+Δ      deadline expira
t0+Δ+ε    NIM_ADD devolve TRUE    (depois do deadline)
```

**O sucesso é descartado como obsoleto.** Nunca vira `Available`. O desfecho terminal do episódio
mantém-se **`Lost` por expiração**. Idêntico para `NIM_SETVERSION`. É R2 a operar: a revalidação do
deadline acontece **depois** da chamada e **antes** da interpretação do resultado.

### Terminalidade da expiração

Comprometido o episódio a `Lost` por expiração: nenhum retry continua · nenhuma continuação de
debounce atrasada corre registo · **nenhum resultado nativo bem-sucedido ressuscita `Available`** ·
`TaskbarCreated` posteriores **não reabrem o mesmo episódio**. `Release`/`EXITING` continua a dominar
o `Lost` e toda continuação de recuperação.

## 6 — Contrato público para a S2

```csharp
public enum TrayAffordanceState
{
    Unavailable = 0,   // ordinal 0 é o valor seguro de qualquer campo ou mock por inicializar
    Available   = 1,
    Recovering  = 2,
    Lost        = 3
}

public interface ITrayAffordance
{
    TrayAffordanceState State { get; }
    event EventHandler<TrayAffordanceChangedEventArgs> StateChanged;   // dispatcher de UI
    Task<TrayAffordanceState> EstablishAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);            // ÚNICA operação terminal
}
```

| Estado | A S2 faz |
|---|---|
| `Available` | `BACKGROUND` legítimo |
| `Recovering` | **segura**; não degrada e não trata como disponível. Limitado pelo deadline de 1500 ms |
| `Lost` | degradação **obrigatória** (Definições → Segundo plano · InfoBar Warning · `FOREGROUND` degradado · saída verdadeira no resto da sessão) |
| `Unavailable` | no arranque `--background`, degradação obrigatória; nunca permanecer headless |

`szTip`/`hIcon` estáticos; nenhum dado de snapshot atravessa o caminho do shell. **Posse terminal
única:** só `ReleaseAsync`; qualquer `NIM_DELETE` síncrono é detalhe interno dela.

## 7 — Os dois orçamentos, independentes

**A — retry de falhas, dentro de um episódio.** 3 tentativas (inicial + 2), atrasos 250 ms e 1000 ms
(~1250 ms programados), sob o deadline de 1500 ms. Um sucesso **pode** repô-lo para um episódio
futuro. Esgotado com falha observada → `Lost` causa A.

**B — frequência de episódios admitidos por broadcast.** Janela deslizante monotónica, 5 por 60 s,
a contar admissões independentemente do desfecho. **Nada além da passagem do tempo o repõe.** Não
conta o episódio de arranque, que não é desencadeado por input externo.

```csharp
// Não conhece sucesso, falha, nem número de tentativas. Não existe API para lho dizer.
internal sealed class EpisodeFrequencyLimiter
{
    public bool TryBeginEpisode(long monotonicTimestamp);   // único método
}
```

**Não pode ser reposto por sucesso porque nada no programa lhe consegue comunicar um sucesso.** Sem
`Reset()`, sem `OnSuccess()`, sem campo partilhado. **Nenhum caminho em que o desfecho de A escreva em
B** — verificável por inspeção da superfície do tipo. **Exceder B não emite `Lost`**: a mensagem
simplesmente não é aceite.

**Deadline (1500 ms) e janela de frequência são coisas distintas:** o primeiro limita *quanto dura um
episódio*, a segunda *quantos episódios começam*. O sucesso pode afetar A; **não pode apagar o
histórico de B**.

> **Temporização, para o registo e sem reabrir:** 1500 ms = ~1250 ms de atrasos programados + ~250 ms
> de folga de execução/agendamento. **A folga é decisão nossa, não garantia de plataforma do Windows.**

## 8 — Condições do Vigil

**CV-1, sete pontos.** Default-deny · identidade da mensagem (o nosso `uCallbackMessage` ou o
`TaskbarCreated` registado; o id é seletor, nunca prova de origem) · `lParam` low word na lista fechada
`NIN_SELECT`/`NIN_KEYSELECT`/`WM_CONTEXTMENU`/`WM_LBUTTONDBLCLK` · `lParam` high word `uID == 1` ·
`wParam` é coordenada não confiável, **só** âncora, nunca índice/offset/dimensão, **saneada** — âncora
fora de todos os monitores ⇒ **DESCARTAR** · nenhuma mensagem transporta ou desreferencia ponteiro ·
teto de impacto de uma forja = incómodo de UI, sem chegar ao `RequestExit` sem clique real.

**CV-6b — validação não conjuntiva.** Quatro casos independentes, todos os campos não relacionados
válidos. **B e C são obrigatórios**; um teste só conjuntivo não é suficiente, e a mutação de **qualquer
uma** das validações tem de falhar testes.

| | callback | `uID` | resultado |
|---|---|---|---|
| A | válido | válido | **aceite** |
| **B** | inválido | válido | **ignorado** |
| **C** | válido | inválido | **ignorado** |
| D | inválido | inválido | ignorado |

**CV-7 — medido, passa.** `TaskbarCreated` recebido num HWND **da thread de UI**, com
`WS_EX_TOOLWINDOW`, top-level, sem dono, nunca mostrado, em processo empacotado **headless**. A
topologia mantém-se e a CV-1 conserva a aprovação na topologia em que foi dada. **Emissor: o
`PostMessage(HWND_BROADCAST, …)` desta sessão, não o Explorer** — o originado pelo Explorer continua
`NOT_RUN`.

**CV-8 — medido; a chamada fica na thread de UI.** Frio ~10,06 ms; steady/churn máx < 4,7 ms; limiar
declarado de um frame a 60 Hz = 16,7 ms. **Estas medições não são garantia de desempenho do Windows.**
**O limitador de frequência mantém-se**, porque 100 ciclos add+delete custaram ≈372 ms de thread de UI:
o custo bruto por si só não seria seguro; são os limites do desenho que o tornam seguro. **Pior caso
com o shell a reiniciar continua `NOT_RUN`.** A troca condicional continua barata se algum dia for
precisa: `INativeTrayRegistration` é o único ponto de contacto e não toca na máquina de estados.

**CV-9 — reentrância com flyout aberto.** Guarda `_flyoutOpen` na thread de UI: com o flyout aberto,
`WM_CONTEXTMENU` adicional ou forjada é descartada por default-deny — **sem segundo flyout, sem
reposicionamento, sem mutação do estado de episódio, sem alteração de visibilidade da janela
auxiliar**. A janela auxiliar é escondida no `Closed` do flyout, e isso é asserido.

## 9 — Evidência de mutação

Seam no limite nativo (`INativeTrayRegistration`); **máquina de estados sob teste é a de produção**;
tempo monotónico determinístico por `TimeProvider`.

| # | Mutação na produção | Teste que TEM de falhar |
|---|---|---|
| 1 | um sucesso repõe/limpa a janela de frequência | **adversarial de sucesso-sempre**: `Add` sempre `true`, broadcasts em ciclo ⇒ B converge para supressão |
| 2 | `Recovering` publicado **depois** do debounce em vez de na admissão | **R1**: não existe instante entre a aceitação e `Recovering` com `Available` publicado |
| 3 | verificação de deadline **pós-chamada nativa** removida | **§6**: `NIM_ADD` que devolve `true` depois do deadline **não** publica `Available`; desfecho fica `Lost`. Idem `NIM_SETVERSION` |
| 4 | expiração do deadline não produz `Lost` | deadline expirado ⇒ `Lost` mesmo sem falha nativa |
| 5 | verificação de geração pós-`await` removida | continuação obsoleta após `Release` não chama `NIM_ADD` |
| 6 | recuperação de `TaskbarCreated` removida | mensagem aceite ⇒ `Add` reinvocado e `Available` reemitido |
| 7 | encaminhamento de callback removido | callback v4 válido ⇒ `OpenRequested`/flyout exatamente uma vez |
| 8 | esgotamento de retries permanece em `BACKGROUND` | falhas observadas ⇒ `Lost` e o consumidor sai de background |
| 9 | **cada uma** das validações da CV-6b, individualmente | os casos B e C falham isoladamente |

Mais: `Unavailable` no ordinal 0 · produtor único de `Available` · `szTip`/`hIcon` estáticos ·
exatamente 3 tentativas · admissão impossível a partir de `Lost`/`Releasing`/`Unavailable` ·
`Release` idempotente · nenhum evento publicado após o release terminal · `NIM_DELETE` exatamente uma
vez. **Nada entregue só com suite verde.**

## 10 — `FORCED-TERMINATION TRAY CLEANUP` (S6)

**`EMPIRICAL QA ACCEPTANCE WINDOW` = 120 s, fixada aqui, antes da corrida.** O Windows não documenta
prazo de limpeza; a janela é empírica e fixada a priori para não poder ser escolhida retroativamente.
Procedimento: `TerminateProcess` pelo watchdog → 120 s sem interação → **uma** passagem do rato sobre a
área de notificação → registar. Órfão transitório aceitável; **órfão persistente = FAIL**; obsoleto não
interativo; lançamento seguinte cria **exatamente um** ícone; sem duplicados; sem consola/WER. **Não se
exige `NIM_DELETE` do processo morto** — o kernel reclama os handles USER/GDI na morte do processo.

## 11 — Veredictos

**Atlas — PENDENTE.** O pedido é direto: **verificar R1 e R2 da secção 0**, e o corolário de que não
existe escalonamento do dispatcher que produza `Available` entre a aceitação e o fim do episódio. Mais:
a resolução da §4 que proponho na secção 2; a terminalidade da expiração; a revalidação pós-chamada
nativa. **Se encontrares outro intervalo pré-episódio ou de `Available` falso, devolve como falha de
raiz da máquina de estados — não como número a ajustar.**

**Prism — PASS registado.** Menu preservado exatamente: Abrir o ServerAlyzer · Modo compacto ·
Atualizar todos · Definições · Sair do ServerAlyzer. Tema multi-raiz dentro do processo em âmbito;
persistência entre arranques fora de âmbito (THEME-1).

**Vigil — PENDENTE.** Duas perguntas: (i) a secção 2 — concorda que o limitador tem de governar a
admissão e nunca esfomear um episódio admitido, e que a leitura literal da §4 reabriria a CV-2? (ii) o
adversarial de sucesso-sempre continua a convergir para supressão nesta máquina de estados?

## 12 — `NOT_RUN` de plataforma real

| | Caso | Estado |
|---|---|---|
| A | registo inicial no shell real | `NOT_RUN` |
| B | tray headless real | `NOT_RUN` |
| C | menu/flyout: teclado, tema, CV-9 com o menu aberto | `NOT_RUN` |
| D | reinício real do Explorer em `FOREGROUND` | `NOT_RUN` — **autorização humana** |
| E | reinício real em `BACKGROUND`/headless | `NOT_RUN` — **autorização humana** |
| F | `TaskbarCreated` entregue **pelo próprio Explorer** | `NOT_RUN` — não promovível do broadcast sintético |
| G | restauro bem-sucedido do ícone | `NOT_RUN` |
| H | sem ícone duplicado | `NOT_RUN` |
| I | sem janela auxiliar visível nem botão na barra de tarefas | `NOT_RUN` |
| J | degradação por falha de registo forçada | `NOT_RUN` |
| K | sem consola / sem WER | `NOT_RUN` |
| L | `FORCED-TERMINATION TRAY CLEANUP` (secção 10) | `NOT_RUN` |
| M | CV-7 — entrega na topologia do desenho | **MEDIDO · PASSA** (emissor sintético) |
| N | CV-8 — custo nativo na thread de UI | **MEDIDO · aceitável** |
| O | CV-8 pior caso com o shell a reiniciar | `NOT_RUN` — **autorização humana** |
| **P** | **1500 ms é operacionalmente adequado num reinício real** | `NOT_RUN` — **autorização humana** |

**Nota de desenho para quem correr o item P.** Se a recuperação real exceder ocasionalmente os
1500 ms, isso **não justifica estender em silêncio a incerteza de `BACKGROUND`**. Avaliar primeiro a
afinação do **calendário de retries dentro do mesmo orçamento**, preservando um contrato estritamente
limitado. Alargar o deadline é a última opção e nunca em silêncio: aumenta a janela em que a app
monitoriza sem afordância de saída provada, que é o risco que esta sub-fatia existe para fechar.

**O Explorer não é reiniciado nesta volta.**
