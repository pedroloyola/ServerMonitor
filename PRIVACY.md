# Política de Privacidade — ServerAlyzer

_Última atualização: 2026-08-27_

A ServerAlyzer é uma aplicação **local-first**. Foi desenhada para que os teus dados fiquem na tua
máquina e sob o teu controlo.

## O essencial

- **Não requer conta.** Não há registo, login nem identificador de utilizador.
- **Não envia métricas nem dados para servidores da ServerAlyzer.** Não existe backend da aplicação.
- **Não há telemetria por omissão.** A app não recolhe nem transmite dados de uso, diagnóstico ou
  analítica nesta versão.
- **Sem nuvem.** Não existe sincronização em nuvem, conta na nuvem nem componente SaaS.

## Como funciona a monitorização

A ligação SSH acontece **diretamente** entre o teu PC e cada servidor que configuras. Nenhum
intermediário recebe as tuas credenciais, métricas ou comandos. A app apenas executa comandos de
**leitura** (métricas de host, listagem só-leitura de containers Docker e de serviços do sistema).

## Onde ficam os teus dados

Todos os dados são locais:

| Dado | Local |
|---|---|
| Configuração de servidores (não sensível) | `%LOCALAPPDATA%\ServerMonitor\servers.json` |
| Histórico de métricas | `%LOCALAPPDATA%\ServerMonitor\history.db` (SQLite) |
| Segredos SSH (passwords, passphrases) | **Windows Credential Manager** |
| Confiança de host keys | ficheiro local separado |
| Dispositivos de descoberta ignorados | ficheiro local separado |
| Preferências (tema, idioma, posição/modo da janela, notificações) | ficheiros locais |

Segredos **nunca** são escritos em ficheiros de configuração nem em logs. O conteúdo de chaves privadas
nunca é copiado — apenas o caminho que escolheres.

## Logs

Os logs são técnicos, locais e sanitizados: não incluem passwords, conteúdo de chaves privadas nem
credenciais. Não há upload de logs.

## Desinstalação

Como os dados ficam **fora** do sandbox do pacote (em `%LOCALAPPDATA%\ServerMonitor` e no Credential
Manager), a desinstalação da app **não** os remove automaticamente. Para os apagar, remove manualmente
a pasta `%LOCALAPPDATA%\ServerMonitor` e as credenciais correspondentes no Gestor de Credenciais do
Windows.

## Alterações

Qualquer alteração futura a esta política (por exemplo, a introdução de funcionalidades opcionais de
nuvem) será documentada aqui e será **opt-in**.
