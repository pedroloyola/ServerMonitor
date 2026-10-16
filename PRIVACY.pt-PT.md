<!--
RASCUNHO (REL.0E). Cada marca "[NEEDS HUMAN: ...]" tem de ser resolvida pelo editor e removida antes de este
URL ser introduzido no Partner Center (release/store/*/support.txt mantém a linha GATED até lá).
-->

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

- **Dados tratados apenas no teu PC.** Tudo o que está descrito em "O que fica guardado no teu PC", "Ficheiros
  que a aplicação lê no teu PC", "Pesquisa na rede local", "Cópia de segurança encriptada" e "Notificações e
  widget" é tratado pela aplicação no teu PC. A aplicação nunca o envia ao editor, por isso o editor nunca o
  recebe, guarda ou vê.
- **Dados que o editor pode receber.** Apenas:
  - o que decidires enviar: um e-mail para o endereço acima, ou um issue, comentário ou relatório de segurança
    no GitHub (no GitHub aplicam-se também os termos e a declaração de privacidade do próprio GitHub);
  - os relatórios, classificações e críticas que a Microsoft disponibiliza aos editores da Store (ver
    "Componentes do Windows e da Microsoft").
- **Dados sob responsabilidade da Microsoft.** A Microsoft Store, os dados de diagnóstico do Windows e o painel
  de Widgets são geridos pela Microsoft ao abrigo da
  [Declaração de Privacidade da Microsoft](https://privacy.microsoft.com/pt-pt/privacystatement), e não desta
  política.
- **Obrigações do editor.** Quanto aos dados do segundo ponto, o editor é o responsável pelo tratamento nos
  termos do Regulamento Geral sobre a Proteção de Dados (RGPD). Podes exercer os teus direitos sobre esses
  dados, como o acesso, a retificação ou o apagamento, pedindo-o ao editor através do endereço de e-mail acima, e
  podes apresentar reclamação a uma autoridade de controlo (em Portugal, a Comissão Nacional de Proteção de Dados,
  CNPD). **[NEEDS HUMAN: um revisor jurídico confirma se a finalidade, o fundamento jurídico e o prazo de
  conservação desta correspondência e destes relatórios, e a lista completa de direitos (incluindo a limitação,
  a oposição e a portabilidade), têm de ser indicados aqui, e com que redação; e se a LGPD brasileira também se
  aplica.]**

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
ficheiros); a aplicação não o escreve em nenhum ficheiro nem o envia para lado nenhum, e só pode ser lido com
ferramentas de rastreio de eventos do Windows no teu PC.

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
- **Relatórios que a Microsoft dá ao editor.** Através do Partner Center, a Microsoft dá aos editores da Store
  relatórios que compila a partir de dados da Store e do Windows, por exemplo: visualizações da página da Store,
  instalações e desinstalações, dispositivos ativos, sessões e a sua duração, por mercado, tipo de dispositivo,
  versão do Windows e versão da aplicação; números e taxas de falhas e bloqueios, com detalhes técnicos das
  falhas, como stack traces (as taxas de falhas e bloqueios usam os dispositivos que partilham dados de
  diagnóstico opcionais); e as classificações e críticas publicadas na Store
  ([análise do Partner Center](https://learn.microsoft.com/windows/apps/publish/analyze-app-performance/msix),
  [relatório de estado de funcionamento](https://learn.microsoft.com/windows/apps/publish/analyze-app-performance/health-report)).
  O ServerAlyzer não acrescenta nada a estes relatórios: não usa o Microsoft Store Services SDK nem regista
  eventos personalizados. **[NEEDS HUMAN: quando a aplicação estiver publicada, o editor confirma, só em
  leitura, em Partner Center → Insights, que são estes os relatórios mostrados para o ServerAlyzer, e atualiza
  este parágrafo se não forem.]**
- **Dados de diagnóstico.** A aplicação assenta no Windows e no Windows App SDK, um runtime da Microsoft
  instalado com ela. O Windows pode enviar dados de diagnóstico à Microsoft, incluindo relatórios de falhas e
  bloqueios de aplicações, de acordo com as tuas definições de dados de diagnóstico do Windows e ao abrigo da
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
