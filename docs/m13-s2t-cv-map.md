# M13 S2-T — MAPA DE CONDIÇÕES CV

**Cumprimento da CV-15.** Condição → secção de desenho → **implementação de produção** (ficheiro:linha)
→ testes → evidência de mutação → estado.

**Regra:** nenhuma condição desaparece em silêncio. Remover redação durante condensação **não** revoga
uma condição. Uma condição só sai marcada `SUPERSEDED BY <regra>`, com justificação.

**Fontes normativas:** `.boss/tmp/m13-s2t-vigil-conditions.md` (condições) ·
`docs/m13-s2t-linearizable-state-machine.md` (desenho) · `.boss/BOSS.md` §9 e §10.

---

## Estado das condições

| CV | Assunto | Desenho | Implementação de produção | Testes | Mutação | Estado |
|---|---|---|---|---|---|---|
| **CV-1** | modelo de confiança da `WndProc`, sete pontos | §9 | `Shell/Tray/TrayCallbackContract.cs` | `TrayCallbackContractTests` | M15–M18 | ver secção 2 |
| **CV-2 / CV-2b** | dois orçamentos independentes | §7 | `Shell/Tray/EpisodeFrequencyLimiter.cs` · `TrayStateMachine.Transition` | `T4`, adversarial | **M8** | **FECHADA** |
| **CV-3** | comportamento sob `TerminateProcess` | §10 | n/a — sem `NIM_DELETE` do processo morto | — | — | `NOT_RUN` humano (item L) |
| **CV-4** | `Unavailable` no ordinal 0 · produtor único de `Available` | §4 | `TrayLifecycleState.cs:14` · `TrayStateMachine.HandleAddCompleted` | contrato de estados | **M5**, **M6** | **FECHADA** |
| **CV-5** | `szTip`/`hIcon` estáticos | §7 | `Shell/Tray/NativeTrayRegistration.cs` | boundary test | — | ver secção 2 |
| **CV-6** | mensagem forjada ignorada | §9 | — | — | — | `SUPERSEDED BY` **CV-6b** |
| **CV-6b** | quatro casos independentes de validação | §9 | `TrayCallbackContract.TryDecode` | quatro `[Fact]` separados | M15–M18 | ver secção 2 |
| **CV-7** | topologia de thread | §11 | `Shell/Tray/TrayHostWindow.cs` | — | — | **MEDIDA · PASSA** (emissor sintético) |
| **CV-8** | custo nativo síncrono na thread de UI | §11 | idem | — | — | **MEDIDA · aceitável** sob o envelope de B |
| **CV-9** | reentrância com flyout aberto | §9 | flyout host | — | — | ver secção 2 |
| **CV-10** | acoplamento limitador ↔ custo de UI | §11 | `EpisodeFrequencyLimiter.DefaultCapacity = 5 / 60 s` | `T4` | **M8** | **FECHADA** |
| **CV-11** | residual de admissão suprimida (LOW, aceite) | §11 | por construção da ordem das guardas | `T4` | — | **FECHADA · residual escrito** |
| **CV-12** | evidência de mutação na entrega | §12 | — | matriz abaixo | **esta secção** | ver secção 3 |
| **CV-13** | só um episódio ADMITIDO por B pode expirar | §3 | `TrayStateMachine.BeginEpisode` (só após `TryBeginEpisode`) | `CV13` | **M14** | **FECHADA** |
| **CV-14** | B não limita tentativas dentro de um episódio | §7 | `EpisodeFrequencyLimiter` com **um** método | `CV14` + arquitetura | **M8** | **FECHADA** |
| **CV-15** | integridade do documento normativo | §8 | — | este ficheiro | — | **ATIVA · este mapa é o cumprimento** |
| **CV-16** | `CleanupVerified` fail-closed | §6.4 | `TrayStateMachine.HandleCleanupCompleted` | `T5` | **M10** | **FECHADA** |
| **CV-17** | notificação informativa antes da saída fail-safe | §6.5 | slot de recursos | — | — | ver secção 2 |
| **CV-18** | contrato fechado da ação da notificação | §6.5 | idem | — | — | ver secção 2 |
| **CV-19** | ressalva do passo 2 para conclusões de efeito | §1 | `TrayStateMachine.Transition`, passo 2 | `T11` | **M13** | **FECHADA** |
| **CV-20** | canal de efeitos fechado por construção | §5.1 | `TrayStateMachine` — tipos `private` aninhados | `T14`, `T15`, `T16`, `T17`, `T18` | **M11** + arquitetura | ver secção 2 |
| **CV-21** | exceção do sink não consome o único disparo | §6.4.1 | **NÃO IMPLEMENTADA AQUI** | — | — | **com o Cortex** (worktree `ServerMonitor-m13-s2`) |
| **CI-1b** | grafias numéricas de enum em payloads hostis | §8 | herdada da S2; a ação `FailSafeExit` entra no mesmo contrato | — | — | **REFERENCIADA**, dívida da S2 |

---

## 1. O que está implementado e provado nesta entrega

*(preenchido abaixo, na secção 3, com a matriz de mutação real)*

## 2. O que está por implementar, e porquê

*(preenchido no relatório final)*

## 3. Matriz de mutação — CV-12

*(preenchido a partir da corrida real)*
