# Server Monitor

Server Monitor é uma aplicação desktop WinUI 3 para Windows, concebida como um painel pessoal de monitorização de servidores com uma interface calma, compacta e baseada em materiais nativos.

## Estado atual

O repositório contém o **Milestone 9 (Compact Widget Mode)**, construído sobre a fundação SSH segura do M3, os collectors Linux/macOS do M4, o motor de monitorização automática do M6, a descoberta passiva do M7 e o system tray + notificações do M8:

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
- recolha e apresentação de métricas reais Linux/macOS;
- monitorização automática com limites de concorrência, retries transitórios, estado de saúde e refresh manual;
- descoberta local passiva de serviços SSH por mDNS/DNS-SD (`_ssh._tcp.local.`), sem scan de subnet ou portas;
- deduplicação entre IPv4/IPv6 e interfaces, remoção com grace/expiry e limites contra floods;
- sugestões de rede separadas dos servidores configurados, com ações Adicionar e Ignorar;
- persistência não sensível de dispositivos ignorados e reset nas Configurações;
- Adicionar a partir da descoberta reutiliza integralmente o fluxo M3 de credenciais, teste, host-key probe e confiança explícita;
- ícone único na área de notificação, com Abrir, Atualizar todos, Configurações e Sair;
- minimizar oculta a janela e mantém monitoring/discovery ativos; fechar com X ou Alt+F4 encerra normalmente;
- Refresh All inclui servidores configurados hidden e reutiliza os limites/single-flight do M6;
- notificações locais por transições reais de saúde, sem alerta no primeiro estado observado;
- deduplicação e cooldown de cinco minutos por servidor/categoria, sem bloquear escalations;
- opção global persistente para notificações, ativa por omissão e sem replay ao reativar;
- nomes apresentados nas notificações são limitados e sanitizados; endpoint, credenciais, trust e erros SSH não são incluídos;
- ServerCards sem métricas fictícias e com estados de conexão e monitorização transitórios;
- modo compacto de widget in-process: a mesma janela alterna entre a apresentação Standard e um widget pequeno e glanceable (`ServerCompactCard`) com estado de saúde, CPU, RAM e disco, reutilizando os mesmos ViewModels e o estado ao vivo do M6;
- entrada para o modo compacto no cabeçalho do dashboard, nas Configurações e no menu do tray; expandir de volta a Standard restaura tamanho/posição sem recriar estado;
- opção "Sempre no topo" exclusiva do modo compacto, desligada por omissão e persistida;
- placement por modo (mode, bounds e DPI de cada modo, always-on-top) persistido em `%LOCALAPPDATA%\ServerMonitor\window-placement.json`, com recuperação de monitor removido, bounds fora do ecrã, coordenadas negativas e mudança de DPI;
- discovery não aparece no modo compacto, mas continua ativo em background.

A descoberta mDNS é local ao segmento de rede visível. Linux pode necessitar de um anúncio compatível, como Avahi; VPNs e descoberta ativa de subnet não fazem parte do M7. Discovery não gera notificações. A aplicação só permanece ativa enquanto o processo estiver em execução: arranque com o Windows, Windows Service e execução após Exit continuam fora do âmbito atual. O Windows pode bloquear ou silenciar banners através das suas definições, Focus Assist/Do Not Disturb ou políticas; isso degrada sem crashar a aplicação.

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
