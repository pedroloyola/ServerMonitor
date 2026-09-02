# M13 S2 — desenho do ciclo de vida (ronda de DESENHO, revisão 3, sem implementação)

**Autor:** Cortex (architecture-core) · **Base:** `6b76e9d` · **Branch:** `agent/m13-s2-lifecycle`
**Revisores:** Atlas (fiabilidade/races) · Prism (UX de fecho/tray) · Vigil (segurança de ativação)

Bloqueia o **M13-QA-8**: *o widget deixa de atualizar quando a app é fechada*. Todos os caminhos abaixo
foram lidos do código desta base, com ficheiro e linha; nada aqui vem de memória.

> **Documentos autoritativos:** `.boss/tmp/m13-s2-scope-decision.md` (âmbito) e
> `.boss/tmp/m13-s2-design-corrections.md` (revisão devolvida). Em caso de divergência, prevalecem eles.
>
> **Revisão 3 responde a:** Atlas ALTA-1 (`UnregisterKey`/`Exit` separados) → secção F.2 · Atlas ALTA-2
> (`Dispose` ilimitado) → F.3 · Atlas MÉDIA-3 (segundo `--background`) → H.2 · Atlas MÉDIA-4 (teste J) →
> I.1/J · Vigil C1 (`try/finally`) → C · Vigil C2+C3 e Prism (tray como única saída) → **secção K** ·
> Vigil C4 (`LaunchModePolicy`) → B.2 · Vigil C5 (aviso não revela frota) e decisão de produto do toast →
> D.1 · Vigil C6 (`NOT_RUN` honesto) → J.

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

Tudo síncrono na UI thread: para o timer de persistência · persiste bounds · desliga
`ModeChanged`/`XamlRoot.Changed` · `_windowController.BeginShutdown()` (275) ·
`_trayService.PrepareForShutdown()` (277) · desliga `AppWindow.Changed`/`ActualThemeChanged`/`Closed` ·
**`_shutdownCoordinator.Shutdown()` (281)**. É esta última linha que define semântica de shutdown a partir
de um evento de janela — o acoplamento que o requisito 5 manda remover.

### A.4 O que `AppShutdownCoordinator.Shutdown()` faz (`AppShutdownCoordinator.cs:36-124`)

1. one-shot por `Interlocked.Exchange` (38);
2. **`Program.ReleaseSingleInstanceKey()` (46)** → `AppInstance.GetCurrent().UnregisterKey()` (`Program.cs:42`);
3. `host.StopAsync` num `Task.Run` (55-57), esperado com bound de **5 s** (`DefaultTimeout`, linha 14);
4. timeout → cancela e adia o dispose para uma continuação;
5. `DisposeHost(host)` → **`host.Dispose()` síncrono e SEM bound** (114-124).

Corre **na UI thread**, dentro do `Window.Closed`, portanto bloqueia a UI até 5 s — e depois, no `Dispose`,
por tempo indeterminado. **Confirmado por leitura: o bound de 5 s cobre só o `StopAsync`** (Atlas ALTA-2).

### A.5 A race de posse (requisito 6) — confirmada por leitura

O passo 2 acontece **antes** do passo 3. Entre a libertação da chave e o fim do `StopAsync` há uma janela
de até 5 s em que a chave `"ServerMonitor"` (`SingleInstancePolicy.cs:12`) não tem dono. Um lançamento
nesse intervalo faz `FindOrRegisterForKey` (`Program.cs:92`), fica `IsCurrent`, e arranca um host completo
— **segundo `MonitoringEngine` e segundo escritor do mesmo `widget-state.json`**.

### A.6 A cadeia que produz o snapshot

`MonitoringEngine` (hosted, `App.xaml.cs:276`) → ciclo → `CompositeMonitoringCycleObserver`
(`App.xaml.cs:193-201`) → `WidgetSnapshotRecorder` (`WidgetSnapshotRecorder.cs:34`) →
`AtomicWidgetStateWriter` → `widget-state.json`. **O snapshot vive exatamente enquanto o host viver.**

### A.7 Estado atual de "esconder"

Minimizar → `AppWindow` `Minimized` (`MainWindow.xaml.cs:201-209`) → `TrayService.HandleWindowMinimized`
→ `ApplicationWindowController.HideForMinimize` (`ApplicationWindowController.cs:42`):
`IsShownInSwitchers = false` + `AppWindow.Hide()`. A janela sobrevive, o host continua, o snapshot
continua. **BACKGROUND já é alcançável hoje** — falta o X seguir o mesmo caminho e o processo saber
terminar sem depender da janela.

### A.8 Factos de plataforma verificados nesta base

- `AppWindowClosingEventArgs` existe no metadata do SDK resolvido (`Microsoft.UI.winmd`): `AppWindow.Closing`
  com `Cancel` é utilizável.
- `AppInstance` expõe **apenas** `FindOrRegisterForKey`, `GetCurrent`, `GetInstances`,
  `RedirectActivationToAsync`, `Restart`, `UnregisterKey`, `Activated`, `Key`, `IsCurrent`, `ProcessId`.
  **Não há** API para recusar um redirect, transferir posse, nem para registar-e-verificar atomicamente.
  É deste facto que sai toda a secção F.
- `IsAlwaysOnTop` é usado **legitimamente** (`AppWindowPlacementAdapter.cs:138-143`) pela definição
  `CompactAlwaysOnTop`. A cobertura O prova **ausência de mutação** nos caminhos de ciclo de vida, não
  proíbe a API.
- **A ativação de notificação hoje restaura SEMPRE o Dashboard**: o adaptador descarta
  `AppNotificationActivatedEventArgs` e emite um `EventArgs.Empty`
  (`WindowsAppNotificationService.cs:297-299`), e o serviço chama `RestoreAndActivate()` incondicionalmente
  (`:184`); o `Show` não põe argumento nenhum na notificação (`:284-291`). **É exatamente a herança
  acidental que o humano proibiu para o toast do aviso** — ver D.1.
- **`TrayService.StartAsync` aborta o arranque se o ícone falhar**: `trayIcon.Start()` dentro de `try`,
  `catch { UnsubscribeLocked(); throw; }` (`TrayService.cs:39-47`) → o `IHost.StartAsync` falha → o catch
  do `OnLaunched` mata a app. Ver secção K.
- **Não existe hoje modo headless nesta base** (zero `--background`/`headless` em `src/`). Por decisão de
  âmbito, a S2 produz o runtime mínimo — B.2.

---

## B. A nova máquina de estados

### B.1 Os três estados

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
                       │    falha de arranque, degradação da secção K)
                       ▼
                  [EXITING]  ── drena ──►  processo termina
                  sem UI nova, sem ativação servida, host a parar
```

`EXITING` é **terminal e one-shot**. **`HEADLESS` não é um quarto estado** — é `BACKGROUND` cujo
`MainWindow` ainda não foi materializado.

**Invariante de saída (novo, secção K):** só se entra ou permanece em `BACKGROUND` enquanto existir pelo
menos uma afordância de saída. Sem tray, `BACKGROUND` não é um estado legítimo.

### B.2 Runtime headless — dentro e fora da S2

| DENTRO da S2 (mínimo primitivo) | FORA (S4 ou proibido) |
|---|---|
| reconhecimento **estrito** de `--background` | manifesto `windows.startupTask` |
| estado inicial = `BACKGROUND` | `StartupTask.RequestEnableAsync` |
| host de monitorização arranca normalmente | UI de definições de arranque |
| tray criado normalmente (com a política K) | orquestração de logon (F1) |
| **sem** Dashboard no arranque | lançamento de irmão pelo provider (F2) |
| ativação legítima posterior **materializa** o Dashboard | `SiblingLaunchSpike`, `SpikeProbe` |
| **mesma** semântica de `AppInstance` | marcadores de arm em `%LOCALAPPDATA%` |
| `RequestExit` sem Dashboard materializado | instrumentação de job object · política de reboot/logon |

A spike da S-1 é **evidência medida, não fonte de código**. Nenhum probe ou marcador de diagnóstico entra
em produção. Sem diálogo escondido de credencial ou confiança.

**`LaunchModePolicy` (Vigil C4).** Função **pura, sem estado**, ao lado da `SingleInstancePolicy`
(`SingleInstancePolicy.cs:18`):

```
LaunchModePolicy.Resolve(IReadOnlyList<string> args) → LaunchMode { Foreground, Background }
```

Contradomínio de **dois** valores, e mais nenhum. Correspondência **exata** e case-insensitive do token
`--background`; sem prefixos, sem `=valor`, sem alias, sem segunda flag, sem parâmetros. Qualquer coisa
que não seja esse token exato é `Foreground`. **Acrescentar valor, parâmetro ou segunda flag reabre o
parecer do Vigil** — está escrito aqui para que o teste o fixe.

---

## C. Quem é dono do `RequestExit`

Serviço novo **`AppLifecycleController`** (singleton, DI, sem XAML), dono do estado e **único** dono de
`RequestExit`. Chamadores legítimos, os únicos quatro: `AppWindow.Closing` com background desligado · tray
"Sair do ServerAlyzer" · falha de arranque · degradação da secção K.

```
RequestExit(reason)                                  // one-shot, idempotente, thread-safe
  1. transição atómica para EXITING (CAS)            // se já EXITING → devolve
  try
  {
      2. para de aceitar/materializar trabalho de foreground
      3. remove o ícone do tray EXPLICITAMENTE + esconde a UI (tolera não haver janela)
      4. StopAsync ordenado, com o bound existente (5 s)
      5. lança o Dispose do host em thread de fundo, NUNCA esperado (F.3)
  }
  finally
  {
      6. Application.Current.Exit()  exatamente UMA vez
  }
  // + watchdog de terminação armado no passo 1 (F.3)
```

**Vigil C1 — `try/finally`:** o passo 6 corre **mesmo que 2–5 falhem**. Uma exceção a meio não pode
deixar o processo vivo. E, ao contrário da revisão anterior, **não há passo de `UnregisterKey`** — ver F.2.

**Vigil C3 — a ordem do passo 3:** o ícone é removido **depois** de `EXITING` estar comprometido e
**antes** da drenagem, para não existirem ≤5 s de ícone que não responde. Nunca antes do passo 1: até aí
a saída ainda não está decidida.

`AppShutdownCoordinator` mantém-se para os passos 4–5, **menos** a chamada `ReleaseSingleInstanceKey`
(linha 46) e **com** o `Dispose` deixado de ser esperado.

---

## D. Semântica do `Closing`

```
OnClosing(args):
   se estado == EXITING          → args.Cancel = false   // é o Exit() a fechar: deixa
   senão se background LIGADO
        e existe afordância de saída (K)
                                 → args.Cancel = true
                                   HideToBackground()
                                   TryNotifyFirstUserBackgroundClose()   // D.1
   senão                         → args.Cancel = true
                                   RequestExit(UserClosedWindow)
```

Nunca deixamos a plataforma destruir a janela por decisão dela: ou escondemos, ou entramos no caminho
autoritativo. Com background ligado o processo é o mesmo, o tray fica, o motor vive, o snapshot continua —
**é isto que fecha o QA-8**.

### D.1 Aviso de primeiro fecho — **toast**, com ativação desenhada de propósito

Superfície aprovada pelo humano: **Windows App Notification (toast)**, por ser a única superfície efémera
que continua visível depois de o Dashboard desaparecer.

**Gatilho, e só este:** primeiro X/Alt-F4 **iniciado pelo utilizador**, de `FOREGROUND` para `BACKGROUND`,
com `BackgroundMonitoringEnabled = true`. **Não** em: lançamento `--background` · minimizar · arranque/logon
· ativação por protocolo · restaurar · ativação headless · qualquer background não iniciado pelo utilizador.
O gatilho vive no `Closing` (D) — o único ponto onde a origem "utilizador fechou a janela" é conhecida —
e nunca em `HideToBackground`, que é partilhado com o minimizar.

**Propriedades:** uma só vez · não modal · não bloqueante · **não atrasa nem cancela** o esconder (é
notificação de facto consumado) · **sem nomes de servidores, endereços ou contagens de frota** (Vigil C5) ·
persistido como **tentado/mostrado após a tentativa única**, mesmo que as notificações do Windows estejam
desativadas ou indisponíveis, e **nunca reinsiste**.

**Ativação do toast — desenhada, não herdada (aviso crítico do humano).** Hoje qualquer clique numa
notificação chama `RestoreAndActivate()` incondicionalmente (A.8). Isso é inaceitável aqui: o toast que
diz "continuo em segundo plano" não pode **desfazer** o esconder que o utilizador acabou de pedir. Logo:

1. o adaptador de plataforma deixa de descartar `AppNotificationActivatedEventArgs` e passa a expor os
   **argumentos** da notificação;
2. toda a notificação passa a levar um argumento de **tipo** (`AddArgument`); as de saúde levam o tipo que
   mapeia para o comportamento atual (restaurar), **explicitamente**, e não por omissão;
3. o toast do aviso leva um tipo próprio cuja ação é **no-op**: dispensa e mais nada — sem
   `RestoreAndActivate`, sem materialização, sem navegação, sem contar para o P;
4. em `EXITING`, qualquer ativação de notificação é descartada (F.1);
5. **clique tardio com o processo já morto:** a ativação é do tipo `AppNotification` e o Windows lança a
   app; sem contexto de background a preservar, propõe-se tratá-la como **lançamento normal de foreground**
   (arrancar escondido a partir de um clique seria pior). Para reduzir o caso, propõe-se **expiração curta**
   no toast do aviso. **Ponto explicitamente marcado para revisão do Prism e do Vigil** — é uma decisão de
   comportamento, não uma consequência técnica.

**Fallback durável nas Definições.** Secção **Background / Segundo plano**, toggle **"Continuar em segundo
plano ao fechar a janela"** (default LIGADO), com descrição completa + `HelpText` de acessibilidade a
explicar: fechar a janela **não** encerra o ServerAlyzer enquanto estiver ativo · a monitorização continua ·
a saída verdadeira está no ícone da área de notificação · **o ícone pode estar nos ícones ocultos do
Windows**. Etiqueta de saída no menu do tray: **"Sair do ServerAlyzer"**, localizada.

**Redação localizada é do Prism** (pt-BR já proposta; faltam pt-PT e en-US). O desenho deixa o slot e as
chaves de recurso; **não inventa texto**.

---

## E. Semântica do `MainWindow.Closed`

**Apenas limpeza local da janela.** Das linhas 262-282 mantém-se a persistência de bounds e o
`unsubscribe`; **saem** `_windowController.BeginShutdown()` (275), `_trayService.PrepareForShutdown()`
(277) e `_shutdownCoordinator.Shutdown()` (281), que migram para o `RequestExit`. Sob a nova máquina de
estados o `Closed` só corre como **consequência** de `Application.Current.Exit()`.

---

## F. Posse do `AppInstance`, terminação e o que fica por fechar

### F.1 O que a API permite, e o que daí decorre

Quem tem a chave registada é o alvo dos redirects; quem chama `FindOrRegisterForKey` com a chave já
registada **não** fica `IsCurrent`, redireciona e sai — **nunca constrói um host**. Não há como recusar um
redirect, transferir posse, nem registar-e-verificar atomicamente (A.8).

**Ativação durante `EXITING` — EXIT WINS** (decisão de produto, fechada): o secundário continua a
redirecionar · o primário reconhece `EXITING` e **não serve** a ativação · sem materialização de Dashboard,
sem reinício do host, sem segundo motor · **a ativação pode ser descartada** · a saída completa-se · um
lançamento posterior, depois de o processo ter saído, arranca normalmente. **`AppInstance.Restart` não é
usado.** Perder um clique durante a drenagem é preferível a violar um Sair explícito.

### F.2 Atlas ALTA-1 — a janela de duplo primário: mitigação real, e o residual honesto

**A crítica está certa.** `UnregisterKey()` e `Exit()` são chamadas separadas e não há CAS entre elas:
"sem trabalho entre ambas" **encurta** a janela, não a fecha. Enquanto o nosso processo estiver vivo com a
chave já libertada, outro processo pode registá-la e tornar-se primário — dois motores, ainda que por
milissegundos. Ordenar não resolve.

**Mitigação derivada: deixar de libertar a chave em vida.** O `RequestExit` **não chama `UnregisterKey`**.
A posse termina num único ponto — **a terminação do processo** — que é atómico do lado do SO e que não
partilhamos com mais ninguém. Não existindo instante em que estejamos vivos e sem posse, **a janela de
duplo primário deixa de existir por construção**, em vez de ser encurtada.

O que **fica** como residual, dito por inteiro:

1. **Ativações perdidas durante a terminação.** Enquanto o processo agoniza, continuamos a ser o alvo dos
   redirects e não servimos nada: o clique é descartado (EXIT WINS). A duração deixa de ser "5 s de
   `StopAsync`" e passa a ser **o tempo de terminação**, hard-bounded pelo watchdog de F.3.
2. **Dependência de uma premissa da plataforma:** que o SO liberta a registo da chave quando o processo
   termina. É a premissa que o código atual já assume por escrito (`Program.cs:44-45`), **não é verificável
   num teste unitário** e passa a item explícito de QA humano (J.3): matar/sair e confirmar que um
   lançamento seguinte fica `IsCurrent`.
3. **Se a terminação falhar**, a chave fica possuída por um processo morto-vivo e **todos** os lançamentos
   seguintes redirecionam para o nada — a app deixa de abrir. É o pior desfecho possível, e é exatamente
   por isso que F.3 deixa de ser opcional: **a mitigação de ALTA-1 só é válida acompanhada da terminação
   garantida de ALTA-2.** As duas ALTA fecham juntas ou não fecham.

**Alternativa considerada e rejeitada:** manter o `UnregisterKey` imediatamente antes do `Exit`. Mantém a
janela de duplo primário (por menor que seja) e troca um risco de *correção* (dois motores a escrever o
mesmo ficheiro) por um risco de *disponibilidade* mais fácil de mitigar por outra via. Preferimos fechar a
correção e limitar a disponibilidade com o watchdog.

### F.3 Atlas ALTA-2 — o tempo de vida completo, não só o `StopAsync`

**A crítica está certa e confirmei-a no código:** `DisposeHost` chama `host.Dispose()` síncrono e sem
bound (`AppShutdownCoordinator.cs:114-124`), tanto no caminho normal como na continuação do timeout. Um
`Dispose` preso deixa o processo em `EXITING` para sempre — o zombie por outra via.

Três medidas, em conjunto:

1. **O `Dispose` deixa de estar no caminho crítico.** É lançado numa thread de fundo (`Task.Run`, thread
   de threadpool, portanto **background**: não impede a terminação do CLR) e **nunca é esperado**. Ao
   chegar aqui o `StopAsync` já drenou os serviços — o `Dispose` é libertação de recursos que o SO reclama
   na terminação de qualquer maneira. O resultado é registado, não aguardado.
2. **Orçamento total, não por passo.** O `RequestExit` passa a ter um **deadline global** contado desde a
   transição para `EXITING`, do qual o bound de 5 s do `StopAsync` é apenas uma parcela.
3. **Watchdog de terminação, armado no passo 1.** Um temporizador **de fundo** (nunca uma thread
   foreground, que por si só atrasaria a saída) verifica, no deadline global, se o processo ainda está
   vivo. Se estiver, escala para **terminação imediata**. Proponho `Process.GetCurrentProcess().Kill()`
   (TerminateProcess): não corre finalizadores, **não gera relatório WER** — o que importa porque a QA-7
   exige ausência de WER — e é o único degrau que não pode ele próprio bloquear. **`Environment.FailFast`
   é explicitamente rejeitado** por escrever um relatório WER.
   O escritor do snapshot é atómico (temp + `File.Replace`), por isso uma terminação abrupta **não pode
   deixar `widget-state.json` corrompido** — é essa propriedade que torna o degrau aceitável.
   **Marcado para revisão explícita de Atlas e Vigil**: é terminação abrupta deliberada, e o valor do
   deadline global é decisão de fiabilidade, não minha.

Com (1)+(2)+(3), "sem zombie" deixa de depender de nenhum caminho correr bem: **passa a haver um limite
superior para o tempo entre `RequestExit` e a morte do processo, aconteça o que acontecer**.

---

## G. Como fecha o A12

```
processo background nunca ativado → menu do tray → Sair do ServerAlyzer → RequestExit
→ host drena → processo termina → SEM ZOMBIE
```

Nenhum passo do `RequestExit` depende de existir janela (o `ApplicationWindowController` já ignora
comandos sem janela anexada, `ApplicationWindowController.cs:119-123`), e o watchdog de F.3 garante o
"termina" mesmo quando algo encrava. Como o runtime headless passa a ser de produção (B.2), **o A12 fecha
em código de produção nesta fatia**, com a verificação final no processo real (J).

---

## H. Ativação em escondido/headless

### H.1 Casos base

- **Escondido → ativação**: `OnActivated` → `ActivationDispatch` → **exatamente um** `RestoreAndActivate`.
- **Headless → ativação**: idêntico, mas a janela não existe: `RestoreAndActivate` **materializa** o
  `MainWindow` e só depois mostra/ativa. Duas implicações: (i) o contador de "um restore por ativação
  lógica" vale **com** materialização pelo meio; (ii) `App.ExecuteActivationIntent`
  (`App.xaml.cs:319-324`) hoje **desiste** quando `_mainWindow is null` — em headless isso descartaria o
  intent, por isso passa a materializar em vez de desistir.
- **EXITING → ativação**: descartada (F.1).
- **Cold-primary**: não passa pelo `ActivationDispatch` — o intent frio entra em
  `Program.ShouldRedirectToExistingInstance` (`Program.cs:100`) e corre no `MarkReady`. Precisa de
  cobertura própria (ressalva do Atlas).

### H.2 Atlas MÉDIA-3 — o segundo `--background`, especificado por inteiro

**Como se distingue.** Um redirect chega a `Program.OnActivated` como `AppActivationArguments`. Para um
lançamento simples, o `Kind` é `Launch` e os dados trazem a linha de comando; o **mesmo**
`LaunchModePolicy.Resolve` (B.2) classifica-a. O `ActivationDispatch` passa a receber dois factos — o
`ActivationIntent?` que já recebia, e um `ActivationOrigin { UserActivation, BackgroundLaunch }` — em vez
de inferir.

**Matriz completa** (o restore é sempre no máximo um, requisito P):

| Ativação recebida | Intent | Estado | Comportamento |
|---|---|---|---|
| protocolo/widget/notificação de saúde | ≠ null | FOREGROUND/BACKGROUND | executa o intent → **1 restore** (materializa se preciso) |
| lançamento normal (sem `--background`) | null | FOREGROUND/BACKGROUND | **1 restore** (é o utilizador a abrir a app) |
| **lançamento `--background`** | null | FOREGROUND | **nada**: sem restore, sem materialização, sem toast |
| **lançamento `--background`** | null | BACKGROUND/headless | **nada**: continua headless, o host já está a correr |
| lançamento `--background` **com** intent de protocolo | ≠ null | qualquer | **o intent ganha** → 1 restore. Uma ação explícita do utilizador não é anulada por uma flag de arranque; combinação improvável, decidida aqui para não ficar por decidir |
| toast do aviso de background | null (tipo no-op) | qualquer | **nada** (D.1) |
| qualquer | qualquer | **EXITING** | **descartada** (F.1) |

**Como se prova:** `ActivationDispatchTests` estendido com a origem como parâmetro — uma linha por célula
da matriz, contando restores, materializações e arranques de host. A célula `--background` em BACKGROUND é
a que impede a S4 de herdar uma janela a aparecer sozinha.

---

## I. Plano de testes

Determinístico = xUnit sem runtime de UI, contra os serviços reais (`AppLifecycleController`,
`AppShutdownCoordinator`, `ActivationDispatch`, `LaunchModePolicy`, `TrayService`), com fakes que **contam**
operações e **bloqueiam sob comando** (barreiras, como na S1).

### I.1 Coberturas A–P

| | Cobertura | Como | Tipo |
|---|---|---|---|
| A | X em foreground, background LIGADO | `Closing` cancela + esconde; host **não** para; ciclos continuam | determinístico |
| B | X em foreground, background DESLIGADO | `Closing` → `RequestExit`; host para; `Exit` 1× | determinístico |
| C | Sair do tray em foreground | `TrayService` → `RequestExit` (deixa de usar `RequestClose`) | determinístico |
| D | Sair do tray em headless | `RequestExit` sem janela materializada; `Exit` 1× | determinístico **de produção** + QA humano |
| E | Sair repetido/concorrente | N chamadas → `Exit` exatamente 1× | determinístico |
| F | escondido → ativação → mesmo processo | 1 restore, 0 hosts novos | determinístico |
| G | headless → ativação → Dashboard materializa | materialização exatamente 1×, 1 restore | determinístico + QA humano |
| H | ativação cold-primary | `PendingActivation`→`MarkReady`, 1 restore | determinístico (novo) |
| I | ativação secundária redirecionada | matriz completa de H.2 | determinístico |
| J | **lançamento durante a drenagem** | **barreira DENTRO do `StopAsync`**: o fake bloqueia; com o stop bloqueado, dispara-se (a) uma tentativa de aquisição de posse — tem de falhar, o processo continua a ser o dono — e (b) uma ativação redirecionada — tem de ser descartada, 0 materializações, 0 hosts novos; liberta-se a barreira e verifica-se `Exit` 1× | determinístico (Atlas MÉDIA-4) |
| K | sem segundo motor | contador de arranques de host = 1 em todos os cenários de J | determinístico |
| L | snapshot continua após esconder | o observer continua a receber ciclos depois de `HideToBackground` | determinístico |
| M | snapshot só para no Sair verdadeiro | o observer para depois de `RequestExit` e não antes | determinístico |
| N | sem zombie | ordem observável (host parado antes de `Exit`; `Exit` sempre que se entra em `EXITING`, mesmo com passos a falhar — Vigil C1) **e** o watchdog: com o `Dispose` bloqueado para sempre, o caminho terminal é atingido dentro do deadline | determinístico + QA humano |
| O | topmost não é mutado | ver I.3 | determinístico |
| P | um `RestoreAndActivate` por ativação lógica | hidden, headless-com-materialização, cold-primary, e **zero** para o toast do aviso | determinístico |

### I.2 Testes de produção acrescentados

`LaunchModePolicy` estrita (token exato aceite; `--background=1`, `--Background:x`, `-background`,
`--backgroundx`, argumento extra → todos `Foreground`; **contradomínio de dois valores**, Vigil C4) ·
arranque headless inicial · headless não cria Dashboard nem entrada na barra de tarefas · monitorização a
correr em headless · **tray existe em headless** · Sair headless termina o processo · ativação headless
materializa · mesmo PID / um motor · **falha do ícone do tray não aborta o arranque** (secção K, Vigil C2)
· **degradação sem afordância de saída** (K) · ícone removido no passo 3 e não antes (C, Vigil C3) ·
`RequestExit` chega ao `Exit` com falhas injetadas em 2–5 (Vigil C1) · **`Dispose` preso não impede a
terminação** (F.3) · ativação durante `EXITING` descartada · **posse mantida durante toda a drenagem** ·
segundo `--background` não restaura (H.2) · **toast do aviso não restaura o Dashboard** e não conta para o
P · aviso disparado só pelo X do utilizador e **não** por minimizar/`--background`/protocolo/restaurar ·
aviso persiste como mostrado mesmo com notificações indisponíveis · **aviso sem nomes/endereços/contagens**
(Vigil C5).

### I.3 Dívida de review herdada

**Atlas — topmost (O).** Duas metades: (i) nenhum ficheiro de navegação/ativação/ciclo de vida referencia
`IsAlwaysOnTop`/`SetAlwaysOnTop`; (ii) teste comportamental — `RestoreAndActivate`, `HideToBackground` e
`RequestExit` contra um fake de placement que **regista mutações de topmost**, exigindo **zero**, com teste
de controlo a confirmar que o `WindowModeCoordinator` em Compact **continua** a mutá-lo.

**Atlas — hidden/headless e cold-primary (P/H).** O contador de restores passa a modelar `Hidden`,
`Headless` (janela inexistente até materializar) e o cold-primary.

**Vigil — `WidgetCardNavigationGrammarTests`.** `TheoryData` derivada de `Enum.GetValues<WidgetSizeHint>()`
e pré-condição positiva (`Assert.NotEmpty`) em `No_snapshot_text_reaches_any_action`.

### I.4 Seams necessários

1. `IAppLifecycleController` (estado + `RequestExit` + `StartedInBackground`);
2. seam de posse de instância única (hoje `Program.ReleaseSingleInstanceKey`, estático) — para J observar
   posse sem `AppInstance` real;
3. `IApplicationWindowController` ganha `HideToBackground()` e materialização tardia;
4. `LaunchModePolicy` pura;
5. seam de terminação (o degrau `Kill` de F.3) para N poder observá-lo sem matar o test host;
6. o adaptador de notificações passa a expor os **argumentos** de ativação (D.1).

---

## J. `NOT_RUN` — honesto (Vigil C6)

**Nenhum destes vira PASS sem QA humano no processo real:**

1. **Interceção real do `AppWindow.Closing`** — a política é testável; a obediência do WinUI ao `Cancel`
   não.
2. **Ausência real de zombie** — X com background ligado → processo vivo, tray presente,
   `widget-state.json` a mudar; tray Sair → processo desaparece do Gestor de Tarefas.
3. **A premissa de F.2**: depois de o processo sair, um lançamento seguinte fica mesmo `IsCurrent` (a
   plataforma liberta a registo da chave na terminação). **A mitigação de ALTA-1 assenta nisto.**
4. **A12 no processo real** — `--background` nunca ativado → tray Sair → processo termina.
5. **Materialização tardia do Dashboard.**
6. **Toast do aviso**: aparece uma vez, não é modal, não atrasa o esconder, **não reabre o Dashboard ao
   ser clicado**, e não aparece em `--background`/minimizar/protocolo/restaurar.
7. **Primeiro arranque com `OnExplicitShutdown`** com o caminho completo montado (requisito 1).
8. **Watchdog de F.3 no processo real** — que o degrau de terminação abrupta não gera WER.

---

## K. O tray é a única afordância de saída em BACKGROUND (Vigil C2+C3, Prism)

**O problema, confirmado no código:** `TrayService.StartAsync` faz `catch { UnsubscribeLocked(); throw; }`
à volta de `trayIcon.Start()` (`TrayService.cs:39-47`). Hoje isso **aborta o arranque**. Em headless as
duas alternativas são igualmente inaceitáveis: abortar deixa o utilizador sem monitorização nenhuma;
continuar sem ícone deixa **um processo a monitorizar que o utilizador não tem como parar** — o zombie do
A12 por outra via.

**Regra de desenho:** *`BACKGROUND` só é um estado legítimo enquanto existir pelo menos uma afordância de
saída.* Daí:

1. **A criação do ícone deixa de ser fatal.** `trayIcon.Start()` falhado é registado e **não** propaga: o
   host arranca à mesma (o motor e o snapshot são o valor do produto).
2. **Retentativa limitada.** A falha mais comum é transitória (Explorer a reiniciar): tenta-se novamente
   um número limitado de vezes, com o `TimeProvider` injetado, sem polling infinito.
3. **Degradação determinística se o ícone continuar ausente**, por esta ordem:
   - **se há Dashboard possível** → força-se `FOREGROUND` (materializa/mostra a janela) **e desliga-se o
     esconder-ao-fechar nesta sessão**: com o X a significar saída verdadeira, a janela passa a ser a
     afordância. Não se altera a definição persistida do utilizador — é uma degradação de sessão.
   - **se nem isso é possível** (headless sem UI) → `RequestExit`. Um processo que o utilizador não pode
     parar não pode continuar a existir.
4. **O ícone é removido só no passo 3 do `RequestExit`** (C), nunca antes: não há ícone morto durante a
   drenagem, e não se remove a única saída antes de a saída estar comprometida.

**Provas:** o arranque com `trayIcon.Start()` a lançar → host arrancado, `StartAsync` **não** lança ·
retentativa até ao limite e depois degradação · degradação em headless sem UI → `RequestExit` ·
`HideToBackground` recusado quando não há afordância · ícone removido depois de `EXITING` e não antes.

---

## Perguntas aos revisores

- **Atlas:** (a) aceitas a mitigação de ALTA-1 — nunca libertar a chave em vida, tornando a terminação do
  processo o único ponto de libertação — junto com o residual declarado em F.2 (ativações perdidas durante
  a terminação, e a dependência da premissa J.3)? (b) o conjunto de F.3 (Dispose fora do caminho crítico +
  deadline global + watchdog com terminação abrupta) fecha mesmo o tempo de vida completo, e qual deve ser
  o deadline?
- **Prism:** as semânticas `FOREGROUND`/`BACKGROUND`/`EXITING`, o toast com ativação no-op (D.1), a
  degradação da secção K (janela a aparecer quando não há tray) e a materialização headless são coerentes
  para o utilizador? Faltam pt-PT e en-US.
- **Vigil:** o `--background` estrito (B.2) continua um modo interno fixo, sem superfície de argumento
  arbitrário, sem prompt escondido e sem alargamento da fronteira de confiança? E o degrau de terminação
  abrupta de F.3 é aceitável dada a atomicidade do escritor do snapshot?

**Não implementei nada.** Paro aqui para nova revisão de Atlas, Prism e Vigil.
