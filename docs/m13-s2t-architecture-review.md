# M13 S2-T — ARCHITECTURE REVIEW

**Autor:** Relay (platform-infra), implementer da S2-T / dono do Windows Shell.
**Branch:** `agent/m13-s2t-tray`, base `221eda4`. **Esta volta é DESENHO. Não há implementação.**
**Reviewers:** Atlas (fiabilidade/races) · Prism (UX tray/flyout/degradação) · Vigil (segurança/fronteira).

> **Evidência de partida, registada com precisão.** Entrega de `TaskbarCreated` em janela top-level
> escondida **foreground: PROVADA**; **headless: PROVADA**, com sonda `HWND_BROADCAST`;
> **`TaskbarCreated` originado pelo Explorer real em headless: `NOT_OBSERVED`**. O último **não** é
> promovido a PASS a partir do broadcast sintético — está na matriz O. **O Explorer não é reiniciado
> nesta volta.**

---

## A — Fronteira de posse nativa

**Possuímos** (novo, em `src/ServerMonitor.App/Shell/Tray/`): a classe de janela, o HWND, o
`Shell_NotifyIconW`, o encaminhamento da mensagem de callback, o ciclo de vida do ícone, o
`TaskbarCreated` e a máquina de estados de retry.

**Não possuímos e não tocamos:** o resto do WinUIEx. `WindowManager`, `WindowState` e
`SetForegroundWindow()` continuam a ser usados pelo `ApplicationWindowController` e ficam
**inalterados**. **Não há fork.** O que sai do caminho crítico é exclusivamente o `WinUIEx.TrayIcon`,
porque é dono do HWND e do encaminhamento de callbacks e não expõe nenhum dos dois (verificado por
reflexão: superfície pública = `Selected`, `ContextMenu`, `LeftDoubleClick`, `RightDoubleClick`,
`IsVisible`, `Tooltip`, `TrayIconId`, `SetIcon`, `Dispose` — **sem HWND**).

**Custo assumido explicitamente:** ao deixar de usar o `WinUIEx.TrayIcon` perdemos três serviços que
ele nos prestava — carregamento de ícone com consciência de DPI, re-registo em `TaskbarCreated`, e
hosting do `MenuFlyout`. Os três passam a ser nossos. O terceiro é o de maior risco (secção H).

## B — Desenho do HWND / classe de janela

| Decisão | Valor | Porquê |
|---|---|---|
| Classe | `ServerAlyzer.TrayHost`, registada uma vez; `ERROR_CLASS_ALREADY_EXISTS` tolerado | idempotência sob re-entrada |
| Parent | `IntPtr.Zero` (top-level) | **NUNCA `HWND_MESSAGE`**: *"a message-only window … does not receive broadcast messages"* |
| Estilo | `WS_OVERLAPPED`, **nunca** `ShowWindow` | invisível sem ser message-only |
| Estilo estendido | **`WS_EX_TOOLWINDOW`** | *"To prevent the window button from being placed on the taskbar, create the unowned window with the WS_EX_TOOLWINDOW extended style"* |
| Dono | nenhum | broadcasts vão a top-level não-possuídas |
| Tamanho | 0×0 | não é mostrada |

> **Medição exigida na implementação, não assumida:** a minha sonda que recebeu o broadcast **não**
> tinha `WS_EX_TOOLWINDOW`. Antes de fechar B, medir que o `TaskbarCreated` continua a chegar **com**
> esse estilo. Se não chegar, `WS_EX_TOOLWINDOW` cai e o "sem botão na barra de tarefas" passa a
> depender da invisibilidade, que a S-1(A) já mediu suficiente para a janela 0×0 do WinUIEx.

**Thread do HWND — decisão com risco declarado.** Proponho criar o HWND **na thread de UI**, e não
numa thread de pump dedicada. Razão: o flyout é XAML e tem afinidade de thread; um HWND na thread de
UI elimina marshalling no caminho do menu e é a topologia que a S-1(A) mediu a funcionar (o HWND do
WinUIEx está na thread de UI). **O que NÃO está medido:** recebi o broadcast numa thread dedicada,
não na thread de UI. Por isso: **gate de implementação** — medir `TaskbarCreated` num HWND da thread
de UI, em headless. Se falhar, o desenho de recurso, já medido a funcionar, é thread de pump
dedicada em background + `DispatcherQueue.TryEnqueue` para tudo o que toque XAML.

## C — Contrato `Shell_NotifyIcon`

`NOTIFYICONDATAW` nossa: `cbSize`, `hWnd` (nosso), `uID` = 1 (estável, igual ao atual),
`uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP`, `uCallbackMessage = WM_APP + n`, `hIcon`, `szTip`.

Sequência de registo, **com o BOOL verificado em cada passo**:

```
NIM_ADD        → BOOL. false ⇒ tentativa falhada.
NIM_SETVERSION → BOOL, uVersion = NOTIFYICON_VERSION_4.
                 Doc: "NIM_SETVERSION must be called every time a notification area icon is added".
                 Doc: devolve false "if the requested version is not supported".
```

**Versão 4 é requisito, não preferência:** dá `wParam = MAKEWPARAM(x,y)` com o ponto de âncora, que é
o que precisamos para posicionar o flyout, e `lParam = MAKELPARAM(evento, uID)`. Medi esta codificação
a funcionar contra o WinUIEx na S-1(A) (injeção de `WM_CONTEXTMENU` com codificação v4). Se
`NIM_SETVERSION` devolver `false`, o contrato de que dependemos não existe: `NIM_DELETE` + tratar como
tentativa falhada. **Não** degradar silenciosamente para v3.

Limpeza: `NIM_DELETE` na saída verdadeira e antes de cada re-registo.

## D — Encaminhamento de callback

WndProc nosso, na thread do HWND. Trabalho mínimo no WndProc: descodificar evento + âncora, e
despachar. Mapa:

| Evento v4 (lParam low word) | Ação |
|---|---|
| `NIN_SELECT` / `NIN_KEYSELECT` | `OpenRequested` |
| `WM_CONTEXTMENU` | mostrar o flyout na âncora `wParam` |
| `WM_LBUTTONDBLCLK` | `OpenRequested` (paridade com hoje) |
| `TaskbarCreated` (registada) | secção E |

Tudo o que toca XAML passa pela `DispatcherQueue` da UI se o HWND ficar numa thread dedicada; se
ficar na thread de UI (proposta), corre direto. **Esta é a parte que o documento manda desenhar e
testar explicitamente: não se assume que o comportamento do WinUIEx sobrevive.** Testes em K.

## E — Ciclo de vida do `TaskbarCreated`

`RegisterWindowMessageW("TaskbarCreated")` uma vez, id guardado (**nunca hardcoded** — medi `0xC073`
nesta sessão, mas o id é atribuído em runtime).

```
recebido → afordância passa a POR PROVAR (não é Lost ainda, não é Available)
         → NIM_DELETE best-effort (resultado ignorado: o ícone pode já não existir)
         → NIM_ADD + NIM_SETVERSION com resultado verificado
         → sucesso ⇒ Available   |   budget esgotado ⇒ Lost
```

**Duas armadilhas tratadas por desenho:** (i) *"On Windows 10, the taskbar also broadcasts this message
when the DPI of the primary display changes"* — logo `TaskbarCreated` **não** significa "o Explorer
morreu"; o handler tem de ser idempotente e **não** pode emitir `Lost` só por ter recebido a mensagem;
(ii) o `NIM_DELETE` antes do `NIM_ADD` mais o `uID` estável impedem ícone duplicado.

## F — Máquina de estados de retry, com números justificados

**A Microsoft não publica números** para `Shell_NotifyIcon` — verifiquei as páginas `Shell_NotifyIconW`
e `The Taskbar`: não há enumeração de causas de falha nem orientação de retry. Portanto os números
**não vêm da documentação** e não vou fingir que vêm. Vêm de dois limites nossos, e digo-o.

| Parâmetro | Valor | Justificação |
|---|---|---|
| Tentativas | **3** (inicial + 2) | absorve falha transitória sem virar poller |
| Atrasos | **250 ms**, depois **1000 ms** | crescente, sem busy spin |
| Orçamento total | **≤ ~1,25 s** | (i) uma ordem de grandeza abaixo do deadline de 10 s do watchdog da S2, para **nunca** poder interagir com o shutdown; (ii) o arranque `--background` tem de chegar a estado **decidido** em ~1 s, porque até lá há monitorização sem afordância de saída provada — o invariante que a S2 consome |

**Derivação de princípio, essa sim documentada:** a resposta oficial a "a barra de tarefas ainda não
está lá" **não é um ciclo de retry, é o `TaskbarCreated`**. Logo o retry existe só para falha
transitória e o caminho durável é o broadcast. Esgotado o budget: **`Lost`/`Unavailable` e parar** —
sem ciclo de recuperação permanente escondido. Só um `TaskbarCreated` posterior volta a tentar.
Cancelável por `CancellationToken`; determinístico por `TimeProvider`. **Atlas revê os números.**

## G — Contrato de afordância para a S2

```csharp
public enum TrayAffordanceState { Unavailable, Available, Lost }

public interface ITrayAffordance
{
    TrayAffordanceState State { get; }
    event EventHandler<TrayAffordanceChangedEventArgs> StateChanged;   // na dispatcher de UI
    Task<TrayAffordanceState> EstablishAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);            // NIM_DELETE na saída verdadeira
}
```

- **Invariante central:** o **único** caminho de código que atribui `Available` é o que leu `true` do
  `NIM_ADD` **e** do `NIM_SETVERSION` através da fronteira nativa. Sem outro produtor de `Available`.
- **Sem estado `Unknown`** — o objetivo é proibir inferência, e um `Unknown` convidaria a tratá-lo
  como bom.
- **`Unavailable` ≠ `Lost`:** nunca estabelecido nesta sessão vs. estabelecido e depois perdido. A S2
  precisa da diferença para a mensagem ao utilizador. `EstablishAsync` devolve o desfecho para o
  arranque poder decidir sem esperar por evento.
- A S2-T **não materializa UI** e não decide política de sessão: emite estado. A materialização de
  Definições > Segundo plano e a InfoBar são da S2.

## H — Hosting do flyout — a decisão de maior risco, com alternativa para o Prism

Ao possuir o HWND, o hosting do menu passa a ser nosso. Três opções:

1. **`XamlRoot` da `MainWindow`** — **inviável em headless**: a S-1(A) mediu que `RootLayout.Loaded`
   só dispara na primeira exibição, logo não há `XamlRoot` num processo nunca ativado.
2. **Janela XAML mínima nossa, nunca ativada, a hospedar o `MenuFlyout`** — é o que o WinUIEx faz. A
   S-1(A) mediu o resultado: janela `WinUIDesktopWin32WindowClass` 0×0 **visível** enquanto o flyout
   está aberto, **sem botão na barra de tarefas** (medido na taskbar real), **sem roubo de foco**
   (foreground manteve-se noutro PID), flyout renderizado com as 5 entradas num processo nunca
   ativado. Preserva a UX do Prism tal como está.
3. **Menu nativo `TrackPopupMenuEx`** — sem qualquer janela XAML auxiliar, é o padrão documentado para
   tray (o `NIM_SETFOCUS` existe precisamente para este fluxo). Elimina por construção o risco de
   janela auxiliar visível. **Custo:** perde estilo/tema XAML e a redação visual do Prism; exige o
   `SetForegroundWindow` clássico antes do menu, o que colide com "sem roubo de foco" e teria de ser
   medido.

**Recomendo a opção 2** como caminho primário, por preservar exatamente o que a S-1(A) já mediu e por
manter a UX aprovada. **Mas ponho a opção 3 explicitamente ao Prism e ao Atlas**, porque é a única
que remove a janela auxiliar por desenho em vez de por medição repetida. **Não decido isto sozinho.**

## I — Sinalização de degradação

S2-T emite; S2 age. Esgotado o budget no arranque `--background` → `Unavailable` → a S2 **nunca**
permanece headless, materializa Definições > Segundo plano com a InfoBar já no primeiro frame,
sessão `FOREGROUND` degradada, X/Alt-F4 = saída verdadeira nessa sessão, `BackgroundMonitoringEnabled`
**inalterado**. `Lost` já em `BACKGROUND` → o mesmo caminho. **A S2-T nunca volta a background
automaticamente**: só um `TaskbarCreated` reabre uma tentativa, e a decisão de sair da sessão
degradada é da S2, que a decisão do humano fixa como "não oscilar nessa sessão".

## J — Migração para fora do `WinUIEx.TrayIcon`

1. `ITrayIconAdapter` mantém os cinco eventos e ganha a superfície de `ITrayAffordance`.
2. Novo `ShellTrayIconAdapter` (nosso) substitui `WinUIExTrayIconAdapter`; este é **removido**, com os
   seus testes.
3. `TrayService` deixa de inferir sucesso de `Start()` e passa a consumir `EstablishAsync`. O
   `DegradeWithoutTrayIcon()` existente passa a ser acionado pelo estado, não por exceção.
4. WinUIEx **continua** como dependência para `WindowManager`/extensões de janela. Sem fork, sem
   remoção do pacote.
5. **Trabalho novo que o WinUIEx fazia por nós:** carregar o `.ico` com consciência de DPI
   (`LoadIconMetric`/`LoadImageW`) e libertar o `HICON`. Não é acessório — um ícone errado a 150% de
   escala é regressão visível. Prism revê.

## K — Testes e as seis contra-provas por mutação

**Seam no limite nativo, não na máquina de estados:** `INativeTrayRegistration { bool Add(); bool SetVersion(); bool Delete(); }`.
O duplo devolve booleanos; **a máquina de estados sob teste é a de produção**. Tempo por
`FakeTimeProvider`.

| # | Mutação na produção | Teste que TEM de falhar |
|---|---|---|
| 1 | `NIM_ADD` falhado tratado como sucesso — **é a regressão que existe hoje** | `Add=false` ⇒ estado nunca chega a `Available`; degradação emitida |
| 2 | `NIM_ADD` bem-sucedido tratado como falha | `Add=true` ⇒ `Available` exatamente uma vez; sem degradação espúria |
| 3 | re-registo em `TaskbarCreated` removido | após a mensagem, `Add` é reinvocado e `Available` é reemitido |
| 4 | esgotamento de retries a permanecer em `BACKGROUND` | 3 falhas ⇒ `Unavailable` e o consumidor sai de background |
| 5 | encaminhamento de callback removido | callback v4 sintético ⇒ `OpenRequested`/flyout pedidos exatamente uma vez |
| 6 | sinal `Lost` suprimido | perda após `Available` ⇒ `Lost` observado pelo consumidor |

Mais: retry exatamente 3 tentativas e não 4; atrasos exatos sob tempo falso; cancelamento a
interromper entre tentativas; `TaskbarCreated` idempotente (duas mensagens ⇒ sem ícone duplicado, sem
`Lost` espúrio); `ReleaseAsync` chama `NIM_DELETE` exatamente uma vez. **Nada provado só com fakes** —
as mutações são contra a classe de produção.

## L — Veredicto Atlas — **PENDENTE**

Perguntas que lhe ponho: os números de F (3 tentativas, 250 ms/1000 ms, ≤1,25 s) e a justificação;
a decisão de thread do HWND em B e o gate de medição associado; a idempotência do `TaskbarCreated`
incluindo o caso DPI; se o seam de K está no sítio certo para as seis mutações morderem.

## M — Veredicto Prism — **PENDENTE**

Perguntas: opção 2 vs opção 3 na secção H; o custo de UX da opção 3; o carregamento de ícone com DPI
em J; a redação da degradação de I.

## N — Veredicto Vigil — **PENDENTE**

Perguntas: a fronteira nativa nova não alarga superfície de ataque (sem conteúdo dinâmico em
`szTip`/`hIcon`, sem dados de snapshot no caminho do shell); o WndProc não confia em `wParam`/`lParam`
sem validar; o `HICON` é libertado; nenhum estado sensível atravessa a fronteira.

## O — Matriz de QA de plataforma real — tudo `NOT_RUN`

| | Caso | Estado |
|---|---|---|
| A | registo inicial no shell real | `NOT_RUN` |
| B | tray headless real | `NOT_RUN` |
| C | menu/flyout | `NOT_RUN` |
| D | reinício real do Explorer em `FOREGROUND` | `NOT_RUN` — **exige autorização humana** |
| E | reinício real em `BACKGROUND`/headless | `NOT_RUN` — **exige autorização humana** |
| F | `TaskbarCreated` entregue **pelo próprio Explorer** | `NOT_RUN` (hoje `NOT_OBSERVED`) |
| G | restauro bem-sucedido do ícone | `NOT_RUN` |
| H | sem ícone duplicado | `NOT_RUN` |
| I | sem janela auxiliar visível nem botão na barra de tarefas | `NOT_RUN` |
| J | degradação por falha de registo forçada | `NOT_RUN` |
| K | sem consola / sem WER | `NOT_RUN` |

**O Explorer não é reiniciado nesta volta.** Quando existir build com forma de produção da S2-T, peço
autorização humana explícita para D/E/F como checkpoint dedicado.
