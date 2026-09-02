# M13 S2-T — ARCHITECTURE REVIEW (revisão 2)

**Autor:** Relay (platform-infra), implementer da S2-T / dono do Windows Shell.
**Branch:** `agent/m13-s2t-tray`, base `221eda4`. **Esta volta continua a ser DESENHO. Sem implementação.**
**Revisão 2** responde a: Atlas DEVOLVIDO (3 ALTA + 1 MÉDIA) · Prism PASS COM RESSALVAS (1 ALTA) ·
Vigil PASS COM CONDIÇÕES (CV-1 a CV-6, duas bloqueantes).

> **Evidência de partida.** `TaskbarCreated` em janela top-level escondida: **foreground PROVADA**,
> **headless PROVADA** com sonda `HWND_BROADCAST`. **Originado pelo Explorer real em headless:
> `NOT_OBSERVED`** — não promovido a partir do broadcast sintético. **O Explorer não é reiniciado.**

---

## A — Fronteira de posse nativa

Possuímos, em `src/ServerMonitor.App/Shell/Tray/`: classe de janela, HWND, `Shell_NotifyIconW`,
encaminhamento de callback, ciclo de vida do ícone, `TaskbarCreated`, retry, e a raiz XAML mínima que
hospeda o menu.

**Sem fork.** Sai do caminho crítico apenas o `WinUIEx.TrayIcon`, porque é dono do HWND e do
encaminhamento e não expõe nenhum dos dois (superfície pública verificada por reflexão: `Selected`,
`ContextMenu`, `LeftDoubleClick`, `RightDoubleClick`, `IsVisible`, `Tooltip`, `TrayIconId`, `SetIcon`,
`Dispose` — sem HWND). O resto do WinUIEx (`WindowManager`, `SetForegroundWindow`) continua a ser
usado pelo `ApplicationWindowController` e fica intacto.

## B — HWND, classe de janela, e **modelo de thread** *(Atlas ALTA-3)*

| Decisão | Valor | Porquê |
|---|---|---|
| Parent | `IntPtr.Zero` (top-level) | **nunca `HWND_MESSAGE`** — message-only não recebe broadcasts |
| Estilo | `WS_OVERLAPPED`, nunca `ShowWindow` | invisível sem ser message-only |
| Estilo estendido | `WS_EX_TOOLWINDOW` | forma documentada de não ter botão na barra de tarefas |
| Dono | nenhum | broadcasts vão a top-level não-possuídas |

### Modelo de thread — definido

- **O HWND é criado na thread de UI do XAML.** A sua `WndProc` corre nessa thread.
- **Não criamos bomba de mensagens nenhuma.** A bomba é a que a `DispatcherQueue` do XAML já corre.
  Não há thread nova, não há `GetMessage` nosso, não há pump que possa ficar órfão.
- **Não pode bloquear o arranque.** `EstablishAsync` corre na thread de UI mas **nunca a bloqueia**:
  os atrasos entre tentativas são `await` sobre `TimeProvider`, nunca `Thread.Sleep` nem `.Wait()`.
  O limite total (≤ ~1,25 s, secção F) é um limite de *latência até decisão*, não de bloqueio. A S2
  **não pode** esperar sincronamente por ele.
- **Interação com o watchdog:** o watchdog da S2 é infraestrutura de tempo de vida de processo, em
  thread própria de background, deliberadamente independente da thread de UI. **Nenhum código de tray
  está no caminho do watchdog.** Se a thread de UI encravar, o estabelecimento do tray fica parado —
  e o watchdog dispara na mesma. Esta é a razão de não pôr o watchdog nesta thread.
- **Interação com a drenagem do `StopAsync`:** `ReleaseAsync` (`NIM_DELETE`) tem afinidade à thread de
  UI e obedece à ordenação da S2 §14 — o ícone só sai depois de o Exit estar comprometido. Se a
  dispatcher já estiver a encerrar e rejeitar o enqueue, `ReleaseAsync` **falha depressa e não
  bloqueia a drenagem**; o ícone órfão resultante é o caso já coberto por CV-3 / S6.
- **Reentrância:** a `WndProc` pode reentrar enquanto o flyout está aberto. **Nenhum lock é mantido
  através de um `ShowAt`**, e o estado de re-registo é um sinalizador simples mutado só nesta thread,
  o que elimina corrida sem precisar de lock.

> **Gates de medição, não assunções.** Medi o broadcast (i) **sem** `WS_EX_TOOLWINDOW` e (ii) numa
> **thread dedicada**. A proposta usa o estilo **e** a thread de UI. Antes de fechar B, medir
> `TaskbarCreated` num HWND da thread de UI **com** `WS_EX_TOOLWINDOW`, em headless. Se falhar, o
> recurso é a topologia já medida: thread de pump dedicada + `DispatcherQueue.TryEnqueue` para XAML.

## C — Contrato `Shell_NotifyIcon`

`NOTIFYICONDATAW` nossa: `cbSize`, `hWnd` nosso, `uID = 1`, `uFlags = NIF_MESSAGE|NIF_ICON|NIF_TIP`,
`uCallbackMessage = WM_APP + n`, `hIcon`, `szTip`.

```
NIM_ADD        → BOOL verificado. false ⇒ tentativa falhada.
NIM_SETVERSION → BOOL verificado, uVersion = NOTIFYICON_VERSION_4.
                 false ⇒ NIM_DELETE e tentativa falhada. NUNCA degradar em silêncio para v3.
```

v4 é requisito: dá a âncora em `wParam` (`MAKEWPARAM(x,y)`) e `MAKELPARAM(evento, uID)` em `lParam` —
codificação que medi a funcionar na S-1(A). Limpeza: `NIM_DELETE` na saída verdadeira e antes de cada
re-registo.

### Ícone com DPI *(Prism)*

`LoadIconMetric(LIM_SMALL)`, ou `LoadImageW` dimensionado por
`GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForWindow(hwnd))`. **Nunca 16×16 fixo** — borrado a 150%,
o caso mais comum em portáteis; a app é PerMonitorV2 e o `.ico` tem 16/20/24/32/48 a 32 bpp.
No handler de `TaskbarCreated` (que também dispara em mudança de DPI): recarregar o `HICON`, fazer
`NIM_MODIFY`, e **libertar o `HICON` antigo DEPOIS do `NIM_MODIFY`, nunca antes**. 250% (40×40) não
existe no `.ico`; o shell escala do 48 — aceite, não bloqueia.

## D — Encaminhamento de callback e **modelo de confiança da `WndProc`** *(Vigil CV-1, bloqueante)*

**Default-deny.** Tudo o que não corresponder exatamente ao contrato vai a `DefWindowProcW` sem efeito.

1. A mensagem tem de ser **o nosso `uCallbackMessage`** registado. Qualquer outro id → ignorado.
2. `lParam` low word validado contra a **lista fechada** de eventos v4:
   `NIN_SELECT`, `NIN_KEYSELECT`, `WM_CONTEXTMENU`, `WM_LBUTTONDBLCLK`. Tudo o resto → descartado.
3. `lParam` high word tem de ser **`uID == 1`**. Diferente → descartado.
4. `wParam` é **coordenadas não confiáveis**: usado **só** como âncora do flyout, **nunca** como
   índice, offset, dimensão ou tamanho de buffer, e **saneado** contra a área de trabalho do monitor
   antes de qualquer uso.
5. **Nenhuma mensagem transporta ponteiro e nada é desreferenciado.** Não há dados de estrutura
   vindos da mensagem.

Mapa (após validação): `NIN_SELECT`/`NIN_KEYSELECT` → `OpenRequested`; `WM_CONTEXTMENU` → flyout na
âncora; `WM_LBUTTONDBLCLK` → `OpenRequested` (paridade com hoje).

**Teclado** *(Prism MÉDIA)*: `WM_CONTEXTMENU` chega por `Shift+F10`/tecla Menu com âncora válida em
v4; o flyout tem de **receber foco de teclado** para navegar as 5 entradas **sem a janela auxiliar ser
ativada**. Item de medição.

## E — Ciclo de vida do `TaskbarCreated`, **coalescido e limitado** *(Vigil CV-2, bloqueante · Atlas ALTA-1 e ALTA-2)*

**Premissa de ameaça, explícita:** `TaskbarCreated` é **input não autenticado**. Qualquer processo
local pode fazer `PostMessage(HWND_BROADCAST, RegisterWindowMessage("TaskbarCreated"))` — **foi
exatamente assim que produzi a medição de entrega desta sub-fatia**. Portanto a mensagem **nunca**
pode, por si só, comandar uma transição de sessão da S2.

### O que "por provar" significa, e por quanto tempo *(Atlas ALTA-1)*

`Unproven` é **estado interno da S2-T e não é observável pela S2**. Ao receber a mensagem, o estado
público **mantém-se `Available`** enquanto o re-registo está em voo. A duração é limitada pelo mesmo
orçamento da secção F (≤ ~1,25 s) mais o debounce. Só há transição pública quando uma tentativa
**observa** falha real do `NIM_ADD`. Consequência deliberada: um broadcast forjado não consegue pôr a
S2 em estado degradado.

### Ordenação e sobreposição *(Atlas ALTA-2)*

**Não existe ordenação garantida entre broadcasts, e o desenho não depende de nenhuma.** O
coalescing torna a ordenação irrelevante:

```
mensagem recebida
  → debounce 250 ms (dentro da janela, colapsa no sinalizador pendente)
  → se já houver re-registo EM VOO: marcar "pendente" e regressar. NUNCA iniciar um segundo.
  → senão, e se o teto o permitir: um passo de re-registo
        NIM_DELETE best-effort (resultado ignorado) → NIM_ADD + NIM_SETVERSION verificados
  → ao terminar: se "pendente", correr no máximo mais UM passo
```

Como a `WndProc` e o sinalizador vivem os dois na thread de UI, não há corrida e mensagens
sobrepostas colapsam em, no máximo, um passo adicional.

### Teto e reposição de orçamento

| Parâmetro | Valor | Justificação |
|---|---|---|
| Debounce | **250 ms** | mesma escala do primeiro retry; colapsa rajadas |
| Em voo | **1** | elimina re-entrada e ícones duplicados por construção |
| Teto | **5 passos por 60 s** (janela deslizante) | muito acima do ritmo legítimo (reinício do Explorer + mudanças de DPI), baixo o suficiente para limitar uma inundação forjada |
| Reposição | **um re-registo bem-sucedido repõe o contador** | um reinício legítimo do Explorer nunca esgota o orçamento; só falhas repetidas o consomem |

Teto excedido → **parar de re-registar**. **Não emitir `Lost`.** `Lost` só é produzido por falha
**observada** do shell, nunca pela chegada de uma mensagem — vale para o caso DPI **e para o caso
adversarial**. Números são escolha de engenharia limitada pelos nossos orçamentos, não valores
documentados; **Atlas revê**.

## F — Retry, com a derivação explícita *(Atlas MÉDIA)*

**A Microsoft não publica números** para `Shell_NotifyIcon`: verifiquei `Shell_NotifyIconW` e
`The Taskbar` — não há enumeração de causas de falha nem orientação de retry. A derivação é nossa e
tem três passos:

1. **Limite superior duro.** O deadline global do watchdog da S2 é 10 s. Para o tray **nunca** poder
   interagir com o shutdown, o seu orçamento fica uma ordem de grandeza abaixo: **≤ ~1,25 s**.
2. **Limite inferior funcional.** O arranque `--background` tem de chegar a estado **decidido** em
   ~1 s, porque até lá existe monitorização sem afordância de saída provada — precisamente o
   invariante que a S2 consome. Isto exclui um único disparo sem retry.
3. **Forma.** Como a resposta documentada a "shell não pronta" é o `TaskbarCreated` e não um poller,
   o retry só tem de absorver falha **transitória**: **3 tentativas** (inicial + 2), atrasos
   crescentes **250 ms** e **1000 ms** = ~1,25 s, dentro de (1) e a satisfazer (2).

Cancelável por `CancellationToken`, determinístico por `TimeProvider`, sem busy spin. Esgotado o
orçamento: parar. Sem ciclo de recuperação permanente escondido. Só um `TaskbarCreated` posterior
reabre tentativas, sujeito ao teto de E.

## G — Contrato de afordância para a S2

```csharp
public enum TrayAffordanceState
{
    Unavailable = 0,   // CV-4: ordinal 0 é o valor seguro
    Available   = 1,
    Lost        = 2
}

public interface ITrayAffordance
{
    TrayAffordanceState State { get; }
    event EventHandler<TrayAffordanceChangedEventArgs> StateChanged;   // na dispatcher de UI
    Task<TrayAffordanceState> EstablishAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);
}
```

- **Invariante central:** o **único** caminho que atribui `Available` é o que leu `true` do `NIM_ADD`
  **e** do `NIM_SETVERSION` pela fronteira nativa. **Não existe segundo produtor de `Available`**
  (fixado por teste, CV-4).
- **Sem `Unknown`** — convidaria a ser tratado como bom. `Unproven` é interno e nunca sai (secção E).
- `Unavailable` (nunca estabelecido nesta sessão) ≠ `Lost` (estabelecido e depois perdido): a S2
  precisa da diferença para a mensagem.
- **CV-5:** `szTip` e `hIcon` são **estáticos** — string localizada da aplicação e recurso do pacote.
  **Nenhum nome de servidor, endereço, contagem ou valor de snapshot atravessa o caminho do shell.**
  Fixado por teste.
- A S2-T **emite estado e não materializa UI**.

## H — Hosting do flyout — **OPÇÃO A FECHADA**

**Janela XAML mínima, nunca ativada.** Decisão do Prism, aceite pelo humano. Razões registadas:

1. **Decisiva:** um `TrackPopupMenuEx` seria **claro mesmo em modo escuro** — o Windows 11 só escurece
   menus Win32 para apps que usem **APIs não documentadas do uxtheme** (`SetPreferredAppMode`,
   `FlushMenuThemes`), e depender de ordinais não suportados é **inaceitável para submissão à Store**.
2. A opção nativa exigiria `SetForegroundWindow` no nosso HWND — **exatamente o roubo de foco que a
   S-1(A) mediu ausente**.
3. No Windows 11 o próprio shell usa flyouts WinUI na tray.

**Custo aceite:** a janela auxiliar existe, e a sua invisibilidade prova-se **por medição** (item I da
matriz O), não por construção. A S-1(A) já mediu esse comportamento na janela equivalente do WinUIEx:
0×0, sem botão na barra de tarefas, sem roubo de foco, flyout com as 5 entradas num processo nunca
ativado.

### Tema — ressalva ALTA do Prism, e um achado que a agrava

O tema é aplicado **por raiz** (`ThemeService.cs:33`, `_rootElement.RequestedTheme = …`), não à
`Application`. A janela do flyout tem raiz própria e, em headless, **não existe raiz da MainWindow** —
`ThemeService.Attach` só é chamado em `MainWindow.xaml.cs:54`.

**Correção:** `IThemeService` passa a **multi-raiz** (`Attach`/`Detach`, aplicando a todas as raízes
registadas); a raiz do host do flyout regista-se e recebe `RequestedTheme` **do mesmo serviço**, pelo
que flyout e Dashboard nunca divergem.

> **Achado que tenho de reportar em vez de contornar:** o Prism pede que o `ThemeService` resolva *a
> preferência persistida* sem `MainWindow`. **Essa preferência não é persistida em lado nenhum.**
> `ThemeService.Current` é memória pura, semeado a `System`, e só é mutado pelo `SettingsViewModel`
> (`:103`); não existe camada de persistência de definições em `Infrastructure/Persistence`. Ou seja,
> hoje **ninguém** consegue honrar "preferência persistida", e a escolha do utilizador perde-se a cada
> arranque. A S2-T fecha a **divergência** (mesma `Current` nas duas raízes), que é o defeito que o
> Prism identificou; a **persistência** é lacuna pré-existente, fora do âmbito da S2-T, e fica para o
> humano encaminhar.

### Menu — divergência a resolver, não a escolher por mim

Prism diz "ordem e etiquetas **inalteradas**" e lista *Abrir · Atualizar todos · Modo compacto ·
Definições · sep · Sair*. O **código atual** (`WinUIExTrayIconAdapter.OnContextMenu`) e a **captura da
S-1(A)** mostram *Abrir · Modo compacto · Atualizar todos · Definições · sep · Sair* — "Modo compacto"
e "Atualizar todos" trocados. Como a instrução é **não redesenhar**, mantenho a ordem do código e da
medição, e **peço ao Prism que confirme** qual das duas é a pretendida.

## I — Sinalização de degradação

S2-T emite, S2 age. `Unavailable` no arranque `--background` → a S2 nunca permanece headless,
materializa Definições > Segundo plano com InfoBar no primeiro frame visível, sessão `FOREGROUND`
degradada, X/Alt-F4 = saída verdadeira nessa sessão, `BackgroundMonitoringEnabled` **inalterado**.
`Lost` já em `BACKGROUND` → mesmo caminho. A S2-T **nunca** regressa a background automaticamente.

## J — Migração para fora do `WinUIEx.TrayIcon`

1. `ITrayIconAdapter` mantém os cinco eventos e ganha a superfície de `ITrayAffordance`.
2. `ShellTrayIconAdapter` (nosso) substitui o `WinUIExTrayIconAdapter`, que é removido com os testes.
3. `TrayService` deixa de inferir sucesso de `Start()` e passa a consumir `EstablishAsync`;
   `DegradeWithoutTrayIcon()` passa a ser acionado por estado, não por exceção.
4. WinUIEx continua como dependência para `WindowManager`/extensões de janela. Sem fork, sem remoção.
5. Trabalho novo assumido: ícone com DPI e libertação do `HICON` (secção C), e a raiz XAML do host.

## K — Testes e contra-provas por mutação

Seam no **limite nativo**: `INativeTrayRegistration { bool Add(); bool SetVersion(); bool Delete(); }`.
O duplo devolve booleanos; **a máquina de estados sob teste é a de produção**. Tempo por
`FakeTimeProvider`.

| # | Mutação na produção | Teste que TEM de falhar |
|---|---|---|
| 1 | `NIM_ADD` falhado tratado como sucesso — **é a regressão de hoje** | `Add=false` ⇒ nunca `Available`; degradação emitida |
| 2 | `NIM_ADD` bem-sucedido tratado como falha | `Add=true` ⇒ `Available` exatamente uma vez |
| 3 | re-registo em `TaskbarCreated` removido | após a mensagem, `Add` reinvocado e `Available` reemitido |
| 4 | esgotamento de retries a permanecer em `BACKGROUND` | 3 falhas ⇒ `Unavailable` e o consumidor sai de background |
| 5 | encaminhamento de callback removido | callback v4 válido ⇒ `OpenRequested`/flyout exatamente uma vez |
| 6 | sinal `Lost` suprimido | perda após `Available` ⇒ `Lost` observado |
| **7** | **validação da mensagem removida** *(CV-6)* | **mensagem forjada com `uCallbackMessage` errado e `uID ≠ 1` é ignorada** |

Mais: `Unavailable` no ordinal 0 e ausência de segundo produtor de `Available` (CV-4); `szTip`/`hIcon`
estáticos (CV-5); exatamente 3 tentativas e não 4; atrasos exatos sob tempo falso; cancelamento entre
tentativas; **debounce, um-em-voo, teto por janela, e reposição do orçamento por sucesso** (CV-2);
`TaskbarCreated` idempotente sem ícone duplicado e **sem `Lost` espúrio**; `ReleaseAsync` chama
`NIM_DELETE` exatamente uma vez.

## L / M / N — veredictos **PENDENTES**

**Atlas:** modelo de thread (B), `Unproven` interno e limitado (E), coalescing/ordenação (E), teto e
reposição de orçamento, derivação dos números (F).
**Prism:** divergência de ordem do menu (H), multi-raiz de tema e o achado de não-persistência (H),
foco de teclado sem ativar a janela auxiliar (D).
**Vigil:** CV-1 a CV-6 tal como redigidos acima; confirmação de que `Lost` é inalcançável por mensagem.

## O — Matriz de QA de plataforma real — tudo `NOT_RUN`

| | Caso | Estado |
|---|---|---|
| A | registo inicial no shell real | `NOT_RUN` |
| B | tray headless real | `NOT_RUN` |
| C | menu/flyout, incluindo teclado e tema correto | `NOT_RUN` |
| D | reinício real do Explorer em `FOREGROUND` | `NOT_RUN` — **autorização humana** |
| E | reinício real em `BACKGROUND`/headless | `NOT_RUN` — **autorização humana** |
| F | `TaskbarCreated` entregue **pelo próprio Explorer** | `NOT_RUN` (hoje `NOT_OBSERVED`) |
| G | restauro bem-sucedido do ícone | `NOT_RUN` |
| H | sem ícone duplicado | `NOT_RUN` |
| I | sem janela auxiliar visível nem botão na barra de tarefas | `NOT_RUN` |
| J | degradação por falha de registo forçada | `NOT_RUN` |
| K | sem consola / sem WER | `NOT_RUN` |
| **L** | **`FORCED-TERMINATION TRAY CLEANUP`** *(novo, S6)* | `NOT_RUN` |

**L, critérios:** após `TerminateProcess` pelo watchdog — nenhum ícone **permanentemente** órfão;
renderização obsoleta transitória aceitável **só** se removida naturalmente pelo Windows ao detetar o
HWND dono morto; o ícone obsoleto **não pode continuar interativo**; o lançamento seguinte cria
**exatamente um** ícone; sem duplicados; tray funcional após reinício; sem consola/WER.
**Não se exige `NIM_DELETE` do processo morto. Órfão persistente = FAIL.**

**CV-3, comportamento sob `TerminateProcess`:** não há `NIM_DELETE`; o órfão transitório é esperado;
**não há fuga de handle** — o kernel reclama os handles USER/GDI (o `HICON`, o HWND e a classe de
janela) na morte do processo; e como o `uID` é estável e o HWND antigo está morto, o lançamento
seguinte produz exatamente um ícone.

**O Explorer não é reiniciado nesta volta.**
