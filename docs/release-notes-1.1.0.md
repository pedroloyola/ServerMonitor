# ServerAlyzer 1.1.0 — Notas de versão

Esta versão traz o **widget oficial do Windows 11**. A aplicação continua local-first: sem conta, sem
telemetria e sem nuvem.

## Novidade principal — Widget do Windows 11

O ServerAlyzer passa a estar disponível no painel de Widgets do Windows (**Win + W** → *Adicionar
widgets*), em três tamanhos:

- **Pequeno** — o veredito da frota: quantos servidores estão saudáveis e há quanto tempo os dados
  foram lidos.
- **Médio** — telemetria compacta: o indicador da frota e os servidores com CPU, memória e disco.
- **Grande** — a vista mais completa: por servidor, uptime e memória/disco em GB usados e totais, mais
  um resumo da frota (saudáveis, alerta, crítico, offline).

Detalhes de comportamento:

- **Frescura explícita.** Com a aplicação fechada, o widget mostra a última leitura conhecida e diz há
  quanto tempo foi feita — nunca apresenta dados antigos como se fossem atuais.
- **Clicar navega.** Clicar no cartão abre o painel; clicar numa linha abre o painel com esse servidor
  em destaque. A aplicação abre sempre em modo normal.
- **Uma só instância.** Um clique no widget reutiliza a janela existente. Não há segunda janela, segundo
  ícone na área de notificação nem segunda ligação aos servidores.
- **Só-leitura.** O widget não executa comandos nem altera nada nos servidores.

## Privacidade do widget

O widget corre num processo separado que **não faz SSH, não usa a rede e não acede a credenciais**. Lê
apenas um resumo local já sanitizado, guardado em
`%LOCALAPPDATA%\ServerMonitor\widget-state.json`, com um identificador opaco, o nome do servidor, o
estado de saúde e as métricas. Endereços, utilizadores, chaves de host e segredos nunca chegam a esse
ficheiro.

## Requisitos

- **Windows 11 x64** para a aplicação.
- **Windows 11 22H2 (build 22621)** ou mais recente para o widget. Em Windows 11 21H2 a aplicação
  instala e funciona normalmente; apenas o widget não é oferecido pelo sistema.

## Atualização a partir da 1.0.x

A atualização preserva tudo: servidores configurados, definições, tema, posição da janela, confiança de
host keys, credenciais e histórico de métricas. Não há passos manuais nem novos pedidos de password.

## Versões

Produto **1.1.0**; pacote da Microsoft Store **1.1.0.0**. O quarto campo da versão é reservado pela
Store e mantém-se a zero.

## Limitações conhecidas

- A observação de **Docker** requer que o utilizador SSH já tenha permissão de acesso ao Docker no
  servidor. A aplicação é só-leitura e não usa `sudo`.
- No **macOS**, `launchd` reporta serviços a correr e parados, mas não distingue falhas reais de saídas
  de tarefas one-shot.
- O widget do Windows depende do painel de Widgets do sistema, disponível a partir do Windows 11 22H2.

## Privacidade e segurança

Ver [`PRIVACY.md`](../PRIVACY.md) e [`SECURITY.md`](../SECURITY.md). Reporta vulnerabilidades via
GitHub Security Advisories.
