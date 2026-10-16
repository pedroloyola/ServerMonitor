# Política de Privacidade do ServerAlyzer

[English](PRIVACY.md) · **Português (Portugal)** · [Português (Brasil)](PRIVACY.pt-BR.md)

_Última atualização: 2026-10-16 · aplica-se ao ServerAlyzer 1.1.7 e posteriores para Windows_

O ServerAlyzer é uma aplicação Windows **local-first** para monitorizar os teus próprios servidores Linux e
macOS por SSH. Foi desenhado para que os teus dados fiquem no teu PC e sob o teu controlo.

## O essencial

- **Sem conta.** Não há registo, início de sessão nem identificador de utilizador.
- **Sem servidores do ServerAlyzer.** A aplicação não tem backend e não envia nada ao programador: nem
  métricas, nem a lista de servidores, nem credenciais, nem dados de utilização.
- **Sem telemetria, análise de utilização, relatórios de falhas nem publicidade** na aplicação.
- **Sem nuvem.** Não há sincronização na nuvem nem conta online.
- **As únicas ligações de rede que a aplicação faz por iniciativa própria** são ligações SSH aos servidores que
  adicionas e, só quando a inicias, uma pesquisa na tua rede local (ver "Pesquisa na rede local").
- **Resolução de nomes.** Para te ligares a um servidor indicado por nome, o Windows resolve esse nome através
  do serviço DNS da tua rede, ou por mDNS no caso de nomes `.local`. Num servidor alcançado através de um jump
  host, é o jump host que resolve o nome. Nenhum serviço do ServerAlyzer intervém.

## Quem é responsável

O ServerAlyzer é desenvolvido e publicado na Microsoft Store por **Pedro Loyola**, pessoa singular residente em
Portugal, com o nome de editor **Pedro Loy** (o "editor").

Contacto para questões de privacidade: **serveralyzer@pedroloyola.com**. Também podes abrir um issue em
<https://github.com/pedroloyola/ServerMonitor/issues>. Comunica problemas de segurança de forma privada através
dos GitHub Security Advisories do mesmo repositório (separador Security), e não num issue público.

## O que o editor pode e não pode receber

O ServerAlyzer envolve quatro tipos de dados, e esta política mantém-nos separados.

1. **Dados que ficam só no teu dispositivo.** Tudo o que está descrito em "O que fica guardado no teu PC",
   "Ficheiros que a aplicação lê no teu PC", "Pesquisa na rede local", "Cópia de segurança encriptada" e
   "Notificações e widget" é tratado pela aplicação no teu PC. A aplicação nunca o envia ao editor, por isso o
   editor nunca o recebe, guarda ou vê.
2. **Mensagens que decides enviar ao editor.** Um e-mail para **serveralyzer@pedroloyola.com**, ou um issue, comentário ou
   relatório de segurança privado no GitHub.
3. **Informação que a Microsoft mostra ao editor no Partner Center.** Relatórios sobre a aplicação na Store que
   a própria Microsoft compila, e as classificações e críticas publicadas na Store (ver "Componentes do Windows
   e da Microsoft"). Não são telemetria do ServerAlyzer.
4. **Dados tratados pela Microsoft e por outros serviços independentes.** A Microsoft Store, o Windows
   (incluindo os seus dados de diagnóstico e o painel de Widgets), o GitHub e o serviço de e-mail que usas tratam
   dados ao abrigo dos seus próprios termos e declarações de privacidade, por exemplo a
   [Declaração de Privacidade da Microsoft](https://privacy.microsoft.com/pt-pt/privacystatement), e não desta
   política.

## Como o editor trata os dados que recebe

**Responsável pelo tratamento.** Quanto às mensagens do ponto 2, e quanto ao uso que o próprio editor faz da
informação do ponto 3, o responsável pelo tratamento nos termos do Regulamento Geral sobre a Proteção de Dados
(RGPD) é o editor: Pedro Loyola (nome de editor Pedro Loy), pessoa singular residente em Portugal, contacto
**serveralyzer@pedroloyola.com**. A recolha e a compilação da informação do Partner Center pela Microsoft regem-se pela
Declaração de Privacidade da Microsoft.

| | Mensagens que envias (ponto 2) | Informação do Partner Center (ponto 3) |
|---|---|---|
| **Dados** | O que escreves; o teu nome e endereço de e-mail tal como o teu programa de e-mail os envia, ou o teu nome de utilizador do GitHub; e os detalhes técnicos que decidas incluir. | Estatísticas sobre a aplicação na Store, como visualizações da página, instalações, sessões, falhas e bloqueios e os dispositivos afetados; e as classificações e críticas publicadas na Store, com o nome que a Microsoft mostra em cada crítica. |
| **Finalidade** | Ler e responder à tua mensagem, e investigar, corrigir ou melhorar o que comunicas. | Perceber como a aplicação é instalada e usada, encontrar e corrigir falhas e bloqueios, e ler e responder às críticas. As exportações ocasionais de relatórios servem apenas para auditoria e diagnóstico. |
| **Fundamento jurídico** | Interesses legítimos (artigo 6.º, n.º 1, alínea f), do RGPD): responder a quem contacta o editor voluntariamente e dar apoio à aplicação. És tu que inicias o contacto, esperas uma resposta, e só é usado o que decides enviar. | Interesses legítimos (artigo 6.º, n.º 1, alínea f), do RGPD): manter a aplicação fiável e responder ao feedback. A Microsoft disponibiliza esta informação a todos os editores da Store, é sobretudo estatística, e as críticas já são públicas. |
| **Durante quanto tempo** | E-mails: guardados enquanto forem necessários para tratar a tua mensagem e o seguimento, e apagados o mais tardar 12 meses depois da última mensagem da conversa, salvo se forem necessários para efeitos de declaração, exercício ou defesa de um direito num processo judicial. Issues e comentários no GitHub: são públicos e ficam no GitHub até serem apagados; podes editar ou apagar os teus comentários, ou pedir ao editor que apague um issue ou comentário. Relatórios de segurança privados: ficam confidenciais no GitHub até o problema estar resolvido; o GitHub propõe que sejas mencionado no aviso de segurança resultante, e só és identificado se aceitares; podes pedir ao editor que retire a menção; os e-mails sobre um relatório seguem a regra dos e-mails. | O editor consulta esta informação no Partner Center e pode ocasionalmente exportar relatórios, apenas para auditoria e diagnóstico. Os relatórios exportados ficam apenas nos dispositivos do próprio editor e são apagados quando deixarem de ser necessários para essa finalidade, e o mais tardar 12 meses depois da exportação. A Microsoft pode também enviar ao editor notificações por e-mail sobre a aplicação, como alertas sobre o estado de funcionamento; esses e-mails seguem a regra dos e-mails. Dentro do Partner Center aplicam-se os termos da Microsoft. |
| **Destinatários** | No e-mail: a Cloudflare, Inc., cujo serviço Email Routing recebe as mensagens enviadas para o endereço do editor e as reencaminha para a caixa de correio do editor, e a Microsoft, cujo serviço Outlook aloja essa caixa de correio. A Cloudflare indica que o Email Routing não examina, não põe em fila nem guarda e-mails, que as mensagens são entregues em tempo real, e que trata o envelope da mensagem para a entregar, mantendo intactos os cabeçalhos e o corpo ([Cloudflare](https://blog.cloudflare.com/introducing-email-routing/)). Quanto ao conteúdo no GitHub, a GitHub, Inc., que indica agir como responsável pelo tratamento ao abrigo da sua própria [declaração de privacidade](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement). O editor não partilha as tuas mensagens com mais ninguém. Os issues e comentários que publicas no GitHub ficam visíveis para qualquer pessoa. Se um relatório disser respeito a uma componente de terceiros, o editor pode transmitir os detalhes técnicos, sem os teus dados pessoais, a quem mantém essa componente. | A Microsoft, que gere a Store e o Partner Center. As críticas são públicas na Store; a Microsoft indica que o nome e a imagem da tua conta Microsoft podem ser mostrados com elas. Mais ninguém; os relatórios exportados ficam nos dispositivos do próprio editor. |

**Sem decisões automatizadas.** Não há decisões automatizadas nem definição de perfis.

**Os teus direitos.** Nos termos do RGPD, podes pedir ao editor o acesso aos teus dados, a sua retificação ou
apagamento, a limitação do seu tratamento e, quando o RGPD o preveja, a sua portabilidade.

**O teu direito de oposição.** Como o editor se baseia em interesses legítimos, podes opor-te a qualquer
momento, por motivos relacionados com a tua situação particular, ao tratamento dos teus dados descrito acima. O
editor deixa então de os tratar, a não ser que apresente razões imperiosas e legítimas para esse tratamento que
prevaleçam sobre os teus interesses, direitos e liberdades, ou para efeitos de declaração, exercício ou defesa
de um direito num processo judicial (artigo 21.º, n.º 1, do RGPD).

**Como fazer um pedido.** Envia o teu pedido por e-mail para **serveralyzer@pedroloyola.com** e diz o que pedes. Os
pedidos são gratuitos. Se o editor tiver dúvidas razoáveis quanto à tua identidade, pode pedir-te apenas a
informação necessária para a confirmar. O editor responde sem demora injustificada e no prazo de um mês. Esse
prazo pode ser prorrogado por mais dois meses, no máximo, quando necessário, tendo em conta a complexidade e o número de
pedidos; nesse caso, o editor informa-te no primeiro mês, com os motivos. Quanto aos dados que a Microsoft ou o
GitHub detêm, como a tua conta Microsoft, as tuas críticas na Store ou a tua conta do GitHub, contacta-os
diretamente.

**Reclamações.** Podes apresentar reclamação a uma autoridade de controlo; em Portugal, a Comissão Nacional de
Proteção de Dados (CNPD, <https://www.cnpd.pt>).

**Transferências internacionais.** O editor está em Portugal. O GitHub indica que guarda e trata dados pessoais
em vários locais, incluindo os Estados Unidos, e que recorre às cláusulas contratuais-tipo da Comissão Europeia
e à sua certificação ao abrigo do Quadro de Privacidade de Dados UE-EUA
([declaração de privacidade do GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)). A Microsoft, que também aloja a caixa de correio do editor (Outlook), indica que pode guardar e tratar dados pessoais na tua
região, nos Estados Unidos e noutros países, e que recorre às cláusulas contratuais-tipo e ao Quadro de
Privacidade de Dados UE-EUA (Declaração de Privacidade da Microsoft). A Cloudflare indica que, nas transferências a partir do Espaço Económico Europeu, recorre às
cláusulas contratuais-tipo da UE e à sua certificação ao abrigo do Quadro de Privacidade de Dados UE-EUA, e que o
seu Data Processing Addendum normalizado está incorporado por referência no seu Self-Serve Subscription Agreement
([perguntas frequentes da Cloudflare sobre o RGPD](https://www.cloudflare.com/trust-hub/gdpr/)).

**Brasil (LGPD).** Nos termos do seu artigo 3.º, a Lei Geral de Proteção de Dados Pessoais (LGPD) brasileira aplica-se
quando o tratamento é realizado no Brasil, quando tem por objetivo a oferta ou o fornecimento de bens ou
serviços, ou o tratamento de dados, de pessoas localizadas no Brasil, ou quando os dados pessoais foram
recolhidos no Brasil. Se é esse o caso depende destes factos, por exemplo se o ServerAlyzer é disponibilizado na
Microsoft Store brasileira; a existência de uma versão desta política em português do Brasil não faz, por si só,
com que a LGPD se aplique. Quando a LGPD se aplica, tens também os direitos do seu artigo 18.º, que podes exercer
através de **serveralyzer@pedroloyola.com**, e podes apresentar reclamação à Autoridade Nacional de Proteção de Dados
(ANPD).


## Como funciona a monitorização

- O ServerAlyzer liga-se **diretamente** do teu PC a cada servidor que adicionas, por SSH. Se configurares um
  jump host para um servidor, a ligação passa por esse jump host. Nenhum serviço do ServerAlyzer fica pelo meio.
- Antes de confiar num servidor, a aplicação mostra a impressão digital da chave do host e pede-te que a
  confirmes. As chaves confiáveis ficam guardadas no teu PC.
- A aplicação executa comandos **só de leitura**: métricas do sistema (CPU, memória, disco, tempo de atividade,
  nome do host, versão do sistema operativo), a lista de containers Docker e a lista de serviços do sistema
  (systemd ou launchd). Não instala nada, não altera definições e não usa `sudo` nos teus servidores.
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
| Servidores | `servers.json`, `routed-servers.json` | Nome, host, porta, utilizador SSH, método de início de sessão, o **caminho** de uma chave privada que escolhas, jump host e preferências do servidor (sistema operativo, intervalo de atualização, oculto ou não, data em que foi adicionado, uma referência interna à palavra-passe guardada). Sem palavras-passe. |
| Chaves de host confiáveis | `known-hosts.json`, `known-hosts.routes.json` | Host, porta, tipo de chave e impressão digital. |
| Histórico de métricas | `history.db` (SQLite) | Por servidor: hora, estado de saúde, % de CPU, % de memória, % de disco. Guardado durante **30 dias**: os registos mais antigos são apagados automaticamente enquanto a aplicação está a correr (quando arranca e depois uma vez por dia). Podes limpá-lo em **Definições › Dados e servidores**. |
| Resumo para o widget | `widget-state.json` | Para o widget do Windows: um identificador interno do servidor, o nome que deste ao servidor (a aplicação nunca o substitui pelo host nem pelo endereço IP), estado de saúde, utilização de CPU, memória e disco (percentagem, e GB usados e totais), tempo de atividade e hora da última atualização. |
| Dispositivos de rede ignorados | `ignored-devices.json` | Impressões digitais unidirecionais (hashes SHA-256) dos dispositivos que escolheste ignorar. Sem nomes nem endereços. |
| Preferências | `language-settings.json`, `notification-settings.json`, `background-settings.json`, `window-placement.json` | Idioma, escolhas de notificações e de monitorização em segundo plano, posição da janela. |
| Cópia temporária do restauro | pasta `restore-journal` | Uma cópia temporária usada só enquanto uma cópia de segurança está a ser restaurada, para que um restauro interrompido possa ser desfeito. |

**As palavras-passe e as frases-passe de chaves** ficam guardadas no **Gestor de Credenciais do Windows**,
para a tua conta Windows neste PC. Nunca são escritas nos ficheiros acima. O conteúdo dos ficheiros de chave
privada nunca é copiado: a aplicação guarda apenas o caminho que escolheste.

As listas de containers Docker e de serviços são mostradas na aplicação e não são guardadas.

A aplicação **não escreve ficheiros de log**. O componente do widget pode emitir rastreio técnico de eventos do
Windows (só nomes de operações e tipos de erro, nunca nomes de servidores, dados dos servidores nem caminhos de
ficheiros); a aplicação não o escreve em nenhum ficheiro nem o envia para lado nenhum, e só pode ser lido no teu
PC com ferramentas de diagnóstico do Windows (rastreio de eventos ou um depurador).

## Ficheiros que a aplicação lê no teu PC

- `%USERPROFILE%\.ssh\config`, e os ficheiros que ele inclui a partir de `%USERPROFILE%\.ssh`, apenas quando
  escolhes **Importar de SSH**. Ficheiros incluídos fora dessa pasta, e ficheiros que sejam ligações, não são lidos.
- Para sugerir uma chave, a aplicação verifica se os ficheiros de chave predefinidos `id_ed25519`, `id_ecdsa`
  ou `id_rsa` existem em `%USERPROFILE%\.ssh`, usando apenas informação do ficheiro (nome e tamanho). Não os
  abre. Um ficheiro de chave só é lido para iniciar sessão num servidor que configuraste com essa chave.

## Pesquisa na rede local

- Podes pedir ao ServerAlyzer que procure servidores SSH que se anunciam na tua rede local (mDNS/DNS-SD, tipo
  de serviço `_ssh._tcp`). A pesquisa **nunca começa sozinha**, só quando escolhes **Procurar na rede local**.
- Enquanto corre, a aplicação envia consultas mDNS normais na tua rede local e escuta as respostas (porta UDP
  5353). **Não** varre portas nem intervalos de endereços, e não se liga aos dispositivos que encontra.
- A pesquisa termina quando a paras, sais da página ou escondes a janela; ao fim de cerca de **40 segundos** se
  não encontrar nada novo; e, em qualquer caso, ao fim de **10 minutos**.
- Os dispositivos encontrados (nome, nome do host, endereços, porta) ficam **apenas em memória** e são
  descartados quando a pesquisa termina. Os dados adicionais dos anúncios (TXT) são ignorados.
- Na primeira pesquisa, **a Firewall do Windows pode perguntar** se permites o ServerAlyzer nas tuas redes. A
  aplicação precisa disso para receber as respostas. Se não permitires, a pesquisa pode não encontrar nada; o
  resto continua a funcionar. O Windows guarda a tua resposta como regras da Firewall do Windows para a
  aplicação, que ficam depois da pesquisa; podes revê-las ou removê-las nas definições da Firewall do Windows.
- Adicionar um servidor encontrado passa pelo mesmo teste de ligação e pela mesma confirmação da chave do host
  que qualquer outro servidor.

## Cópia de segurança encriptada

- Se criares uma cópia de segurança, o ficheiro contém a tua lista de servidores, as rotas, as chaves de host
  confiáveis, as definições de notificações e de monitorização em segundo plano **e as tuas palavras-passe e
  frases-passe guardadas**.
- O conteúdo do ficheiro é encriptado com **AES-256-GCM**, com uma chave derivada de uma frase-passe que
  escolhes para a cópia de segurança (PBKDF2-HMAC-SHA256, 1 000 000 iterações). O ficheiro não pode ser lido
  sem essa frase-passe e não pode ser recuperado se a esqueceres. Um cabeçalho curto com os parâmetros técnicos
  da encriptação (versão do formato, algoritmos, número de iterações, salt, nonce e comprimento) fica guardado
  sem encriptação, como este método exige; não contém nenhum dos teus dados.
- O ficheiro é guardado **apenas onde escolheres**. A aplicação nunca o envia para lado nenhum. Se o guardares
  numa pasta sincronizada com a nuvem, aplicam-se a esse ficheiro os termos desse serviço.
- Os ficheiros de chave privada e o histórico de métricas não são incluídos.

## Notificações e widget

- Os alertas são notificações locais do Windows. Mostram o nome que deste ao servidor e o seu estado (por
  exemplo, "está em estado de atenção") e ficam no centro de notificações do Windows até expirarem ou até as
  limpares. A aplicação não usa nenhum serviço de notificações push.
- O widget do ServerAlyzer mostra os dados de `widget-state.json`. Para os apresentar, a aplicação entrega o
  conteúdo do cartão (um identificador interno do servidor, o nome que deste ao servidor, estado de saúde,
  métricas, tempo de atividade) ao Windows no teu PC, que o mostra no painel de Widgets, uma funcionalidade do
  Windows fornecida pela Microsoft. O ServerAlyzer não o envia para nenhum serviço online. A Microsoft indica
  que recolhe dados de diagnóstico do Windows a partir do painel de Widgets de acordo com as tuas definições de
  dados de diagnóstico, e que cada widget é controlado pelas definições da aplicação que o fornece, aqui o
  ServerAlyzer
  ([Suporte da Microsoft: Widgets](https://support.microsoft.com/pt-pt/windows/stay-up-to-date-with-widgets-in-windows-7ba79aaa-dac6-4687-b460-ad16a06be6e4)).

## Componentes do Windows e da Microsoft

- A aplicação é distribuída e atualizada através da Microsoft Store. As instalações, atualizações,
  classificações e críticas são tratadas pela Microsoft, ao abrigo da Declaração de Privacidade da Microsoft.
- **Relatórios que a Microsoft mostra ao editor.** No Partner Center, a Microsoft mostra ao editor relatórios
  sobre a aplicação ServerAlyzer na Store que a própria Microsoft compila: aquisições (como visualizações da
  página da Store e instalações), utilização (como sessões), estado de funcionamento (como falhas e bloqueios,
  os dispositivos afetados e detalhes técnicos das falhas) e as classificações e críticas publicadas na Store
  ([análise do Partner Center](https://learn.microsoft.com/windows/apps/publish/analyze-app-performance/msix),
  [relatório de estado de funcionamento](https://learn.microsoft.com/windows/apps/publish/analyze-app-performance/health-report)).
  A Microsoft recolhe estes dados ao abrigo da Declaração de Privacidade da Microsoft e, no caso das falhas e
  bloqueios, de acordo com as tuas definições de dados de diagnóstico do Windows. Estes relatórios não são
  telemetria do ServerAlyzer: a aplicação não envia nada para eles, não usa o Microsoft Store Services SDK nem
  regista eventos personalizados.
- **Dados de diagnóstico.** A aplicação assenta no Windows e no Windows App SDK, um runtime da Microsoft
  instalado com ela. O Windows pode enviar dados de diagnóstico à Microsoft, incluindo relatórios de falhas e
  bloqueios de aplicações através do Relatório de Erros do Windows (Windows Error Reporting), de acordo com as tuas definições de dados de diagnóstico do Windows e ao abrigo da
  Declaração de Privacidade da Microsoft. O ServerAlyzer não lhes acrescenta nada.
- O ServerAlyzer é uma aplicação de ambiente de trabalho do Windows e declara a capacidade `runFullTrust` da
  Microsoft Store, pelo que funciona como um programa de ambiente de trabalho normal e não dentro da sandbox das
  aplicações da Store. Usa-a para se ligar por SSH aos servidores que adicionas, para procurar servidores na tua
  rede local apenas quando inicias uma pesquisa, para guardar as palavras-passe e frases-passe dos servidores no
  Gestor de Credenciais do Windows, no teu dispositivo, e para disponibilizar as notificações, o ícone na área
  de notificação e o widget do Windows. Esta capacidade não dá à aplicação direitos de administrador.

## Ligações que abrem o teu navegador

As ligações na aplicação, como a página do projeto no GitHub em **Definições › Dados e servidores**, só abrem
no teu navegador quando clicas nelas. Nesse site aplicam-se os termos de privacidade respetivos.

## Componentes de código aberto

O ServerAlyzer inclui componentes de código aberto que correm no teu PC e não enviam nada aos seus autores:
SSH.NET, Tmds.MDns, Microsoft.Data.Sqlite e SQLitePCLRaw (SQLite), WinUIEx, o Windows App SDK, bibliotecas .NET
e os ícones Hugeicons. As licenças estão listadas em `THIRD-PARTY-NOTICES.md`, no repositório.

## O teu controlo sobre os teus dados

- Na aplicação podes remover um servidor e, em **Definições › Dados e servidores**, limpar o histórico de
  métricas (**Limpar histórico**) e voltar a mostrar os dispositivos ignorados (**Voltar a mostrar dispositivos
  ignorados**).
- Também podes ver as entradas das palavras-passe e frases-passe guardadas no **Gestor de Credenciais** do
  Windows e removê-las.
- **Desinstalação:** o Windows remove a pasta de dados do pacote com os ficheiros acima. Duas coisas ficam até
  as removeres tu:
  - **As palavras-passe e frases-passe SSH que guardaste**, no Gestor de Credenciais do Windows. Os nomes das
    entradas não contêm o nome do servidor, o endereço nem o utilizador. Para as remover, abre o **Painel de
    Controlo** → **Gestor de Credenciais** → **Credenciais do Windows** → **Credenciais Genéricas** e remove cada
    entrada que comece por `ServerMonitor:v1:ssh` (ou, se usaste uma versão antiga,
    `pedroloyola.ServerMonitor:v1:ssh`).
  - **As regras da Firewall do Windows com o nome `ServerMonitor.App`**, se permitiste o acesso quando a
    Firewall do Windows perguntou durante uma pesquisa na rede local. É o Windows que cria estas regras; a
    aplicação não as cria nem as remove. Para as remover, abre a **Segurança do Windows** → **Firewall e proteção
    de rede** → **Definições avançadas** → **Regras de Entrada** e elimina as regras com o nome
    `ServerMonitor.App`.

  A aplicação nunca altera a tua pasta `.ssh`. Os ficheiros de cópia de segurança que guardaste noutro sítio
  nunca são tocados.

## Crianças

O ServerAlyzer é uma ferramenta técnica para o público em geral e não se destina a crianças; não tem
funcionalidades dirigidas a crianças. A aplicação não recolhe dados pessoais de ninguém, incluindo crianças.

## Alterações a esta política

As alterações são publicadas neste ficheiro com uma nova data de "Última atualização", e o histórico do
ficheiro é público no repositório. Se alguma vez for acrescentada uma funcionalidade opcional na nuvem, será
descrita aqui antes de ser lançada e será **opt-in**.
