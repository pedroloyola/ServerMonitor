# Resolução de problemas — ServerAlyzer

## Ligação SSH

### "Host desconhecido" / a app pede para confirmar um fingerprint
Isto é intencional e seguro. Na primeira ligação a um servidor, a app mostra o **fingerprint SHA-256**
da host key e pede confirmação explícita. Confirma apenas se o fingerprint corresponder ao esperado
(podes obtê-lo no servidor com `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`). **Não** existe
aceitação automática — isto protege contra ligações a um servidor errado ou a um intermediário.

### "A host key não corresponde" / ligação bloqueada
A app **bloqueia** quando a host key de um servidor muda em relação à que confiaste. Isto pode indicar
uma reinstalação legítima do servidor **ou** um ataque. Verifica no servidor porque é que a chave mudou;
só depois de confirmares que é legítima é que deves repor a confiança. A app nunca contorna isto
automaticamente.

### Pede a password de novo depois de a ter guardado
As passwords/passphrases ficam no **Windows Credential Manager**. Se a app volta a pedir, verifica se a
credencial não foi removida externamente (Gestor de Credenciais do Windows → Credenciais do Windows).
A referência é reintroduzida de forma segura; o segredo não é lido a partir de ficheiros.

## Docker

### "Permissão negada" ao listar containers
A observação de Docker é **só-leitura** e usa a permissão do próprio utilizador SSH. Se aparecer
permissão negada, é porque esse utilizador não tem acesso ao Docker no servidor. Resolve isso **no
servidor**, segundo a política da tua organização (por exemplo, adicionando o utilizador ao grupo
apropriado). A app **não** usa `sudo` nem eleva privilégios.

### Docker não aparece / "não instalado"
Se o servidor não tem Docker, a secção aparece como indisponível. É esperado. A app não instala nada
no servidor.

## Serviços do sistema

### systemd/launchd indisponível ou sem serviços
Serviços requerem `systemd` (Linux) ou `launchd` (macOS). Em sistemas sem `systemd`, a secção de
serviços fica indisponível. No macOS, `launchd` distingue serviços a correr/parados, mas não deteta
falhas de tarefas one-shot como estado de falha.

## Descoberta de rede

### A descoberta não encontra o meu servidor
A descoberta é **passiva** (mDNS/DNS-SD, `_ssh._tcp.local.`) e não varre portas nem subnets. O servidor
só aparece se anunciar o serviço SSH via mDNS/Bonjour/Avahi na **mesma rede local**. Podes sempre
**adicionar manualmente** um servidor por endereço.

## Notificações

### Não recebo notificações
As notificações são locais e disparam apenas em **transições reais** de saúde (não no primeiro estado
observado) e respeitam um *cooldown*. Confirma que estão ativas em **Configurações**. O modo **Não
Incomodar** do Windows pode suprimir o banner — nesse caso a notificação continua a chegar à Central de
Notificações.

## Dados e definições

### Onde ficam os meus dados?
- `%LOCALAPPDATA%\ServerMonitor\servers.json` — configuração não sensível.
- `%LOCALAPPDATA%\ServerMonitor\history.db` — histórico (SQLite).
- Windows Credential Manager — segredos SSH.
- Ficheiros locais separados — host keys confiadas, dispositivos ignorados, preferências de janela.

### Desinstalei a app mas os dados continuam lá
É intencional: os dados ficam fora do pacote e não são apagados na desinstalação. Para os remover,
apaga a pasta `%LOCALAPPDATA%\ServerMonitor` e as credenciais correspondentes no Gestor de Credenciais.
