# Arquitetura — Milestone 2.5

```text
ServerMonitor.App  ──→ ServerMonitor.Core
        │
        └────────────→ ServerMonitor.Infrastructure

ServerMonitor.Infrastructure ──→ ServerMonitor.Core
ServerMonitor.Collectors     ──→ ServerMonitor.Core
```

`ServerMonitor.App` contém a composição por dependency injection, a shell WinUI 3, Views, ViewModels, diálogos, serviços estritamente relacionados com apresentação, recursos de localização e design tokens. A UI depende de `IServerService` e não acede ao repositório nem ao sistema de ficheiros.

`ServerMonitor.Core` contém o modelo `Server`, validação, contratos e o serviço de gestão. `Infrastructure` implementa apenas a persistência JSON de configuração não sensível. `Collectors` continua sem implementação funcional.

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

## Fluxo de gestão

```text
View / ViewModel
      │
      ▼
IServerService
      │ valida, normaliza e gere operações
      ▼
IServerRepository
      │
      ▼
%LOCALAPPDATA%\ServerMonitor\servers.json
```

O ficheiro contém apenas `Id`, `Name`, `Host`, `Port`, `Username`, `OperatingSystem`, `IsHidden` e `CreatedAt`. O modelo não possui campos para passwords, chaves, tokens ou credenciais.

## Fronteira de apresentação compacta

O Dashboard escolhe a apresentação visual no XAML. O domínio, `IServerService`, persistência e ViewModels não conhecem a largura da janela nem um modo de widget. Um futuro Compact Widget Mode poderá trocar a apresentação sem duplicar estado. Um eventual Windows Widget Provider será uma integração separada, conforme a ADR-005.
