# ADR-017 — Empacotamento Windows e Distribuição Pública (M12)

Estado: **aceite**. O M12 **não** adiciona funcionalidades de monitorização. Transforma a aplicação
unpackaged de desenvolvimento numa aplicação Windows **publicamente distribuível, instalável,
atualizável e segura** através da Microsoft Store, sem depender da máquina de desenvolvimento. Constrói
sobre toda a base M1–M11 e **não** inicia o M13 (Widget oficial) nem o suporte a macOS desktop.

## Contexto

Até ao M11 a aplicação é um executável **unpackaged**, **self-contained**, **x64**
(`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`), WinUI 3 sobre Windows App SDK 2.3.1,
com tray via WinUIEx e notificações locais via `AppNotificationManager`. Distribui-se copiando o output
de build — sem *package identity*, sem canal de atualização, sem instalação limpa, e com dois problemas
públicos herdados:

1. **Namespace de credenciais pessoal** — o target do Credential Manager usa o prefixo
   `pedroloyola.ServerMonitor:v1:ssh` (ADR-007). É um identificador pessoal impróprio como namespace
   público, mas **renomear diretamente quebraria as credenciais existentes** dos utilizadores atuais.
2. **Multi-instância** — apps Windows App SDK desktop são multi-instância por omissão. Para o Server
   Monitor isso significaria 2 trays, 2 `MonitoringEngine`, 2 escritores SQLite, notificações e polling
   SSH duplicados.

Além disso, uma distribuição pública exige *package identity* estável e neutra, manifesto mínimo,
versão coerente, documentação pública (README/PRIVACY/SECURITY/CHANGELOG), CI reproduzível e QA em
máquina limpa.

## Investigação (documentação Microsoft atual, 2025–2026)

Fontes primárias consultadas (Microsoft Learn / WindowsAppSDK):

- *Package your app using single-project MSIX* — modificar um projeto WinUI existente para MSIX **sem**
  projeto de empacotamento separado (`EnableMsixTooling=true`, `Package.appxmanifest` no projeto da app,
  `msbuild /p:GenerateAppxPackageOnBuild=true`). Limitação: um único executável por MSIX (não é blocker —
  temos um só exe); single-project não produz `.msixbundle` (não é blocker — x64 único).
- *How to create a single-instanced WinUI app with C#* — padrão oficial `DISABLE_XAML_GENERATED_MAIN` +
  `Program.Main` com `AppInstance.FindOrRegisterForKey` / `RedirectActivationToAsync` /
  `SetForegroundWindow`, decidido **antes** de criar qualquer janela.
- *Windows App SDK deployment guides* (framework-dependent packaged vs self-contained) — num MSIX de Store,
  o Windows App Runtime é instalado como *framework package* ao lado da app; self-contained aumenta
  tamanho e é desnecessário para a Store.
- *Publish your first Windows app* / *self-contained deploy* — submissão à Store exige *package identity*
  (MSIX); `Publisher`/`Identity Name`/PFN vêm do Partner Center.

Consequência relevante para o **P-009/L-014**: o workaround do
`Microsoft.WindowsAppRuntime.Insights.Resource.dll` só é necessário porque a app é
**unpackaged + self-contained**. Num MSIX **framework-dependent** esse DLL vem no *framework package* e o
workaround deixa de ser necessário — mas **mantém-se** para o build Debug/unpackaged.

## Decisão

### 1. Estratégia de distribuição — Full MSIX (single-project) + Microsoft Store como canal principal

Comparação formal:

| Critério | A. Full MSIX (Store) | B. MSIX external location | C. Unpackaged installer | D. Store EXE/MSI |
|---|---|---|---|---|
| Package identity | ✅ nativa | ✅ | ❌ | parcial |
| Atualizações | ✅ Store nativas | manual/AppInstaller | custom updater ❌ | Store |
| Assinatura | ✅ Store assina | precisa cert próprio | precisa cert próprio | precisa cert |
| Notificações (M8) packaged | ✅ (resolve P-009) | ✅ | ❌ workaround | — |
| Instalação limpa | ✅ | média | média | média |
| Uninstall limpo | ✅ | ✅ | frágil | médio |
| Compatibilidade M13 (Widget) | ✅ exige identity | ✅ | ❌ | ✅ |
| Complexidade CI/CD | média | média | baixa | alta |

**Escolha: A.** Alinha com a direção preferida, resolve identity + updates + assinatura + P-009 num só
mecanismo nativo e deixa o M13 desbloqueado (Widget Provider exige *package identity*). Sem blocker
técnico que justifique outra estratégia.

### 2. Modelo de empacotamento — single-project MSIX

Sem projeto `.wapproj` separado. O `ServerMonitor.App.csproj` ganha `Package.appxmanifest` +
`EnableMsixTooling=true` e produz o MSIX diretamente. Menor complexidade compatível com o repositório real
(§23). Um só executável (§limitação aceite).

### 3. Modelo de runtime — framework-dependent no pacote de produção

O MSIX de produção é **framework-dependent**: depende do *framework package* do Windows App Runtime, que
a Store provisiona automaticamente. Vantagens: pacote menor, arranque limpo garantido pela Store, e
**elimina o workaround P-009 no build packaged**. O build **Debug/unpackaged** mantém
`WindowsAppSDKSelfContained=true` + o workaround do resource DLL — o fluxo de desenvolvimento não muda
(§7/§26). Uma só arquitetura de aplicação; apenas o *deployment profile* difere.

### 4. Package identity — produção (Partner Center, reservada 2026-08-28)

O nome comercial público é **ServerAlyzer**. A identidade de produção foi reservada no Partner Center e
está aplicada no manifesto de produção (`Package.appxmanifest`, por omissão em `-p:Packaged=true`):

- **Display name / product name:** `ServerAlyzer`.
- **Package/Identity/Name:** `PedroLoy.ServerAlyzer`.
- **Package/Identity/Publisher:** `CN=32C0A056-FD57-422E-A59C-A8C26434951D`.
- **PublisherDisplayName:** `Pedro Loy`.
- **Package Family Name:** `PedroLoy.ServerAlyzer_htb92ajfwgw1e`.
- **Microsoft Store ID:** `9N6ZBSBN1TD2`.
- **QA local (DEV):** identidade separada `ServerAlyzer.Dev` / `CN=ServerAlyzer Dev` em
  `Package.Dev.appxmanifest`, usada só com `-p:DevIdentity=true` e assinada por cert DEV self-signed
  (nunca no repo) — **nunca** entra no MSIX de produção.

Os **namespaces internos** (`ServerMonitor.App/.Core/.Infrastructure/.Collectors`), o **executável**
(`ServerMonitor.App.exe`) e a **identidade de armazenamento** (pasta `%LOCALAPPDATA%\ServerMonitor`,
namespace de credenciais `ServerMonitor:v1:ssh`) permanecem `ServerMonitor` — implementation detail que
**não** é renomeado (branding ≠ storage identity; evita churn e uma migração de credenciais desnecessária).

### 4b. Matriz de suporte — Windows 11 x64 (confirmada)

O suporte oficial inicial é **Windows 11 x64**. O pacote de produção declara
`TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0"` (Windows 11 21H2) e
`MaxVersionTested="10.0.22000.0"` — **Windows 10 não é oferecido nem testado**. Isto impede a Store de
disponibilizar o pacote em Windows 10 sem QA/suporte.

Implementação sem tocar código nem retargetar o SDK: o `TargetFramework` de compilação permanece
`net10.0-windows10.0.19041.0` (superfície de API 19041, sem ambiguidades novas); o floor Windows 11 é
imposto **apenas no manifesto** via `AppxAddDefaultTargetDeviceFamilyItem=false`, que impede o build de
regenerar `TargetDeviceFamily` a partir de `TargetPlatformMinVersion`. `MaxVersionTested` é mantido no
mesmo baseline Windows 11 (sem reivindicar suporte não testado). O M13 (Widgets) será Windows 11 de
qualquer forma.

### 5. Migração do namespace de credenciais (backward-compatible, lossless)

Novo namespace neutro: **`ServerMonitor:v1:ssh`** (substitui `pedroloyola.ServerMonitor:v1:ssh`; mantém a
estrutura `{prefixo}:{serverId:N}:{kind}:{referenceId:N}` — só o prefixo muda). **Não** se renomeia
diretamente. Regras:

- **Write** → sempre o namespace **novo**; *best-effort* apaga o *target* legado do mesmo `reference`
  (evita órfãos; cobre update de password §13).
- **Read** → 1) tenta novo; se existir, devolve. 2) senão, tenta legado; se existir → **migra**:
  escreve novo → **relê e verifica** o novo → só então *best-effort* apaga o legado → devolve o segredo.
- **Falha de write/verify na migração** → mantém o legado, **devolve o segredo** (autenticação continua),
  não perde credencial, não volta a pedir password.
- **Delete** → apaga novo **e** legado; falha de apagar é não-destrutiva (o novo é autoritativo).

**Política de legacy (§12): opção A com segurança** — o legado é removido **apenas após** write+verify do
novo confirmados; qualquer falha de remoção é engolida (não-destrutiva). Justificação: a verificação
read-back torna a remoção segura e evita credenciais órfãs. Revisto por security-review (Vigil).

### 6. Single-instance (produção)

`DISABLE_XAML_GENERATED_MAIN` + `Program.Main` próprio. `AppInstance.FindOrRegisterForKey` corre **antes**
de qualquer inicialização pesada (DI, tray, escritor SQLite, `MonitoringEngine`) — §89. Segundo launch:
localiza a instância existente, `RedirectActivationToAsync`, traz a janela para foreground **no modo atual**
(Standard/Compact/tray-restore), e **termina**. Click em notificação (M8) → activation para a instância
existente. O single-instance é também a proteção do escritor SQLite (§88) — não depende do lock do
`history.db`. **Debug QA harnesses** (`--qa-health/-discovery/-compact/-history/-workloads`) usam uma
chave de instância única/bypass; o código QA nunca entra no Release (§20).

### 7. Versionamento

- **Product SemVer** (Git tag): rumo a `1.0.0` (primeira stable). **Não** se cria `v1.0.0` sem aprovação
  humana; o M12 prepara o *release candidate*.
- **Package version** (4-part): `1.0.0.0` (`Major.Minor.Build.0`, revisão 0 reservada à Store).
- **Assembly/File version:** derivadas do SemVer.
- O ecrã **About** lê a versão **real**: `Package.Current.Id.Version` quando packaged; fallback seguro
  para a versão do assembly quando unpackaged/dev.

### 8. Persistência de dados — preservar caminhos atuais

Os dados vivem hoje em `%LOCALAPPDATA%\ServerMonitor\` (`servers.json`, `history.db`, definições de
notificações, placement de janela, discovery ignorado, known-hosts) + segredos no Credential Manager.
**Não** se migra para `LocalState` do pacote só porque passa a existir identity. Uma app packaged com
identity continua a aceder ao `%LOCALAPPDATA%\ServerMonitor\` real (é uma app desktop full-trust, não UWP
sandboxed) → **upgrade preserva todos os dados** sem re-onboarding (§33/§34). Uma só *source of truth*
por tipo de dado (§35). Segredos continuam no Credential Manager (§84).

### 9. Manifesto mínimo e capabilities

`Package.appxmanifest` mínimo: `runFullTrust` (app desktop) e o necessário para tray/notificações. **Não**
se declara `broadFileSystemAccess` — o *file picker* (chave privada em `%USERPROFILE%\.ssh`, §32) e as APIs
full-trust já resolvem. Nenhuma capability "por garantia". Auditado por security-review.

### 10. Assinatura

- **Store:** assinatura pela Store (canal principal).
- **Distribuição direta externa:** exigiria estratégia de *trusted signing* — dependência humana externa.
- O M12 **não** compra certificado nem cria certificado de produção falso. Certificado **DEV** self-signed
  apenas para QA local, claramente rotulado. **Nenhuma** chave privada de assinatura no repositório
  (gate de pesquisa §61).

### 11. Pipeline de release

CI GitHub Actions determinístico, *least-privilege*, `x64`, build fresco, test, *vulnerability check*,
build de pacote. Workflow de release separado: tag → checkout do SHA exato → restore → build → test →
pacote → checksums SHA-256 → artefactos. **Upload automático para a Store não é implementado** sem
segredos/estratégia de conta aprovados (§66). Actions confiáveis/*pinned*, `permissions` mínimas (§65).

### 12. Política de clean-machine e de uninstall

QA em Windows 11 x64 limpo (Windows Sandbox ou VM descartável) é gate **bloqueante** (§47). Como os dados
ficam **fora** do sandbox do pacote (`%LOCALAPPDATA%`), o **uninstall não os remove** silenciosamente
(§45): documenta-se o que permanece, porquê e como limpar. Sem *cleanup* destrutivo automático.

### 13. Atualização

Sem *custom updater* (§63). A atualização usa o mecanismo nativo da Store/MSIX (atómico). A app tolera
`old data schema + new binary` (migrações M10 continuam; adições de settings backward-compatible; §85).

## Dependência do M13

O M13 (Widget Provider oficial do Windows) **exige** *package identity* — que este ADR estabelece. O M12
deixa a identity pronta mas **não** implementa qualquer widget.

## Consequências

- **Positivas:** identity estável e neutra; updates/assinatura nativos da Store; P-009 resolvido no
  packaged; single-instance protege engine/SQLite/tray; upgrade preserva dados; M13 desbloqueado; fluxo de
  dev inalterado.
- **Custos/limitações:** submissão à Store depende de passos humanos no Partner Center (Publisher ID,
  aceitação de termos, screenshots) — o M12 pode terminar "code/package-ready; Store submission pending
  human action". QA em VM limpa e round-trips reais de notificação/tray podem ficar NOT_RUN conforme
  ambiente.

## Checkpoints humanos (não decididos pelo Boss)

Reserva/criação da app na Store · Partner Center · **Publisher ID / Package Family Name / Store Product ID**
reais · certificado/assinatura de produção · segredos de CI · pagamento · aceitação de termos · publicação
pública · mudança de nome comercial. Valores de Store **não** são inventados; entram por checkpoint.
