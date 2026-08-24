# ADR-006 — Transporte SSH e confiança na identidade do host

Estado: **aceite e implementado no Milestone 3**.

## Contexto e âmbito

O cliente Windows precisa de testar ligações SSH a Linux e macOS sem backend e sem aceitar silenciosamente servidores desconhecidos. O Milestone 3 limita-se a autenticação, host-key trust, teste explícito e deteção de sistema operativo; não contém polling, métricas, descoberta ou comandos remotos arbitrários.

## Biblioteca escolhida

É utilizada **SSH.NET 2026.0.0**, fixada no projeto Infrastructure e completamente escondida atrás de `ISshConnectionService`.

Razões:

- licença MIT compatível com o projeto;
- manutenção ativa e adoção ampla;
- target `.NET 8+`, compatível com `.NET 10`;
- suporte a ED25519, RSA-SHA2, ECDSA, password e private keys OpenSSH/PEM/PuTTY;
- `ConnectAsync(CancellationToken)` e execução assíncrona cancelável de comandos;
- evento explícito `HostKeyReceived`, necessário para bloquear hosts desconhecidos e mismatches.

Foram rejeitados:

- `Tmds.Ssh`: tecnicamente sólido, mas ainda pré-1.0 e com superfície menos estável;
- executar `ssh.exe`: prompts e parsing frágeis, lifecycle e credenciais difíceis de controlar;
- Rebex: licença comercial;
- bibliotecas antigas sem manutenção ou maturidade suficiente.

## Política criptográfica

O adaptador aplica uma allowlist moderna. São permitidos ED25519, ECDSA e RSA-SHA2, Curve25519/ECDH/DH-SHA2, ChaCha20-Poly1305, AES-GCM/AES-CTR e HMAC-SHA2.

São removidos dos defaults da biblioteca:

- `ssh-rsa` com SHA-1;
- DH group1/group14-SHA1;
- AES-CBC e 3DES;
- HMAC-SHA1;
- DSA.

Servidores que apenas suportem algoritmos legados falham com um erro tipado e não recebem fallback inseguro.

## Host-key trust

A confiança usa TOFU explícito em duas fases:

1. sem identidade conhecida, o transporte usa autenticação `none`, captura algoritmo e fingerprint e define sempre `CanTrust = false`;
2. a UI apresenta endpoint, algoritmo e fingerprint SHA-256;
3. apenas após “Confiar e conectar” a identidade é persistida;
4. a ligação é repetida com a credencial e a fingerprint aprovada;
5. nas ligações seguintes, qualquer mismatch bloqueia a autenticação e apresenta as fingerprints conhecida e recebida;
6. nunca existe `AcceptAnyHostKey`, fallback silencioso ou substituição automática.

O formato canónico é `SHA256:<Base64 sem padding>`. A comparação descodifica os 32 bytes e utiliza `CryptographicOperations.FixedTimeEquals`. A confiança é ligada ao endpoint normalizado `(host, port)` e à família de host key, não apenas ao `Server.Id` ou endereço apresentado.

As identidades confiadas, que não são segredos, são guardadas em:

```text
%LOCALAPPDATA%\ServerMonitor\known-hosts.json
```

Uma primeira confiança continua vulnerável a MITM se o utilizador não comparar a fingerprint por um canal independente; a UI explica esta limitação.

O carregamento do trust store é fail-closed: JSON malformado, entradas incompletas ou endpoints duplicados bloqueiam operações SSH em vez de serem reinterpretados como hosts nunca vistos ou sobrescritos silenciosamente.

## Async, timeout e erros

Cada operação cria e dispõe o seu próprio `SshClient`. `ConnectAsync`, `TestConnectionAsync` e `DetectOperatingSystemAsync` recebem `CancellationToken` e utilizam um watchdog de timeout ligado ao token do chamador.

Exceções da biblioteca e sockets são convertidas para estados/códigos próprios. Mensagens cruas não chegam à UI. Cancelamento do utilizador e timeout são distintos.

## Deteção de sistema operativo

Após autenticação e validação da host key, apenas é executado:

```text
uname -s
```

`Linux` é mapeado para `Linux` e `Darwin` para `MacOS`. Não são recolhidos CPU, RAM, disco, uptime ou throughput.

## Logging

Logs podem conter `Server.Id`, host, porta, estado, timeout, duração e tipo de exceção. Não podem conter password, passphrase, conteúdo/path de private key, valores do Credential Manager, banners, stdout/stderr ou objetos de request completos.

## Limitações

- não existe suporte ao Windows OpenSSH agent neste milestone;
- private keys são limitadas a ficheiros regulares até 1 MiB em drives fixed/removable; paths UNC, drives de rede/virtuais e reparse points no path são rejeitados para impedir I/O remoto ou leitura não limitada durante o parsing síncrono da biblioteca;
- o TOFU inicial exige verificação humana por canal externo;
- strings exigidas pela API SSH não podem ser zeradas deterministicamente pelo runtime .NET, embora a aplicação minimize o seu lifetime e limpe buffers próprios;
- a verificação do path e a abertura da private key são chamadas consecutivas, mas a API de alto nível ainda deixa uma pequena janela TOCTOU; eliminar totalmente essa janela exigirá resolução do path final por handle nativo;
- algoritmos apenas SHA-1 são deliberadamente incompatíveis.
