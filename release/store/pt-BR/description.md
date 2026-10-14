O ServerAlyzer é um painel tranquilo e compacto para Windows 11 que mostra o status dos servidores Linux e macOS que você mantém: seu homelab, os serviços que você hospeda por conta própria ou as máquinas que você administra. Ele se conecta a cada servidor direto do seu PC, via SSH. Você não instala nenhum agente nos servidores, não cria conta e não existe nenhuma nuvem do ServerAlyzer no meio do caminho.

Todos os servidores em uma olhada
A Visão geral mostra como está cada servidor, destaca o que precisa de atenção e indica quando uma leitura está desatualizada. A lista de Servidores traz CPU, memória e disco de cada servidor, e a página de cada servidor mostra o tempo ativo. Os servidores são verificados automaticamente em segundo plano, e você pode atualizar quando quiser. Quando um valor não pode ser lido, o ServerAlyzer avisa em vez de mostrar zero.

Histórico no seu PC
As leituras de CPU, memória e disco ficam em um histórico local por 30 dias. Os gráficos vão da última hora até os últimos 30 dias.

Serviços e containers, somente leitura
Na página de cada servidor, veja os containers Docker e os serviços do sistema (systemd no Linux, launchd no macOS). O ServerAlyzer apenas lê: ele nunca inicia, para ou altera nada nos seus servidores.

SSH confiável
Antes de confiar em um servidor, o ServerAlyzer mostra a impressão digital SHA-256 da chave do host e pede que você a confirme. Se a chave mudar depois, a conexão é bloqueada. Senhas e frases secretas ficam no Gerenciador de Credenciais do Windows. Na autenticação por chave, só o local da sua chave privada é salvo, nunca o conteúdo dela. Nos servidores, o aplicativo executa apenas um conjunto fixo de comandos de leitura, sem sudo e sem comandos digitados por você.

Configuração rápida
Um guia inicial ajuda você a adicionar o primeiro servidor. Você pode preencher um servidor a partir de um host do seu arquivo de configuração SSH, escolher uma das suas chaves privadas padrão e usar um teste de conexão que mostra cada etapa, para você ver o que falhou. Servidores acessados por um único jump host (ProxyJump) também são suportados.

Integrado ao Windows 11
O ServerAlyzer pode continuar na área de notificação quando você fecha a janela e avisar com notificações do Windows quando a saúde de um servidor muda. O modo compacto oferece uma visão pequena de todos os servidores, que pode ficar sempre no topo. O widget do Windows 11 mostra seus servidores no painel de Widgets, em três tamanhos, e sempre indica há quanto tempo os dados foram lidos. Há temas claro, escuro e do sistema, além de suporte a navegação por teclado, leitores de tela e alto contraste. Disponível em português (Brasil), português (Portugal) e inglês.

Pesquisa na rede local, só quando você pede
Enquanto você ainda não tem servidores, é possível procurar na sua rede local servidores SSH que se anunciam (mDNS/DNS-SD). A pesquisa só é feita quando você escolhe e nunca varre portas nem faixas de endereços.

Backup criptografado
Salve seus servidores, chaves de host confiáveis e senhas salvas em um arquivo protegido por uma frase secreta escolhida por você, e restaure tudo depois. Os arquivos de chave privada nunca são incluídos.

Privacidade desde a origem
Sem conta, e o ServerAlyzer não coleta telemetria. Sua lista de servidores, o histórico e as credenciais salvas ficam no seu PC, a menos que você exporte um backup criptografado, que fica onde você salvar. O ServerAlyzer só usa a rede para chegar aos servidores que você adiciona (e à sua rede local quando você inicia uma pesquisa); ele não envia seus dados para o ServerAlyzer.

Requisitos
Um PC com Windows 11 (x64). O widget precisa do Windows 11 versão 22H2 ou mais recente. Os servidores precisam rodar Linux ou macOS e estar acessíveis via SSH a partir do seu PC; a lista de serviços precisa de systemd (Linux) ou launchd (macOS). O ServerAlyzer funciona só no Windows: ele monitora Macs, mas não existe versão do aplicativo para macOS.
