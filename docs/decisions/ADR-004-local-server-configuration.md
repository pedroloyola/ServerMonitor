# ADR-004 — Configuração local não sensível

Estado: **aceite e implementado no Milestone 2**.

## Decisão

A lista manual de servidores é persistida como JSON em:

```text
%LOCALAPPDATA%\ServerMonitor\servers.json
```

A UI utiliza `IServerService`; apenas `JsonServerRepository`, na Infrastructure, acede ao ficheiro.

## Limites de segurança

O documento armazena exclusivamente configuração não sensível. Passwords, chaves privadas, tokens, referências de credenciais e fingerprints não fazem parte do modelo nem da serialização.

Uma gravação utiliza ficheiro temporário e substituição após serialização completa. JSON inválido é ignorado com logging para impedir falhas no arranque.
