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
  serviço DNS da sua rede, ou por mDNS no caso de nomes `.local`. Em um servidor acessado por meio de um jump
  host, é o jump host que resolve o nome. Nenhum serviço do ServerAlyzer participa.

## Quem é o responsável

O ServerAlyzer é desenvolvido e publicado na Microsoft Store por **Pedro Loyola**, pessoa física residente em
Portugal, com o nome de editor **Pedro Loy** (o "editor").

Contato para dúvidas sobre privacidade: **serveralyzer@pedroloyola.com**. Você também pode abrir uma issue em
<https://github.com/pedroloyola/ServerMonitor/issues>. Informe problemas de segurança de forma privada pelos
GitHub Security Advisories do mesmo repositório (aba Security), e não em uma issue pública.

## O que o editor pode e não pode receber

O ServerAlyzer envolve três tipos de dados, e esta política os mantém separados.

- **Dados que ficam somente no seu dispositivo.** Tudo o que está descrito em "O que fica salvo no seu PC",
  "Arquivos que o aplicativo lê no seu PC", "Pesquisa na rede local", "Backup criptografado" e "Notificações e
  widget" é tratado pelo aplicativo no seu PC. O aplicativo nunca envia isso ao editor, por isso o editor nunca
  recebe, guarda ou vê esses dados.
- **Mensagens que você decide enviar ao editor.** Um e-mail para serveralyzer@pedroloyola.com, ou uma issue, comentário ou
  relatório de segurança no GitHub. Elas contêm o que você escreve e o seu endereço de e-mail ou nome de usuário
  do GitHub.
- **Relatórios que a Microsoft mostra ao editor no Partner Center.** Relatórios sobre o aplicativo na Store que
  a Microsoft compila e mostra ao editor, e as avaliações e comentários publicados na Store (veja "Componentes
  do Windows e da Microsoft"). Eles não são telemetria do ServerAlyzer.

A Microsoft Store, os dados de diagnóstico do Windows e o painel de Widgets são operados pela Microsoft nos
termos da [Política de Privacidade da Microsoft](https://privacy.microsoft.com/pt-br/privacystatement), e não
desta política.

## Como o editor trata os dados que recebe

Quanto às mensagens e aos relatórios do Partner Center acima, o editor é o controlador nos termos do
Regulamento Geral sobre a Proteção de Dados da União Europeia (GDPR).

| | Mensagens que você envia | Relatórios do Partner Center, avaliações e comentários |
|---|---|---|
| **Finalidade** | Ler e responder à sua mensagem, e corrigir ou melhorar o que você relata. | Entender as instalações, o uso e a confiabilidade do aplicativo, corrigir falhas e travamentos, e ler e responder aos comentários. |
| **Base legal proposta** | Legítimo interesse (art. 6(1)(f) do GDPR): responder a mensagens enviadas voluntariamente e dar suporte ao aplicativo que você usa. | Legítimo interesse (art. 6(1)(f) do GDPR): manter a qualidade do aplicativo, usando os relatórios que a Microsoft disponibiliza a todos os editores da Store. |
| **Por quanto tempo** | E-mails: pelo tempo necessário para tratar a sua mensagem e o acompanhamento, e depois são apagados [OWNER: prazo máximo, ou o critério para apagar]. GitHub: as issues e comentários são públicos e ficam no GitHub até serem excluídos; você pode editar ou excluir os seus comentários, ou pedir ao editor que exclua uma issue ou comentário. | O editor não guarda cópias fora do Partner Center [OWNER: confirmar, ou informar o critério]. Dentro do Partner Center valem os termos da própria Microsoft. |
| **Destinatários** | O serviço de e-mail que hospeda o endereço acima [OWNER: nome do provedor de e-mail]. No GitHub, a GitHub, Inc., que publica o conteúdo nos termos da sua própria [declaração de privacidade](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement). | A Microsoft, que opera a Store e o Partner Center. Os comentários são públicos na Store; a Microsoft informa que o nome e a imagem da sua conta Microsoft podem ser exibidos com eles. |

**Seus direitos.** Você pode pedir ao editor, pelo serveralyzer@pedroloyola.com, o acesso aos seus dados, a correção ou a exclusão
deles, a limitação do tratamento, ou se opor a ele; quando for o caso, você também pode pedir a portabilidade
dos dados. O editor responde em até um mês, como o GDPR exige. Os dados que a Microsoft mantém são tratados pela
Microsoft (veja a Política de Privacidade da Microsoft). Você também pode apresentar uma reclamação a uma
autoridade de proteção de dados; em Portugal, a Comissão Nacional de Proteção de Dados (CNPD,
<https://www.cnpd.pt>).

**Transferências internacionais.** O editor está em Portugal. O GitHub informa que armazena e trata dados
pessoais em vários locais, inclusive nos Estados Unidos, e que recorre às cláusulas contratuais padrão da
Comissão Europeia e à sua certificação no Data Privacy Framework UE-EUA
([declaração de privacidade do GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)). A Microsoft informa que pode armazenar e tratar dados pessoais na
sua região, nos Estados Unidos e em outros países, e que recorre às cláusulas contratuais padrão e ao Data
Privacy Framework UE-EUA (Política de Privacidade da Microsoft). [OWNER: onde o provedor de e-mail armazena as
mensagens, se for fora da UE.]

**Brasil (LGPD).** Esta política também é publicada em português do Brasil, e o aplicativo pode ser oferecido
na Store brasileira [OWNER: confirmar os mercados]. **[NEEDS HUMAN: decisão jurídica pendente. Um revisor
jurídico aprova as finalidades, as bases legais, os critérios de retenção, os destinatários e a redação sobre
transferências acima, decide se a LGPD se aplica (e, se sim, acrescenta os direitos dela e a ANPD), e o editor
preenche os itens [OWNER: …].]**

## Como funciona o monitoramento

- O ServerAlyzer se conecta **diretamente** do seu PC a cada servidor que você adiciona, por SSH. Se você
  configurar um jump host para um servidor, a conexão passa por esse jump host.
  Nenhum serviço do ServerAlyzer fica no meio.
- Antes de confiar em um servidor, o aplicativo mostra a impressão digital da chave do host e pede que você a
  confirme. As chaves confiáveis ficam salvas no seu PC.
- O aplicativo executa comandos **somente leitura**: métricas do sistema (CPU, memória, disco, tempo ativo,
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
| Servidores | `servers.json`, `routed-servers.json` | Nome, host, porta, usuário SSH, método de login, o **caminho** de uma chave privada que você escolher, jump host e preferências do servidor (sistema operacional, intervalo de atualização, oculto ou não, data em que foi adicionado, uma referência interna à senha salva). Sem senhas. |
| Chaves de host confiáveis | `known-hosts.json`, `known-hosts.routes.json` | Host, porta, tipo de chave e impressão digital. |
| Histórico de métricas | `history.db` (SQLite) | Por servidor: horário, estado de saúde, % de CPU, % de memória, % de disco. Mantido por **30 dias**: os registros mais antigos são apagados automaticamente enquanto o aplicativo está em execução (quando ele inicia e depois uma vez por dia). Você pode limpá-lo em **Configurações › Dados e servidores**. |
| Resumo para o widget | `widget-state.json` | Para o widget do Windows: um identificador interno do servidor, o nome que você deu ao servidor (o aplicativo nunca o substitui pelo host nem pelo endereço IP), estado de saúde, uso de CPU, memória e disco (porcentagem, e GB usados e totais), tempo ativo e horário da última atualização. |
| Dispositivos de rede ignorados | `ignored-devices.json` | Impressões digitais unidirecionais (hashes SHA-256) dos dispositivos que você escolheu ignorar. Sem nomes nem endereços. |
| Preferências | `language-settings.json`, `notification-settings.json`, `background-settings.json`, `window-placement.json` | Idioma, escolhas de notificações e de monitoramento em segundo plano, posição da janela. |
| Cópia temporária da restauração | pasta `restore-journal` | Uma cópia temporária usada só enquanto um backup está sendo restaurado, para que uma restauração interrompida possa ser desfeita. |

**As senhas e as frases secretas (passphrases) de chaves** ficam salvas no **Gerenciador de Credenciais do
Windows**, para a sua conta do Windows neste PC. Elas nunca são gravadas nos arquivos acima. O conteúdo dos
arquivos de chave privada nunca é copiado: o aplicativo guarda apenas o caminho que você escolheu.

As listas de containers Docker e de serviços são mostradas no aplicativo e não são salvas.

O aplicativo **não grava arquivos de log**. O componente do widget pode emitir rastreamento técnico de eventos
do Windows (só nomes de operações e tipos de erro, nunca nomes de servidores, dados dos servidores nem caminhos
de arquivos); o aplicativo não o grava em nenhum arquivo nem o envia para lugar nenhum, e ele só pode ser lido
no seu PC com ferramentas de diagnóstico do Windows (rastreamento de eventos ou um depurador).

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
- O conteúdo do arquivo é criptografado com **AES-256-GCM**, com uma chave derivada de uma frase secreta que você
  escolhe para o backup (PBKDF2-HMAC-SHA256, 1.000.000 de iterações). O arquivo não pode ser lido sem essa frase
  secreta e não pode ser recuperado se você a esquecer. Um cabeçalho curto com os parâmetros técnicos da
  criptografia (versão do formato, algoritmos, número de iterações, salt, nonce e comprimento) fica salvo sem criptografia, como esse método exige; ele não
  contém nenhum dos seus dados.
- O arquivo é salvo **somente onde você escolher**. O aplicativo nunca o envia para lugar nenhum. Se você o
  salvar em uma pasta sincronizada com a nuvem, os termos desse serviço valem para esse arquivo.
- Os arquivos de chave privada e o histórico de métricas não são incluídos.

## Notificações e widget

- Os alertas são notificações locais do Windows. Eles mostram o nome que você deu ao servidor e o seu estado
  (por exemplo, "está em estado de atenção") e ficam na central de notificações do Windows até expirarem ou até
  você limpá-las. O aplicativo não usa nenhum serviço de notificações push.
- O widget do ServerAlyzer mostra os dados de `widget-state.json`. Para exibi-los, o aplicativo entrega o
  conteúdo do cartão (um identificador interno do servidor, o nome que você deu ao servidor, estado de saúde,
  métricas, tempo ativo) ao Windows no seu PC, que o exibe no painel de Widgets, um recurso do Windows fornecido pela
  Microsoft. O ServerAlyzer não o envia para nenhum serviço online. A Microsoft informa que coleta dados de
  diagnóstico do Windows a partir do painel de Widgets de acordo com suas configurações de dados de diagnóstico,
  e que cada widget é controlado pelas configurações do aplicativo que o fornece, aqui o ServerAlyzer
  ([Suporte da Microsoft: Widgets](https://support.microsoft.com/pt-br/windows/stay-up-to-date-with-widgets-in-windows-7ba79aaa-dac6-4687-b460-ad16a06be6e4)).

## Componentes do Windows e da Microsoft

- O aplicativo é distribuído e atualizado pela Microsoft Store. As instalações, atualizações, avaliações e
  comentários são tratados pela Microsoft, nos termos da Política de Privacidade da Microsoft.
- **Relatórios que a Microsoft mostra ao editor.** No Partner Center, a Microsoft mostra ao editor relatórios
  sobre o aplicativo ServerAlyzer na Store que a própria Microsoft compila: aquisições (como visualizações da
  página da Store e instalações), uso (como sessões), integridade (como falhas e travamentos, os dispositivos
  afetados e detalhes técnicos das falhas) e as avaliações e comentários publicados na Store
  ([análise do Partner Center](https://learn.microsoft.com/windows/apps/publish/analyze-app-performance/msix),
  [relatório de integridade](https://learn.microsoft.com/windows/apps/publish/analyze-app-performance/health-report)).
  A Microsoft coleta esses dados nos termos da Política de Privacidade da Microsoft e, no caso das falhas e
  travamentos, de acordo com suas configurações de dados de diagnóstico do Windows. Esses relatórios não são
  telemetria do ServerAlyzer: o aplicativo não envia nada para eles, não usa o Microsoft Store Services SDK nem
  registra eventos personalizados.
- **Dados de diagnóstico.** O aplicativo é baseado no Windows e no Windows App SDK, um runtime da Microsoft
  instalado com ele. O Windows pode enviar dados de diagnóstico à Microsoft, inclusive relatórios de falhas e
  travamentos de aplicativos, de acordo com suas configurações de dados de diagnóstico do Windows e nos termos
  da Política de Privacidade da Microsoft. O ServerAlyzer não acrescenta nada a eles.
- O ServerAlyzer é um aplicativo de área de trabalho do Windows e declara a capacidade `runFullTrust` da
  Microsoft Store, por isso é executado como um programa de área de trabalho comum, e não dentro da sandbox dos
  aplicativos da Store. Ele a usa para se conectar por SSH aos servidores que você adicionar, para
  procurar servidores na sua rede local somente quando você inicia uma pesquisa, para guardar as senhas e frases
  secretas dos servidores no Gerenciador de Credenciais do Windows, no seu dispositivo, e para oferecer as
  notificações, o ícone na área de notificação e o widget do Windows. Essa capacidade não dá ao aplicativo
  direitos de administrador.

## Links que abrem o seu navegador

Os links no aplicativo, como a página do projeto no GitHub em **Configurações › Dados e servidores**, só abrem no
seu navegador quando você clica neles. Nesse site valem os termos de privacidade dele.

## Componentes de código aberto

O ServerAlyzer inclui componentes de código aberto que rodam no seu PC e não enviam nada aos seus autores:
SSH.NET, Tmds.MDns, Microsoft.Data.Sqlite e SQLitePCLRaw (SQLite), WinUIEx, o Windows App SDK, bibliotecas .NET
e os ícones Hugeicons. As licenças estão listadas em `THIRD-PARTY-NOTICES.md`, no repositório.

## O seu controle sobre os seus dados

- No aplicativo você pode remover um servidor e, em **Configurações › Dados e servidores**, limpar o histórico
  de métricas (**Limpar histórico**) e mostrar novamente os dispositivos ignorados (**Mostrar novamente os
  dispositivos ignorados**).
- Você também pode ver as entradas das senhas e frases secretas salvas no **Gerenciador de Credenciais** do
  Windows e removê-las.
- **Desinstalação:** o Windows remove a pasta de dados do pacote com os arquivos acima. Duas coisas permanecem
  até você removê-las:
  - **As senhas e frases secretas SSH que você salvou**, no Gerenciador de Credenciais do Windows. Os nomes das
    entradas não contêm o nome do servidor, o endereço nem o usuário. Para removê-las, abra o **Painel de
    Controle** → **Gerenciador de Credenciais** → **Credenciais do Windows** → **Credenciais Genéricas** e remova
    cada entrada que comece com `ServerMonitor:v1:ssh` (ou, se você usou uma versão antiga,
    `pedroloyola.ServerMonitor:v1:ssh`).
  - **As regras do Firewall do Windows com o nome `ServerMonitor.App`**, se você permitiu o acesso quando o
    Firewall do Windows perguntou durante uma pesquisa na rede local. É o Windows que cria essas regras; o
    aplicativo não as cria nem as remove. Para removê-las, abra a **Segurança do Windows** → **Firewall e
    proteção de rede** → **Configurações avançadas** → **Regras de Entrada** e exclua as regras com o nome
    `ServerMonitor.App`.

  O aplicativo nunca altera sua pasta `.ssh`. Os arquivos de backup que você salvou em outro lugar nunca são
  tocados.

## Crianças

O ServerAlyzer é uma ferramenta técnica para o público em geral e não se destina a crianças; ele não tem
recursos voltados para crianças. O aplicativo não coleta dados pessoais de ninguém, inclusive de crianças.

## Alterações nesta política

As alterações são publicadas neste arquivo com uma nova data de "Última atualização", e o histórico do arquivo
é público no repositório. Se algum dia for adicionado um recurso opcional na nuvem, ele será descrito aqui
antes de ser lançado e será **opt-in**.
