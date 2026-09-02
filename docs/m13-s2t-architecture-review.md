# M13 S2-T — ARCHITECTURE REVIEW 4

**Autor:** Relay (platform-infra), implementer da S2-T / dono do Windows Shell.
**Branch:** `agent/m13-s2t-tray`, base `221eda4`. **Continua a ser DESENHO. Sem implementação.**
**Revisão 4** aplica a decisão de temporização do humano
(`TRAY RECOVERY GLOBAL MONOTONIC DEADLINE = 1500 ms`) e regista o resultado dos gates CV-7 e CV-8,
que foram **medidos** e passaram. Mantém tudo o que a revisão 3 fechou para Atlas, Vigil e Prism.
**Sem novo split.** **Sem implementação — a review formal de arquitetura é o portão de temporização.**

> **Conflito entre textos, resolvido à vista e não em silêncio.** O requisito §2 do humano manda **não
> continuar a publicar `Available`** durante debounce/retries. As condições do Vigil, §3.3, dizem o
> contrário: *"o estado público continua `Available` enquanto um re-registo está em voo"*. Pela regra
> de precedência, o texto do humano vence, e adoto `Recovering` **público**. **A propriedade de
> segurança do Vigil fica intacta**: quem degrada a sessão da S2 é `Lost`, e `Lost` continua a nascer
> só de falha observada — `Recovering` **não** degrada nada. Peço ao Vigil que confirme que a sua
> preocupação (input não autenticado a comandar transição de sessão) continua fechada nesta forma.

---

## A — Máquina de estados interna final

```
        Establish()                     NIM_ADD+SETVERSION = true
Unavailable ─────────────► Recovering ──────────────────────────► Available
     ▲                        │  ▲                                    │
     │                        │  │  TaskbarCreated aceite             │
     │  budget A esgotado     │  └────────────────────────────────────┘
     │  com falha observada   │
     │                        ▼
     └──────────────────── Lost            (qualquer estado) ──Release──► Releasing (terminal)
```

- **`Unavailable`** — nunca estabelecido nesta sessão.
- **`Recovering`** — a afordância **não está positivamente provada**; há um episódio limitado em
  curso. **Não reclama `Available`. Não degrada a S2.**
- **`Available`** — só depois de `NIM_ADD` **e** `NIM_SETVERSION` terem devolvido `true` pela
  fronteira nativa. **Produtor único.**
- **`Lost`** — **exclusivamente** de falha **observada** de `NIM_ADD`/`NIM_SETVERSION` que esgote o
  orçamento A. Nunca de uma mensagem, nunca de supressão por orçamento B, nunca de expiração de
  deadline sem falha observada.
- **`Releasing`** — terminal, absorvente. Nada sai daqui.

## B — Contrato público de afordância para a S2

```csharp
public enum TrayAffordanceState
{
    Unavailable = 0,   // CV-4: ordinal 0 é o valor seguro de qualquer campo/mock por inicializar
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

Como a S2 consome, normativo:

| Estado | A S2 faz |
|---|---|
| `Available` | `BACKGROUND` é legítimo |
| `Recovering` | **segura**: não degrada, e **não** trata como disponível para decisões novas. Tolerado **apenas** durante o deadline de recuperação da secção E |
| `Lost` | degradação **obrigatória**: Definições → Segundo plano · InfoBar Warning · sessão `FOREGROUND` degradada · saída verdadeira no resto da sessão |
| `Unavailable` | no arranque `--background`, degradação obrigatória; nunca permanecer headless |

**Invariante revisto, tal como mandado:** `BACKGROUND` tolera **apenas** um intervalo de revalidação
curto e estritamente limitado, durante o qual o tray **não é representado como `Available`**. Não
reestabelecido dentro do contrato limitado → `Lost` → degradação.

**CV-5 mantém-se:** `szTip`/`hIcon` estáticos; nenhum dado de snapshot atravessa o caminho do shell.

## C — Os dois orçamentos, estruturalmente independentes *(§1 · CV-2b)*

### Orçamento A — retry de falhas, **dentro** de um episódio

3 tentativas (inicial + 2), atrasos 250 ms e 1000 ms, cancelável, determinístico sob `TimeProvider`.
**Um sucesso pode repô-lo** para um episódio futuro — um reinício legítimo do Explorer não pode
consumir o direito a recuperar de uma falha futura. Esgotado com falha observada → `Lost`.

### Orçamento B — frequência de **episódios iniciados**

Janela deslizante **monotónica**, a contar **episódios iniciados, independentemente do desfecho**.
Proposta: **5 episódios por 60 s**. **Nada além da passagem do tempo o repõe.**

### A independência é estrutural, não uma regra que alguém tem de lembrar

```csharp
// Não conhece sucesso, falha, nem número de tentativas. Não existe API para lho dizer.
internal sealed class EpisodeFrequencyLimiter
{
    public bool TryBeginEpisode(long monotonicTimestamp);   // único método
}
```

**O limitador não pode ser reposto por sucesso porque nada no programa lhe consegue comunicar um
sucesso.** Não há `Reset()`, não há `OnSuccess()`, não há campo partilhado. Contadores separados,
tipos separados, **nenhum caminho em que o desfecho de A escreva em B** — verificável por inspeção da
superfície do tipo, não por disciplina.

### Ordem das guardas, e porque o gate de frequência vem **antes** de invalidar

```
TaskbarCreated
  → debounce (250 ms; rajadas colapsam num sinalizador pendente)
  → se episódio EM VOO: marcar pendente, sair. Nunca um segundo em paralelo.
  → EpisodeFrequencyLimiter.TryBeginEpisode(now)
        false → a mensagem NÃO TEM EFEITO NENHUM: não invalida o registo, não publica Recovering,
                não emite Lost. Fica-se no estado atual.
        true  → publicar Recovering → correr o episódio com o orçamento A e o deadline de E
```

**Porque é esta a ordem, e não a inversa.** Se invalidássemos primeiro e só depois consultássemos B,
uma supressão deixaria a máquina presa em `Recovering` sem forma de sair: não houve falha observada,
logo `Lost` é proibido, e voltar a `Available` seria publicar disponibilidade não provada. Com o gate
antes, **uma mensagem suprimida é exatamente equivalente a uma mensagem que nunca chegou** — o que é a
resposta correta para input não autenticado, e torna *"exceder B não emite `Lost`"* verdadeiro por
construção em vez de por regra.

**Trade-off assumido:** se um reinício legítimo do Explorer for suprimido por B, ficamos com
`Available` sobre um ícone morto até a janela deslizar. É por isso que o teto de B é generoso face ao
ritmo legítimo plausível (reinício do Explorer + mudanças de DPI). **Números são de engenharia, não
documentados; Atlas é o dono da revisão.**

## D — Debounce e coalescing

Debounce de 250 ms; **um episódio em voo**; mensagem durante um episódio marca pendente e origina **no
máximo mais um** episódio, ele próprio sujeito ao gate de B. **Não existe ordenação garantida entre
broadcasts e o desenho não depende de nenhuma** — o coalescing torna a ordem irrelevante. O
sinalizador pendente é por geração e é limpo no `Release`.

## E — `TRAY RECOVERY GLOBAL MONOTONIC DEADLINE` = **1500 ms** *(§6 · decisão do humano)*

Distinção que a revisão 2 confundia, agora com o valor aprovado e fundamentado por medição:

- **Orçamento programado de atrasos** = 250 ms + 1000 ms = **~1250 ms**. É apenas a soma dos `await`.
  **Não é limite superior de nada**, porque ignora o custo das chamadas síncronas ao shell.
- **Deadline monotónico global de recuperação** = **1500 ms**. É o limite duro real: ~1250 ms de
  atrasos programados **+ ~250 ms de folga de execução e agendamento**. Capturado **uma vez** no
  início do episódio com `TimeProvider.GetTimestamp()`.

> **Os 250 ms de folga são decisão nossa, não garantia de plataforma do Windows.** O Windows não
> promete tempo nenhum para o `Shell_NotifyIcon`. A folga foi dimensionada a partir do custo nativo
> **medido** nos gates: `COLD NIM_ADD` ~10,06 ms · `STEADY` mediana ~3,16 / máx ~4,36 ms · `CHURN`
> p95 ~4,44 / máx ~4,61 ms. O desenho anterior tinha 750 ms de margem — substancialmente
> sobredimensionada face ao que as chamadas custam de facto.

### Significado semântico — é uma decisão de segurança e ciclo de vida, não uma otimização

Os 1500 ms **são uma decisão de segurança/ciclo de vida do ServerAlyzer**. Significam:

```
afordância positiva NÃO reestabelecida antes do deadline monotónico global
  → a recuperação FALHA
  → publicar Lost
  → a S2 degrada para FOREGROUND → Definições > Segundo plano → InfoBar Warning
  → semântica de saída verdadeira no resto da sessão
```

### O deadline NUNCA é estendido — quatro proibições explícitas

**O deadline pertence a UM episódio de recuperação, é monotónico e não é reiniciável.** A aplicação
**nunca** o pode estender por nenhuma destas razões:

1. **um `NIM_ADD` ter tido sucesso** — um sucesso parcial dentro do episódio não compra tempo;
2. **ter chegado outro `TaskbarCreated`** — mensagem nova não prolonga o episódio em curso;
3. **o orçamento de retry (A) ter sido reposto** — repor A é sobre episódios **futuros**, nunca sobre
   o tempo restante deste;
4. **o debounce ter reiniciado** — o debounce coalesce mensagens, não desloca o relógio do episódio.

**É imposto, não prometido:** antes de **cada tentativa** e antes de **cada atraso**, o episódio
verifica o tempo monotónico restante; se não chegar para a operação seguinte, **abandona sem a
iniciar**. O deadline é também o que limita publicamente o intervalo `Recovering` da secção B — é ele
que torna o "curto e estritamente limitado" um número verificável em vez de uma intenção.

### Os dois limites continuam independentes — não confundir

O **deadline de recuperação (1500 ms)** e a **janela de frequência adversarial (orçamento B)** são
coisas diferentes e não se tocam. O deadline limita *quanto tempo um episódio pode durar*; a janela
limita *quantos episódios podem começar*. **Um sucesso pode afetar um orçamento futuro de retry de
falhas (A), mas NÃO PODE apagar o histórico de frequência (B)** — e, como a secção C mostra, nem
sequer tem forma de o comunicar.

### Nota de desenho para quem fizer o QA de reinício real do Explorer

Quando existir implementação com forma de produção, o QA real medirá se 1500 ms é operacionalmente
adequado. **Se a recuperação real do Explorer exceder ocasionalmente os 1500 ms, isso NÃO justifica
estender em silêncio a incerteza de `BACKGROUND`.** A avaliação correta começa por afinar o
**calendário de retries** — por exemplo a distribuição dos atrasos dentro do mesmo orçamento —
**preservando um contrato de recuperação estritamente limitado**. Alargar o deadline é a última
opção, nunca a primeira, e nunca em silêncio: aumenta a janela em que a app monitoriza sem afordância
de saída provada, que é exatamente o risco que esta sub-fatia existe para fechar.

## F — Geração terminal e ordenação do `Release` *(§3 · §4)*

**Posse terminal única.** A superfície pública tem **uma** operação terminal: `ReleaseAsync`. **Não
existe `RemoveTrayIcon` público.** Qualquer helper síncrono de `NIM_DELETE` é detalhe interno dessa
operação. **Um dono, um estado terminal, uma sequência de limpeza.**

```
ReleaseAsync (idempotente)
  1. Interlocked.Increment(_generation)      // invalida toda continuação em voo
  2. estado := Releasing                     // absorvente
  3. cancelar o CTS do episódio
  4. NIM_DELETE (interno, uma vez)
```

**Propriedade exigida:** começado o `Release`, **nenhuma continuação de geração antiga pode recriar o
ícone**. Após `Releasing`: um `TaskbarCreated` **não** inicia recuperação; callbacks **não** reabrem o
flyout; **nenhum** `Available`/`Lost` é publicado.

## G — Barreiras de continuação assíncrona

Regra de código, verificável por inspeção e por mutação:

```csharp
if (!StillCurrent(myGeneration)) return;    // geração · cancelamento · estado terminal
```

- Chamado **depois de cada `await`**, sem exceção.
- **`NIM_ADD` só pode ser invocado a partir de um caminho que chamou `StillCurrent` imediatamente
  antes, sem qualquer `await` entre a verificação e a chamada.**
- Teste de barreira determinística da §5: suspender antes do `NIM_ADD` seguinte → `Release` → retomar
  → a continuação observa geração obsoleta → **sem `NIM_ADD`, sem ressurreição**.

## H — Tratamento completo das condições do Vigil

**CV-1 (referência normativa, sete pontos).** Default-deny · identidade da mensagem (nosso
`uCallbackMessage` ou o `TaskbarCreated` registado; o id é seletor, nunca prova de origem) · `lParam`
low word na lista fechada `NIN_SELECT`/`NIN_KEYSELECT`/`WM_CONTEXTMENU`/`WM_LBUTTONDBLCLK` · `lParam`
high word `uID == 1` · `wParam` é coordenada não confiável, só âncora, nunca índice/offset/dimensão ·
nenhuma mensagem transporta ou desreferencia ponteiro · teto de impacto de uma forja = incómodo de UI,
sem chegar ao `RequestExit` sem clique real.

> **Escolha explícita no ponto 5, que a CV-1 deixa ao implementer: DESCARTAR.** Uma âncora fora de
> todas as áreas de trabalho de monitor faz a mensagem ser descartada, não corrigida. Razão: um ponto
> fora de qualquer monitor não tem origem legítima possível, e descartar é fail-closed e trivialmente
> testável. Asserido pelo caso 4 da CV-6b.

**CV-2/CV-2b** — fechadas pela secção C, mais o **teste adversarial de sucesso-sempre** (secção I).

**CV-7 — gate de medição do modelo de thread: MEDIDO, PASSA.** `TaskbarCreated` (id `0xC073`) foi
recebido num HWND **criado na thread de UI**, **com `WS_EX_TOOLWINDOW`**, top-level, sem dono, nunca
mostrado, num processo empacotado **headless** (`MainWindowHandle = 0` durante toda a medição).
**Consequência: a topologia da secção B mantém-se, e a CV-1 NÃO precisa de ser reavaliada noutra
topologia** — o caminho de recurso (thread de pump dedicada + `TryEnqueue`) fica por usar, e o
sinalizador de "em voo" continua protegido por afinidade de thread.

> **Honestidade sobre o emissor:** o broadcast foi o `PostMessage(HWND_BROADCAST, …)` desta sessão,
> **não o Explorer**. O que fica provado é a **entrega** nesta topologia, que era a metade em dúvida.
> O `TaskbarCreated` originado pelo Explorer real continua `NOT_RUN` (item F da secção N).

**CV-8 — `Shell_NotifyIcon` síncrono na thread de UI: MEDIDO, aceitável.** Custos na thread de UI:

| Cenário | `NIM_ADD` | `NIM_DELETE` |
|---|---|---|
| COLD (1.ª chamada do processo) | **10,06 ms** | — |
| STEADY (n=20) | mediana 3,16 · máx 4,36 ms | mediana 0,38 · máx 0,67 ms |
| CHURN (n=100, sem pausa) | mediana 3,11 · p95 4,44 · máx **4,61 ms** | máx 0,74 ms |

Limiar declarado: um frame a 60 Hz = **16,7 ms**. A pior chamada isolada (10,06 ms, fria) fica abaixo
de um frame; em regime o máximo é 4,6 ms. **O redesenho condicional NÃO é acionado: a chamada nativa
permanece na thread de UI.**

> **Mas o limitador de frequência MANTÉM-SE, e a medição é a razão.** 100 ciclos add+delete
> consumiram **≈372 ms** de thread de UI (~3,7 ms por ciclo). Churn sustentado **consome tempo
> significativo da thread que desenha a aplicação**. É o debounce mais o orçamento B que tornam isto
> inofensivo — com debounce de 250 ms o teto é ~4 episódios/s ≈ 15 ms/s (~1,5% da UI), e com B
> (5 episódios/60 s) ≈ 0,03%. **O custo bruto por si só não seria seguro; são os limites do desenho
> que o tornam seguro.**
>
> **Pior caso ainda por medir:** o shell a reiniciar. Exige a autorização de reinício do Explorer e
> mantém-se `NOT_RUN`. O COLD de 10,06 ms é o pior observável sem essa autorização.

**Nota sobre o `FindWindowW`.** Durante a medição, `FindWindowW` pelo nome de classe a partir de outro
processo devolveu `0`, mas `EnumWindows` encontrou a janela sem dificuldade. **Isto não é — e não pode
ser tratado como — propriedade de segurança.** Não determinei a causa, a obscuridade nunca foi o
controlo, e o modelo de ameaça do ponto 7 da CV-1 mantém-se válido pela via da enumeração.

**CV-9 — reentrância com flyout aberto.** Guarda `_flyoutOpen` na thread de UI: com o flyout aberto,
**toda** `WM_CONTEXTMENU` adicional é descartada por default-deny — não empilha segundo flyout, não
reposiciona o aberto, e não escreve no sinalizador de "em voo" nem no estado de episódio (caminhos
disjuntos por desenho). A janela auxiliar é escondida no `Closed` do flyout, e isso é asserido.

## I — Plano de evidência de mutação *(§11 · CV-6b)*

Seam no limite nativo (`INativeTrayRegistration { bool Add(); bool SetVersion(); bool Delete(); }`);
**máquina de estados sob teste é a de produção**; tempo por `TimeProvider` monotónico determinístico.

### Os seis exigidos antes da próxima review

| # | Mutação na produção | Teste que TEM de falhar |
|---|---|---|
| 1 | **um sucesso repõe/limpa a janela de frequência de `TaskbarCreated`** | **adversarial: `Add` sempre `true`, broadcasts em ciclo ⇒ B converge para supressão** |
| 2 | `Available` continua publicado durante `Recovering` | após `TaskbarCreated` aceite, o estado publicado é `Recovering`, nunca `Available` |
| 3 | verificação de geração pós-`await` removida | continuação obsoleta após `Release` **não** chama `NIM_ADD`; sem ressurreição |
| 4 | esgotamento de retries a permanecer em `BACKGROUND` | 3 falhas observadas ⇒ `Lost` e o consumidor sai de background |
| 5 | recuperação de `TaskbarCreated` removida | mensagem aceite ⇒ `Add` reinvocado e `Available` reemitido |
| 6 | encaminhamento de callback removido | callback v4 válido ⇒ `OpenRequested`/flyout exatamente uma vez |

> A mutação 1 é a que fecha o buraco real: **exercitar só o caminho de falha deixaria por cobrir
> exatamente o defeito que existia**, porque com `Add` sempre a falhar o teto engatava por acidente.

### CV-6b — quatro casos **independentes**, não uma conjunção

Cada caso tem **todos os outros campos válidos**, para isolar um único filtro; e **cada um tem de
falhar** se a validação correspondente for removida da classe de produção:

1. id de mensagem errado · evento válido · `uID == 1` → ignorado.
2. `uID ≠ 1` · id correto · evento válido → ignorado.
3. evento fora da lista fechada (v3, ou fora de gama) · id correto · `uID == 1` → ignorado.
4. `wParam` fora de qualquer monitor · tudo o resto válido → **descartado** (escolha da secção H).

### Restantes

`Unavailable` no ordinal 0 · produtor único de `Available` · `szTip`/`hIcon` estáticos · exatamente 3
tentativas e não 4 · deadline monotónico verificado **antes de cada tentativa e de cada atraso** ·
`Release` idempotente · `TaskbarCreated` após `Releasing` não inicia recuperação · nenhum evento
publicado após o release terminal · CV-9 com flyout aberto · `NIM_DELETE` exatamente uma vez.

**Nada entregue só com suite verde.** A evidência de mutação acompanha a entrega.

## J — Critério de observação do `FORCED-TERMINATION TRAY CLEANUP` *(§8)*

**Fixado aqui, antes da corrida humana. Não será escolhido retroativamente.**

> **`EMPIRICAL QA ACCEPTANCE WINDOW` = 120 segundos.** Não é garantia de plataforma: **o Windows não
> documenta prazo de limpeza de ícones órfãos**, e por isso a janela é rotulada como empírica e é
> fixada a priori precisamente para não poder ser ajustada depois de se observar o resultado.

Procedimento, por esta ordem: `TerminateProcess` pelo watchdog → observar a área de notificação
durante **120 s sem qualquer interação** → depois **uma única passagem do rato** sobre a área de
notificação (gesto clássico que leva o shell a reclamar ícones cujo HWND dono morreu) → registar.

Critérios: renderização obsoleta transitória **aceitável**; **órfão persistente para além da janela =
FAIL**; o ícone obsoleto **não pode continuar interativo**; o lançamento seguinte cria **exatamente
um** ícone utilizável; sem duplicados; tray funcional após reinício; sem consola/WER.
**Não se exige `NIM_DELETE` do processo morto.**

## K / L / M — veredictos, e o portão de temporização

**Esta review formal É o portão de temporização.** O humano dispensou pré-review separada do Atlas.

**K — Atlas: PENDENTE.** As quatro decisões explícitas que lhe são pedidas:
1. **1500 ms é internamente consistente com o calendário de retries?** (~1250 ms de atrasos + ~250 ms
   de folga, contra o custo nativo medido em H.)
2. **O deadline é mesmo monotónico e não reiniciável?** (as quatro proibições da secção E.)
3. **A recuperação NÃO pode excedê-lo por via da lógica de agendamento/retry?** (verificação do tempo
   restante antes de cada tentativa **e** de cada atraso.)
4. **O `Release` continua a dominar a recuperação em CADA `await`?** (secções F e G.)
Mais, herdado: os números de A (3 / 250 / 1000) e de B (5 / 60 s); a ordem das guardas em C e o
trade-off de um reinício legítimo suprimido.

**L — Prism: PASS registado; revê apenas as consequências visíveis da janela de recuperação mais
curta.** Ordem do menu **fechada e preservada exatamente**: Abrir o ServerAlyzer · Modo compacto ·
Atualizar todos · Definições · Sair do ServerAlyzer — a substituição do backend **não redesenha o
menu**. Tema multi-raiz dentro do processo **em âmbito**; persistência entre arranques **fora de
âmbito** (THEME-1 no backlog, não é critério de aceitação).

**M — Vigil: PENDENTE.** Duas coisas: (i) revalidar o **caso adversarial de sucesso-sempre** contra o
limitador de frequência independente (mutação 1 da secção I); (ii) confirmar que publicar `Recovering`
em vez de `Available` durante o re-registo **não** enfraquece a CV-2 — o meu argumento é que não,
porque quem degrada a S2 é `Lost` e `Recovering` não degrada nada.

## N — `NOT_RUN` restantes (QA de plataforma real)

| | Caso | Estado |
|---|---|---|
| A | registo inicial no shell real | `NOT_RUN` |
| B | tray headless real | `NOT_RUN` |
| C | menu/flyout: teclado, tema, e **CV-9 com o menu aberto** | `NOT_RUN` |
| D | reinício real do Explorer em `FOREGROUND` | `NOT_RUN` — **autorização humana** |
| E | reinício real em `BACKGROUND`/headless | `NOT_RUN` — **autorização humana** |
| F | `TaskbarCreated` entregue **pelo próprio Explorer** | `NOT_RUN` — não promovível do broadcast sintético |
| G | restauro bem-sucedido do ícone | `NOT_RUN` |
| H | sem ícone duplicado | `NOT_RUN` |
| I | sem janela auxiliar visível nem botão na barra de tarefas | `NOT_RUN` |
| J | degradação por falha de registo forçada | `NOT_RUN` |
| K | sem consola / sem WER | `NOT_RUN` |
| L | `FORCED-TERMINATION TRAY CLEANUP` (janela de 120 s da secção J) | `NOT_RUN` |
| **M** | **CV-7** — `TaskbarCreated` em HWND da thread de UI com `WS_EX_TOOLWINDOW`, headless | **MEDIDO · PASSA** (emissor sintético; ver H) |
| **N** | **CV-8** — custo de `NIM_ADD`/`NIM_DELETE` na thread de UI | **MEDIDO · aceitável** (ver H) |
| O | **CV-8 pior caso com o shell a reiniciar** | `NOT_RUN` — **autorização humana** |

**M e N eram gates do desenho e estão fechados por medição** — fixaram a topologia de thread e
fundamentaram os 1500 ms. **Tudo o resto continua `NOT_RUN`, e o Explorer não é reiniciado.**
