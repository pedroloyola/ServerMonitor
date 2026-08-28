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

## P-011 — `Microsoft.Data.Sqlite` puxa payload nativo SQLite vulnerável (transitivo)
- **Observation (M10):** adicionar `Microsoft.Data.Sqlite` 10.0.0 fez `dotnet build` avisar NU1903 —
  `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 tem advisory HIGH (GHSA-2m69-gcr7-jv3q, CVE do SQLite nativo).
- **Cause:** o meta-package MDS 10.0.0 fixa transitivamente `SQLitePCLRaw.bundle_e_sqlite3` 2.1.11,
  que embrulha um `e_sqlite3` nativo com CVE.
- **Fix:** referência top-level explícita `SQLitePCLRaw.bundle_e_sqlite3` **2.1.13** (mesma linha 2.1.x
  → compat API com MDS 10; sem risco do major bump 3.0.x). `dotnet list package --vulnerable
  --include-transitive` passa a 0. THIRD-PARTY-NOTICES atualizado (SQLitePCLRaw Apache-2.0; SQLite public domain).
- **Learning:** o scan de vulnerabilidades tem de correr **depois** de adicionar qualquer dependência
  com payload nativo; um meta-package pode fixar um native transitivo vulnerável. Preferir override
  minimal na mesma linha major antes de saltar de major. [L-014, §99, §113]

## P-012 — Connection SQLite não disposta quando o pragma pós-open falha
- **Observation (M10, teste `Reset_RecoversFromCorruption`):** com DB corrupta, o reset não recuperava —
  `File.Delete` do ficheiro falhava (lock).
- **Cause:** `OpenConfiguredAsync` fazia `OpenAsync` e depois executava pragmas; num ficheiro corrupto o
  pragma lança **depois** do open bem-sucedido, e a `SqliteConnection` local (fora de `using`) nunca era
  disposta → handle aberto no pool → lock do ficheiro → delete/reset bloqueado. Connection leak real em
  **qualquer** caminho de falha, não só reset.
- **Fix:** `try { open; pragmas; return; } catch { await connection.DisposeAsync(); throw; }`. No reset,
  `SqliteConnection.ClearAllPools()` antes de apagar os ficheiros. Teste ficou verde.
- **Learning:** recurso adquirido antes de um passo que pode lançar tem de ser libertado no caminho de
  exceção **antes** de propagar; um teste de recuperação de corrupção (não só o happy path) revela o leak.
  Fronteira nativa (file lock) só falha com ficheiro real — fake não a exercita. [P-010, L-016, §100]

## P-009 — AppNotification `IsSupported` verdadeiro, mas `Register` falha no self-contained
- **Observation:** o harness M8 executava transições corretamente, mas nenhum alerta Server Monitor aparecia no Notification Center; `AppNotificationManager.IsSupported()` devolvia `true`.
- **Cause:** o output unpackaged/self-contained do Windows App SDK 2.3.1 omitia `Microsoft.WindowsAppRuntime.Insights.Resource.dll`. `Register()` falhava com `0x8007007E` apesar de o DLL redistributable existir no MSIX do próprio package Runtime resolvido.
- **Fix:** referência explícita de versão idêntica a `Microsoft.WindowsAppSDK.Runtime` com path property; target MSBuild `Unzip` extrai apenas o resource DLL para o output e contém errors fail-fast se package/payload faltar.
- **Validation:** sonda real passou de `Register`/`Show` com `Setting=Enabled`; o harness real entregou Warning, Critical, Offline e Recovery no Notification Center, e click restaurou a mesma janela/PID.
- **Learning:** capability check, registo e entrega são gates distintos. Validar a API no binário self-contained real e auditar o payload, não apenas a superfície managed. [L-014, L-002]

## P-014 — Packaged MSIX quebra invariantes que o unpackaged escondia (M12)
- **Observation (M12):** ao empacotar a app em MSIX single-project surgiram três riscos que o build
  unpackaged nunca expôs: (a) a migração de credenciais on-read não era linearizável (Read a migrar podia
  ser sobreposto por Write/Delete concorrente → clobber/ressurreição); (b) o `AppNotificationManager`
  packaged exige extensões de manifesto (`windows.toastNotificationActivation` + `com` server + CLSID +
  `Arguments="----AppNotificationActivated:"`) que o unpackaged regista programaticamente; (c) o workaround
  P-009 do Insights.Resource.dll deixa de ser necessário no packaged framework-dependent.
- **Cause:** invariantes de concorrência/ativação/deployment diferentes entre unpackaged self-contained e
  packaged framework-dependent.
- **Fix:** (a) `SemaphoreSlim(1,1)` a serializar Write/Read/Delete no `WindowsCredentialStore` — como a app
  é single-instance (1 processo), um gate in-process lineariza tudo; verify-before-delete; falha sempre
  não-destrutiva. Teste determinístico: bloquear a 1ª native write dentro do fake (hook) e chamar
  `WriteAsync` **direto na thread do teste** (avança síncrono até `await _gate.WaitAsync`) → `IsCompleted==
  false` prova o gate sem timing. (b) extensões de manifesto adicionadas + CLSID estável. (c) target do
  workaround condicionado a `'$(Packaged)' != 'true'`.
- **Learning:** um perfil de deployment novo (MSIX) precisa de re-auditar concorrência, ativação e payload
  nativo — "passa unpackaged" não prova "correto packaged". O **runtime** packaged (notificação/ativação/
  install) só se valida com smoke real; em Windows **Home** (sem Sandbox/Hyper-V/admin/dev-mode) isso é
  blocker de ambiente honesto (NOT RUN ≠ PASS), não evitável. Single-project MSIX compila headless com
  `dotnet build -p:Packaged=true`. [P-009, P-013, L-016, §27, §106]

## P-013 — Agentes concorrentes num worktree partilhado contendem em locks de build-server (M11)
- **Observation (M11, FULL ORCHESTRA):** múltiplos agentes (Cortex/Relay/Atlas) a compilar em paralelo no MESMO worktree viam erros transitórios de build — locks em ficheiros `obj/` e "ref-assemblies em falta" — não causados pelo código.
- **Cause:** `dotnet build` mantém build-servers persistentes (VBCSCompiler/MSBuild node) que bloqueiam artefactos intermédios; dois builds simultâneos na mesma árvore disputam esses handles. Agrava com `Platforms=x64` (paths `bin/x64` vs `bin`) [ver P-008].
- **Fix:** antes dos gates, `dotnet build-server shutdown` e/ou `--disable-build-servers`; idealmente serializar os builds de gate ou dar worktree próprio a trabalho verdadeiramente paralelo. Rebuild limpo `--no-incremental` do alvo exato antes de contar (P-008).
- **Learning:** orquestração multi-agente num worktree único precisa de disciplina de build — o Boss coordena a serialização dos gates finais ou isola por worktree. Um erro de build transitório sob concorrência não é necessariamente regressão de código: confirmar reexecutando isolado. [P-008, L-011]

## P-015 — Exe stale ignora silenciosamente o flag `--qa-*` e mostra dados REAIS (W1)
- **Observation (W1, screenshots do website):** lançado o exe Debug com `--qa-store-screenshot`, a janela abriu com os servidores reais já configurados (dados live via mDNS/SSH) em vez do catálogo sintético — o harness não aplicou. A captura foi feita antes de se notar.
- **Cause:** o exe em `bin\x64\Debug\...` era de um build ANTERIOR à existência do harness (o build fresco do csproj saiu para `bin\Debug\...` — variante do P-008). Um binário que não conhece o flag não falha: arranca em modo normal, com dados reais.
- **Fix:** capturas apagadas; rebuild + launch do path correto; verificação OBRIGATÓRIA pós-launch de que os dados são sintéticos (UIA dump à procura dos nomes do catálogo QA, ex.: "Home Server/10.0.0.20") ANTES de qualquer captura.
- **Learning:** um flag de harness desconhecido é um no-op silencioso — o gate não é "a app abriu", é "a app abriu COM o catálogo sintético". Confirmar sempre o conteúdo antes de capturar/gravar. [P-008, L-002]

## P-016 — Microsoft Store reserva o 4.º campo da versão (Revision) — tem de ser 0 (M12)
- **Observation (M12, 2026-08-28):** o Partner Center REJEITOU o MSIX `1.0.0.1` antes da submissão: "não é permitido especificar no manifesto uma Versão com número de revisão diferente de zero". A estratégia de usar o 4.º campo (`1.0.0.1`) como "package revision" — deliberadamente escolhida para manter DisplayVersion=1.0.0 — é inválida para a Store.
- **Cause:** a Microsoft Store reserva o 4.º componente `Major.Minor.Build.Revision` (Revision) para uso interno próprio; qualquer pacote submetido tem de ter **Revision = 0**.
- **Fix:** uma atualização de Store incrementa **Major/Minor/Build** e mantém **Revision = 0**. `1.0.0.0 → 1.0.1.0 → 1.0.2.0` (NUNCA `1.0.0.1`/`1.0.0.2`). Aplicado: Package/Identity Version + FileVersion = `1.0.1.0`; AssemblyVersion mantido no baseline `1.0.0.0` (sem churn de binding); Product/InformationalVersion = `1.0.0`. Consequência: `AppVersionProvider.DisplayVersion` (=Major.Minor.Build do runtime) passa a mostrar **1.0.1** quando packaged (lê `Package.Current`) e **1.0.0** unpackaged — divergência documentada e aceite (Product 1.0.0 vs Store package 1.0.1.0). Verificar SEMPRE a Version no **manifesto EXTRAÍDO** do MSIX, não só no output do build.
- **Learning:** separar 5 conceitos distintos — Package/Identity Version (Store, Revision=0), FileVersion, AssemblyVersion (binding), Product/Informational SemVer, e DisplayVersion (runtime). Não usar o 4.º campo como "revisão de pacote" em pacotes de Store. [L-021, §7 ADR-017]
