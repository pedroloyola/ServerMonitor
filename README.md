# Server Monitor

Server Monitor é uma aplicação desktop WinUI 3 para Windows, concebida como um painel pessoal de monitorização de servidores com uma interface calma, compacta e baseada em materiais nativos.

## Estado atual

O repositório contém o **Milestone 11 (Docker + Serviços, READ-ONLY)**, construído sobre a fundação SSH segura do M3, os collectors Linux/macOS do M4, o motor de monitorização automática do M6, a descoberta passiva do M7, o system tray + notificações do M8, o modo compacto do M9 e o histórico local do M10:

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
- discovery não aparece no modo compacto, mas continua ativo em background;
- histórico local de métricas (CPU, memória e disco) persistido em SQLite (`Microsoft.Data.Sqlite`) em `%LOCALAPPDATA%\ServerMonitor\history.db` — LOCAL-FIRST, sem conta, sync nem telemetria;
- gravação como side-effect assíncrona e degradável: uma falha de base de dados (locked, corrupta, disco cheio) nunca interrompe a monitorização nem bloqueia o coletor SSH;
- o histórico grava apenas o resultado do ciclo **fresco** (métrica `null` quando a recolha falha, nunca um valor stale reciclado); `unknown ≠ zero`;
- amostragem de no máximo uma amostra a cada 30 s por servidor, retenção local de 30 dias (limpeza no arranque e diária) e apenas métricas na base de dados (nunca segredos, credenciais, host keys ou erros SSH);
- página de Histórico por servidor (menu de ações → "Histórico") com gráficos de linha minimalistas CPU/Memória/Disco, eixo Y fixo 0–100%, marca `#1846E1`, e representação visual de descontinuidades (offline/sem dados) sem interpolar através de períodos sem medição;
- seletor de intervalo 1 h / 6 h / 24 h / 7 dias / 30 dias com downsampling determinístico (limite de pontos) e cancelamento por geração — trocas rápidas de intervalo nunca deixam uma resposta antiga sobrepor a seleção atual;
- estados de carregamento, sem dados e histórico indisponível; resumo acessível por gráfico;
- ação "Limpar histórico" nas Configurações, com confirmação explícita, que remove apenas o histórico (servidores, credenciais, host keys, ignorados e definições ficam intactos);
- quando uma base antiga ou corrompida fica indisponível, ação explícita "Repor histórico" recria apenas a base local após confirmação; nunca existe auto-delete;
- o histórico só aparece no modo Standard; o modo compacto continua glanceable e sem gráficos;
- observabilidade **read-only** de containers Docker e de serviços geridos (systemd no Linux, launchd no macOS) por servidor — OBSERVAR, NUNCA ADMINISTRAR: sem start/stop/restart/exec/rm, sem sudo, sem execução arbitrária de comandos;
- catálogo SSH **fechado** de seis comandos de leitura, em constantes de código sem qualquer interpolação de host, utilizador, config ou UI: `docker version --format '{{.Server.Version}}'`, `docker ps -a --no-trunc --format '{{json .}}'`, `systemctl list-units --type=service` e `list-unit-files` (LC_ALL=C, `--plain --no-legend --no-pager`), e `launchctl print system` (só domínio system);
- disponibilidade **tipada** por servidor — Docker e serviços falham de forma independente: `NotInstalled`, `PermissionDenied`, `Unavailable`, `Available`, `Error` (e `Unsupported` para OS sem service manager suportado); nunca uma lista falsa vazia;
- Docker monitoriza-se **apenas se** o utilizador SSH já tiver acesso ao daemon (ex.: pertencer ao grupo `docker`); `permission denied` vira `PermissionDenied`, nunca uma escalada com sudo;
- estado e health de container em campos separados (`.State` direto; health parseado do parentético de `.Status`, com `None` = sem healthcheck distinto de `Unknown`); CPU/memória por container ficam `null` (`docker stats` fora do M11);
- serviços systemd com estado runtime (`ActiveState`/`SubState`) e enablement (enabled/disabled/static/masked); launchd expõe apenas estado do domínio system, com `Description`/enablement/sub-state `null` (sem portabilidade falsa);
- `launchctl print system` pode exigir root em macOS moderno; sem sudo, esse caso é tipado como `PermissionDenied` (a validar no host real);
- snapshot de workloads **só em memória**, separado do snapshot de métricas — sem SQLite, sem JSON, sem persistência; reconstruído a cada arranque e removido quando o servidor sai da configuração;
- recolha sem timers novos: ride do sinal de ciclo do M6 (segundo observador, ao lado do histórico, isolado por composite), com política de "due" a cada 60 s por servidor e single-flight por servidor — os workloads nunca coletam mais depressa que o host nem que 60 s, e uma falha de workloads nunca afeta a monitorização de host;
- refresh manual e Refresh All forçam e coalescem a recolha de workloads, ignorando o throttle; carry-over honesto de freshness (listas anteriores ficam *stale*, `unknown ≠ zero`, timestamps não recuam);
- limites contra output hostil: ≤ 512 containers, ≤ 2048 serviços, ≤ 256 caracteres por campo (com flag `Truncated` observável), decode UTF-8 estrito e sanitização de control-chars, sequências ANSI/CSI e overrides bidi (Trojan-Source) sobre texto remoto não confiável;
- o store de workloads nunca contém segredos, credenciais, host keys, username nem erros SSH crus; logging só regista `ServerId`/estado/contagem/duração;
- os workloads têm secção própria no detalhe do servidor (Standard Mode) e **não** alteram o estado de saúde do host, **não** geram notificações, **não** entram no histórico e **não** aparecem no modo compacto; ações remotas (restart de serviços/containers) ficam explicitamente fora do M11.

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
