# ADR-004 — Configuração local não sensível

Estado: **aceite e implementado no Milestone 2**.

## Decisão

A lista manual de servidores é persistida como JSON em:

```text
%LOCALAPPDATA%\ServerMonitor\servers.json
```

A UI utiliza `IServerService`; apenas `JsonServerRepository`, na Infrastructure, acede ao ficheiro.

## Limites de segurança

O documento armazena exclusivamente configuração não sensível. A partir do Milestone 3 inclui o método de autenticação, caminho opcional da private key e um `CredentialReferenceId` opaco. Passwords, passphrases, conteúdo de chaves, tokens e valores de credenciais nunca fazem parte do modelo ou serialização.

Fingerprints confiadas são configuração de segurança não secreta, guardada separadamente em `%LOCALAPPDATA%\ServerMonitor\known-hosts.json`, conforme a ADR-006.

Uma gravação utiliza ficheiro temporário e substituição após serialização completa. JSON inválido é ignorado com logging para impedir falhas no arranque.
