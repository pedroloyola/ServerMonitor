<!--
RASCUNHO (REL.0E). Cada marca "[NEEDS HUMAN: ...]" tem de ser resolvida pelo editor e removida antes de este
URL ser introduzido no Partner Center (release/store/*/support.txt mantém a linha GATED até lá).
-->

# Política de Privacidade da ServerAlyzer

[English](PRIVACY.md) · **Português (Portugal)** · [Português (Brasil)](PRIVACY.pt-BR.md)

_Última atualização: 2026-10-16 · aplica-se à ServerAlyzer 1.1.7 e posteriores para Windows_

A ServerAlyzer é uma aplicação Windows **local-first** para monitorizar os teus próprios servidores Linux e
macOS por SSH. Foi desenhada para que os teus dados fiquem no teu PC e sob o teu controlo.

## O essencial

- **Sem conta.** Não há registo, início de sessão nem identificador de utilizador.
- **Sem servidores da ServerAlyzer.** A aplicação não tem backend e não envia nada ao programador: nem
  métricas, nem a lista de servidores, nem credenciais, nem dados de utilização.
- **Sem telemetria, análise de utilização, relatórios de falhas nem publicidade** na aplicação.
- **Sem nuvem.** Não há sincronização na nuvem nem conta online.
- **As únicas ligações de rede que a aplicação faz por iniciativa própria** são ligações SSH aos servidores que
  adicionas e, só quando a inicias, uma pesquisa na tua rede local (ver "Pesquisa na rede local").

## Quem é responsável

A ServerAlyzer é publicada na Microsoft Store por **[NEEDS HUMAN: nome legal e país do editor]**.
A aplicação guarda todos os dados no teu dispositivo e não transmite nada ao editor, por isso o editor não
recebe nem guarda os teus dados. **[NEEDS HUMAN: confirmar esta posição com um revisor jurídico, incluindo se é
preciso indicar aqui um responsável pelo tratamento e respetivos contactos ao abrigo do RGPD ou da LGPD.]**

Contacto: abre um issue em <https://github.com/pedroloyola/ServerMonitor/issues>. Comunica problemas de
segurança de forma privada através dos GitHub Security Advisories do mesmo repositório (separador Security), e
não num issue público. **[NEEDS HUMAN: acrescentar um endereço de e-mail de contacto para privacidade, se for
exigido.]**

## Como funciona a monitorização

- A ServerAlyzer liga-se **diretamente** do teu PC a cada servidor que adicionas, por SSH. Se configurares um
  servidor de salto (jump host) para um servidor, a ligação passa por esse servidor de salto. Nenhum serviço
  da ServerAlyzer fica pelo meio.
- Antes de confiar num servidor, a aplicação mostra a impressão digital da chave do host e pede-te que a
  confirmes. As chaves confiadas ficam guardadas no teu PC.
- A aplicação executa comandos **só de leitura**: métricas do sistema (CPU, memória, disco, tempo ligado, nome
  do host, versão do sistema operativo), a lista de containers Docker e a lista de serviços do sistema (systemd
  ou launchd). Não instala nada, não altera definições e não usa `sudo` nos teus servidores.
- Liga-se enquanto a aplicação está aberta e, se ativares a monitorização em segundo plano, enquanto corre na
  área de notificação.

## O que fica guardado no teu PC

Todos os dados ficam guardados localmente, numa pasta chamada `ServerMonitor` nos dados locais de aplicações
da tua conta de utilizador. Quando a aplicação é instalada a partir da Microsoft Store, o Windows mantém essa
pasta dentro da pasta de dados do próprio pacote da aplicação
(`%LOCALAPPDATA%\Packages\<nome do pacote>\LocalCache\Local\ServerMonitor`). A aplicação não guarda dados num
perfil itinerante (roaming) nem os sincroniza para lado nenhum.

| Dados | Ficheiro | Conteúdo |
|---|---|---|
| Servidores | `servers.json`, `routed-servers.json` | Nome, host, porta, utilizador SSH, método de início de sessão, o **caminho** de uma chave privada que escolhas, servidor de salto. Sem passwords. |
| Chaves de host confiadas | `known-hosts.json`, `known-hosts.routes.json` | Host, porta, tipo de chave e impressão digital. |
| Histórico de métricas | `history.db` (SQLite) | Por servidor: hora, estado de saúde, % de CPU, % de memória, % de disco. Guardado durante **30 dias** e depois apagado automaticamente. Podes limpá-lo nas Definições. |
| Resumo para o widget | `widget-state.json` | Para o widget do Windows: o nome de apresentação do servidor (nunca o host nem o endereço IP), estado de saúde, utilização de CPU, memória e disco, tempo ligado e hora da última atualização. |
| Dispositivos de rede ignorados | `ignored-devices.json` | Impressões digitais unidirecionais (hashes SHA-256) dos dispositivos que escolheste ignorar. Sem nomes nem endereços. |
| Preferências | `language-settings.json`, `notification-settings.json`, `background-settings.json`, `window-placement.json` | Idioma, escolhas de notificações e de monitorização em segundo plano, posição da janela. |
| Cópia de segurança do restauro | pasta `restore-journal` | Uma cópia temporária usada só enquanto um backup está a ser restaurado, para que um restauro interrompido possa ser desfeito. |

**As passwords e as passphrases de chaves** ficam guardadas no **Gestor de Credenciais do Windows**, para a
tua conta Windows neste PC. Nunca são escritas nos ficheiros acima. O conteúdo dos ficheiros de chave privada
nunca é copiado: a aplicação guarda apenas o caminho que escolheste.

As listas de containers Docker e de serviços são mostradas na aplicação e não são guardadas.

A aplicação **não escreve ficheiros de log**.

## Ficheiros que a aplicação lê no teu PC

- `%USERPROFILE%\.ssh\config`, apenas quando escolhes **Importar de SSH**.
- Para sugerir uma chave, a aplicação verifica se os ficheiros de chave predefinidos `id_ed25519`, `id_ecdsa`
  ou `id_rsa` existem em `%USERPROFILE%\.ssh`, usando apenas informação do ficheiro (nome e tamanho). Não os
  abre. Um ficheiro de chave só é lido para iniciar sessão num servidor que configuraste com essa chave.

## Pesquisa na rede local

- Podes pedir à ServerAlyzer que procure servidores SSH que se anunciam na tua rede local (mDNS/DNS-SD, tipo
  de serviço `_ssh._tcp`). A pesquisa **nunca começa sozinha**, só quando escolhes **Procurar na rede local**.
- Enquanto corre, a aplicação envia consultas mDNS normais na tua rede local e escuta as respostas (porta UDP
  5353). **Não** varre portas nem intervalos de endereços, e não se liga aos dispositivos que encontra.
- A pesquisa termina quando a paras, sais da página ou escondes a janela; ao fim de cerca de **40 segundos** se
  não encontrar nada novo; e, em qualquer caso, ao fim de **10 minutos**.
- Os dispositivos encontrados (nome, nome do host, endereços, porta) ficam **apenas em memória** e são
  descartados quando a pesquisa termina. Os dados adicionais dos anúncios (TXT) são ignorados.
- Na primeira pesquisa, **a Firewall do Windows pode perguntar** se permites a ServerAlyzer nas tuas redes. A
  aplicação precisa disso para receber as respostas. Se não permitires, a pesquisa pode não encontrar nada; o
  resto continua a funcionar.
- Adicionar um servidor encontrado passa pelo mesmo teste de ligação e pela mesma confirmação da chave do host
  que qualquer outro servidor.

## Backup cifrado

- Se criares um backup, o ficheiro contém a tua lista de servidores, as rotas, as chaves de host confiadas, as
  definições de notificações e de monitorização em segundo plano **e as tuas passwords e passphrases
  guardadas**.
- O ficheiro inteiro é cifrado com **AES-256-GCM**, com uma chave derivada de uma password de backup que
  escolhes (PBKDF2-HMAC-SHA256, 1 000 000 iterações). O ficheiro não pode ser lido sem essa password e não pode
  ser recuperado se a esqueceres.
- O ficheiro é guardado **apenas onde escolheres**. A aplicação nunca o envia para lado nenhum. Se o guardares
  numa pasta sincronizada com a nuvem, aplicam-se a esse ficheiro os termos desse serviço.
- Os ficheiros de chave privada e o histórico de métricas não são incluídos.

## Notificações e widget

- Os alertas são notificações locais do Windows. Mostram o nome de apresentação do servidor e o seu estado
  (por exemplo, "está em estado de atenção") e ficam no centro de notificações do Windows até expirarem ou até as
  limpares. A aplicação não usa nenhum serviço de notificações push.
- O widget da ServerAlyzer mostra os dados de `widget-state.json`. O Windows apresenta-os no painel de Widgets.

## Componentes do Windows e da Microsoft

- A aplicação é distribuída e atualizada através da Microsoft Store. As instalações, atualizações,
  classificações e críticas são tratadas pela Microsoft, ao abrigo da Declaração de Privacidade da Microsoft.
- A aplicação assenta no Windows e no Windows App SDK. Os dados de diagnóstico que o próprio Windows recolhe
  regem-se pelas tuas definições de privacidade do Windows e pela Declaração de Privacidade da Microsoft, e não
  por esta política. **[NEEDS HUMAN: confirmar esta redação sobre os dados de diagnóstico do Windows e do
  Windows App SDK.]**
- O pacote declara a capacidade `runFullTrust`, de que as aplicações deste tipo precisam para funcionar como
  uma aplicação de ambiente de trabalho normal do Windows. A ServerAlyzer usa-a apenas para o que esta política
  descreve. **[NEEDS HUMAN: confirmar esta redação.]**

## Ligações que abrem o teu navegador

As ligações na aplicação, como a página do projeto no GitHub nas Definições, só abrem no teu navegador quando
clicas nelas. Nesse site aplicam-se os termos de privacidade respetivos.

## Componentes de código aberto

A ServerAlyzer inclui componentes de código aberto que correm no teu PC e não enviam nada aos seus autores:
SSH.NET, Tmds.MDns, Microsoft.Data.Sqlite e SQLitePCLRaw (SQLite), WinUIEx, o Windows App SDK, bibliotecas .NET
e os ícones Hugeicons. As licenças estão listadas em `THIRD-PARTY-NOTICES.md`, no repositório.

## O teu controlo sobre os teus dados

- Na aplicação podes remover um servidor, limpar o histórico de métricas (**Limpar histórico**) e voltar a
  mostrar os dispositivos ignorados (**Voltar a mostrar dispositivos ignorados**).
- Também podes ver e remover as passwords guardadas no **Gestor de Credenciais** do Windows.
- **Desinstalação:** **[NEEDS HUMAN: confirmar medindo a desinstalação do pacote da Store. Esperado: o Windows
  remove a pasta de dados do pacote com os ficheiros acima, e as passwords e passphrases no Gestor de
  Credenciais ficam até as removeres.]** Os ficheiros de backup que guardaste noutro sítio nunca são tocados.

## Crianças

**[NEEDS HUMAN: decidir se é precisa uma declaração sobre crianças. A aplicação é uma ferramenta técnica e não
recolhe dados de ninguém.]**

## Alterações a esta política

As alterações são publicadas neste ficheiro com uma nova data de "Última atualização", e o histórico do
ficheiro é público no repositório. Se alguma vez for acrescentada uma funcionalidade opcional na nuvem, será
descrita aqui antes de ser lançada e será **opt-in**.
