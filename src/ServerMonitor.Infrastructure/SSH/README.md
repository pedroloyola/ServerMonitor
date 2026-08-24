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
