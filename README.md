# Server Monitor

Server Monitor é uma aplicação desktop WinUI 3 para Windows, concebida como um painel pessoal de monitorização de servidores com uma interface calma, compacta e baseada em materiais nativos.

## Estado atual

O repositório contém apenas o **Milestone 1 (Bootstrap)**:

- solution e separação inicial entre App, Core, Infrastructure e Collectors;
- shell WinUI 3 com MVVM e dependency injection;
- logging técnico;
- localização `pt-BR`, `pt-PT` e `en-US`, com fallback `pt-BR`;
- temas Light, Dark e System;
- MainWindow, empty state, acesso a Configurações e botão Adicionar servidor;
- tokens e controlos glass reutilizáveis.

Não existem ainda SSH, descoberta de rede, métricas, persistência de servidores, credenciais, notificações ou system tray.

## Requisitos

- Windows 10 1809 ou superior (Windows 11 recomendado);
- .NET SDK 10;
- arquitetura x64.

## Compilar e testar

```powershell
dotnet build ServerMonitor.slnx
dotnet test ServerMonitor.slnx --no-build
```

Para executar o shell:

```powershell
dotnet run --project src/ServerMonitor.App/ServerMonitor.App.csproj
```

Consulte [CONTEXT.md](CONTEXT.md) para a especificação completa e os limites dos próximos marcos.

## Licença

MIT. Consulte [LICENSE](LICENSE).
