# Arquitetura — Milestone 8

```text
ServerMonitor.App  ──→ ServerMonitor.Core
        ├────────────→ ServerMonitor.Infrastructure
        └────────────→ ServerMonitor.Collectors

ServerMonitor.Infrastructure ──→ ServerMonitor.Core
ServerMonitor.Collectors     ──→ ServerMonitor.Core
        └────────────────────→ ServerMonitor.Infrastructure
```

`ServerMonitor.App` contém a composição por dependency injection, a shell WinUI 3, Views, ViewModels, diálogos, serviços estritamente relacionados com apresentação, recursos de localização e design tokens. A UI depende de contratos do Core e não acede diretamente à persistência, ao Credential Manager nem à biblioteca SSH.

`ServerMonitor.Core` contém o modelo `Server`, o snapshot normalizado, validação, estados de conexão, identidades de host, contratos e os workflows de gestão de perfil. `Infrastructure` implementa persistência JSON não sensível, Windows Credential Manager, host-key trust, o adaptador SSH.NET e as portas remotas Linux e macOS de comandos fechados. `Collectors` contém os collectors Linux e macOS, os parsers puros e o `MetricsCollectorRouter`; conhece apenas as portas especializadas da Infrastructure, nunca SSH.NET.

## Decisões do bootstrap

- `.NET 10` e Windows App SDK `2.3.1`;
- aplicação unpackaged x64 no Milestone 1;
- MVVM simples, sem framework MVVM adicional;
- `Microsoft.Extensions.Hosting` para DI e lifecycle;
- `Microsoft.Extensions.Logging.Debug` para logging sem ficheiros ou dados sensíveis;
- recursos RESW externos e `pt-BR` como idioma padrão/fallback;
- `DesktopAcrylicBackdrop` para o backdrop da janela, Acrylic interno com tint subtil e fallback opaco/alto contraste;
- shell leve com `Frame`, sem sidebar permanente;
- `ServerFullCard` e `ServerCompactCard` partilham `ServerCardViewModel` e o controlo de ações; apenas a apresentação standard está exposta.

`DesktopAcrylicKind.Thin` foi avaliado através de `DesktopAcrylicController`. Na combinação validada de Windows 11 e Windows App SDK 2.3.1, a ligação direta do controller causou uma falha nativa durante o arranque; por isso a implementação utiliza o `DesktopAcrylicBackdrop` suportado pela shell. `SystemBackdropElement` também não é aplicado a cada card: repetir backdrops de sistema em superfícies adjacentes aumentaria custo e sobreposição de materiais, enquanto o Acrylic interno já fornece a separação necessária.

## Fluxo de configuração e segredos

```text
View / ViewModel
      │
      ▼
IServerProfileService
      │ coordena configuração e referência opaca
      ▼
IServerService ──────────────→ %LOCALAPPDATA%\ServerMonitor\servers.json
      │
      └─ IServerCredentialStore → Windows Credential Manager
```

O ficheiro contém configuração não sensível: identidade lógica, endpoint, utilizador, OS, método de autenticação, caminho da chave privada e uma referência GUID opaca. Passwords, passphrases e conteúdo de chaves nunca são serializados.

## Fluxo SSH seguro

```text
ViewModel → ISshConnectionService → probe sem credencial
                                  ├─ host desconhecido → aprovação explícita
                                  ├─ mismatch → bloqueio
                                  └─ host confiado → autenticação → uname -s
```

`SSH.NET` fica encapsulado em `Infrastructure/SSH`. A primeira ligação não envia a credencial: captura a host key com autenticação `none`, apresenta a fingerprint SHA-256 e só repete a ligação autenticada após confiança explícita. A confiança fica separada em `%LOCALAPPDATA%\ServerMonitor\known-hosts.json`. Estados de conexão são transitórios, tipados e não implicam monitorização periódica.

Detalhes de transporte, política criptográfica e credenciais constam das ADR-006 e ADR-007.

## Pipeline manual de métricas Linux e macOS

```text
ServerFullCard → ServerCardViewModel → IServerMetricsCollector
                                      │
                                      ▼
                            MetricsCollectorRouter
                       (por Server.OperatingSystem;
                        Auto resolvido via uname -s do M3)
                          ┌───────────┴───────────┐
                          ▼                       ▼
                 LinuxMetricsCollector    MacOsMetricsCollector
                          │                       │
                          ▼                       ▼
              ILinuxMetricsRemoteSource   IMacOsMetricsRemoteSource
                          └───────────┬───────────┘
                                      ▼
                  trust probe → autenticação → sessão única
                                      │
                                      ▼
                   catálogo Linux/macOS fixo → raw output
                                      │
                                      ▼
                      parsers puros → ServerMetricsSnapshot
```

O refresh é iniciado apenas pelo utilizador e protegido por single-flight por `ServerId`. Snapshot, estado de refresh e último erro vivem num store transitório em memória; não são escritos em `servers.json`. A UI recebe apenas `IServerMetricsCollector` (o router) e nunca recebe um executor ou texto shell; não conhece as diferenças entre Linux e macOS.

Falhas individuais de parsing mantêm a métrica como `null`, distinguindo unknown de zero. Falhas de trust, autenticação, transporte, timeout ou cancelamento preservam o resultado SSH tipado e não devolvem snapshot. Os catálogos e a estratégia de falha parcial constam das ADR-008 (Linux) e ADR-010 (macOS) e de `docs/metrics.md`.

## Monitorização automática (Milestone 6)

O `MonitoringEngine` (App, `IHostedService`) agenda uma recolha por servidor num loop `async` próprio, com limite global de concorrência, retries só para falhas transitórias e todo o tempo através de um `TimeProvider` injetável. Publica `ServerMonitoringState` (saúde, refresh em curso, stale, timestamps, último erro) no `IServerMonitoringStateStore` transitório. A UI observa esse estado (o card não tem timers) e o refresh manual é encaminhado por `IMonitoringEngine.RefreshNowAsync`, partilhando o single-flight do agendador e reiniciando o intervalo. A saúde usa `ServerHealth` + `MonitoringThresholds`, distinta do estado de conexão SSH. Detalhes de loop, sleep/resume, hidden, logging e política constam da ADR-011.

## Descoberta passiva de rede (Milestone 7)

```text
_ssh._tcp.local. → TmdsMdnsServiceBrowser → IMdnsServiceBrowser
                                                │ Found/Updated/Removed validados
                                                ▼
                                      ServerDiscoveryService
                                  (runtime store + IHostedService)
                                      │                 │
                         ignored-devices.json       Dashboard
                                      │                 │ Adicionar
                                      └────────────┐    ▼
                                               fluxo M3 existente
                                      credenciais → probe → trust → save
                                                               │
                                                               ▼
                                                     M6 via ServersChanged
```

O browser `Tmds.MDns` está encapsulado em `Infrastructure` e observa apenas `_ssh._tcp`. O contrato fakeável `IMdnsServiceBrowser` transporta observações já limitadas e validadas; o `ServerDiscoveryService` agrega anúncios do mesmo service instance entre NICs, preserva IPv4/IPv6, aplica grace/expiry com `TimeProvider` e coalesce notificações materiais. O store runtime é independente dos stores de métricas e monitoring.

Ignorar persiste apenas o hash SHA-256 da identidade provisória DNS-SD em `%LOCALAPPDATA%\ServerMonitor\ignored-devices.json`, separado de `servers.json` e do host-trust. A identidade mDNS serve somente para dedup/UX; após Adicionar, a host-key SSH continua a ser a identidade de confiança. Discovery nunca lê credenciais, inicia SSH, aceita host keys, grava fingerprints ou chama o motor M6. Um servidor guardado é monitorizado apenas pelo reconcile normal de `ServersChanged`.

A descoberta está limitada ao segmento onde multicast é visível; não promete atravessar routers ou VPNs. Linux só aparece se publicar o serviço, por exemplo através de Avahi. Scanning ativo fica deferido para M7.1/futuro. Decisões, limites de input/flood e alternativas Windows constam da ADR-012.

## Shell discreta e alertas locais (Milestone 8)

```text
AppWindow ──minimize──→ TrayService ──→ WinUIExTrayIconAdapter ──→ Shell_NotifyIcon
   ▲                         │
   └──── mesma janela ───────┼── Open / Settings / Exit
                             └── Refresh All → IMonitoringEngine

IServerMonitoringStateStore → ServerAlertCoordinator → IUserNotificationService
              baseline + policy + cooldown             → AppNotificationManager
```

`TrayService` coordena apenas comandos da shell. Existe um único `TrayIcon`, com cleanup explícito e re-registo após `TaskbarCreated` fornecido pelo WinUIEx. Minimizar oculta a janela dos switchers sem recriar a `MainWindow`; Open restaura e ativa essa mesma instância. X/Alt+F4 e Exit seguem o `AppShutdownCoordinator` existente. Em shutdown, o tray é removido na UI thread antes de o host parar, e Discovery, Monitoring, alertas e notifications são drenados na ordem inversa de startup.

`RefreshAllCoordinator` enumera somente servidores configurados — incluindo hidden — e chama `IMonitoringEngine.RefreshNowAsync`. Não executa SSH, não inclui discovery-only e não contorna o limite global nem o single-flight do M6. Chamadas globais simultâneas partilham o mesmo batch e uma falha não cancela os restantes servidores.

`ServerAlertCoordinator` observa o store transitório do M6. A primeira observação por servidor estabelece baseline silencioso; estados repetidos e `Unknown` não notificam. Transições para Warning, Critical e Offline, e recuperações para online/Healthy, seguem a policy da ADR-013. Cooldown de cinco minutos é aplicado à mesma categoria por servidor através de `TimeProvider`, sem bloquear escalations para Critical ou Offline. Hidden não significa mute; discovery nunca produz alertas.

`IUserNotificationService` isola a policy da API Windows. A implementação usa `AppNotificationManager`, regista antes de mostrar e remove o registo no shutdown. O build unpackaged/self-contained extrai do package Runtime 2.3.1 o resource DLL de versão idêntica exigido por `Register`; a ausência do payload falha o build, em vez de produzir uma app silenciosamente sem notificações. `IsSupported()`, falhas de registo e o estado do sistema são tratados como capability gates: processo elevado, indisponibilidade, Focus Assist/Do Not Disturb ou policy resultam em no-op/log, não em falha da monitorização. O conteúdo contém apenas título localizado e display name sanitizado — nunca endpoint, credenciais, fingerprint, paths ou erro SSH. Click, quando entregue pelo Windows, restaura a janela existente.

`NotificationsEnabled` é persistido separadamente em `%LOCALAPPDATA%\ServerMonitor\notification-settings.json`, com `true` como default compatível. Desativar continua a avançar o baseline; reativar não reproduz estados passados.

Decisões de dependency, lifetime, semântica de janela, anti-spam e limitações unpackaged constam da ADR-013.

## Fronteira de apresentação compacta

O Dashboard escolhe a apresentação visual no XAML. O domínio, `IServerService`, persistência e ViewModels não conhecem a largura da janela nem um modo de widget. Um futuro Compact Widget Mode poderá trocar a apresentação sem duplicar estado. Um eventual Windows Widget Provider será uma integração separada, conforme a ADR-005.
