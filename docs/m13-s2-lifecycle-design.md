# M13 S2 — desenho do ciclo de vida (ronda de DESENHO, sem implementação)

**Autor:** Cortex (architecture-core) · **Base:** `6b76e9d` · **Branch:** `agent/m13-s2-lifecycle`
**Revisores:** Atlas (fiabilidade/races) · Prism (UX de fecho/tray) · Vigil (segurança de ativação)

Bloqueia o **M13-QA-8**: *o widget deixa de atualizar quando a app é fechada*. Todos os caminhos abaixo
foram lidos do código desta base, com ficheiro e linha; nada aqui vem de memória.

> **Documento autoritativo de âmbito:** `.boss/tmp/m13-s2-scope-decision.md` (humano, 2026-09-02). Em
> caso de divergência, prevalece esse texto. Esta revisão do desenho incorpora-o: **opção (a) com
> fronteira estrita** (runtime headless mínimo dentro da S2, fontes de arranque continuam na S4),
> **EXIT WINS** na ativação durante `EXITING`, regra de ordenação final chave→`Exit`, e as restrições do
> aviso de primeiro fecho.

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
  de utilizador `CompactAlwaysOnTop` do modo Compact. A cobertura O **não pode proibir a API**; tem de
  provar que os caminhos de navegação/ciclo de vida **não mutam** o estado topmost (aceite pelo humano).
- **Não existe hoje modo headless nesta base**: zero ocorrências de `--background`/`headless` em `src/`.
  Por decisão de âmbito, **a S2 produz esse runtime mínimo** — secção B.2.

---

## B. A nova máquina de estados

### B.1 Os três estados

Um único enum autoritativo, propriedade de um novo serviço `IAppLifecycleController`:

```
                    ┌──────────────── RestoreAndActivate ◄───────────┐
                    ▼                                                │
   [FOREGROUND]  ──── X/Alt-F4 com background LIGADO ──►  [BACKGROUND]
   Dashboard visível                                   Dashboard oculto
   host a correr        ◄── ativação/tray Abrir ──      OU nunca criado
   snapshot vivo                                        tray presente
        │                                               host a correr
        │                                               snapshot vivo
        │                                                       │
        └──────── RequestExit ◄─────────────────────────────────┘
                       │   (X com background DESLIGADO, tray Sair,
                       │    falha de arranque)
                       ▼
                  [EXITING]  ── drena ──►  processo termina
                  sem UI nova, sem ativação servida, host a parar
```

`EXITING` é **terminal e one-shot**: entra-se uma vez, nunca se sai. **`HEADLESS` não é um quarto
estado** — é `BACKGROUND` cujo `MainWindow` ainda não foi materializado. Um processo `--background` nunca
ativado *é* um `BACKGROUND` legítimo; é por isso que o runtime headless pertence a esta fatia.

### B.2 Runtime headless — o que a S2 inclui, e o que NÃO inclui

Fronteira estrita, tal como decidida. **A S2 fornece o ALVO headless seguro; a S4 decide as FONTES que o
podem arrancar.**

| DENTRO da S2 (mínimo primitivo) | FORA (S4 ou proibido) |
|---|---|
| reconhecimento **estrito** de `--background` (token exato, sem gramática livre, sem valores) | manifesto `windows.startupTask` |
| estado inicial do ciclo de vida = `BACKGROUND` | `StartupTask.RequestEnableAsync` |
| construir e arrancar o host de monitorização normalmente | UI de definições de arranque |
| criar o tray normalmente | orquestração de logon (F1) |
| **NÃO** ativar/criar o Dashboard no arranque background | lançamento de irmão pelo provider (F2) — PASSIVO é NO-GO, USER-ACTION por resolver |
| ativação legítima posterior **materializa** o Dashboard | `SiblingLaunchSpike`, `SpikeProbe` |
| **mesma** semântica de `AppInstance` | marcadores de arm em `%LOCALAPPDATA%` |
| `RequestExit` funciona **sem Dashboard materializado** | instrumentação de job object |
| — | política de arranque de processo pelo provider · política de reboot/logon |

A spike da S-1 é **evidência medida e referência**, não fonte de código: reauditar e produzir. **Nenhum
`SpikeProbe` nem marcador de diagnóstico entra em produção.** Sem diálogo escondido de credencial ou de
confiança em modo background.

**Onde vive o reconhecimento.** Uma política pura ao lado da que já existe
(`SingleInstancePolicy.ResolveInstanceKey`, `SingleInstancePolicy.cs:18`), testável sem WinRT:
`LaunchModePolicy.Resolve(args) → Foreground | Background`, com correspondência **exata** e
case-insensitive de `--background`; qualquer outra coisa é `Foreground`. Sem prefixos, sem `=valor`, sem
alias. É esta a superfície que o Vigil audita.

**Consequência no arranque** (`App.OnLaunched`, `App.xaml.cs:408-427`): em `Background` não se resolve nem
ativa o `MainWindow`; o host arranca na mesma e o router é marcado `ready` na mesma. A materialização
tardia passa a ser responsabilidade de `RestoreAndActivate` (secção H).

---

## C. Quem é dono do `RequestExit`

Um serviço novo, **`AppLifecycleController`** (singleton, DI, sem dependência de XAML), dono do estado
acima e **único** dono de `RequestExit`. Fica em `Services/`, testável sem runtime de UI.

```
RequestExit(reason)                         // one-shot, idempotente, thread-safe
  1. transição atómica para EXITING, uma só vez  → se já EXITING, devolve (cobertura E)
  2. deixa de aceitar/materializar trabalho de ciclo de vida de foreground
  3. esconde a UI conforme apropriado (tolera não haver janela)
  4. MANTÉM a chave do AppInstance registada
  5. StopAsync ordenado com a política limitada existente (5 s)
  6. só depois da drenagem: UnregisterKey
  7. Application.Current.Exit()  exatamente UMA vez
```

Chamadores legítimos, **os únicos três**: `AppWindow.Closing` com background desligado · tray "Sair" ·
caminho de falha de arranque. Ninguém mais fecha a app.

`AppShutdownCoordinator` mantém-se como está para os passos 5/6 (é bom código, já é one-shot e bounded),
mas **perde a chamada `ReleaseSingleInstanceKey` da linha 46**, que passa a ser ordenada pelo controller.

**Regra dura de ordenação (decisão do humano):** entre o passo 6 e o passo 7 **não pode haver `await` nem
trabalho significativo**. Se a implementação vier a exigir algum, é tecnicamente obrigatório justificá-lo
e submetê-lo a revisão explícita — não passa em silêncio.

---

## D. Semântica do `Closing`

`AppWindow.Closing` passa a ser o ponto de interceção, e é **o único sítio** que decide entre esconder e
sair:

```
OnClosing(args):
   se estado == EXITING          → args.Cancel = false   // é o Exit() a fechar a janela: deixa
   senão se background LIGADO    → args.Cancel = true
                                   HideToBackground()    // IsShownInSwitchers=false + AppWindow.Hide()
                                   NotifyFirstBackgroundClose()   // slot do Prism, ver D.1
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

### D.1 Aviso de primeiro fecho — restrições, com o conteúdo deixado ao Prism

Mostrado no **primeiro** X/Alt-F4 do utilizador que transite `FOREGROUND → BACKGROUND`. O desenho fixa
apenas as propriedades técnicas:

- **não modal** — não bloqueia a janela nem o utilizador;
- **não atrasa nem cancela** a transição: esconder acontece de qualquer maneira, o aviso é notificação de
  um facto consumado, nunca uma confirmação;
- **sem nag**: uma vez e nunca mais, persistido pela abstração de preferência/estado já existente (mesma
  forma do `INotificationSettingsService`/`JsonNotificationSettingsService`, ficheiro próprio);
- **nunca é mostrado a um processo que arrancou com `--background`** — o controller regista
  `StartedInBackground` no arranque e o aviso é suprimido nesse caso, porque não houve transição de
  utilizador nenhuma;
- **redação e apresentação são do Prism.** O desenho deixa o slot (`NotifyFirstBackgroundClose`) e não
  inventa texto nem escolhe superfície.

`BackgroundMonitoringEnabled` — **default LIGADO**, persistido pelo mesmo padrão; a etiqueta em Definições
é do Prism.

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

| # | Passo | Porquê aqui |
|---|---|---|
| 1 | transição atómica para `EXITING` | a partir daqui `OnActivated` não serve ativações e o `Closing` não cancela |
| 2 | para de aceitar/materializar trabalho de foreground | nada de Dashboard novo, nada de reinício de host |
| 3 | esconde a UI (tolera não existir janela) | resposta imediata ao utilizador; nada disto liberta a chave |
| 4-5 | **chave mantida** + `StopAsync` bounded (5 s) | um lançamento concorrente redireciona e sai, sem motor |
| 6 | `UnregisterKey()` | ponto terminal: host já parado, nada de nosso a escrever o snapshot |
| 7 | `Application.Current.Exit()` | uma só vez, **sem `await` nem trabalho entre 6 e 7** |

O passo 6 podia ser omitido (a saída do processo liberta a chave). Mantém-se explícito porque encurta a
janela entre "host parado" e "processo desaparecido" e porque torna a ordem legível no código em vez de
depender do SO.

### F.1 Ativação durante `EXITING` — **EXIT WINS** (decisão de produto, documentada)

Semântica fixada, e é deliberadamente uma perda aceite:

- o processo secundário **continua a redirecionar** — não se torna primário independente;
- o primário reconhece `EXITING` e **não serve a ativação**: sem materialização de Dashboard, sem
  reinício do host, sem segundo motor;
- **a ativação pode ser descartada**;
- a saída verdadeira completa-se;
- depois de o processo antigo ter saído de facto, um lançamento posterior do utilizador arranca
  normalmente.

**`AppInstance.Restart` NÃO é usado.** Perder um clique durante a drenagem de ≤ 5 s é preferível a violar
um Sair explícito do utilizador relançando a aplicação. Esta é a regra, não um resíduo por resolver.

---

## G. Como fecha o A12 (Sair explícito a partir de headless)

Critério de aceitação obrigatório:

```
processo background nunca ativado → menu do tray → Sair → RequestExit
→ host de monitorização drena → processo termina → SEM ZOMBIE
```

Hoje é impossível: sem janela não há `Closed`, e sem `Closed` não há `Shutdown()` nem fim de dispatcher.
Com o desenho: tray "Sair" → `RequestExit` → o passo 3 tolera `_window == null` (o
`ApplicationWindowController` já ignora comandos sem janela anexada, `ApplicationWindowController.cs:119-123`)
→ passos 4-7 correm iguais → `Application.Current.Exit()` termina o dispatcher → o processo sai.
**Nenhum passo do `RequestExit` depende da existência de uma janela.** Como o runtime headless passa a ser
de produção (B.2), **o A12 fecha em código de produção nesta fatia** e deixa de ser um `NOT_RUN` artificial.

É também por isto que o requisito 1 se cumpre: `OnExplicitShutdown` **só** aterra acompanhado deste
caminho completo — sozinho produziria o zombie medido na S-1.

---

## H. Comportamento de ativação em escondido/headless

- **Escondido → ativação** (widget, protocolo, notificação): `OnActivated` → `ActivationDispatch` (já
  existe, `8472cf0`) → **exatamente um** `RestoreAndActivate` → `IsShownInSwitchers = true` +
  `AppWindow.Show()` + `Activate()`. Mesmo processo, mesma janela, sem segundo host.
- **Headless → ativação**: idêntico, exceto que a janela ainda não existe. `RestoreAndActivate`
  **materializa** o `MainWindow` e só depois mostra/ativa. Duas implicações que a implementação tem de
  respeitar: (i) o contador de "um restore por ativação lógica" continua a valer **com** materialização
  pelo meio; (ii) `App.ExecuteActivationIntent` (`App.xaml.cs:319-324`) hoje **desiste** quando
  `_mainWindow is null` — em headless isso descartaria o intent, por isso passa a materializar em vez de
  desistir.
- **EXITING → ativação**: descartada, por EXIT WINS (F.1).
- **Segundo lançamento `--background` com um primário vivo**: redireciona como qualquer outro, mas **não
  pode restaurar a UI** do primário — seria um arranque automático a trazer o Dashboard à frente sem o
  utilizador pedir. O `ActivationDispatch` passa a distinguir "ativação de utilizador" de "lançamento
  background": só a primeira restaura quando não há intent. (Buraco encontrado ao rever o desenho contra a
  decisão de âmbito; sem isto, a S4 herdaria um surgimento de janela indesejado.)
- **Cold-primary**: não passa pelo `ActivationDispatch` — o intent frio é entregue em
  `Program.ShouldRedirectToExistingInstance` (`Program.cs:100`) e executado no `MarkReady`. É a segunda
  metade da ressalva do Atlas e precisa de cobertura própria.

---

## I. Plano de testes

Determinístico = xUnit sem runtime de UI, contra os serviços reais (`AppLifecycleController`,
`AppShutdownCoordinator`, `ActivationDispatch`, `LaunchModePolicy`), com fakes de janela/tray/host/posse
que **contam** operações. Com o headless a ser de produção, os itens que na versão anterior deste desenho
eram "spike-only" passam a testes de produção.

### I.1 Coberturas A–P do Atlas

| | Cobertura | Como | Tipo |
|---|---|---|---|
| A | X em foreground, background LIGADO | política de `Closing` → cancela + esconde; host **não** para; ciclos continuam | determinístico |
| B | X em foreground, background DESLIGADO | política de `Closing` → `RequestExit`; host para; `Exit` 1× | determinístico |
| C | Sair do tray em foreground | `TrayService` → `RequestExit` (deixa de usar `RequestClose`) | determinístico |
| D | Sair do tray em headless | `RequestExit` sem janela materializada → mesma sequência, `Exit` 1× | determinístico **de produção** + QA humano |
| E | Sair repetido/concorrente | N chamadas → `Exit` exatamente 1× | determinístico |
| F | escondido → ativação → mesmo processo | 1 restore, 0 hosts novos | determinístico |
| G | headless → ativação → Dashboard materializa | fake de janela criada sob procura; 1 restore, materialização exatamente 1× | determinístico **de produção** + QA humano |
| H | ativação cold-primary | `PendingActivation`→`MarkReady`, 1 restore (fecha buraco do Atlas) | determinístico (novo) |
| I | ativação secundária redirecionada | `ActivationDispatchTests` estendido | determinístico |
| J | lançamento redirecionado **durante a drenagem** | seam de posse: em `EXITING` a chave continua possuída, o secundário redireciona, 0 hosts novos, ativação descartada | determinístico |
| K | sem segundo motor | contador de arranques de host; J e C garantem 1 | determinístico |
| L | snapshot continua após esconder | o observer continua a receber ciclos depois de `HideToBackground` | determinístico |
| M | snapshot só para no Sair verdadeiro | o observer para depois de `RequestExit` e não antes | determinístico |
| N | sem zombie após Sair verdadeiro | ordem observável: host parado **antes** de `Exit`; `Exit` sempre que o host parou | determinístico + QA humano (processo real) |
| O | topmost não é mutado por navegação/ciclo de vida | ver I.3 | determinístico |
| P | um `RestoreAndActivate` por ativação lógica | hidden, headless-com-materialização e cold-primary | determinístico |

### I.2 Itens de produção acrescentados pela decisão de âmbito

arranque headless inicial (`LaunchModePolicy` estrita: `--background` reconhecido, tudo o resto não) ·
headless não cria Dashboard nem entrada na barra de tarefas · monitorização a correr em headless · tray
existe em headless · **Sair do tray headless termina o processo** · ativação headless materializa o
Dashboard · mesmo PID / um motor · ativação escondida restaura · cold-primary · **ativação durante
`EXITING` ignorada em segurança** · **chave do `AppInstance` possuída durante toda a drenagem** · saída
verdadeira sem zombie · esconder mantém o snapshot a avançar · exatamente um `RestoreAndActivate` ·
**segundo lançamento `--background` não restaura a UI do primário** (H).

### I.3 Dívida de review herdada, fechada nesta fatia

**Atlas — topmost (O).** Não se proíbe a API. Estende-se o `ActivationForegroundBoundaryTests` para provar
que **nenhum ficheiro de navegação/ativação/ciclo de vida** referencia `IsAlwaysOnTop`/`SetAlwaysOnTop`, e
acrescenta-se um teste comportamental: percorrer `RestoreAndActivate`, `HideToBackground` e `RequestExit`
contra um fake de placement que **regista mutações de topmost** e exigir **zero** mutações, enquanto um
teste de controlo confirma que o `WindowModeCoordinator` em Compact **continua** a mutá-lo (o
comportamento legítimo não pode partir).

**Atlas — hidden/headless e cold-primary (P/H).** O contador de `RestoreAndActivate` passa a modelar
estado real: `Hidden`, `Headless` (janela inexistente até materializar) e o caminho cold-primary, que não
passa pelo `ActivationDispatch`.

**Vigil — `WidgetCardNavigationGrammarTests`.** A `TheoryData` de tamanhos passa a derivar de
`Enum.GetValues<WidgetSizeHint>()`, para que um `WidgetSizeHint` novo não fique por percorrer em silêncio;
e `No_snapshot_text_reaches_any_action` ganha uma pré-condição positiva (`Assert.NotEmpty` sobre as ações
recolhidas) para não poder passar sobre um conjunto vazio.

### I.4 Seams necessários (nenhum altera comportamento de produção)

1. `IAppLifecycleController` — estado observável e `RequestExit`;
2. seam fino sobre a posse de instância única (hoje `Program.ReleaseSingleInstanceKey`, estático) para J
   poder observar posse sem `AppInstance` real;
3. `IApplicationWindowController` ganha `HideToBackground()` (partilhado com o `HideForMinimize` atual) e
   materialização tardia da janela;
4. `LaunchModePolicy` pura (sem WinRT), como a `SingleInstancePolicy` já é.

---

## J. O que fica `NOT_RUN` e precisa de QA humano

Marcado como tal, e **`NOT_RUN` não vira `PASS`**:

1. **Interceção real do `AppWindow.Closing`** — nenhum teste headless prova que o WinUI respeita o
   `Cancel`. A política é testável; o comportamento da plataforma não.
2. **Ausência real de zombie** (N) — X com background ligado → processo vivo, tray presente,
   `widget-state.json` a mudar; tray Sair → processo desaparece do Gestor de Tarefas.
3. **A12 no processo real** — `--background` nunca ativado → tray Sair → processo termina.
4. **Materialização tardia do Dashboard** (G) — precisa de janela real.
5. **Aviso de primeiro fecho** — que aparece uma vez, não é modal, não atrasa o esconder, e **não aparece**
   num processo arrancado com `--background`.
6. **Primeiro arranque com `OnExplicitShutdown`** já com o caminho completo montado (requisito 1).

### Decisões já tomadas pelo humano (fecham o que eu tinha reportado)

1. **Âmbito headless:** opção (a) com fronteira estrita — runtime mínimo na S2, fontes na S4 (B.2).
2. **Ativação durante `EXITING`:** EXIT WINS, sem `AppInstance.Restart` (F.1).
3. **Ordenação:** sem `await` nem trabalho significativo entre `UnregisterKey` e `Exit` (C).
4. **Aviso de primeiro fecho:** não modal, sem nag, não atrasa nem cancela, persistido pela abstração
   existente, nunca em processos `--background` (D.1).
5. **Topmost:** não se proíbe a API; prova-se ausência de mutação (I.3).

### Único ponto ainda por decidir (do Prism, não meu)

Redação e superfície do aviso de primeiro fecho, e a etiqueta de `BackgroundMonitoringEnabled` em
Definições. O desenho deixa o slot e não inventa texto.

---

## Perguntas explícitas aos revisores

- **Atlas:** manter a posse do `AppInstance` durante o `StopAsync` fecha mesmo a janela de duplo primário,
  incluindo a ativação durante `EXITING` (F.1) e o segundo lançamento `--background` (H)?
- **Prism:** as semânticas `FOREGROUND`/`BACKGROUND`/`EXITING`, o aviso de primeiro fecho e a
  materialização headless são coerentes para o utilizador?
- **Vigil:** o `--background` estrito continua um modo de lançamento interno fixo (B.2), sem superfície de
  argumento arbitrário controlado pelo utilizador, sem prompt de credencial escondido e sem alargamento da
  fronteira de confiança?

**Não implementei nada.** Paro aqui para revisão de Atlas, Prism e Vigil.
