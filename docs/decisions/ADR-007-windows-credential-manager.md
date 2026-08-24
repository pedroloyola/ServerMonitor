# ADR-007 — Segredos SSH no Windows Credential Manager

Estado: **aceite e implementado no Milestone 3**.

## Decisão

Passwords SSH e passphrases de private keys são guardadas como credenciais genéricas no Windows Credential Manager através de `CredWriteW`, `CredReadW`, `CredDeleteW` e `CredFree`.

```text
Type    = CRED_TYPE_GENERIC
Persist = CRED_PERSIST_LOCAL_MACHINE
```

Não é adicionada uma dependência externa para esta integração. `PasswordVault` foi rejeitado porque o modelo de locker/roaming é mais amplo do que o armazenamento local pretendido para esta utility unpackaged.

## Referências opacas

`servers.json` contém apenas um `CredentialReferenceId` GUID. O target nativo é construído internamente a partir de valores controlados:

```text
pedroloyola.ServerMonitor:v1:ssh:{serverId}:{password|key-passphrase}:{referenceId}
```

Host, username, nome e caminho da chave não fazem parte do target. Um ficheiro JSON adulterado não consegue indicar um target arbitrário de outra aplicação.

## Lifecycle

- Add/Replace: gravar uma referência nova, persistir configuração, apagar a referência anterior;
- falha de persistência: apagar a credencial staged;
- Keep: não ler nem regravar a credencial;
- Clear: persistir referência nula e só depois apagar a antiga;
- Remove: remover configuração e depois apagar a credencial;
- Hide/Restore: não tocar em credenciais;
- credencial removida externamente: devolver erro tipado e pedir nova introdução.

Um segredo staged fica associado ao endpoint normalizado, username, método de autenticação e caminho da chave. Alterar qualquer parte desse contexto descarta imediatamente o segredo; uma referência existente também só pode ser conservada quando todo o contexto permanece igual.

Uma referência nova por substituição reduz a janela não transacional entre JSON e Credential Manager.

## Private keys

O conteúdo da private key nunca é copiado para JSON, Credential Manager ou LocalApplicationData. Apenas o caminho absoluto escolhido pelo utilizador pode ser persistido. O ficheiro é aberto diretamente pela biblioteca SSH e permanece protegido pelas ACLs escolhidas pelo utilizador. Apenas a passphrase opcional é guardada no Credential Manager.

## Memória e logs

Segredos efémeros usam buffers próprios descartáveis, com limpeza por `CryptographicOperations.ZeroMemory`; buffers UTF-8 managed/unmanaged do interop também são zerados. `PasswordBox` é limpo após captura. Nenhum objeto sensível expõe o conteúdo através de `ToString`.

O Credential Manager protege o segredo em repouso, mas não é uma barreira contra malware ou process dumps no mesmo contexto do utilizador. APIs SSH que exigem `string` impedem limpeza determinística de todas as cópias internas; o lifetime é reduzido tanto quanto possível.

Se `CredDeleteW` falhar depois de uma configuração já ter sido atualizada/removida, pode permanecer uma credencial órfã ainda protegida no Credential Manager. Reconciliação automática por prefixo fica fora do M3 para não enumerar ou apagar credenciais sem um workflow explícito e auditável.
