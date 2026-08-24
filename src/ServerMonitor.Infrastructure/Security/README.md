# Segurança

`WindowsCredentialStore` implementa `IServerCredentialStore` através do Windows Credential Manager nativo. Passwords e passphrases são guardadas como credenciais genéricas locais ao computador; o target contém apenas identificadores gerados pela aplicação.

Private keys não são copiadas para o Credential Manager. A configuração persistida contém somente o caminho não sensível e a referência opaca à credencial.
