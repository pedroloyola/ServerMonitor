# SSH — fundação M3 e extensão M4

O transporte utiliza SSH.NET 2026.0.0 apenas através de `ISshConnectionService`.

- host keys desconhecidas são sondadas com autenticação `none`;
- fingerprints SHA-256 exigem confirmação explícita;
- mismatches bloqueiam antes da autenticação;
- password/private key são resolvidas através de abstrações seguras;
- algoritmos SHA-1, CBC, 3DES e `ssh-rsa` legado são removidos;
- `uname -s` continua reservado à deteção de Linux/macOS;
- no M4, uma sessão autenticada executa somente o catálogo interno de comandos Linux
  documentado em `docs/metrics.md`, com deadline global e limites de output.

Não existe polling nem uma API pública de execução arbitrária de comandos. Nenhum
valor fornecido pelo utilizador é concatenado no catálogo de comandos do M4.

## ProxyJump de um salto (M14.4b-2)

Um servidor com `Route` só é alcançado através do seu jump host (`SshJumpTunnel`); nunca é marcado direto.

- sonda-antes-da-autenticação nos DOIS saltos: a chave do jump é verificada no store direto
  (endpoint do jump), a do destino no store roteado (`SshRoute(jump, destino lógico)`), nunca
  `127.0.0.1:porta`; sem TOFU automático;
- listener local literal `127.0.0.1:0`, provado IPv4-loopback e do próprio processo pela tabela TCP;
  o destino é marcado em `127.0.0.1:BoundPort`;
- `LoopbackOriginatorGate`: armado para exatamente uma ligação imediatamente antes de cada connect
  do destino, só admite uma origem `127.0.0.1` cujo PID dono é este processo (fail closed);
- teardown único e idempotente: sessões do destino → forward → jump;
- classificação estrutural (`ServerVersion` recebido ou não), nunca por texto de exceção.
