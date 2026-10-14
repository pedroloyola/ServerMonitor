O ServerAlyzer é um painel calmo e compacto para Windows 11 que te mostra o estado dos servidores Linux e macOS que geres: o teu homelab, os serviços que alojas em casa ou as máquinas que administras. Liga-se a cada servidor diretamente a partir do teu PC, por SSH. Não instalas nenhum agente nos servidores, não crias conta e não há nenhuma nuvem do ServerAlyzer pelo meio.

Todos os servidores num relance
A Visão geral mostra como está cada servidor, destaca o que precisa de atenção e indica quando uma leitura está desatualizada. A lista de Servidores junta CPU, memória e disco de cada servidor, e a página de cada servidor mostra o tempo de atividade. Os servidores são verificados automaticamente em segundo plano e podes atualizar quando quiseres. Quando um valor não pode ser lido, o ServerAlyzer diz-te isso em vez de mostrar zero.

Histórico no teu PC
As leituras de CPU, memória e disco ficam num histórico local durante 30 dias. Os gráficos vão da última hora aos últimos 30 dias.

Serviços e containers, só de leitura
Na página de cada servidor, vê os containers Docker e os serviços do sistema (systemd no Linux, launchd no macOS). O ServerAlyzer só lê: nunca inicia, para nem altera nada nos teus servidores.

SSH em que podes confiar
Antes de confiar num servidor, o ServerAlyzer mostra-te a impressão digital SHA-256 da chave do host e pede-te que a confirmes. Se a chave mudar mais tarde, a ligação é bloqueada. As palavras-passe e frases-passe ficam no Gestor de Credenciais do Windows. Na autenticação por chave, guarda-se apenas a localização da tua chave privada, nunca o seu conteúdo. Nos servidores, a aplicação corre apenas um conjunto fixo de comandos de leitura, sem sudo e sem comandos escritos por ti.

Pronto a usar em pouco tempo
Um guia de início ajuda-te a adicionar o primeiro servidor. Podes preencher um servidor a partir de um host do teu ficheiro de configuração SSH, escolher uma das tuas chaves privadas habituais e usar um teste de ligação que mostra cada passo, para veres o que falhou. Também podes usar servidores alcançados através de um único jump host (ProxyJump).

Feito para o Windows 11
O ServerAlyzer pode continuar na área de notificação quando fechas a janela e avisar-te com notificações do Windows quando o estado de um servidor muda. O modo compacto dá-te uma vista pequena de todos os servidores, que pode ficar sempre no topo. O widget do Windows 11 mostra os teus servidores no painel de Widgets, em três tamanhos, e indica sempre há quanto tempo os dados foram lidos. Tem temas claro, escuro e do sistema, e suporta navegação por teclado, leitores de ecrã e alto contraste. Disponível em português (Portugal), português (Brasil) e inglês.

Pesquisa na rede local, só quando pedes
Enquanto ainda não tens servidores, podes procurar na tua rede local servidores SSH que se anunciam (mDNS/DNS-SD). A pesquisa só corre quando a escolhes e nunca varre portas nem intervalos de endereços.

Cópia de segurança encriptada
Guarda os teus servidores, chaves de host confiáveis e palavras-passe guardadas num ficheiro protegido por uma frase-passe à tua escolha, e restaura-os mais tarde. Os ficheiros de chave privada nunca são incluídos.

Privacidade desde a conceção
Sem conta, e o ServerAlyzer não recolhe telemetria. A lista de servidores, o histórico e as credenciais guardadas ficam no teu PC, a não ser que exportes uma cópia de segurança encriptada, que fica onde a guardares. O ServerAlyzer só usa a rede para chegar aos servidores que adicionas (e à tua rede local quando inicias uma pesquisa); não envia os teus dados para o ServerAlyzer.

Requisitos
Um PC com Windows 11 (x64). O widget precisa do Windows 11 versão 22H2 ou mais recente. Os servidores têm de correr Linux ou macOS e estar acessíveis por SSH a partir do teu PC; a lista de serviços precisa de systemd (Linux) ou launchd (macOS). O ServerAlyzer só funciona no Windows: monitoriza Macs, mas não existe uma versão da aplicação para macOS.
