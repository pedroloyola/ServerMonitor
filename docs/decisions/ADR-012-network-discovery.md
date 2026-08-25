# ADR-012 — Descoberta de servidores na rede local (mDNS/DNS-SD)

Estado: **aceite; fundação implementada no Milestone 7 (Wave 2)**.

## Contexto e âmbito

O CONTEXT.md (§55–70) exige que a aplicação descubra automaticamente servidores SSH na
rede local, sem obrigar o utilizador a conhecer o IP. O primeiro mecanismo é
**mDNS / DNS-SD** para o serviço `_ssh._tcp.local.` (§57), de forma **contínua/passiva e sem
tráfego agressivo** (§57/§70). Descoberto **nunca** significa confiado (§62): a adição passa
pelo fluxo TOFU do M3 (ADR-006). Antes do Add, a identidade mDNS é apenas uma chave
provisória de dedup/sugestão; depois do probe e trust explícitos, a identidade confiável
continua a ser o fingerprint da host key SSH. Hostname e endpoint podem melhorar a UX, mas
nunca constituem prova criptográfica de identidade.

Esta ADR cobre a descoberta passiva completa do M7: Core + Infrastructure + runtime
store/serviço no App + lifecycle, UI/ViewModels da secção "Encontrados na rede", localização,
testes e harness de QA. A descoberta de subnet e qualquer teste-de-SSH ativo (§60) ficam
**deferidos para M7.1/futuro** por decisão explícita.

## Mecanismo escolhido

**`Tmds.MDns` 0.9.1**, atrás de um adaptador próprio (`TmdsMdnsServiceBrowser`).

- **Licença:** MIT (expressão SPDX no `.nuspec`), compatível com o projeto.
- **Dependências:** **nenhuma** (grupos de dependências vazios para `net8.0` e
  `netstandard2.0`); compatível com `net10.0`. Não acrescenta superfície transitiva.
- **Manutenção:** publicado em 2026-08-17 (ativo).
- **Modelo:** `ServiceBrowser` puramente gerido, com eventos `ServiceAdded/Changed/Removed` —
  exatamente o modelo passivo-contínuo pedido. Resolve endereços por interface, é
  self-contained (não depende de Bonjour/Avahi instalados) e vive na camada `Infrastructure`
  (`net10.0` puro) sem exigir TFM Windows nem package identity — relevante porque a app é
  **unpackaged** (`WindowsPackageType=None`).

### Alternativas rejeitadas / deferidas

- **WinRT `Windows.Networking.ServiceDiscovery.DnsSd` (`DnssdServiceWatcher`)** — **rejeitado**.
  A documentação da Microsoft marca-o como *não suportado, sujeito a alteração/remoção* e
  recomenda `Windows.Devices.Enumeration`; historicamente pouco fiável a fazer browse de
  `_ssh._tcp` e com fricção de package-identity em apps unpackaged. Não serve de fundação.
- **Win32 `DnsServiceBrowse` (`dnsapi.dll`, `windns.h`)** — **fallback nativo viável**. Usa o
  responder mDNS embutido no Windows 10+ (sem Bonjour), é assíncrono com callback por
  resultado (`DnsServiceBrowseCancel` para parar) e cobre IPv4/IPv6. Preterido no M7 porque
  exigiria interop cru (marshalling de `DNS_SERVICE_BROWSE_REQUEST` + `DNS_RECORD`, lifetime do
  callback), TFM Windows na `Infrastructure` e surfacing de remoções mais grosseiro — pior
  testabilidade para ganho marginal. Continua documentado como plano B caso o Tmds falhe.
- **Makaretu.Dns.Multicast(.New)** — MIT e gerido, mas inclui também um responder que não
  precisamos e a manutenção é mais esporádica (último release 2024-11). Fallback secundário.
- **Bonjour SDK / mdnsresponder** — **rejeitado**: exigiria instalação de software Apple.
- **Scan de subnet** — **deferido** (§60): opcional, iniciado explicitamente, limitado à porta
  SSH; não é um scanner de portas genérico. Fora do M7 Wave 2.

## Arquitetura e fronteiras

Descoberta é I/O de rede multicast, ortogonal ao transporte SSH. **Nunca** toca `SSH.NET`,
credenciais, host-trust nem métricas (fronteira análoga à dos Collectors). Camadas:

```text
IMdnsServiceBrowser (Core seam: Found/Updated/Removed)
        ▲                         │ DiscoveryObservation (validada)
        │ implementa              ▼
TmdsMdnsServiceBrowser      ServerDiscoveryService (App: runtime store + IHostedService)
(Infrastructure)                  │  merge por identidade · expiry/grace · notificações
                                  ▼
                          IServerDiscoveryService (Core) ── Dashboard / Settings

IIgnoredDeviceStore (Core) ─ JsonIgnoredDeviceStore (Infrastructure) → ignored-devices.json
```

- **Core** (`Discovery/`, `Interfaces/`): `ServiceInstanceIdentity` (dedup),
  `DiscoveryObservation`, `DiscoveredService`, `DiscoveryInputPolicy` (limites de input puro),
  e os contratos `IMdnsServiceBrowser`, `IIgnoredDeviceStore`, `IServerDiscoveryService`.
- **Infrastructure**: `TmdsMdnsServiceBrowser` (adaptador + mapeamento validado),
  `MdnsServiceBrowserOptions`, `JsonIgnoredDeviceStore`, `IgnoredDeviceStorageOptions`.
- **App**: `ServerDiscoveryService` (runtime store + lifecycle), `DiscoveryOptions`, wiring DI.

### Seam determinístico e fakeável

`IMdnsServiceBrowser` expõe apenas `Found/Updated/Removed(DiscoveryObservation)` + `Start/Stop`.
Nenhum tipo da biblioteca atravessa a fronteira. Um fake de teste levanta os três eventos para
conduzir o store sem rede — o seam pedido para testes determinísticos.

## Identidade, dedup e input não confiável

- **Dedup por identidade** = `(instance name normalizado, service type, domínio)`, **nunca por
  IP**. A comparação é **case-insensitive e trailing-dot-insensitive** (dedup de variações de
  caixa/ponto final), preservando a **caixa de exibição da primeira observação**. Observações do
  mesmo instance em NICs diferentes fundem-se; nomes de instância distintos permanecem distintos.
  `StableHash` (SHA-256, hex minúsculo canónico, não sensível) é a chave persistida para
  "ignorados".
- **Hostname canónico**: a biblioteca expõe só o primeiro label em `Hostname` e o `Domain`
  separado; o mapper compõe o FQDN canónico (minúsculo, sem ponto final, sem duplicar sufixo).
- **Merge**: endereços unidos entre interfaces, deduplicados preservando **IPv4 e IPv6 (com
  scope id)**, limitados a **16** por serviço; host/porta vêm da observação mais recente.
  `FirstSeenAt`/`LastSeenAt` mantidos. O snapshot expõe `DiscoveryId` (handle estável por sessão)
  e `Source = Mdns` explícitos, sem OS nem segredos.
- **`DiscoveryInputPolicy`** (puro, testável) aceita apenas `_ssh._tcp`, rejeita
  nome/hostname vazios, caracteres de controlo ou bidi, labels DNS inválidos ou com mais de
  63 caracteres, hostnames absurdamente longos e portas inválidas; **TXT nunca é lido nem
  retido**; sem inferência de sistema operativo. Limites: 16 endereços/serviço, **512**
  records runtime com **256 lugares reservados para sugestões visíveis**, **2048** identidades
  ignoradas e **256 KiB** no ficheiro de ignorados. Hashes de identidade
  persistidos são validados como hex SHA-256 exato (64 chars minúsculos) na escrita e na leitura.

## Expiry, grace e política de flood

A biblioteca **não honra TTL** e não expõe cadência de query. Por isso o expiry vive no store,
com todo o tempo via **`TimeProvider` injetável** (seams prontos para `FakeTimeProvider`):

- **Cadência de query**: `QueryParameters.QueryInterval` é configurada a partir das opções para
  **30 s** (default da biblioteca é 10 s), validada positiva e limitada a [5 s, 5 min].
- **Expiry** por observação/interface: dimensionado em ~3× a cadência de 30 s (default 95 s).
- **Grace de remoção**: um goodbye/`ServiceRemoved` só remove após ~5 s, evitando piscar em
  flaps ou re-anúncios imediatos; o sweep finaliza.
- **Notificações apenas em mudança material**: `DiscoveredChanged` dispara só quando o conjunto
  visível muda (host/porta/endereços/visibilidade), **não** a cada pacote/anúncio. Mudanças são
  coalescidas numa janela de 100 ms e versionadas para não perder alterações reentrantes;
  bumps de last-seen não notificam.
- **Defesa contra flood**: o conjunto rastreado é limitado a 512 records e reserva pelo menos
  256 lugares para sugestões visíveis, impedindo identidades ignoradas de esgotarem a store;
  excedentes são descartados (log Debug).
- **Lifecycle**: `IHostedService` ligado ao host. `Start/Stop` idempotentes; gerações vedam
  callbacks antigos após Stop/restart e tarefas de sweep/notificação são canceladas e drenadas.
  O shutdown da janela é bounded e só dispõe o host depois de `StopAsync` realmente terminar,
  mesmo quando o provider não coopera imediatamente com cancelamento. Logging: Debug em
  lifecycle/found/updated/removed; **sem** spam Information por anúncio.

## Segurança e privacidade

- Descoberto **nunca** é confiado nem conectado automaticamente; a adição segue o TOFU do M3.
- Nenhuma credencial, chave privada, fingerprint SSH ou métrica é referenciada pela descoberta.
- `ignored-devices.json` guarda só hashes de identidade não sensíveis, separado de
  `servers.json` e de `known-hosts.json`. Input malformado/oversize degrada para conjunto vazio
  (com aviso), sem bloquear a app. `IgnoreAsync` **reporta se a persistência teve sucesso**: se o
  store recusar (hash inválido ou capacidade atingida), a sugestão **não** é escondida nem sequer
  na sessão. `ResetIgnored` **repara** um ficheiro corrupto/oversize reescrevendo-o vazio mesmo
  quando o conjunto carregado já estava vazio.

## Limitações conhecidas

- Só se descobre o que anuncia `_ssh._tcp` (macOS com Remote Login; Linux com Avahi). Ubuntu
  sem Avahi não surge — daí o scan de subnet/manual como camadas futuras.
- Descoberta é apenas na rede local elegível; VPNs (Tailscale/WireGuard) como redes confiáveis
  ficam para o futuro (§61).
- Sem TTL da biblioteca, o expiry é heurístico (95 s) e não igual ao TTL real do anúncio.

## Dependência distribuída

`Tmds.MDns` passa a ser dependência de **runtime** distribuída e está registada em
`THIRD-PARTY-NOTICES.md` com a licença MIT.
