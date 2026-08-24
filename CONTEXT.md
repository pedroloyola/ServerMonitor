# Server Monitor — Contexto do Projeto

> **Estado:** Documento inicial de contexto e especificação  
> **Data de referência:** 24 de agosto de 2026  
> **Utilização:** Este ficheiro deve servir como fonte de verdade para Codex, Gemini e outros agentes de desenvolvimento usados neste repositório.

---

## 1. Visão geral

**Server Monitor** é uma aplicação desktop para Windows destinada a monitorizar, de forma rápida e visual, servidores pessoais/remotos.

A aplicação deve funcionar como um pequeno painel de monitorização com estética **glassmorphism**, permanecer acessível através da área de notificação do Windows e permitir consultar o estado dos servidores sem abrir ferramentas de administração completas.

O projeto começa com dois servidores:

1. **Ubuntu Server**
2. **Servidor macOS**

A primeira versão não pretende substituir Grafana, Prometheus, Datadog ou ferramentas de observabilidade empresariais. O objetivo é criar um monitor pessoal, leve, rápido e visualmente cuidado.

---

## 2. Objetivo principal

Permitir verificar num único local:

- se cada servidor está online ou offline;
- latência;
- utilização de CPU;
- utilização de RAM;
- utilização de armazenamento;
- uptime;
- última atualização;
- problemas básicos que exijam atenção.

A aplicação deve atualizar os dados automaticamente e emitir notificações quando um servidor deixar de responder.

---

## 3. Princípios do projeto

O desenvolvimento deve seguir estes princípios:

1. **Simplicidade primeiro**
   - Não introduzir infraestrutura desnecessária.
   - O MVP deve funcionar sem backend central.

2. **Zero custos recorrentes**
   - Preferir tecnologias gratuitas e open-source.
   - Não depender de APIs pagas ou SaaS externos.

3. **Segurança**
   - Nunca guardar passwords, tokens ou chaves SSH em texto simples.
   - Aplicar princípio do menor privilégio.

4. **Separação de responsabilidades**
   - UI, conectividade SSH, recolha de métricas e domínio devem permanecer desacoplados.

5. **Compatibilidade multi-OS no servidor**
   - Linux e macOS usam comandos e mecanismos diferentes.
   - A UI não deve conhecer essas diferenças.

6. **Desenvolvimento incremental**
   - Implementar primeiro um MVP sólido.
   - Funcionalidades avançadas entram apenas depois de a base estar estável.

7. **UI cuidada**
   - O produto deve parecer uma aplicação Windows moderna, e não uma ferramenta administrativa genérica.

---

# 4. Plataforma cliente

## 4.1 Sistema operativo alvo

Principal:

- **Windows 11**

Compatibilidade secundária:

- Windows 10 1809+ quando não implicar compromissos relevantes.

---

## 4.2 Stack tecnológica

Stack recomendada:

- **C#**
- **.NET 10**
- **WinUI 3**
- **Windows App SDK**
- **XAML**
- arquitetura MVVM

À data deste documento, o WinUI 3 é o framework recomendado pela Microsoft para novas aplicações desktop Windows.

Versão estável de referência do Windows App SDK à data deste documento:

- **Windows App SDK 2.4.0**

Não bloquear o projeto permanentemente nesta versão. Atualizações futuras devem ser avaliadas antes de atualizar dependências.

---

# 5. Arquitetura inicial

O MVP não deverá utilizar Prometheus nem um backend próprio.

```text
Windows 11
│
└── Server Monitor
    │
    ├── SSH ──→ Ubuntu Server
    │
    └── SSH ──→ macOS Server
```

A aplicação liga-se diretamente aos servidores através de SSH e recolhe as métricas necessárias.

---

# 6. Arquitetura lógica

```text
┌───────────────────────────────┐
│             UI                │
│          WinUI 3              │
└───────────────┬───────────────┘
                │
                ▼
┌───────────────────────────────┐
│        Application Layer      │
│   Refresh / Alerts / State    │
└───────────────┬───────────────┘
                │
                ▼
┌───────────────────────────────┐
│             Core              │
│ Models / Interfaces / Rules   │
└───────────────┬───────────────┘
                │
       ┌────────┴────────┐
       ▼                 ▼
┌──────────────┐   ┌──────────────┐
│ SSH Service  │   │ Collectors   │
│              │   │              │
└──────────────┘   ├── Linux      │
                   └── macOS      │
```

---

# 7. Estrutura recomendada do repositório

```text
ServerMonitor/
│
├── README.md
├── CONTEXT.md
│
├── src/
│   │
│   ├── ServerMonitor.App/
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Controls/
│   │   ├── Styles/
│   │   ├── Assets/
│   │   └── Services/
│   │
│   ├── ServerMonitor.Core/
│   │   ├── Models/
│   │   ├── Interfaces/
│   │   ├── Enums/
│   │   └── Domain/
│   │
│   ├── ServerMonitor.Infrastructure/
│   │   ├── SSH/
│   │   ├── Security/
│   │   └── Persistence/
│   │
│   └── ServerMonitor.Collectors/
│       ├── Linux/
│       └── MacOS/
│
├── tests/
│   ├── ServerMonitor.Core.Tests/
│   └── ServerMonitor.Collectors.Tests/
│
└── docs/
    ├── architecture.md
    ├── metrics.md
    └── decisions/
```

A estrutura pode ser simplificada durante o bootstrap, mas a separação conceptual deve ser preservada.

---

# 8. Modelo de servidor

Modelo conceptual inicial:

```csharp
Server
{
    Id
    Name
    Host
    Port
    Username
    OperatingSystem
    AuthenticationMethod
    RefreshInterval
    IsEnabled
}
```

`OperatingSystem`:

```text
Linux
MacOS
Unknown
```

Nunca incluir password ou conteúdo de chaves privadas diretamente neste modelo persistido.

---

# 9. Modelo normalizado de métricas

A UI deve consumir um único modelo de métricas independentemente do sistema operativo.

Exemplo conceptual:

```csharp
ServerMetrics
{
    ServerId
    Timestamp

    IsOnline
    LatencyMs

    CpuUsagePercent

    MemoryUsedBytes
    MemoryTotalBytes
    MemoryUsagePercent

    DiskUsedBytes
    DiskTotalBytes
    DiskUsagePercent

    UptimeSeconds

    Hostname
    OperatingSystem
}
```

Opcionalmente:

```csharp
MetricStatus
{
    Normal
    Warning
    Critical
    Unknown
}
```

---

# 10. Recolha de métricas

## 10.1 Interface comum

Os collectors devem implementar uma interface comum.

Exemplo conceptual:

```csharp
public interface IServerMetricsCollector
{
    Task<ServerMetrics> CollectAsync(
        Server server,
        CancellationToken cancellationToken);
}
```

A seleção do collector deve ser feita através de factory/resolver:

```text
Linux  → LinuxMetricsCollector
macOS  → MacOsMetricsCollector
```

A UI nunca deve executar diretamente comandos SSH.

---

# 11. Ubuntu Server

Métricas preferenciais:

### CPU

Fontes possíveis:

```bash
/proc/stat
```

ou ferramentas padrão disponíveis no sistema.

Evitar depender de utilitários adicionais sem necessidade.

### RAM

```bash
/proc/meminfo
```

ou:

```bash
free
```

### Disco

```bash
df
```

### Uptime

```bash
/proc/uptime
```

ou:

```bash
uptime
```

### Hostname

```bash
hostname
```

---

# 12. macOS Server

O macOS necessita de comandos específicos.

Possíveis fontes:

### CPU

```bash
top
```

ou mecanismos derivados de:

```bash
ps
sysctl
```

### RAM

```bash
vm_stat
```

e:

```bash
sysctl hw.memsize
```

### Disco

```bash
df
```

### Uptime

```bash
sysctl kern.boottime
```

ou:

```bash
uptime
```

### Hostname

```bash
hostname
```

O `MacOsMetricsCollector` deve encapsular todas estas diferenças.

---

# 13. SSH

A comunicação inicial será feita por SSH.

Preferência de autenticação:

1. chave SSH;
2. chave SSH protegida;
3. password apenas quando necessário.

Porta padrão:

```text
22
```

mas deve ser configurável individualmente por servidor.

---

# 14. Segurança

## 14.1 Regra crítica

Nunca guardar isto:

```json
{
  "password": "password123"
}
```

em:

- JSON;
- YAML;
- SQLite;
- logs;
- ficheiros de configuração;
- source code;
- Git.

---

## 14.2 Credenciais

Utilizar mecanismos protegidos pelo Windows, preferencialmente:

- Windows Credential Manager;
- APIs de proteção de dados do Windows quando apropriado.

A configuração persistida deve conter apenas uma referência à credencial.

Exemplo:

```text
CredentialId: server-monitor:ssh:ubuntu-main
```

---

## 14.3 Chaves SSH

Sempre que possível:

- autenticação por chave;
- utilizador SSH dedicado;
- permissões mínimas;
- sem acesso root direto;
- validar host keys;
- não aceitar automaticamente hosts desconhecidos em produção.

---

# 15. Atualização das métricas

Intervalo inicial recomendado:

```text
30 segundos
```

Configurável por servidor.

Valores possíveis:

```text
10 s
30 s
60 s
5 min
```

Evitar polling excessivamente agressivo.

---

# 16. Estado dos servidores

Estados visuais:

```text
Healthy
Warning
Critical
Offline
Unknown
```

Definição inicial:

### Healthy

Servidor acessível e métricas dentro dos limites.

### Warning

Servidor acessível, mas pelo menos uma métrica ultrapassa threshold de aviso.

### Critical

Servidor acessível, mas pelo menos uma métrica ultrapassa threshold crítico.

### Offline

SSH/health check falhou após o mecanismo de retry definido.

### Unknown

Ainda sem dados suficientes ou estado indeterminado.

---

# 17. Thresholds iniciais

Valores padrão:

## CPU

```text
Warning:  80%
Critical: 95%
```

## RAM

```text
Warning:  80%
Critical: 95%
```

## Disco

```text
Warning:  80%
Critical: 90%
```

Devem posteriormente ser configuráveis por servidor.

---

# 18. Tratamento de falhas

Um timeout isolado não deve imediatamente marcar um servidor como offline.

Fluxo sugerido:

```text
Request falhou
   │
   ▼
Retry curto
   │
   ├── sucesso → servidor continua online
   │
   └── falha
        │
        ▼
   segundo retry
        │
        ├── sucesso → online
        └── falha → offline
```

Não bloquear a UI durante tentativas de conexão.

Todo o acesso remoto deve ser assíncrono e cancelável.

---

# 19. MVP — funcionalidades

## Obrigatórias

- adicionar servidor;
- editar servidor;
- remover servidor;
- Ubuntu;
- macOS;
- SSH;
- autenticação segura;
- online/offline;
- latência;
- CPU;
- RAM;
- disco;
- uptime;
- atualização automática;
- atualização manual;
- system tray;
- notificação quando um servidor fica offline;
- persistência da lista de servidores;
- dark mode;
- UI glassmorphism com linguagem visual inspirada no Apple Style;
- loading state;
- error state.

---

# 20. Fora do MVP

Não implementar inicialmente:

- Prometheus;
- Grafana;
- backend cloud;
- conta de utilizador;
- sincronização entre dispositivos;
- dashboard web;
- aplicação mobile;
- logs remotos completos;
- terminal SSH integrado;
- execução arbitrária de comandos;
- restart remoto;
- gestão Docker;
- histórico longo;
- base de dados de séries temporais;
- Microsoft Store;
- sistema multiutilizador.

Estas funcionalidades podem ser reconsideradas depois do MVP.

---

# 21. Evolução prevista

## V1.1

- network upload/download;
- personalização de thresholds;
- nomes personalizados;
- ordenação dos servidores;
- preferência de intervalo de refresh;
- métricas adicionais.

## V2

- histórico local;
- gráficos;
- Docker;
- estados de serviços;
- eventos;
- notificações avançadas.

## V3

- ações remotas seguras;
- restart de serviços;
- restart de containers;
- integração opcional com Prometheus;
- dashboard expandido;
- widget oficial do Windows, se continuar a fazer sentido.

---

# 22. Direção de UI

## 22.1 Linguagem visual

A interface deve utilizar uma estética:

**Apple-style glassmorphism + Fluent Design + Windows 11**

A direção visual deve combinar:

- clareza e minimalismo associados às interfaces Apple;
- superfícies translúcidas e frosted glass;
- profundidade subtil;
- cantos generosamente arredondados;
- tipografia limpa;
- espaçamento consistente;
- animações discretas;
- integração técnica e comportamental com Windows 11.

Não copiar literalmente componentes, ícones, fontes proprietárias ou assets da Apple.

A aplicação deve parecer inspirada na filosofia visual de macOS/iOS, mas continuar a comportar-se como uma aplicação Windows nativa.

Não criar uma reprodução genérica de dashboards SaaS.

---

# 23. Glassmorphism

Características principais:

- superfícies translúcidas;
- blur/background material;
- sensação de profundidade;
- bordas subtis;
- sombras suaves;
- cantos arredondados;
- separação visual através de layers e não de linhas pesadas;
- contraste suficiente para legibilidade.

Sempre que possível, privilegiar materiais nativos do Windows como:

- **Mica**
- **Acrylic**

em vez de simular blur pesado manualmente.

---

# 24. Tema principal

A aplicação será inicialmente **dark-first**.

Base aproximada:

```text
Background:
preto / grafite / azul muito escuro

Glass panels:
branco com baixa opacidade

Border:
branco com opacidade muito baixa

Text primary:
branco quase puro

Text secondary:
cinzento claro

Accent:
azul/violeta frio

Healthy:
verde

Warning:
âmbar

Critical:
vermelho

Offline:
vermelho/desaturado
```

As cores exatas devem ser centralizadas em design tokens e não espalhadas pelo XAML.

---

# 25. Design tokens

Criar tokens centralizados.

Exemplo conceptual:

```text
Radius.Small      = 8
Radius.Medium     = 12
Radius.Large      = 16
Radius.XLarge     = 20

Spacing.XS        = 4
Spacing.S         = 8
Spacing.M         = 12
Spacing.L         = 16
Spacing.XL        = 24
Spacing.XXL       = 32
```

Glass:

```text
Glass.BackgroundOpacity
Glass.BorderOpacity
Glass.BlurStrength
Glass.ShadowOpacity
```

Nunca hardcodear os mesmos valores repetidamente em dezenas de componentes.

---

# 26. Janela principal

A aplicação deve comportar-se como uma pequena aplicação/widget.

Características desejadas:

- janela compacta;
- redimensionável;
- dimensões mínimas definidas;
- posição persistida;
- tamanho persistido;
- minimizar para system tray;
- opção de iniciar com Windows;
- opção futura "Always on Top".

Não implementar imediatamente um Widget Provider oficial do Windows.

---

# 27. Layout principal

Estrutura aproximada:

```text
┌──────────────────────────────────────┐
│ Servers                        ↻  ⚙ │
│                                      │
│ ┌──────────────────────────────────┐ │
│ │ ● Ubuntu Server                 │ │
│ │                                  │ │
│ │ CPU       RAM       DISK          │ │
│ │ 16%       41%       52%           │ │
│ │                                  │ │
│ │ Uptime 18d             23 ms      │ │
│ └──────────────────────────────────┘ │
│                                      │
│ ┌──────────────────────────────────┐ │
│ │ ● Mac Server                    │ │
│ │                                  │ │
│ │ CPU       RAM       DISK          │ │
│ │ 31%       63%       71%           │ │
│ │                                  │ │
│ │ Uptime 7d              18 ms      │ │
│ └──────────────────────────────────┘ │
│                                      │
│ Updated 12:42                         │
└──────────────────────────────────────┘
```

---

# 28. Server Card

Cada servidor é apresentado num `ServerCard`.

## Conteúdo

- indicador de estado;
- nome;
- sistema operativo;
- CPU;
- RAM;
- disco;
- uptime;
- latência;
- timestamp da última leitura.

Exemplo:

```text
┌──────────────────────────────────┐
│ ● Ubuntu Server                  │
│ Ubuntu 24.xx                     │
│                                  │
│ CPU       RAM       DISK          │
│ 16%       41%       52%           │
│                                  │
│ Uptime 18d             23 ms      │
└──────────────────────────────────┘
```

---

# 29. Hierarquia visual

Prioridade:

1. estado do servidor;
2. nome;
3. métricas principais;
4. alertas;
5. detalhes secundários.

Evitar excesso de informação simultânea.

---

# 30. Progress indicators

CPU, RAM e disco podem utilizar indicadores compactos.

Exemplo:

```text
CPU
████░░░░░░
41%
```

ou progress bars extremamente discretas.

Não utilizar gauges circulares grandes no MVP.

---

# 31. Estados visuais

## Online

```text
● Ubuntu Server
```

Indicador verde discreto.

## Warning

```text
● Ubuntu Server
CPU 86%
```

Estado âmbar.

## Critical

```text
● Ubuntu Server
DISK 94%
```

Estado vermelho.

## Offline

O card permanece visível, mas:

- conteúdo ligeiramente desaturado;
- indicador vermelho;
- métricas anteriores podem permanecer visíveis como stale;
- mostrar última vez em que respondeu.

Exemplo:

```text
● Mac Server
OFFLINE

Last seen 4 min ago
```

---

# 32. Animações

As animações devem ser subtis.

Permitido:

- fade;
- scale muito pequeno;
- transições de opacidade;
- atualização suave de progress bars;
- hover discreto.

Evitar:

- bounce;
- animações excessivas;
- glow permanente;
- movimento constante;
- efeitos gaming.

Objetivo:

```text
quiet
premium
functional
```

---

# 33. Interação

Hover num card:

- ligeiro aumento da luminosidade do glass;
- border ligeiramente mais visível.

Click:

- abrir detalhe do servidor.

Menu contextual futuro:

```text
Refresh
Edit
Disable monitoring
Remove
```

---

# 34. Vista de detalhe

Não é obrigatória para o primeiro commit funcional, mas deve estar prevista.

```text
Ubuntu Server

● ONLINE

CPU
24%

RAM
3.4 GB / 8 GB
42%

Disk
82 GB / 160 GB
51%

Latency
21 ms

Uptime
18d 4h 32m

Last refresh
12:42:13
```

---

# 35. Empty state

Se não existirem servidores:

```text
No servers yet.

Add your first server to start monitoring.

[ Add Server ]
```

Manter a mesma estética glassmorphism.

---

# 36. Add Server

Campos:

```text
Name
Host / IP
Port
Username
Operating System
Authentication Method
```

Authentication:

```text
SSH Key
Password
```

Idealmente permitir deteção automática do sistema operativo depois de testar a conexão.

Botões:

```text
Test Connection
Save
Cancel
```

---

# 37. Loading

Nunca congelar a interface.

Estado inicial:

```text
Connecting...
```

Pode utilizar skeleton/loading indicators discretos.

Não substituir toda a UI por um spinner global sempre que ocorre refresh.

---

# 38. System tray

A aplicação deve possuir um ícone na área de notificação.

Comportamento esperado:

```text
Left click
→ abrir/ocultar aplicação

Right click
→ Open
→ Refresh All
→ Settings
→ Exit
```

Fechar a janela pode, por defeito, minimizar para tray em vez de terminar o processo.

Isto deve ser configurável posteriormente.

---

# 39. Notificações

MVP:

notificar quando:

```text
Server Online → Offline
```

Opcionalmente:

```text
Server Offline → Online
```

Evitar repetir notificações em cada ciclo de polling.

Implementar deduplicação baseada em mudança de estado.

---

# 40. Persistência

Persistir:

- servidores;
- nome;
- host;
- porta;
- username;
- OS;
- intervalo de refresh;
- preferências da aplicação;
- posição/tamanho da janela;
- thresholds.

Não persistir credenciais sensíveis em texto simples.

SQLite pode ser introduzido se justificar a complexidade.

Para o MVP, configuração estruturada local pode ser suficiente desde que as credenciais fiquem separadas e protegidas.

---

# 41. Logging

Implementar logging técnico desde cedo.

Níveis:

```text
Debug
Information
Warning
Error
Critical
```

Nunca registar:

- passwords;
- private keys;
- tokens;
- conteúdo sensível de credenciais.

Logs devem ajudar a diagnosticar:

- falha SSH;
- timeout;
- parsing inválido;
- comando incompatível;
- exceção de collector.

---

# 42. Parsing

Não colocar parsing de outputs diretamente nos ViewModels.

Cada collector deve:

1. executar o comando;
2. receber raw output;
3. fazer parsing;
4. validar;
5. converter para `ServerMetrics`.

Criar testes unitários com outputs reais capturados dos servidores.

---

# 43. Testes

Prioridade:

## Unit tests

- parsing Linux;
- parsing macOS;
- cálculo de percentagens;
- thresholds;
- transições de estado.

## Integration tests

Quando possível:

- SSH local;
- servidor de teste;
- comandos reais.

Nunca tornar os testes principais dependentes dos servidores pessoais estarem online.

---

# 44. Performance

A aplicação deve permanecer leve.

Objetivos:

- startup rápido;
- baixa utilização de CPU em idle;
- polling assíncrono;
- nenhuma espera SSH na UI thread;
- cancelamento correto quando a aplicação termina;
- evitar timers duplicados;
- evitar conexões simultâneas desnecessárias.

---

# 45. Concorrência

Cada servidor pode ser atualizado em paralelo, mas estabelecer limites razoáveis.

Exemplo:

```text
Server A ─┐
Server B ─┼→ parallel async refresh
Server C ─┘
```

Não bloquear um servidor porque outro está lento.

---

# 46. Dados stale

Cada leitura deve ter timestamp.

Se a atualização falhar mas ainda existir uma leitura anterior:

```text
CPU 24%
Last updated 3 min ago
```

A UI deve distinguir:

- dado atual;
- dado stale;
- sem dados.

---

# 47. Convenções de desenvolvimento

## Código

- nomes em inglês;
- `async/await`;
- nullable reference types ativados;
- dependency injection;
- interfaces apenas quando trazem valor real;
- evitar abstração prematura;
- evitar singletons globais mutáveis.

## UI

- XAML reutilizável;
- componentes próprios para elementos repetidos;
- resources/dictionaries para tokens;
- nenhuma lógica SSH em code-behind.

---

# 48. Regras para agentes de IA

Codex, Gemini ou outro agente que trabalhe neste repositório deve:

1. Ler este `CONTEXT.md` antes de alterações arquiteturais.
2. Não alterar a stack principal sem justificar.
3. Não introduzir dependências SaaS.
4. Não adicionar custos recorrentes.
5. Não guardar credenciais inseguramente.
6. Não misturar comandos Linux e macOS.
7. Não executar SSH diretamente a partir da UI.
8. Não implementar funcionalidades fora do scope atual sem pedido.
9. Preservar a estética definida.
10. Criar código testável.
11. Atualizar documentação quando uma decisão estrutural mudar.
12. Preferir soluções simples a arquiteturas excessivamente genéricas.

---

# 49. Ordem de implementação recomendada

## Fase 1 — Bootstrap

- criar solution;
- criar projetos;
- configurar DI;
- criar models;
- criar interfaces;
- criar shell WinUI.

## Fase 2 — SSH

- criar serviço SSH;
- testar conexão;
- timeouts;
- host validation;
- autenticação segura.

## Fase 3 — Linux

- implementar Linux collector;
- CPU;
- RAM;
- disco;
- uptime;
- testes.

## Fase 4 — macOS

- implementar macOS collector;
- CPU;
- RAM;
- disco;
- uptime;
- testes.

## Fase 5 — UI

- main window;
- ServerCard;
- glassmorphism;
- loading;
- error;
- offline;
- refresh.

## Fase 6 — Configuração

- Add Server;
- Edit Server;
- persistência;
- Credential Manager.

## Fase 7 — Tray + Notifications

- system tray;
- refresh all;
- offline alerts;
- restore alerts.

## Fase 8 — Hardening

- retries;
- cancellation;
- stale state;
- logging;
- testes de regressão;
- performance.

---

# 50. Critérios de aceitação do MVP

O MVP está concluído quando:

- [ ] A aplicação inicia normalmente no Windows 11.
- [ ] É possível adicionar o Ubuntu Server.
- [ ] É possível adicionar o servidor macOS.
- [ ] As credenciais não são armazenadas em texto simples.
- [ ] A aplicação consegue testar a conexão SSH.
- [ ] O Ubuntu apresenta CPU, RAM, disco e uptime.
- [ ] O macOS apresenta CPU, RAM, disco e uptime.
- [ ] Cada servidor apresenta estado online/offline.
- [ ] Existe latência aproximada.
- [ ] O refresh ocorre automaticamente.
- [ ] Existe refresh manual.
- [ ] Um servidor lento não bloqueia os restantes.
- [ ] A UI não bloqueia durante SSH.
- [ ] A aplicação funciona na system tray.
- [ ] Existe notificação quando um servidor fica offline.
- [ ] Alertas não são repetidos a cada refresh.
- [ ] A janela mantém posição e tamanho.
- [ ] A interface segue a estética glassmorphism definida.
- [ ] O código de Linux e macOS está separado.
- [ ] Parsers relevantes possuem testes.
- [ ] Logs não expõem segredos.

---

# 51. Definição estética resumida

Quando houver dúvida de design, seguir esta referência conceptual:

```text
Windows 11
+
Fluent Design
+
Glassmorphism
+
Dark UI
+
Minimal monitoring dashboard
+
Subtle status colours
+
High information density without clutter
```

Evitar:

```text
generic admin dashboard
cyberpunk
RGB gaming
huge charts
strong gradients everywhere
neon glow
excessive borders
over-animation
```

O resultado deve transmitir:

```text
minimal
precise
calm
modern
native
premium
```

---

# 52. Decisão arquitetural inicial

**ADR-001**

Para o MVP:

```text
Windows Client
→ SSH
→ Ubuntu/macOS
```

Não utilizar:

```text
Prometheus
Grafana
custom server agent
cloud backend
```

Motivo:

- apenas dois servidores;
- menor complexidade;
- zero infraestrutura adicional;
- custo zero;
- permite validar rapidamente utilidade e UX.

Esta decisão pode ser revista se:

- o número de servidores aumentar significativamente;
- forem necessários históricos extensos;
- forem necessárias métricas de alta frequência;
- Docker/services passarem a ser centrais;
- surgir necessidade de acesso a partir de vários clientes.

---

# 53. Fonte de verdade

Este documento descreve a visão inicial do projeto.

Quando houver conflito entre:

- código experimental;
- sugestões automáticas;
- prompts anteriores;
- implementações geradas por IA;

e este documento, **este documento deve prevalecer**, salvo se uma decisão posterior tiver sido explicitamente aprovada e documentada.



---

# 54. Revisão de produto — descoberta, gestão, idioma e open source

Esta revisão passa a fazer parte da especificação principal do projeto.

As seguintes decisões substituem qualquer interpretação anterior que entre em conflito com elas.

---

# 55. Descoberta automática de servidores

A aplicação deve conseguir descobrir automaticamente servidores ou dispositivos compatíveis na rede local.

Objetivo:

```text
Abrir Server Monitor
        │
        ▼
Descoberta automática
        │
        ├── Ubuntu Server encontrado
        ├── Mac Server encontrado
        └── Outros hosts SSH encontrados
```

O utilizador não deve ser obrigado a conhecer previamente o IP de cada servidor quando a descoberta local for possível.

---

# 56. Estratégia de descoberta

A descoberta deve ser implementada em camadas.

Ordem preferencial:

```text
1. mDNS / DNS-SD / Bonjour
2. hosts já conhecidos/cache
3. descoberta de subnet opcional
4. introdução manual
```

A aplicação nunca deve depender exclusivamente de uma única técnica.

---

# 57. mDNS / Bonjour

Primeiro mecanismo de descoberta:

```text
mDNS / DNS-SD
```

Serviço prioritário:

```text
_ssh._tcp.local.
```

Isto permite encontrar equipamentos que anunciem SSH através de Bonjour/mDNS.

Particularmente útil para:

- macOS;
- Linux com Avahi/mDNS configurado;
- outros dispositivos que publiquem `_ssh._tcp`.

A descoberta mDNS deve ser contínua ou periodicamente atualizada enquanto a aplicação estiver ativa, sem gerar tráfego agressivo.

---

# 58. macOS e descoberta

Quando **Remote Login / SSH** está ativo no macOS, o equipamento pode anunciar o serviço SSH através de Bonjour.

A aplicação deve aproveitar esta capacidade sempre que estiver disponível.

Um Mac descoberto pode aparecer inicialmente como:

```text
Encontrado na rede

Mac Studio
macOS
192.168.1.42
SSH

[ Adicionar ]
[ Ignorar ]
```

A descoberta não significa autenticação.

O utilizador continua a ter de fornecer ou selecionar credenciais válidas antes de o servidor passar a ser monitorizado.

---

# 59. Ubuntu e descoberta

No Ubuntu, a descoberta via mDNS pode depender de serviços como Avahi e da configuração do servidor.

Consequentemente, não assumir que todo Ubuntu Server anuncia `_ssh._tcp` automaticamente.

Se o servidor não surgir por mDNS, o utilizador pode:

- utilizar a descoberta de subnet;
- adicionar o servidor manualmente;
- opcionalmente configurar anúncio mDNS no servidor.

A aplicação não deve exigir alterações no Ubuntu para funcionar.

---

# 60. Descoberta de subnet

A aplicação pode oferecer um segundo método:

```text
Procurar dispositivos na rede
```

Este mecanismo deve ser opcional e iniciado de forma explícita ou através de uma preferência claramente configurável.

Objetivo:

- identificar hosts ativos na subnet local;
- testar a presença de SSH;
- sugerir potenciais servidores.

Não implementar um scanner de portas genérico.

O scan deve limitar-se às portas relevantes para o projeto, inicialmente:

```text
22 / TCP
```

ou à porta SSH configurada pelo utilizador quando aplicável.

---

# 61. Redes a considerar

Descoberta automática apenas em redes locais elegíveis.

Exemplos:

```text
192.168.0.0/16
10.0.0.0/8
172.16.0.0/12
```

Não efetuar scanning da Internet pública.

Não atravessar routers/subnets automaticamente sem configuração explícita.

VPNs como Tailscale/WireGuard poderão ser consideradas futuramente como redes de confiança configuráveis.

---

# 62. Segurança da descoberta

Um servidor descoberto nunca deve ser automaticamente considerado confiável.

Fluxo:

```text
Servidor descoberto
      │
      ▼
Apresentar ao utilizador
      │
      ▼
Adicionar
      │
      ▼
SSH handshake
      │
      ▼
Mostrar/verificar host key fingerprint
      │
      ▼
Guardar confiança
```

Não usar:

```text
AcceptAnyHostKey = true
```

como comportamento de produção.

---

# 63. Identidade de dispositivos

Endereços IP podem mudar.

Sempre que possível, associar um servidor persistido a uma identidade mais estável.

Prioridade conceptual:

```text
SSH host key fingerprint
→ hostname/mDNS identity
→ host/IP
```

Não utilizar apenas o IP como identidade permanente do servidor.

---

# 64. Estados de descoberta

Um dispositivo pode ter um dos seguintes estados:

```text
Discovered
Added
Hidden
Ignored
Offline
```

Definições:

### Discovered

Foi encontrado na rede, mas ainda não foi adicionado.

### Added

Foi explicitamente adicionado e faz parte da monitorização.

### Hidden

Está configurado e pode continuar monitorizado, mas não aparece no dashboard principal.

### Ignored

Foi descoberto, mas o utilizador indicou que não pretende vê-lo como sugestão.

### Offline

Servidor previamente adicionado que atualmente não responde.

---

# 65. Adicionar servidor

O utilizador pode adicionar servidores através de:

```text
Descoberta automática
Descoberta de rede
Introdução manual
```

A ação `Adicionar` deve abrir uma etapa curta de configuração:

```text
Nome
Username
Authentication
Port
```

Host e OS devem ser pré-preenchidos quando conhecidos.

---

# 66. Remover servidor

`Remover` significa:

- deixar de monitorizar;
- remover configuração não sensível;
- remover referência a credenciais;
- eliminar credenciais associadas quando confirmado;
- deixar de mostrar o servidor no dashboard.

Se o dispositivo voltar a ser descoberto futuramente, pode voltar a aparecer como `Discovered`, salvo se também estiver marcado como `Ignored`.

---

# 67. Ocultar servidor

`Ocultar` não significa remover.

Um servidor oculto:

- continua guardado;
- pode continuar a ser monitorizado;
- pode continuar a gerar alertas conforme configuração;
- não aparece na vista principal.

Deve existir uma área:

```text
Servidores ocultos
```

onde possa ser restaurado.

---

# 68. Ignorar dispositivo descoberto

Dispositivos encontrados automaticamente podem ser marcados como:

```text
Ignorar
```

Isto é diferente de `Ocultar`.

`Ignorar` destina-se a equipamentos que o utilizador não pretende adicionar.

Exemplo:

```text
Raspberry Pi de outro membro da rede
Printer
NAS não monitorizado
Outro Mac
```

A aplicação deve persistir uma identidade suficiente para evitar sugerir repetidamente o mesmo dispositivo.

Deve existir opção:

```text
Redefinir dispositivos ignorados
```

---

# 69. UI da descoberta

Exemplo:

```text
┌────────────────────────────────────────┐
│ Servidores                         +   │
│                                        │
│ Seus servidores                       │
│                                        │
│ ┌────────────────────────────────────┐ │
│ │ ● Ubuntu Server                   │ │
│ │ CPU 16% · RAM 41% · SSD 52%       │ │
│ └────────────────────────────────────┘ │
│                                        │
│ Encontrados na rede                    │
│                                        │
│ ┌────────────────────────────────────┐ │
│ │ Mac Studio                         │ │
│ │ macOS · SSH · 192.168.1.42         │ │
│ │                                    │ │
│ │ [ Adicionar ]        [ Ignorar ]   │ │
│ └────────────────────────────────────┘ │
└────────────────────────────────────────┘
```

A secção de descoberta deve ser visualmente secundária em relação aos servidores já adicionados.

---

# 70. Descoberta em background

A descoberta mDNS pode ocorrer em background enquanto a aplicação está ativa.

Não apresentar notificações constantes de novos dispositivos.

Comportamento preferencial:

```text
+ 2 servidores encontrados
```

como indicador discreto dentro da própria aplicação.

Notificações Windows para descoberta devem estar desligadas por defeito.

---

# 71. Apple Style — direção visual

A referência visual passa a ser:

```text
macOS / iOS design philosophy
+
Apple-style frosted glass
+
Windows 11 native behaviour
```

A intenção é reproduzir princípios, não copiar a interface do macOS.

Princípios:

- hierarquia simples;
- poucos elementos visíveis de cada vez;
- superfícies com blur;
- muito espaço negativo;
- controlos compactos;
- consistência;
- reduzido ruído visual;
- feedback imediato;
- animação suave;
- aparência premium sem ornamentação excessiva.

---

# 72. Superfícies Apple-style glass

Os cards devem aproximar-se visualmente de:

```text
frosted glass
```

Características:

- background translúcido;
- blur do conteúdo atrás;
- ligeira tonalidade;
- stroke branco muito subtil;
- sombra ampla e suave;
- highlights discretos;
- cantos arredondados.

Evitar:

- blur exagerado;
- transparência que prejudique leitura;
- bordas brancas fortes;
- glow neon;
- gradientes saturados.

---

# 73. Corner radius

A interface pode utilizar cantos mais arredondados do que o Fluent Design padrão.

Direção inicial:

```text
Small      10
Medium     14
Large      18
XLarge     24
Window     20–24
```

Os valores finais devem ser afinados visualmente.

---

# 74. Tipografia

Não utilizar ou redistribuir fontes proprietárias da Apple apenas para imitar macOS.

Preferência inicial:

```text
Segoe UI Variable
```

por integração nativa com Windows.

Alternativas open-source podem ser avaliadas posteriormente.

A hierarquia tipográfica deve, no entanto, seguir uma filosofia Apple-like:

- títulos fortes mas não pesados;
- labels pequenas;
- números de métricas com excelente legibilidade;
- uso controlado de pesos;
- pouca variação de tamanho.

---

# 75. Iconografia

Não copiar SF Symbols ou assets proprietários da Apple.

Utilizar:

- símbolos nativos adequados do Windows;
- ícones próprios;
- bibliotecas open-source compatíveis com a licença do projeto.

Todos os ícones devem partilhar:

- stroke consistente;
- proporções semelhantes;
- estilo minimalista.

---

# 76. Cor

A interface deve ser predominantemente neutra.

Base:

```text
charcoal
black
soft grey
frosted white
```

Cor deve comunicar estado, não decoração.

```text
Green  → healthy
Amber  → warning
Red    → critical/offline
Blue   → interactive/accent
```

Evitar múltiplas cores de destaque simultâneas.

---

# 77. Light mode

Embora o projeto seja dark-first, deve ser arquitetado para suportar:

```text
Light
Dark
System
```

O comportamento padrão deve ser:

```text
System
```

Os materiais glass devem adaptar contraste e opacidade ao tema.

---

# 78. Idioma e localização

Idioma de referência do produto:

```text
Português do Brasil — pt-BR
```

Toda a copy inicial deve ser escrita em português do Brasil.

No entanto, a aplicação deve detetar automaticamente o idioma preferido do Windows.

---

# 79. Seleção automática de idioma

No primeiro arranque:

```text
Windows UI Culture
        │
        ▼
Idioma suportado?
   │           │
  Sim         Não
   │           │
   ▼           ▼
usar idioma   pt-BR
do sistema    fallback
```

Utilizar os mecanismos de localização do .NET/Windows e recursos externos.

Não hardcodear strings de UI diretamente nos componentes.

---

# 80. Idiomas iniciais

Suporte recomendado para o primeiro release:

```text
pt-BR — Português (Brasil)
en-US — English
pt-PT — Português (Portugal)
```

Prioridade:

```text
1. pt-BR
2. en-US
3. pt-PT
```

Outros idiomas podem ser adicionados pela comunidade posteriormente.

---

# 81. Seleção manual de idioma

Settings:

```text
Idioma
○ Usar idioma do sistema
○ Português (Brasil)
○ Português (Portugal)
○ English
```

Padrão:

```text
Usar idioma do sistema
```

Se o idioma do sistema não estiver disponível:

```text
pt-BR
```

---

# 82. Estrutura de localização

Toda a UI deve usar resource keys.

Exemplo conceptual:

```text
Resources/
├── pt-BR/
│   └── Resources.resw
├── pt-PT/
│   └── Resources.resw
└── en-US/
    └── Resources.resw
```

Exemplo:

```text
Server.Status.Online
Server.Status.Offline
Server.Action.Add
Server.Action.Hide
Server.Action.Remove
Discovery.Title
Settings.Language
```

Evitar resource keys baseadas na frase completa.

---

# 83. Formatação regional

Idioma e região não devem ser confundidos.

Datas, horas e números devem respeitar as preferências regionais do Windows quando possível.

Exemplos:

```text
24/08/2026
12:42
41,5 %
```

podem variar conforme a cultura do sistema.

Unidades técnicas devem permanecer consistentes.

---

# 84. Open source

O projeto deve ser desenvolvido publicamente como software de código aberto.

Objetivos:

- código auditável;
- transparência;
- permitir contribuições;
- facilitar suporte a novos sistemas;
- permitir forks;
- construir comunidade;
- manter independência de serviços proprietários pagos.

---

# 85. Licença recomendada

Licença inicial recomendada:

```text
MIT License
```

Motivos:

- simples;
- permissiva;
- amplamente utilizada em projetos .NET;
- permite uso pessoal e comercial;
- permite modificação;
- permite redistribuição;
- baixa fricção para contribuições.

A licença definitiva deve ser adicionada ao repositório antes do primeiro release público.

---

# 86. Compatibilidade de dependências

Toda dependência adicionada ao projeto deve ter licença compatível com a licença escolhida.

Antes de adicionar uma biblioteca:

1. verificar licença;
2. verificar atividade/manutenção;
3. verificar necessidade real;
4. evitar dependências sem licença clara;
5. documentar third-party notices quando aplicável.

Dependências copyleft fortes devem ser avaliadas antes da inclusão.

---

# 87. Estrutura open-source do repositório

Adicionar progressivamente:

```text
LICENSE
README.md
CONTRIBUTING.md
CODE_OF_CONDUCT.md
SECURITY.md
CHANGELOG.md
THIRD-PARTY-NOTICES.md
```

Não é necessário criar todos no primeiro commit.

Prioridade inicial:

```text
LICENSE
README.md
CONTEXT.md
.gitignore
```

---

# 88. Segurança num projeto open-source

Nenhum segredo pessoal pode entrar no repositório.

Nunca commit:

```text
IPs privados pessoais quando desnecessários
public IPs sensíveis
usernames pessoais
SSH keys
passwords
tokens
Credential Manager exports
logs reais com dados sensíveis
```

Fixtures de testes devem utilizar informação fictícia.

---

# 89. Configuração local

Configuração real do utilizador deve ficar fora do Git.

Exemplo:

```text
%LOCALAPPDATA%\ServerMonitor\
```

O repositório deve conter apenas exemplos seguros:

```text
config.example.json
```

quando necessário.

---

# 90. Contribuições futuras

A arquitetura deve permitir que contribuidores adicionem novos collectors.

Exemplo futuro:

```text
IServerMetricsCollector
├── Linux
├── macOS
├── FreeBSD
├── Windows Server
├── TrueNAS
└── Proxmox
```

Sem tornar o MVP excessivamente abstrato.

---

# 91. Atualização do MVP

O MVP passa a incluir:

- [ ] descoberta mDNS/Bonjour de serviços SSH na rede local;
- [ ] apresentar dispositivos descobertos;
- [ ] adicionar servidor descoberto;
- [ ] adicionar servidor manualmente;
- [ ] remover servidor;
- [ ] ocultar servidor;
- [ ] restaurar servidor oculto;
- [ ] ignorar dispositivo descoberto;
- [ ] deteção automática do idioma do sistema;
- [ ] pt-BR como idioma de referência e fallback;
- [ ] recursos preparados para localização;
- [ ] Apple-style glassmorphism;
- [ ] dark/light/system architecture;
- [ ] preparação do repositório como projeto open-source.

A descoberta de subnet completa pode entrar em **V1.1** caso aumente demasiado o scope do MVP.

---

# 92. Nova definição estética resumida

Quando houver dúvida de design, seguir:

```text
Apple-inspired restraint
+
macOS-like frosted glass
+
Windows 11 native integration
+
minimal server monitoring
```

O resultado deve transmitir:

```text
clean
quiet
precise
premium
soft
responsive
trustworthy
```

A referência Apple deve influenciar:

- proporção;
- espaçamento;
- profundidade;
- movimento;
- hierarquia;
- simplicidade.

Não deve resultar numa cópia literal de macOS.

---

# 93. Decisão arquitetural — Discovery

**ADR-002**

Para descoberta local:

```text
mDNS / DNS-SD (_ssh._tcp)
        │
        ▼
dispositivos encontrados
        │
        ▼
user selects Add
        │
        ▼
SSH trust + credentials
        │
        ▼
monitoring
```

Fallback:

```text
manual add
```

Descoberta de subnet:

```text
optional / V1.1
```

Motivo:

- mDNS é de baixo impacto;
- funciona particularmente bem no ecossistema Apple;
- evita scanning desnecessário;
- mantém a UX simples;
- permite adicionar outros mecanismos posteriormente.

---

# 94. Decisão de produto — Open source

**ADR-003**

O projeto será pensado como:

```text
Open Source
License: MIT (recomendada)
Repository: public-ready
```

Consequências:

- nenhuma credencial no source;
- dependências precisam de revisão de licença;
- documentação deve ser suficiente para terceiros;
- arquitetura deve permitir contribuições sem comprometer simplicidade;
- assets visuais devem ser próprios ou licenciados adequadamente.

