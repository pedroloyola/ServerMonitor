# SSH — Milestone 3

O transporte utiliza SSH.NET 2026.0.0 apenas através de `ISshConnectionService`.

- host keys desconhecidas são sondadas com autenticação `none`;
- fingerprints SHA-256 exigem confirmação explícita;
- mismatches bloqueiam antes da autenticação;
- password/private key são resolvidas através de abstrações seguras;
- algoritmos SHA-1, CBC, 3DES e `ssh-rsa` legado são removidos;
- apenas `uname -s` é executado para deteção de Linux/macOS.

Não existem métricas, polling ou execução arbitrária de comandos neste milestone.
