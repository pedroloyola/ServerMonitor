<!--
RASCUNHO (REL.0E). Cada marca "[NEEDS HUMAN: ...]" precisa ser resolvida pelo editor e removida antes de esta
URL ser informada no Partner Center (release/store/*/support.txt mantém a linha GATED até lá).
-->

# Política de Privacidade do ServerAlyzer

[English](PRIVACY.md) · [Português (Portugal)](PRIVACY.pt-PT.md) · **Português (Brasil)**

_Última atualização: 2026-10-16 · vale para o ServerAlyzer 1.1.7 e posteriores para Windows_

O ServerAlyzer é um aplicativo Windows **local-first** para monitorar seus próprios servidores Linux e macOS
por SSH. Ele foi projetado para que seus dados fiquem no seu PC e sob o seu controle.

## O essencial

- **Sem conta.** Não há cadastro, login nem identificador de usuário.
- **Sem servidores do ServerAlyzer.** O aplicativo não tem backend e não envia nada ao desenvolvedor: nem
  métricas, nem a lista de servidores, nem credenciais, nem dados de uso.
- **Sem telemetria, análise de uso, relatórios de falhas nem publicidade** no aplicativo.
- **Sem nuvem.** Não há sincronização na nuvem nem conta online.
- **As únicas conexões de rede que o aplicativo faz por iniciativa própria** são conexões SSH com os servidores
  que você adiciona e, só quando você a inicia, uma pesquisa na sua rede local (veja "Pesquisa na rede local").
- **Resolução de nomes.** Para se conectar a um servidor informado por nome, o Windows resolve esse nome pelo
  serviço DNS da sua rede, ou por mDNS no caso de nomes `.local`. Em um servidor acessado por meio de um
  servidor de salto, é o servidor de salto que resolve o nome. Nenhum serviço do ServerAlyzer participa.

## Quem é o responsável

O ServerAlyzer é publicado na Microsoft Store por **[NEEDS HUMAN: nome legal e país do editor]**.
O aplicativo guarda todos os dados no seu dispositivo e não transmite nada ao editor, por isso o editor não
recebe nem guarda os seus dados. **[NEEDS HUMAN: confirmar esta posição com um revisor jurídico, inclusive se é
preciso indicar aqui um controlador e os respectivos contatos nos termos da LGPD ou do GDPR.]**

Contato: abra uma issue em <https://github.com/pedroloyola/ServerMonitor/issues>. Informe problemas de
segurança de forma privada pelos GitHub Security Advisories do mesmo repositório (aba Security), e não em uma
issue pública. **[NEEDS HUMAN: adicionar um endereço de e-mail de contato para privacidade, se for exigido.]**

## Como funciona o monitoramento

- O ServerAlyzer se conecta **diretamente** do seu PC a cada servidor que você adiciona, por SSH. Se você
  configurar um servidor de salto (jump host) para um servidor, a conexão passa por esse servidor de salto.
  Nenhum serviço do ServerAlyzer fica no meio.
- Antes de confiar em um servidor, o aplicativo mostra a impressão digital da chave do host e pede que você a
  confirme. As chaves confiáveis ficam salvas no seu PC.
- O aplicativo executa comandos **somente leitura**: métricas do sistema (CPU, memória, disco, tempo ligado,
  nome do host, versão do sistema operacional), a lista de containers Docker e a lista de serviços do sistema
  (systemd ou launchd). Ele não instala nada, não altera configurações e não usa `sudo` nos seus servidores.
- Ele se conecta enquanto o aplicativo está aberto e, se você ativar o monitoramento em segundo plano, enquanto
  roda na área de notificação.

## O que fica salvo no seu PC

Todos os dados ficam salvos localmente, em uma pasta chamada `ServerMonitor` nos dados locais de aplicativos da
sua conta de usuário. Quando o aplicativo é instalado pela Microsoft Store, o Windows mantém essa pasta dentro
da pasta de dados do próprio pacote do aplicativo
(`%LOCALAPPDATA%\Packages\<nome do pacote>\LocalCache\Local\ServerMonitor`). O aplicativo não salva dados em
um perfil móvel (roaming) nem os sincroniza com nenhum lugar.

| Dados | Arquivo | Conteúdo |
|---|---|---|
| Servidores | `servers.json`, `routed-servers.json` | Nome, host, porta, usuário SSH, método de login, o **caminho** de uma chave privada que você escolher, servidor de salto e preferências do servidor (sistema operacional, intervalo de atualização, oculto ou não, data em que foi adicionado, uma referência interna à senha salva). Sem senhas. |
| Chaves de host confiáveis | `known-hosts.json`, `known-hosts.routes.json` | Host, porta, tipo de chave e impressão digital. |
| Histórico de métricas | `history.db` (SQLite) | Por servidor: horário, estado de saúde, % de CPU, % de memória, % de disco. Mantido por **30 dias** e depois apagado automaticamente. Você pode limpá-lo nas Configurações. |
| Resumo para o widget | `widget-state.json` | Para o widget do Windows: um identificador interno do servidor, o nome de exibição do servidor (nunca o host nem o endereço IP), estado de saúde, uso de CPU, memória e disco (porcentagem, e GB usados e totais), tempo ligado e horário da última atualização. |
| Dispositivos de rede ignorados | `ignored-devices.json` | Impressões digitais unidirecionais (hashes SHA-256) dos dispositivos que você escolheu ignorar. Sem nomes nem endereços. |
| Preferências | `language-settings.json`, `notification-settings.json`, `background-settings.json`, `window-placement.json` | Idioma, escolhas de notificações e de monitoramento em segundo plano, posição da janela. |
| Cópia de segurança da restauração | pasta `restore-journal` | Uma cópia temporária usada só enquanto um backup está sendo restaurado, para que uma restauração interrompida possa ser desfeita. |

**As senhas e as frases secretas (passphrases) de chaves** ficam salvas no **Gerenciador de Credenciais do
Windows**, para a sua conta do Windows neste PC. Elas nunca são gravadas nos arquivos acima. O conteúdo dos
arquivos de chave privada nunca é copiado: o aplicativo guarda apenas o caminho que você escolheu.

As listas de containers Docker e de serviços são mostradas no aplicativo e não são salvas.

O aplicativo **não grava arquivos de log**.

## Arquivos que o aplicativo lê no seu PC

- `%USERPROFILE%\.ssh\config`, e os arquivos que ele inclui a partir de `%USERPROFILE%\.ssh`, somente quando você
  escolhe **Importar de SSH**. Arquivos incluídos fora dessa pasta, e arquivos que sejam links, não são lidos.
- Para sugerir uma chave, o aplicativo verifica se os arquivos de chave padrão `id_ed25519`, `id_ecdsa` ou
  `id_rsa` existem em `%USERPROFILE%\.ssh`, usando apenas informações do arquivo (nome e tamanho). Ele não os
  abre. Um arquivo de chave só é lido para fazer login em um servidor que você configurou com essa chave.

## Pesquisa na rede local

- Você pode pedir ao ServerAlyzer que procure servidores SSH que se anunciam na sua rede local (mDNS/DNS-SD,
  tipo de serviço `_ssh._tcp`). A pesquisa **nunca começa sozinha**, só quando você escolhe **Procurar na rede
  local**.
- Enquanto ela roda, o aplicativo envia consultas mDNS comuns na sua rede local e escuta as respostas (porta
  UDP 5353). Ele **não** varre portas nem faixas de endereços, e não se conecta aos dispositivos que encontra.
- A pesquisa termina quando você a interrompe, sai da página ou oculta a janela; depois de cerca de **40
  segundos** se não encontrar nada novo; e, em qualquer caso, depois de **10 minutos**.
- Os dispositivos encontrados (nome, nome do host, endereços, porta) ficam **somente na memória** e são
  descartados quando a pesquisa termina. Os dados adicionais dos anúncios (TXT) são ignorados.
- Na primeira pesquisa, **o Firewall do Windows pode perguntar** se você permite o ServerAlyzer nas suas redes.
  O aplicativo precisa disso para receber as respostas. Se você não permitir, a pesquisa pode não encontrar
  nada; o resto continua funcionando. O Windows salva sua resposta como regras do Firewall do Windows para o
  aplicativo, que permanecem depois da pesquisa; você pode revisá-las ou removê-las nas configurações do Firewall
  do Windows.
- Adicionar um servidor encontrado passa pelo mesmo teste de conexão e pela mesma confirmação da chave do host
  que qualquer outro servidor.

## Backup criptografado

- Se você criar um backup, o arquivo contém sua lista de servidores, as rotas, as chaves de host confiáveis,
  as configurações de notificações e de monitoramento em segundo plano **e suas senhas e frases secretas
  salvas**.
- O conteúdo do arquivo é criptografado com **AES-256-GCM**, com uma chave derivada de uma senha de backup que você
  escolhe (PBKDF2-HMAC-SHA256, 1.000.000 de iterações). O arquivo não pode ser lido sem essa senha e não pode
  ser recuperado se você a esquecer. Um cabeçalho curto com os parâmetros técnicos da criptografia (versão do
  formato, algoritmo, número de iterações, salt) fica salvo sem criptografia, como esse método exige; ele não
  contém nenhum dos seus dados.
- O arquivo é salvo **somente onde você escolher**. O aplicativo nunca o envia para lugar nenhum. Se você o
  salvar em uma pasta sincronizada com a nuvem, os termos desse serviço valem para esse arquivo.
- Os arquivos de chave privada e o histórico de métricas não são incluídos.

## Notificações e widget

- Os alertas são notificações locais do Windows. Eles mostram o nome de exibição do servidor e o seu estado
  (por exemplo, "está em estado de atenção") e ficam na central de notificações do Windows até expirarem ou até
  você limpá-las. O aplicativo não usa nenhum serviço de notificações push.
- O widget do ServerAlyzer mostra os dados de `widget-state.json`. Para exibi-los, o aplicativo entrega o
  conteúdo do cartão (nome de exibição, estado de saúde, métricas, tempo ligado) ao painel de Widgets do
  Windows, um componente da Microsoft. **[NEEDS HUMAN: confirmar se os dados entregues ao painel de Widgets
  ficam sujeitos à Política de Privacidade da Microsoft, e dizer isso aqui.]**

## Componentes do Windows e da Microsoft

- O aplicativo é distribuído e atualizado pela Microsoft Store. Instalações, atualizações, classificações e
  avaliações são tratadas pela Microsoft, nos termos da Política de Privacidade da Microsoft.
- O aplicativo é construído sobre o Windows e o Windows App SDK. Os dados de diagnóstico que o próprio Windows
  coleta são regidos pelas suas configurações de privacidade do Windows e pela Política de Privacidade da
  Microsoft, e não por esta política. **[NEEDS HUMAN: confirmar esta redação sobre os dados de diagnóstico do
  Windows e do Windows App SDK.]**
- O pacote declara a capacidade `runFullTrust`, de que aplicativos desse tipo precisam para funcionar como um
  aplicativo de área de trabalho comum do Windows. O ServerAlyzer a usa somente para o que esta política
  descreve. **[NEEDS HUMAN: confirmar esta redação.]**

## Links que abrem o seu navegador

Os links no aplicativo, como a página do projeto no GitHub nas Configurações, só abrem no seu navegador quando
você clica neles. Nesse site valem os termos de privacidade dele.

## Componentes de código aberto

O ServerAlyzer inclui componentes de código aberto que rodam no seu PC e não enviam nada aos seus autores:
SSH.NET, Tmds.MDns, Microsoft.Data.Sqlite e SQLitePCLRaw (SQLite), WinUIEx, o Windows App SDK, bibliotecas .NET
e os ícones Hugeicons. As licenças estão listadas em `THIRD-PARTY-NOTICES.md`, no repositório.

## O seu controle sobre os seus dados

- No aplicativo você pode remover um servidor, limpar o histórico de métricas (**Limpar histórico**) e mostrar
  novamente os dispositivos ignorados (**Mostrar novamente os dispositivos ignorados**).
- Você também pode ver e remover as senhas salvas no **Gerenciador de Credenciais** do Windows.
- **Desinstalação:** **[NEEDS HUMAN: confirmar medindo a desinstalação do pacote da Store. Esperado: o Windows
  remove a pasta de dados do pacote com os arquivos acima, e as senhas e frases secretas no Gerenciador de
  Credenciais permanecem até você removê-las.]** Os arquivos de backup que você salvou em outro lugar nunca são
  tocados.

## Crianças

**[NEEDS HUMAN: decidir se é necessária uma declaração sobre crianças. O aplicativo é uma ferramenta técnica e
não coleta dados de ninguém.]**

## Alterações nesta política

As alterações são publicadas neste arquivo com uma nova data de "Última atualização", e o histórico do arquivo
é público no repositório. Se algum dia for adicionado um recurso opcional na nuvem, ele será descrito aqui
antes de ser lançado e será **opt-in**.
