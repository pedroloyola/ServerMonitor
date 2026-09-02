# M13 S2 — desenho do ciclo de vida (ronda de DESENHO, sem implementação)

**Autor:** Cortex (architecture-core) · **Base:** `6b76e9d` · **Branch:** `agent/m13-s2-lifecycle`
**Revisores:** Atlas (fiabilidade/races) · Prism (UX de fecho/tray) · Vigil (segurança de ativação)

Bloqueia o **M13-QA-8**: *o widget deixa de atualizar quando a app é fechada*. Todos os caminhos abaixo
foram lidos do código desta base, com ficheiro e linha; nada aqui vem de memória.

---

## A. Os caminhos de shutdown que existem HOJE

### A.1 Quem termina o processo hoje

Ninguém, explicitamente. `Application.Current.Exit()` só é chamado no caminho de **falha de arranque**
(`App.xaml.cs:445`). Em funcionamento normal o processo morre porque o `DispatcherShutdownMode` está no
default (`OnLastWindowClose`): fechada a última janela, o dispatcher termina, `Application.Start` regressa
e o `Main` (`Program.cs:69`) devolve 0.

**Consequência direta do defeito QA-8:** a única maneira de a janela desaparecer é destruí-la, e destruí-la
mata o processo — logo mata o `MonitoringEngine`, logo o `widget-state.json` congela.

### A.2 Os três disparadores reais

| Disparador | Percurso exato hoje |
|---|---|
| **X / Alt-F4** | destruição da janela → `Window.Closed` → `MainWindow.OnWindowClosed` (`MainWindow.xaml.cs:262`) |
| **Tray → Sair** | `TrayService.OnExitRequested` (`TrayService.cs:151`) → `windowController.RequestClose()` (`TrayService.cs:163`) → `_window.Close()` (`ApplicationWindowController.cs:102`) → **mesmo `Window.Closed` acima** |
| **Falha de arranque** | `App.OnLaunched` catch → `TrayService.PrepareForShutdown()` + `AppShutdownCoordinator.Shutdown()` → `Exit()` (`App.xaml.cs:436-445`) |

Ou seja: **o tray Sair não tem caminho próprio** — pede o fecho da janela e depende do `Closed`.

### A.3 O que `Window.Closed` faz hoje (`MainWindow.xaml.cs:262-282`)

Por ordem, tudo síncrono na UI thread: para o timer de persistência · persiste bounds ·
desliga `ModeChanged`/`XamlRoot.Changed` · `_windowController.BeginShutdown()` (linha 275) ·
`_trayService.PrepareForShutdown()` (277) · desliga `AppWindow.Changed`/`ActualThemeChanged`/`Closed` ·
**`_shutdownCoordinator.Shutdown()` (281)**.

É esta última linha que define semântica de shutdown a partir de um evento de janela — exatamente o
acoplamento que o requisito 5 manda remover.

### A.4 O que `AppShutdownCoordinator.Shutdown()` faz (`AppShutdownCoordinator.cs:36-80`)

1. one-shot por `Interlocked.Exchange` (38);
2. **`Program.ReleaseSingleInstanceKey()` (46)** → `AppInstance.GetCurrent().UnregisterKey()` (`Program.cs:42`);
3. `host.StopAsync` num `Task.Run` (55-57), esperado com bound de **5 s** (`DefaultTimeout`, linha 14);
4. timeout → cancela, adia o dispose para uma continuação, e regressa;
5. `host.Dispose()`.

Corre **na UI thread**, dentro do `Window.Closed`, portanto bloqueia a UI até 5 s.

### A.5 A race de posse (requisito 6) — confirmada por leitura

O passo 2 acontece **antes** do passo 3. Entre a libertação da chave e o fim do `StopAsync` há uma janela
de até 5 s em que a chave `"ServerMonitor"` (`SingleInstancePolicy.cs:12`) não tem dono. Um lançamento
nesse intervalo faz `FindOrRegisterForKey` (`Program.cs:92`), fica `IsCurrent`, e arranca um host completo
— **segundo `MonitoringEngine` e segundo escritor do mesmo `widget-state.json`** enquanto o antigo ainda
drena. O comentário nas linhas 43-45 documenta a intenção (não redirecionar para um processo a morrer),
mas o preço é o segundo motor.

### A.6 A cadeia que produz o snapshot (o que "continuar a atualizar" significa)

`MonitoringEngine` (hosted, `App.xaml.cs:276`) → ciclo → `CompositeMonitoringCycleObserver`
(`App.xaml.cs:193-201`) → `WidgetSnapshotRecorder` (`WidgetSnapshotRecorder.cs:34`, `IMonitoringCycleObserver`)
→ `AtomicWidgetStateWriter` → `widget-state.json`. **O snapshot vive exatamente enquanto o host viver.**

### A.7 Estado atual de "esconder"

Já existe e funciona: minimizar → `AppWindow` `Minimized` (`MainWindow.xaml.cs:201-209`) →
`TrayService.HandleWindowMinimized` → `ApplicationWindowController.HideForMinimize`
(`ApplicationWindowController.cs:42`): `IsShownInSwitchers = false` + `AppWindow.Hide()`. A janela
sobrevive, o host continua, o snapshot continua. **É a prova de que BACKGROUND já é alcançável** — falta
só o X seguir o mesmo caminho e o processo saber terminar sem depender da janela.

### A.8 Factos de plataforma verificados nesta base (não de memória)

- `AppWindowClosingEventArgs` existe no metadata do SDK resolvido (`Microsoft.UI.winmd`, WindowsAppSDK
  2.x): `AppWindow.Closing` com `Cancel` é utilizável. O requisito 2 é implementável com API real.
- `AppInstance` expõe **apenas** `FindOrRegisterForKey`, `GetCurrent`, `GetInstances`,
  `RedirectActivationToAsync`, `Restart`, `UnregisterKey`, `Activated`, `Key`, `IsCurrent`, `ProcessId`
  (metadata `Microsoft.Windows.AppLifecycle.winmd` + docs XML). **Não existe** API para recusar um
  redirect, nem para transferir a posse da chave. A posse termina por `UnregisterKey()` ou por saída do
  processo — mais nada. Todo o desenho de F assenta nisto.
- `IsAlwaysOnTop` **é usado legitimamente** (`AppWindowPlacementAdapter.cs:138-143`) a mando da definição
  de utilizador `CompactAlwaysOnTop` do modo Compact. A cobertura O não pode proibir a API; tem de proibir
  que um caminho de **ativação/ciclo de vida** lhe toque.
- **Não existe hoje modo headless nesta base**: zero ocorrências de `--background`/`headless` em `src/`.
  Ver secção J (decisão de âmbito para o humano).

---

## B. A nova máquina de estados

Um único enum autoritativo, propriedade de um novo serviço `IAppLifecycleController`:

```
                    ┌──────────────── RestoreAndActivate ◄───────────┐
                    ▼                                                │
   [FOREGROUND]  ──── X/Alt-F4 com background LIGADO ──►  [BACKGROUND]
   janela visível                                        janela oculta
   host a correr        ◄── ativação/tray Abrir ──        tray presente
   snapshot vivo                                          host a correr
        │                                                 snapshot vivo
        │                                                       │
        └──────── RequestExit ◄─────────────────────────────────┘
                       │   (X com background DESLIGADO, tray Sair,
                       │    falha de arranque)
                       ▼
                  [EXITING]  ── drena ──►  processo termina
                  sem UI nova, sem ativação servida, host a parar
```

`EXITING` é **terminal e one-shot**: entra-se uma vez, nunca se sai. `HEADLESS` não é um quarto estado —
é `BACKGROUND` cujo `MainWindow` ainda não foi materializado; toda a lógica de estado é a mesma e a
materialização tardia é um detalhe de `RestoreAndActivate`.

---

## C. Quem é dono do `RequestExit`

Um serviço novo, **`AppLifecycleController`** (singleton, DI, sem dependência de XAML), dono do estado
acima e **único** dono de `RequestExit`. Fica em `Services/`, testável sem runtime de UI.

```
RequestExit(reason)                         // one-shot, idempotente, thread-safe
  1. se já EXITING → devolve (cobertura E)
  2. marca EXITING     → a partir daqui nenhuma ativação é servida e nenhum Closing cancela
  3. UI-thread, best-effort: esconde a janela (parece fechada já) + tray silencioso
  4. off-UI: host.StopAsync com bound  (o AppShutdownCoordinator atual, menos o passo da chave)
  5. liberta a posse de instância única  (ver F)
  6. Application.Current.Exit()  exatamente UMA vez
```

Chamadores legítimos, **os únicos três**: `AppWindow.Closing` com background desligado · tray "Sair" ·
caminho de falha de arranque. Ninguém mais fecha a app.

`AppShutdownCoordinator` mantém-se como está para os passos 4/5 (é bom código, já é one-shot e bounded),
mas **perde a chamada `ReleaseSingleInstanceKey` da linha 46**, que passa a ser ordenada pelo controller.

---

## D. Semântica do `Closing`

`AppWindow.Closing` passa a ser o ponto de interceção, e é **o único sítio** que decide entre esconder e
sair:

```
OnClosing(args):
   se estado == EXITING          → args.Cancel = false   // é o Exit() a fechar a janela: deixa
   senão se background LIGADO    → args.Cancel = true
                                   HideToBackground()    // IsShownInSwitchers=false + AppWindow.Hide()
                                   aviso de primeira vez (uma só vez, ver req. 8)
   senão                         → args.Cancel = true    // não deixamos a plataforma destruir
                                   RequestExit(UserClosedWindow)
```

Três consequências deliberadas:

1. **Nunca deixamos a plataforma destruir a janela por decisão dela.** Ou escondemos, ou entramos no
   caminho autoritativo. Isso elimina qualquer semântica de shutdown implícita.
2. Com background ligado o processo é o mesmo, o tray fica, o motor vive, o snapshot continua — **é isto
   que fecha o QA-8**.
3. Com background desligado o utilizador vê a janela desaparecer imediatamente (passo 3 de `RequestExit`)
   enquanto a drenagem corre, em vez de ficar 5 s com a janela na frente.

---

## E. Semântica do `MainWindow.Closed`

Passa a ser **apenas limpeza local da janela** e nada mais. Concretamente, das linhas 262-282 mantém-se a
persistência de bounds e o `unsubscribe`; **saem** `_windowController.BeginShutdown()` (275),
`_trayService.PrepareForShutdown()` (277) e `_shutdownCoordinator.Shutdown()` (281), que migram para o
`RequestExit`. Sob a nova máquina de estados o `Closed` só chega a correr como **consequência** de
`Application.Current.Exit()`, e nunca como causa de coisa nenhuma (requisito 5).

---

## F. Ordenação do shutdown vs. posse do `AppInstance` — derivada da API real

**O que a API permite** (secção A.8): quem tem a chave registada é o alvo dos redirects; quem chama
`FindOrRegisterForKey` com a chave já registada **não** fica `IsCurrent`, redireciona e sai — portanto
**nunca chega a construir um host**. Não há como recusar um redirect nem transferir posse.

**Logo o invariante exigido — "enquanto o processo antigo drena, um processo novo não pode tornar-se host
primário independente" — obtém-se exatamente por uma coisa: MANTER a chave registada durante toda a
drenagem.** Não é uma questão de ordenar chamadas: é a posse ser a única barreira que a plataforma dá.

Ordem proposta em `RequestExit`:

| # | Passo | Porquê aqui |
|---|---|---|
| 1 | marca `EXITING` | a partir daqui `OnActivated` não serve ativações e o `Closing` não cancela |
| 2 | esconde janela + tray silencioso | resposta imediata ao utilizador; nada disto liberta a chave |
| 3 | `host.StopAsync` bounded (5 s) | **a chave continua registada** — um lançamento concorrente redireciona e sai, sem motor |
| 4 | `UnregisterKey()` | ponto terminal: host já parado, nada de nosso a escrever o snapshot |
| 5 | `Application.Current.Exit()` | uma só vez |

**Resíduo assumido e a reportar (não decido):** entre 1 e 4, um clique no widget/atalho redireciona para
este processo, que já não serve ativações — **o clique perde-se** (janela ≤ 5 s). Hoje perde-se de forma
igual *e* ainda arranca um segundo motor, por isso isto é estritamente melhor. Se o humano quiser
recuperar o clique, a API oferece `AppInstance.Restart(args)` — relançar-se a si próprio no ponto
terminal. **É decisão de produto** (um clique durante o fecho reabrir a app pode surpreender) e por isso
fica **fora** deste desenho até haver decisão.

Nota adicional derivada: o passo 4 podia ser omitido (a saída do processo liberta a chave). Proponho
mantê-lo explícito porque encurta a janela entre "host parado" e "processo desaparecido", e porque torna a
ordem legível no código em vez de depender do SO.

---

## G. Como fecha o A12 (Sair explícito a partir de headless)

Hoje é impossível: sem janela não há `Closed`, e sem `Closed` não há `Shutdown()` nem fim de dispatcher.
Com o desenho: tray "Sair" → `RequestExit` → passo 2 tolera `_window == null` (o
`ApplicationWindowController` já ignora comandos sem janela anexada, `ApplicationWindowController.cs:119-123`)
→ passos 3-5 correm iguais → `Application.Current.Exit()` termina o dispatcher → o processo sai.
**Nenhum passo do `RequestExit` depende da existência de uma janela** — é essa a propriedade que fecha o
A12 e que também impede o zombie do requisito 1: `OnExplicitShutdown` só entra acompanhado deste caminho.

---

## H. Comportamento de ativação em escondido/headless

- **Escondido → ativação** (widget, protocolo, notificação): `OnActivated` → `ActivationDispatch` (já
  existe, `8472cf0`) → **exatamente um** `RestoreAndActivate` → `IsShownInSwitchers = true` +
  `AppWindow.Show()` + `Activate()`. Mesmo processo, mesma janela, sem segundo host.
- **Headless → ativação**: idêntico, exceto que `RestoreAndActivate` materializa primeiro o `MainWindow`
  (criação tardia). O contador de "um restore por ativação lógica" tem de continuar a valer com
  materialização pelo meio — é a ressalva do Atlas sobre o `8472cf0` (ver I/P).
- **EXITING → ativação**: recusada (nem restaura, nem executa intent).
- **Cold-primary**: não passa pelo `ActivationDispatch` — o intent frio é entregue em
  `Program.ShouldRedirectToExistingInstance` (`Program.cs:100`) e executado no `MarkReady`. É a segunda
  metade da ressalva do Atlas e precisa de cobertura própria.

---

## I. Plano de testes mapeado a A–P

Determinístico = xUnit sem runtime de UI, contra os serviços reais (o `AppLifecycleController`, o
`AppShutdownCoordinator`, o `ActivationDispatch`, com fakes de janela/tray/host que **contam** operações).

| | Cobertura | Como | Tipo |
|---|---|---|---|
| A | X em foreground, background LIGADO | política de `Closing` → cancela + esconde; host **não** para; contador de snapshot continua | determinístico |
| B | X em foreground, background DESLIGADO | política de `Closing` → `RequestExit`; host para; `Exit` 1× | determinístico |
| C | Sair do tray em foreground | `TrayService` → `RequestExit` (deixa de usar `RequestClose`) | determinístico |
| D | Sair do tray em headless | `RequestExit` com janela nula → mesma sequência | determinístico (real: NOT_RUN, ver J) |
| E | Sair repetido | N chamadas concorrentes → `Exit` exatamente 1× | determinístico |
| F | escondido → ativação → mesmo processo | `ActivationDispatch` + controller: 1 restore, 0 novos hosts | determinístico |
| G | headless → ativação → Dashboard materializa | fake de janela que só existe após materializar; 1 restore | determinístico + QA humano |
| H | ativação cold-primary | cobre o buraco do Atlas: caminho `PendingActivation`→`MarkReady`, 1 restore | determinístico (novo) |
| I | ativação secundária redirecionada | já coberto por `ActivationDispatchTests`, estendido | determinístico |
| J | lançamento redirecionado **durante a drenagem** | fake de `AppInstance` (a posse é um seam) — enquanto `EXITING`, `FindOrRegisterForKey` não cede posse e nenhum host novo arranca | determinístico |
| K | sem segundo motor | contador de arranques de host no fake; J e C garantem 1 | determinístico |
| L | snapshot continua após esconder | o observer do ciclo continua a receber ciclos depois de `HideToBackground` | determinístico |
| M | snapshot só para no Sair verdadeiro | o observer para depois de `RequestExit` e não antes | determinístico |
| N | sem zombie após Sair verdadeiro | ordem observável: host parado **antes** de `Exit`, e `Exit` chamado sempre que o host parou | determinístico + QA humano (processo real) |
| O | sem TOPMOST/`IsAlwaysOnTop` | estende `ActivationForegroundBoundaryTests`: nenhum ficheiro de ativação/ciclo de vida toca `IsAlwaysOnTop`; o único escritor continua a ser o adapter de placement a mando do Compact | determinístico (fecha ressalva do Atlas) |
| P | um `RestoreAndActivate` por ativação lógica | estende `ActivationDispatchTests` com estado real hidden/headless e com o cold-primary | determinístico (fecha ressalva do Atlas) |

**Ressalvas herdadas fechadas aqui:** O e P (Atlas). E, no `WidgetCardNavigationGrammarTests` (Vigil): a
`TheoryData` de tamanhos passa a derivar de `Enum.GetValues<WidgetSizeHint>()` e o
`No_snapshot_text_reaches_any_action` ganha `Assert.NotEmpty` antes de iterar.

**Seams necessários** (nenhum altera comportamento de produção):
1. `IAppLifecycleController` + estado observável;
2. um seam fino sobre a posse de instância única (hoje `Program.ReleaseSingleInstanceKey`, estático) para
   J poder observar posse sem `AppInstance` real;
3. `IApplicationWindowController` ganha `HideToBackground()` (o `HideForMinimize` renomeado/partilhado) e
   tolerância a janela nula já existente.

---

## J. O que fica `NOT_RUN` e precisa de QA humano

Marcado como tal, e **`NOT_RUN` não vira `PASS`**:

1. **Interceção real do `AppWindow.Closing`** — nenhum teste headless prova que o WinUI respeita o
   `Cancel`. A política é testável; o comportamento da plataforma não.
2. **Ausência real de zombie** (N) — só se prova com o processo real: X com background ligado → processo
   vivo, tray presente, `widget-state.json` a mudar; tray Sair → processo desaparece do Gestor de Tarefas.
3. **Materialização tardia do Dashboard** (G) — precisa de janela real.
4. **Aviso de primeira vez** (req. 8) — texto e forma são do Prism; só QA humano confirma que aparece uma
   vez e nunca mais.
5. **Primeiro arranque com `OnExplicitShutdown`** — o requisito 1 exige medir que o zombie da S-1 não
   volta, com o caminho completo montado.

### Decisões que reporto em vez de tomar

1. **Âmbito do headless.** Não existe código headless nesta base (A.8). Os requisitos 9/10 e as coberturas
   D/G/A12 pressupõem-no. **A S2 implementa o arranque headless, ou apenas o ciclo de vida em que a S-1
   assentará?** O desenho acima é válido nos dois casos (nenhum passo do `RequestExit` depende de janela),
   mas o plano de testes muda: sem headless nesta fatia, D e G ficam determinísticos-com-fake e o A12 só
   fecha quando a S-1 aterrar.
2. **Clique perdido durante a drenagem** (F): aceitar o resíduo, ou usar `AppInstance.Restart`? Produto.
3. **Onde vive o aviso de primeira vez** (diálogo na janela ao esconder pela primeira vez, proposta minha)
   e o texto: Prism.
4. **`BackgroundMonitoringEnabled`** segue o padrão já existente de definição booleana persistida
   (`INotificationSettingsService` + `JsonNotificationSettingsService` + toggle em `SettingsViewModel`),
   ficheiro próprio, **default LIGADO**. Confirmo o padrão; a redação da etiqueta é do Prism.

**Não implementei nada.** Paro aqui para revisão de Atlas, Prism e Vigil.
