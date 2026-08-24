# Arquitetura — Milestone 4

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

## Fronteira de apresentação compacta

O Dashboard escolhe a apresentação visual no XAML. O domínio, `IServerService`, persistência e ViewModels não conhecem a largura da janela nem um modo de widget. Um futuro Compact Widget Mode poderá trocar a apresentação sem duplicar estado. Um eventual Windows Widget Provider será uma integração separada, conforme a ADR-005.
