# ServerAlyzer

**ServerAlyzer** é uma aplicação de desktop para Windows 11 que reúne, num painel calmo e compacto,
o estado dos teus servidores Linux e macOS — CPU, memória, disco, uptime, histórico, containers Docker
e serviços do sistema — através de ligações **SSH diretas** a partir do teu PC.

É **local-first**: não requer conta, não envia dados para nenhum servidor da ServerAlyzer e não faz
telemetria. A monitorização acontece diretamente entre o teu computador e os servidores que configuras.

> Estado: versão **1.1.0**, que acrescenta o widget oficial do Windows 11. Ver
> [Instalação](#instalação) e o [changelog](CHANGELOG.md).

## O que faz

- Mostra métricas reais de host (CPU, RAM, disco, uptime) de servidores **Linux** e **macOS** via SSH.
- Monitoriza automaticamente em segundo plano, com estado de saúde por servidor e refresh manual.
- Guarda **histórico local** e desenha gráficos temporais (CPU/RAM/disco).
- Observa, em modo **só-leitura**, containers **Docker** e serviços **systemd** (Linux) / **launchd** (macOS).
- Descobre servidores SSH na rede local (mDNS/DNS-SD), sem varrer portas ou subnets.
- Vive na **área de notificação** (tray) e emite notificações locais em transições reais de saúde.
- Tem um **modo compacto** sempre-visível, além da apresentação normal.
- Integra-se com o painel de **Widgets do Windows 11** (Win + W), em três tamanhos.

## Funcionalidades

| Área | Detalhe |
|---|---|
| SSH seguro | Confiança de host key por fingerprint SHA-256, sem aceitação automática; mismatch bloqueia. |
| Credenciais | Passwords e passphrases ficam no **Windows Credential Manager**, nunca em ficheiros. |
| Métricas | Linux e macOS, via comandos de leitura fechados; `desconhecido ≠ zero`. |
| Histórico | SQLite local, retenção de 30 dias, gráficos nativos. |
| Workloads | Docker + serviços em modo **só-leitura** (sem start/stop/restart/exec). |
| Descoberta | Passiva, `_ssh._tcp.local.`, com ignorar/repor. |
| Tray & notificações | Um ícone, minimizar-para-tray, alertas locais com cooldown. |
| Modo compacto | Apresentação reduzida sempre-no-topo, na mesma janela. |
| Widget do Windows | Cartão no painel de Widgets do Windows 11 (pequeno/médio/grande), só-leitura, com frescura explícita. Requer Windows 11 22H2+. |
| Idiomas | pt-BR (padrão), pt-PT, en-US. |
| Temas | Claro, Escuro, Sistema. |

## Capturas de ecrã

_As capturas serão adicionadas junto com a listagem pública._ (Ver checklist em
`docs/store-submission-checklist.md`.)

## Instalação

**Versão estável (Microsoft Store):** _pendente de submissão._ A ServerAlyzer será distribuída
principalmente pela **Microsoft Store**, que trata da instalação, assinatura e atualizações
automaticamente. O link será publicado aqui quando a app estiver disponível.

**A partir do código-fonte (developers):** ver [Compilar a partir do código](#compilar-a-partir-do-código).

> Este repositório ainda **não** publica um instalador assinado para download direto. Instalar um
> pacote MSIX de teste exige um certificado de desenvolvimento e o Modo de Programador do Windows —
> destina-se apenas a QA local, não a utilizadores finais.

## Sistemas suportados

- **Windows 11 x64** (aplicação).
- **Windows 11 22H2 (build 22621) ou mais recente** para o widget no painel de Widgets. Em 21H2 a
  aplicação instala e funciona normalmente — apenas o widget não é oferecido pelo sistema.
- Servidores monitorizados: **Linux** (com `systemd` para serviços) e **macOS** (com `launchd`),
  acessíveis por SSH a partir do teu PC.

## Adicionar um servidor

1. Abre a app e escolhe **Adicionar servidor** (ou aceita uma sugestão da descoberta local).
2. Indica endereço, utilizador e método de autenticação (password ou chave privada + passphrase opcional).
3. Ao testar a ligação pela primeira vez, a app mostra o **fingerprint SHA-256** do host e pede
   confirmação explícita antes de confiar. Um fingerprint diferente no futuro **bloqueia** a ligação.
4. A chave privada nunca é copiada — apenas o **caminho** que escolheres é guardado; o ficheiro
   permanece protegido pelas ACLs do sistema.

## Widget do Windows

No Windows 11 22H2 ou mais recente, abre o painel de Widgets com **Win + W**, escolhe **Adicionar
widgets** e seleciona o **ServerAlyzer**. O cartão existe em três tamanhos e mostra o estado da frota,
as métricas de cada servidor e há quanto tempo os dados foram lidos.

- Clicar no cartão abre o painel; clicar numa linha abre o painel com esse servidor em destaque.
- Com a aplicação fechada o widget mostra a última leitura conhecida, sempre datada.
- O widget corre num processo próprio que **não** faz SSH nem acede a credenciais: lê apenas um resumo
  local já sanitizado (identificador opaco, nome, saúde e métricas), sem endereços, utilizadores ou
  chaves de host.

## Modelo de segurança

- Sem auto-trust e sem prompts implícitos: host desconhecido exige aprovação; mismatch bloqueia.
- Segredos apenas no Windows Credential Manager; `servers.json` guarda só configuração não sensível
  e uma referência opaca.
- Catálogo SSH **fechado** e só-leitura para métricas e workloads (sem execução arbitrária, sem sudo).
- Texto remoto é tratado como não confiável (sanitização, limites de tamanho, UTF-8 estrito).

## Dados e privacidade

Ver [`PRIVACY.md`](PRIVACY.md). Em resumo: local-first, sem conta, sem telemetria, sem nuvem. Todos os
dados ficam na tua máquina:

- `%LOCALAPPDATA%\ServerMonitor\servers.json` — configuração não sensível.
- `%LOCALAPPDATA%\ServerMonitor\history.db` — histórico de métricas (SQLite).
- Windows Credential Manager — segredos SSH.
- Ficheiros locais separados — dispositivos ignorados, confiança de host keys, preferências de janela.
- `%LOCALAPPDATA%\ServerMonitor\widget-state.json` — resumo sanitizado que alimenta o widget do
  Windows (sem endereços, utilizadores nem segredos).

## Compilar a partir do código

Requisitos: **.NET 10 SDK**, Windows 11 x64.

```powershell
# Build + testes
dotnet build ServerMonitor.slnx
dotnet test ServerMonitor.slnx

# Correr a app (unpackaged, self-contained)
dotnet run --project src/ServerMonitor.App

# Construir o pacote MSIX de produção (framework-dependent)
dotnet build src/ServerMonitor.App/ServerMonitor.App.csproj -c Release -p:Packaged=true
```

Detalhes de arquitetura em [`docs/architecture.md`](docs/architecture.md) e das decisões em
[`docs/decisions/`](docs/decisions/). Resolução de problemas em
[`docs/troubleshooting.md`](docs/troubleshooting.md).

## Contribuir

Contribuições são bem-vindas via issues e pull requests. Reporta vulnerabilidades de forma responsável
— ver [`SECURITY.md`](SECURITY.md).

## Licença

[MIT](LICENSE). Software de terceiros e respetivas licenças em
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
