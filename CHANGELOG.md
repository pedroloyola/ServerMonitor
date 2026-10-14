# Changelog

Todas as alterações relevantes deste projeto são documentadas aqui. O formato inspira-se em
[Keep a Changelog](https://keepachangelog.com/) e o versionamento segue [SemVer](https://semver.org/).

## [1.1.7] (release candidate, unreleased)

Candidata a versão do programa REL.0 Recovery. **Não publicada**: nada nesta entrada está na Store,
e a promoção depende dos testes em máquina limpa e da aprovação humana. A Store tem publicada a 1.1.1.0.

- **Privacidade da distribuição.** Os builds Release são determinísticos e já não incluem caminhos da
  máquina de build (PDB, `CallerFilePath`, Source Link). Um gate fail-closed verifica binários e o MSIX
  antes de qualquer upload ou passo da Store (#44).
- **.NET incluído no pacote.** O MSIX passa a ser self-contained para a app e para o WidgetProvider, por
  isso deixa de depender de um .NET 10 instalado na máquina; o Windows App Runtime continua a ser uma
  dependência de framework da Store. Um gate verifica o runtime incluído (#48). Ainda não medido numa
  máquina limpa.
- **Inglês (en-US) como idioma por omissão.** No primeiro arranque: pt-BR → pt-BR, outro `pt*` →
  pt-PT, qualquer outro idioma → en-US. Uma escolha de idioma já guardada nunca é substituída (#46).
- **Pesquisa mDNS apenas a pedido.** A app deixa de abrir um listener UDP no arranque ou em segundo
  plano. A pesquisa na rede local só começa por ação explícita na Visão geral vazia, para quando a
  janela é escondida e é limitada (indica que nada foi encontrado ao fim de 40 s sem resultados; termina ao fim de 10 minutos). Quem já tem
  servidores deixa de ver sugestões automáticas da rede (#47).
- **Piso de versão do pacote.** `release/min-package-version.txt` = 1.1.7.0 (acima da Developer
  1.1.2.2 e das versões de QA local 1.1.3.0–1.1.6.0); o pipeline recusa versões abaixo do piso ou com
  revisão diferente de zero (#49).
- **Endurecimento do pipeline de release.** Um push de tag já não cria uma GitHub Release; a submissão
  à Store verifica a proveniência do build consumido e corre os gates de piso, identidade, privacidade
  e runtime antes de configurar a Store; o estado da Store falha se o modo de publicação não for
  Manual; actions fixadas por SHA; CODEOWNERS (#50). A proteção de branch continua a ser um passo
  humano.
- Documentação de preparação da release, transição da Store e remoção do certificado de teste (#45).

> **Nota de versão.** Produto **1.1.7**, pacote **1.1.7.0** (revisão 0). `AssemblyVersion` mantém-se em
> 1.0.0.0.

## [1.1.0] — 2026-08-31

Integração com os **Widgets do Windows 11**. A app continua local-first, sem conta e sem telemetria.

- **Widget oficial do Windows 11.** O ServerAlyzer passa a aparecer no painel de Widgets (Win + W), em
  três tamanhos — **pequeno**, **médio** e **grande** — com o veredito da frota, métricas por servidor
  (CPU, memória, disco), memória e disco em GB usados/totais, uptime e um resumo da frota.
- **Sempre honesto sobre a frescura dos dados.** Com a app fechada o widget mostra a última leitura
  conhecida e indica há quanto tempo foi feita, em vez de fingir dados atuais.
- **Clicar navega.** Clicar no cartão abre o painel; clicar numa linha abre o painel com esse servidor
  em destaque. A app abre sempre em modo normal e mantém-se numa única instância — sem segunda janela,
  segundo ícone na área de notificação ou segunda ligação aos servidores.
- **O widget não liga a nada.** Corre num processo separado que lê apenas um resumo local já
  sanitizado: identificadores opacos, nomes, estado de saúde e métricas. Sem SSH, sem rede, sem acesso
  a credenciais, e sem endereços, utilizadores ou chaves de host.
- Requer **Windows 11 22H2** ou mais recente para o widget. Em Windows 11 21H2 a aplicação instala e
  funciona normalmente; apenas o widget não é oferecido.

> **Nota de versão.** Produto e pacote convergem nesta versão: produto **1.1.0**, pacote da Store
> **1.1.0.0**. A Store reserva o quarto campo da versão (revisão), que se mantém a zero.

## [1.0.0] — 2026-08-28

Primeira versão estável pública. Foco em **empacotamento e distribuição**, sem novas funcionalidades de
monitorização:

- Empacotamento **MSIX single-project** para distribuição pela **Microsoft Store** (framework-dependent,
  x64, Windows 11).
- **Instância única**: um segundo arranque (ou clique numa notificação) reativa a janela existente em
  vez de abrir uma segunda cópia — protege um único tray, escritor de histórico e motor de monitorização.
- **Identidade de pacote neutra** e migração *backward-compatible* do armazenamento de credenciais: as
  credenciais criadas por versões anteriores continuam a funcionar e são migradas sem voltar a pedir a
  password.
- **Ícone oficial** integrado de forma coerente em todas as superfícies do Windows (Menu Iniciar, barra
  de tarefas, janela, Alt-Tab, área de notificação) a partir da identidade visual do ServerAlyzer.
- Ecrã **Sobre** com a versão real da aplicação.
- Documentação pública: README, privacidade, segurança, resolução de problemas.

> **Nota de versão.** A versão do produto é **1.0.0**. O pacote publicado na Microsoft Store apresenta a
> versão **1.0.1.0**: a Store reserva o quarto campo da versão (revisão) para uso próprio, obrigando-o a
> ser zero, pelo que o *build* do pacote foi incrementado para 1.0.1.0 antes da primeira publicação. É a
> mesma versão 1.0.0 do produto.

## Funcionalidades por grupo (0.1 – 0.11)

O produto foi construído de forma incremental. Resumo por área:

### Base e interface
- Aplicação desktop **WinUI 3** (MVVM + injeção de dependências), title bar própria, empty state.
- Design *glassmorphism* inspirado no macOS/Apple, Desktop Acrylic com fallback acessível.
- Temas Claro/Escuro/Sistema; idiomas **pt-BR** (padrão), **pt-PT**, **en-US**.

### Gestão de servidores e SSH seguro
- Adicionar, editar, ocultar, restaurar e remover servidores; validação de campos não sensíveis.
- Autenticação por password ou chave privada (passphrase opcional); segredos no Windows Credential
  Manager; apenas o **caminho** da chave é guardado.
- Confiança de host key por **fingerprint SHA-256**, sem aceitação automática; *mismatch* bloqueia.

### Métricas Linux e macOS
- Deteção de SO e recolha de CPU, memória, disco e uptime por comandos de leitura fechados.
- `desconhecido ≠ zero`: falhas de parsing preservam valor nulo, não zero.

### Monitorização automática
- Motor de monitorização em segundo plano com limites de concorrência, retries transitórios, estado de
  saúde por servidor e refresh manual com *single-flight*.

### Descoberta de rede
- Descoberta passiva `_ssh._tcp.local.` (mDNS/DNS-SD), sem varrer portas/subnets; deduplicação,
  *expiry*, e ignorar/repor persistente.

### System tray e notificações
- Ícone único na área de notificação (Abrir, Atualizar todos, Configurações, Sair); minimizar-para-tray.
- Notificações locais em transições reais de saúde, com deduplicação e *cooldown*; nomes sanitizados.

### Modo compacto
- Widget *glanceable* sempre-no-topo (mesma janela), com recuperação de posição multi-monitor/DPI.

### Histórico local e gráficos
- Histórico em **SQLite** local (retenção 30 dias) com gráficos temporais nativos de CPU/RAM/disco.

### Workloads (só-leitura)
- Containers **Docker** e serviços **systemd** (Linux) / **launchd** (macOS) em modo estritamente
  **só-leitura** — sem start/stop/restart/exec/sudo.

## Limitações conhecidas

- A observação de Docker requer que o utilizador SSH já tenha permissão de acesso ao Docker no servidor.
- `launchd` (macOS) reporta serviços a correr/parados; não distingue falhas reais de saídas de tarefas
  one-shot.
- A distribuição é, nesta fase, pela Microsoft Store; não há instalador assinado para download direto.
