# Server Monitor

Server Monitor é uma aplicação desktop WinUI 3 para Windows, concebida como um painel pessoal de monitorização de servidores com uma interface calma, compacta e baseada em materiais nativos.

## Estado atual

O repositório contém o **Milestone 3 (Secure SSH Connection Foundation)** sobre a base visual e a gestão manual dos milestones anteriores:

- solution e separação inicial entre App, Core, Infrastructure e Collectors;
- shell WinUI 3 com MVVM e dependency injection;
- logging técnico;
- localização `pt-BR`, `pt-PT` e `en-US`, com fallback `pt-BR`;
- temas Light, Dark e System;
- MainWindow com title bar própria, empty state, acesso a Configurações e botão Adicionar servidor;
- Desktop Acrylic com fallback acessível, tokens e controlos glass reutilizáveis;
- adicionar, editar, ocultar, restaurar e remover servidores;
- validação dos campos não sensíveis;
- persistência JSON em `%LOCALAPPDATA%\ServerMonitor\servers.json`;
- autenticação SSH por password ou chave privada, com passphrase opcional;
- segredos protegidos pelo Windows Credential Manager;
- teste explícito de conexão, timeout e cancelamento;
- confiança de host key por fingerprint SHA-256, sem aceitação automática;
- bloqueio de host-key mismatch;
- deteção de Linux/macOS por `uname -s`;
- ServerCards sem métricas fictícias e com estados de conexão transitórios;
- apresentações `ServerFullCard` e `ServerCompactCard` sobre o mesmo estado; o modo compacto ainda não está exposto.

Não existem ainda métricas, monitorização periódica, descoberta de rede, notificações ou system tray.

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
