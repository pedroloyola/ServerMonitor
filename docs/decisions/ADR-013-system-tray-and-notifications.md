# ADR-013 — System tray e notificações locais

Estado: **aceite para o Milestone 8**.

## Contexto

O M8 mantém o Server Monitor discretamente ativo enquanto o processo estiver em execução. A
janela, o tray e as notificações são consumidores do runtime existente: não substituem o
`MonitoringEngine`, não alteram a classificação de health e nunca atravessam a fronteira de
trust/credenciais do M3. A aplicação continua WinUI 3, x64, unpackaged e self-contained.

## Decisão de tray

Usar **WinUIEx 2.9.3** (MIT), atrás de um adapter próprio e estreito.

- `TrayIcon` usa `Shell_NotifyIcon`, oferece menu WinUI e disposal explícito.
- A versão escolhida volta a adicionar o ícone ao receber `TaskbarCreated`, cobrindo restart do
  Explorer sem polling.
- O pacote depende apenas da família Windows App SDK já usada pela aplicação; não introduz
  Windows Forms.
- O projeto mantém exatamente uma instância do adapter/ícone, com callbacks para Open, Refresh
  All, Settings e Exit. O adapter não conhece SSH, servidores descobertos nem métricas.
- O SVG próprio `ServerMonitorTray.svg` fornece o ícone estável. Não há alteração dinâmica de
  cor por health no M8.

Foi rejeitado interop direto de `Shell_NotifyIcon`: apesar de não exigir dependência, uma
implementação correta teria de possuir `HWND`/`HICON`/menus, `NIM_SETVERSION`, eventos de teclado,
foco, DPI, `TaskbarCreated` e callback/disposal concorrente. Essa superfície é maior e mais
frágil que a dependência MIT selecionada. `H.NotifyIcon.WinUI` também é viável, mas acrescenta
timers, geração de ícones/System.Drawing e Efficiency Mode que o produto não precisa.

## Semântica da janela e lifecycle

- **Minimize**: oculta a janela dos switchers; processo, M6, M7 e tray continuam.
- **Open**: mostra/restaura/ativa a mesma `MainWindow`; nunca cria outra janela.
- **Close/X e Alt+F4**: encerram normalmente. M8 não implementa close-to-tray.
- **Exit no tray**: fecha a mesma janela e entra no `AppShutdownCoordinator` authoritative.

Um fence de shutdown rejeita callbacks tardios de Open/Minimize/Refresh/notification activation.
O tray é removido de forma idempotente na UI thread antes do trecho síncrono que espera
`Host.StopAsync`; depois o host para Discovery, Monitoring, AlertCoordinator e App Notifications.
Não existe Windows Service nem execução depois de o processo terminar.

## Refresh All

Um coordinator próprio enumera somente `IServerService` e chama
`IMonitoringEngine.RefreshNowAsync` por servidor, incluindo hidden. Falhas são isoladas por
servidor e chamadas globais concorrentes são coalescidas. O limite global e o single-flight por
`ServerId` continuam exclusivamente no M6. Discovery-only/ignored não entram neste fluxo.

## Alert policy

`ServerAlertCoordinator` observa snapshots do `IServerMonitoringStateStore`. O primeiro estado
de cada servidor estabelece baseline e nunca gera notificação. A policy inicial é:

- Healthy → Warning: Warning;
- Healthy/Warning → Critical: Critical;
- Healthy/Warning/Critical → Offline: Offline;
- Offline → Healthy/Warning/Critical: Recovery/online again;
- Warning/Critical → Healthy: Recovery;
- Critical → Warning, estado repetido, Unknown ou mudança apenas de stale: silencioso.

O coordinator deduplica por transição e aplica cooldown de cinco minutos à mesma categoria por
servidor usando `TimeProvider`. Escalation para categoria superior e Offline não são bloqueados
por uma categoria anterior. Hidden continua a alertar; discovery nunca alerta.

Quando notificações estão desativadas, o baseline continua a avançar, mas o serviço Windows não
é chamado. Reativar não reproduz histórico: apenas uma transição futura pode alertar.

## Implementação Windows de notificações

Usar `Microsoft.Windows.AppNotifications.AppNotificationManager`, já incluído no Windows App
SDK 2.3.1, atrás de `IUserNotificationService`.

- O handler `NotificationInvoked` é ligado antes de `Register()`; `Unregister()` é chamado no
  shutdown.
- Para app unpackaged, o Windows App SDK regista automaticamente COM/AUMID. Não são criados
  shortcut, CLSID ou manifest UWP paralelos.
- A app é também self-contained. O grafo de componentes 2.3.1 omite do output unpackaged o
  `Microsoft.WindowsAppRuntime.Insights.Resource.dll` que `Register()` carrega, embora o
  redistributable correspondente exista no MSIX do package `Microsoft.WindowsAppSDK.Runtime`
  já resolvido. Um target MSBuild extrai apenas esse DLL de versão idêntica e falha o build se
  não o conseguir colocar no output. Não existe dependência numa instalação externa do runtime.
- `IsSupported()`, a exceção de `Register()` e `Setting` são gates separados. Indisponibilidade,
  processo elevado, policy empresarial, Focus Assist/Do Not Disturb ou bloqueio do utilizador
  degradam para no-op/log apropriado, nunca crash/modal/retry storm.
- Click apenas restaura a janela existente. Não há ações remotas ou argumentos de trust.
- O conteúdo usa `AppNotificationBuilder`, título localizado e display name sanitizado. Não
  inclui host/IP, porta, username, fingerprint, credential reference, path de chave ou erro SSH.

## Segurança e input

O nome de servidor é texto não confiável. Na apresentação são removidos controlos, quebras e
marcadores bidi, whitespace é colapsado e o tamanho é limitado, preservando Unicode/emoji útil.
Isso não altera a identidade persistida. O tray nunca lê Credential Manager, nunca aceita host
key e nunca abre SSH diretamente. Notifications observam apenas o estado produzido por M6.

## Limitações e futuro

- Notificações do Windows App SDK não são suportadas em processo elevado. Erro de registo ou
  capability ausente degrada para tray e monitorização sem crash.
- O Windows decide banners, Notification Center, som e Do Not Disturb.
- Startup with Windows, close-to-tray, regras por servidor, mute, quiet hours, histórico,
  notification de discovery e M9 ficam fora do M8.
