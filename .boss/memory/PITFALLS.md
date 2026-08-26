# Pitfalls — erros que já aconteceram (ou riscos conhecidos)

> Formato: **Observation → Cause → Fix → Learning**. Só entradas com valor para trabalho futuro. Sem trivia.
> O Boss consulta isto antes de tarefas semelhantes e adiciona após bugs/investigações relevantes.

## P-001 — Race na factory do `GetOrAdd` (ServerMetricsStore)
- **Observation:** o refresh do `ServerMetricsStore` ocasionalmente congelava.
- **Cause:** race na conclusão da factory do `ConcurrentDictionary.GetOrAdd` (single-flight mal estabelecido); `Task.Yield` **não** elimina a race.
- **Fix:** single-flight race-free com ordenação determinística de inserção/remoção.
- **Learning:** nunca depender do timing do scheduler para estabelecer ownership no dicionário. Review de concorrência verifica ordenação de insert/remove no single-flight.

## P-002 — Token XAML definido ≠ ligado ao runtime
- **Observation:** recurso/token XAML alterado não refletia na app.
- **Cause:** o recurso definido não estava efetivamente conectado ao runtime.
- **Fix/Learning:** validar UI na **aplicação real** (portal/Computer Use, light+dark). "Definido" não é "usado".

## P-003 — Backdrops de sistema por-elemento (WinUI)
- **Observation:** tentar aplicar backdrop de sistema a cada card.
- **Cause:** `AcrylicBrush` ≠ backdrop de sistema por-elemento no desktop; repetir `SystemBackdropElement` em superfícies adjacentes aumenta custo e sobreposição de materiais. `DesktopAcrylicKind.Thin` via `DesktopAcrylicController` causou falha nativa no arranque (Win11 + WinAppSDK 2.3.1).
- **Fix:** usar `DesktopAcrylicBackdrop` da shell + Acrylic interno com tint subtil; não repetir backdrops de sistema por card.
- **Learning:** materiais de sistema WinUI têm quirks de plataforma; validar no runtime real.

## P-004 — Testes de scheduler com wall-clock
- **Observation:** risco de flaky em testes do `MonitoringEngine`.
- **Cause:** depender do relógio real.
- **Fix:** `FakeTimeProvider`; empurrar delays/intervalos para o futuro e conduzir cada ciclo via `RefreshNowAsync` (acorda o loop pelo wait signal); `RetryDelays` de comprimento zero curto-circuitam `Task.Delay`. Teste de stale conduz o ciclo agendado com `Advance` num poll loop (absorve a race register-then-advance).
- **Learning:** concorrência testa-se de forma determinística, nunca por timing.

## P-005 — `unknown` representado como `0`
- **Observation:** tentação de mostrar 0 quando a métrica falha.
- **Cause:** confundir ausência com zero.
- **Fix/Learning:** falha de parsing mantém `null`. `unknown ≠ zero`, sempre.

## P-007 — TCS órfão em RefreshNowAsync (manual-refresh orphan race)
- **Observation:** refresh manual no exato momento em que o servidor é removido/editado (ou no shutdown) podia deixar o `await request.Task` preso para sempre → spinner do card preso.
- **Cause:** `RefreshNowAsync` lia o monitor sob `_reconcileGate` mas **libertava o gate antes** de `EnqueueManual`. Nessa janela, Stop/Reconcile (mesmo gate) cancelavam+removiam o monitor; o `finally` do loop já tinha drenado `_pending`, e o TCS enfileirado depois ficava órfão. Como o card chama sem token, o registration de cancelamento nunca disparava.
- **Fix:** enfileirar o TCS **dentro** do `_reconcileGate` com guarda `_monitors.TryGetValue(id, out m) && !m.Cts.IsCancellationRequested`; senão cair para one-off. `SignalWake`/`await` ficam fora do gate. Ordena Add-vs-Cancel deterministicamente; o drain do loop completa sempre o request. Sem delays. (2026-08-25, MonitoringEngine.cs:129-167)
- **Learning:** ver [L-001]. Ownership de um recurso partilhado (aqui: enfileirar num loop que outro caminho pode cancelar) tem de ser estabelecido **sob o mesmo lock** que o cancela/remove. Nota residual Low (inalcançável): loop a sair por exceção inesperada sem cancelar o Cts — não corrigido por ser especulativo.

## P-008 — Build incremental do `.slnx` deixa dll de teste stale
- **Observation:** `dotnet test --no-build` "passava" mas `--list-tests` não mostrava um teste acabado de adicionar; o full suite reportava contagem inconsistente.
- **Cause:** `dotnet build ServerMonitor.slnx` (incremental) não recompilou `ServerMonitor.App.Tests` com a edição; o runner usou uma dll antiga.
- **Fix/Learning:** ao adicionar/alterar testes, fazer **rebuild limpo** (`--no-incremental`) antes de confiar no resultado, e confirmar a descoberta com `--list-tests`/filtro pelo nome exato. Reforça o QUALITY_BAR: "compila/passou" só conta contra um build fresco. [L-002]
- **Reforço (M9, 2026-08-26):** `dotnet test <slnx> --no-build` e `dotnet build <csproj>` resolvem para **paths de output diferentes** quando o projeto tem `Platforms=x64` (ex.: `bin/Debug/...` vs `bin/x64/Debug/...`). Correr um logo após o outro com `--no-build` testou uma dll stale e mascarou falhas reais. Fazer **rebuild limpo do alvo exato** (`dotnet build <slnx> -c Debug --no-incremental` → `dotnet test <slnx> --no-build`) antes de ler contagens. Usar `--logger trx` + parse de `outcome="Failed"` para veredictos fiáveis.

## P-006 — Enfraquecer SSH por conveniência
- **Observation:** tentação de auto-trust/prompt para simplificar fluxo.
- **Fix/Learning:** fail-closed em host desconhecido/mismatch; sem auto-trust, sem prompt implícito. Segurança SSH é invariante; tradeoff exige aprovação humana.

## P-010 — `DisplayArea.FindAll()` com `foreach` estoura em app unpackaged/self-contained
- **Observation (M9, QA real):** a app não abria — saía com Exit 0 sem janela. Gates unitários (747) todos verdes.
- **Cause:** `AppWindowPlacementAdapter.GetDisplays()` fazia `foreach (var d in DisplayArea.FindAll())`. Enumerar a `IReadOnlyList<DisplayArea>` do WinRT passa pela projeção genérica CsWinRT de `IEnumerable<DisplayArea>` (`As<IEnumerable>(iid)`), que num app **unpackaged/self-contained** lança `InvalidCastException: "ClassFactory não pode fornecer a classe pedida"`. Isto acontecia no ctor da `MainWindow` (`coordinator.Initialize()` → `ApplyMode` → `GetDisplays`), pelo que a app rebentava no arranque em **qualquer** modo. Os testes usam um **fake adapter**, logo nunca exercitavam o `DisplayArea.FindAll()` real.
- **Fix:** iterar por índice — `var all = DisplayArea.FindAll(); for (i=0..all.Count) { var d = all[i]; }`. O indexer (`GetAt`) projeta cada elemento isoladamente e não dispara a QI genérica que falha.
- **Learning:** ver [L-002]/[L-006]. Fronteiras nativas atrás de um fake ficam **não testadas** — só o arranque real da app as valida (NOT RUN ≠ PASS aplica-se a "compila+unit verdes"). Smoke launch real do binário é gate obrigatório para código WinRT novo. Preferir iteração por índice em coleções WinRT `IReadOnlyList<T>` de projeção genérica. [P-002, L-002, L-006]

## P-009 — AppNotification `IsSupported` verdadeiro, mas `Register` falha no self-contained
- **Observation:** o harness M8 executava transições corretamente, mas nenhum alerta Server Monitor aparecia no Notification Center; `AppNotificationManager.IsSupported()` devolvia `true`.
- **Cause:** o output unpackaged/self-contained do Windows App SDK 2.3.1 omitia `Microsoft.WindowsAppRuntime.Insights.Resource.dll`. `Register()` falhava com `0x8007007E` apesar de o DLL redistributable existir no MSIX do próprio package Runtime resolvido.
- **Fix:** referência explícita de versão idêntica a `Microsoft.WindowsAppSDK.Runtime` com path property; target MSBuild `Unzip` extrai apenas o resource DLL para o output e contém errors fail-fast se package/payload faltar.
- **Validation:** sonda real passou de `Register`/`Show` com `Setting=Enabled`; o harness real entregou Warning, Critical, Offline e Recovery no Notification Center, e click restaurou a mesma janela/PID.
- **Learning:** capability check, registo e entrega são gates distintos. Validar a API no binário self-contained real e auditar o payload, não apenas a superfície managed. [L-014, L-002]
