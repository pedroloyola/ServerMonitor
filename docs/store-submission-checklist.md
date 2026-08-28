# Microsoft Store — Checklist de submissão (M12)

Prepara tudo o que **não** depende de credenciais de conta. Os itens marcados **[HUMANO]** exigem o
Partner Center e não podem ser inventados.

## Identidade de produção (Partner Center — reservada) ✅

| Campo | Valor oficial |
|---|---|
| Product / app name | **ServerAlyzer** |
| Package/Identity/Name | `PedroLoy.ServerAlyzer` |
| Package/Identity/Publisher | `CN=32C0A056-FD57-422E-A59C-A8C26434951D` |
| PublisherDisplayName | `Pedro Loy` |
| Package Family Name | `PedroLoy.ServerAlyzer_htb92ajfwgw1e` |
| Microsoft Store ID | `9N6ZBSBN1TD2` |

Aplicados em `src/ServerMonitor.App/Package.appxmanifest` (produção, por omissão). A identidade **DEV**
(`ServerAlyzer.Dev`) vive em `Package.Dev.appxmanifest` e só é usada com `-p:DevIdentity=true` —
nunca no MSIX de produção.

## Pacote

- [x] Modelo de empacotamento: **MSIX single-project** (sem `.wapproj`), framework-dependent, x64.
- [x] Build reproduzível: `dotnet build src/ServerMonitor.App/ServerMonitor.App.csproj -c Release -p:Packaged=true`.
- [x] Manifesto mínimo: capability única `runFullTrust`; **sem** `broadFileSystemAccess`.
- [x] Payload auditado: sem código-fonte, testes, harness QA, fixtures, `.boss`, `.git`, PII, cert/chave DEV.
- [x] Dependências nativas incluídas (SQLite `e_sqlite3`, SSH.NET, Tmds.MDns, WinUIEx).
- [x] Identidade de produção (`Identity/Name`, `Publisher`, `PublisherDisplayName`) reconciliada com o
      Partner Center e aplicada no manifesto de produção (ver tabela acima).

## Metadados da listagem

- [x] Display name: **ServerAlyzer**.
- [x] Descrição curta (rascunho): _Painel local para monitorizar servidores Linux e macOS por SSH —
      métricas, histórico, Docker e serviços, em modo só-leitura. Local-first, sem conta._
- [x] Notas de versão: `docs/release-notes-1.0.0-rc.md`.
- [x] Requisitos de sistema: Windows 11 x64.
- [x] Categoria sugerida: Programador / Utilitários.
- [x] Idiomas: pt-BR, pt-PT, en-US.
- [x] Privacidade: `PRIVACY.md` (local-first, sem conta, sem telemetria).
- [x] Licença: MIT (`LICENSE`), notices em `THIRD-PARTY-NOTICES.md`.
- [x] URL de suporte/fonte: repositório GitHub.
- [ ] Capturas de ecrã (checklist abaixo).
- [ ] **[HUMANO]** Website oficial da listagem (opcional; pode ficar o repositório GitHub).

## Capturas de ecrã (a produzir)

Resolução recomendada 1366×768 ou superior, tema Claro **e** Escuro:

- [ ] Dashboard com vários servidores (estados de saúde).
- [ ] Página de Histórico com gráficos.
- [ ] Página de Workloads (Docker + serviços).
- [ ] Modo compacto (widget).
- [ ] Configurações (incluindo Sobre).

## Assinatura e distribuição

- Canal principal: **Microsoft Store** → a Store trata da assinatura de produção. **Não** é necessário
  comprar certificado de code-signing para este caminho.
- Certificado **DEV** self-signed apenas para sideload/QA local; chave privada **nunca** versionada.

## Passos exclusivamente humanos (Partner Center)

- [x] Reservar a app / nome na Store (**ServerAlyzer**).
- [x] Obter Publisher ID, Package Family Name e Store Product ID reais (ver tabela de identidade).
- [ ] **[HUMANO]** Aceitar termos da conta/Store.
- [ ] **[HUMANO]** Submeter o pacote (primeira submissão PRIVATE para fechar packaged runtime QA).
- [ ] **[HUMANO]** Decidir tag/versão pública `v1.0.0`.
