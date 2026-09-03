# M13 S2-T — MAPA DE CONDIÇÕES CV

**Cumprimento da CV-15.** Condição → secção de desenho → **implementação de produção** → testes →
evidência de mutação → estado.

**Regra:** nenhuma condição desaparece em silêncio. Remover redação durante condensação **não** revoga
uma condição. Uma condição só sai marcada `SUPERSEDED BY <regra>`, com justificação.

**Fontes normativas:** `.boss/tmp/m13-s2t-vigil-conditions.md` (condições) ·
`docs/m13-s2t-linearizable-state-machine.md` (desenho) · `.boss/BOSS.md` §9 e §10.

**Base de medição:** worktree `ServerMonitor-m13-s2t`, ramo `agent/m13-s2t-tray`.
Baseline dos testes filtrados por `Tray|Theme|Flyout`: **95 passam, 0 falham**.
Gates completos na árvore entregue (`eab2291`): **Debug 1817/1817**, **Release 1782/1782**. A diferença de 35 vem de um
`ItemGroup Condition="'$(Configuration)' != 'Debug'"` no projeto de testes que remove `Qa\**\*.cs` —
condição pré-existente, não introduzida por esta entrega.

---

## Estado das condições

| CV | Assunto | Implementação de produção | Testes | Mutação | Estado |
|---|---|---|---|---|---|
| **CV-1** | modelo de confiança da `WndProc`, sete pontos | `Shell/Tray/TrayCallbackContract.cs` · roteamento em `TrayHostWindow.OnMessage` | `TrayCallbackContractTests` (9) | M15–M18 mortas | **FECHADA** para a função pura; a entrega da mensagem real é QA humana |
| **CV-2 / CV-2b** | dois orçamentos independentes | `EpisodeFrequencyLimiter` · `TrayStateMachine.Transition` | `T4` + convergência adversarial | M8 morta | **FECHADA** |
| **CV-3** | comportamento sob `TerminateProcess` | n/a — não há `NIM_DELETE` de um processo morto | — | — | **`NOT_RUN`** — S6, requer interação humana |
| **CV-4** | `Unavailable` no ordinal 0 · produtor único de `Available` | `TrayLifecycleState.cs` · `HandleAddCompleted` | contrato de estados | M5 (7), M6 (6) mortas | **FECHADA** |
| **CV-5** | `szTip`/`hIcon` estáticos | `NativeTrayRegistration` — resolvidos **uma vez** no construtor | `NativeTrayRegistrationTests` (6) | M23 morta | **FECHADA** na parte decidível |
| **CV-6** | mensagem forjada ignorada | — | — | — | `SUPERSEDED BY` **CV-6b** |
| **CV-6b** | quatro casos independentes de validação | `TrayCallbackContract.TryDecode` | quatro `[Fact]` A/B/C/D, cada um variando **um** campo | M15, M16 mortas | **FECHADA** |
| **CV-7** | topologia de thread | `TrayHostWindow` (janela criada na thread de UI) | — | — | **MEDIDA · PASSA** (S-1(A), emissor sintético) |
| **CV-8** | custo nativo síncrono na thread de UI | idem | — | — | **MEDIDA · aceitável**: `NIM_ADD` mediana 3,16 ms / máx 4,36 ms, `NIM_DELETE` mediana 0,36 ms, contra 16,7 ms por frame a 60 Hz, dentro do envelope de B |
| **CV-9** | reentrância com flyout aberto | `Shell/Tray/FlyoutReentrancyGate.cs` · `OwnedTrayIconAdapter.ShowFlyout` | `FlyoutReentrancyGateTests` (6) | M26, M27 | **FECHADA** |
| **CV-10** | acoplamento limitador ↔ custo de UI | `EpisodeFrequencyLimiter.DefaultCapacity = 5 / 60 s` | `T4` | M8 morta | **FECHADA** |
| **CV-11** | residual de admissão suprimida (LOW, aceite) | ordem das guardas em `Transition` | `T4` | — | **FECHADA · residual escrito** |
| **CV-12** | evidência de mutação na entrega | — | matriz da secção 3 | 42 mutações corridas | **FECHADA com limitações declaradas** (M13, M24, M25) |
| **CV-13** | só um episódio ADMITIDO por B pode expirar | `BeginEpisode`, só depois de `TryBeginEpisode` | `CV13` | M14 morta | **FECHADA** |
| **CV-14** | B não limita tentativas dentro de um episódio | `EpisodeFrequencyLimiter` com **um** método | `CV14` ×2 (inclui teste de arquitetura por reflexão) | M8 morta | **FECHADA** |
| **CV-15** | integridade do documento normativo | — | este ficheiro | — | **ATIVA · este mapa é o cumprimento** |
| **CV-16** | `CleanupVerified` fail-closed | `HandleCleanupCompleted` · `NativeTrayRegistration.Delete` devolve o BOOL real | `T5` | M10 morta | **FECHADA** |
| **CV-17** | notificação informativa antes da saída fail-safe | `Services/FailSafeExitNotice.cs` · `WindowsAppNotificationService.ShowFailSafeExitNotice` (30 min) · gancho no CAS de `AppLifecycleController` | `FailSafeExitNoticeTests` (16) · `WindowsAppNotificationServiceTests` (3) | M36, M37, M37b, M39, M39b, M40 | **FECHADA** |
| **CV-18** | contrato fechado da ação da notificação | `NotificationActivationContract` — **uma linha**: `("FailSafeExit", "OpenDashboard")` | quatro casos independentes + tabela de 9 pares | M38, M38b | **FECHADA** |
| **CV-19** | ressalva do passo 2 para conclusões de efeito | `Transition`, passo 2 | `T11` | **M13 SOBREVIVE** | **IMPLEMENTADA, NÃO PROVADA** — ver 3.1 |
| **CV-20** | canal de efeitos fechado por construção | tipos `private` aninhados em `TrayStateMachine`; capacidade retida só por `EffectExecutor._native` | `TrayCapabilityBoundaryTests` (T14a/b) · `TrayOwnershipCompletenessTests` (T14c) | M11, M19, M20, M22, **M34** | **FECHADA · a imprecisão do T14c foi corrigida** |
| **CV-21** | exceção do sink não consome o único disparo | **NÃO IMPLEMENTADA AQUI** | — | — | **com o Cortex** (`ServerMonitor-m13-s2`) |
| **CI-1b** | grafias numéricas de enum em payloads hostis | a ação `FailSafeExit` entra no mesmo `switch` de pares literais e **herda** o fail-closed | tabela de 9 pares, inclui `("FailSafeExit", "1")` e `("2", "1")` | M38b | **HERDADA · não agravada.** A dívida da S2 continua a ser da S2 |

---

## 1. O que está implementado e provado nesta entrega

1. **O núcleo de decisão.** `TrayStateMachine` é a máquina linearizável aprovada: um `Transition(evento,
   monotonicNow)` chamado direta e sincronamente por cada fonte de evento, com o preâmbulo de três
   guardas — absorção de Release · obsolescência de geração com a ressalva CV-19 · terminalização por
   prazo antes de o evento ser examinado. Os efeitos são **dados passivos** (`record struct` privado, sem
   `Execute`) e só o `EffectExecutor` recebe e retém `INativeTrayRegistration`.
2. **Os dois orçamentos.** A (3 tentativas, 250 ms + 1000 ms) e B (`EpisodeFrequencyLimiter`, 5 episódios
   / 60 s) são independentes **por construção**: B tem exatamente um método público, portanto nada no
   programa tem forma de lhe comunicar um sucesso.
3. **O contrato da `WndProc`.** Função pura, sete pontos, lista fechada de eventos v4, `uID == 1`, e o
   `wParam` tratado como âncora não fiável e **descartado** — não fixado — quando cai fora de todos os
   monitores.
4. **A fronteira nativa.** `NativeTrayRegistration` devolve o `BOOL` real de `Shell_NotifyIcon`, que é a
   razão de existir desta slice: o WinUIEx 2.9.3 deita-o fora. `NIM_SETVERSION` v4 é exigido sem recuo
   silencioso para v3, porque sob v3 os parâmetros do callback significam outra coisa e o contrato CV-1
   estaria a validar campos que não existem.
5. **A janela hospedeira.** `TrayHostWindow` é top-level, sem dono, nunca mostrada e **não**
   `HWND_MESSAGE` — a forma que a S-1(A) mediu como recebendo `TaskbarCreated` (id `0xC073`) nos casos
   headless e em primeiro plano, empacotados.
6. **A escalada `CS8509`.** Aplicada de facto à árvore, com prova diferencial (secção 3.2).

## 2. O que está por implementar, e porquê

**Não falta código.** A ligação em DI, o flyout e a notificação de saída fail-safe estão feitos. O que
resta é verificação que exige um desktop e um humano.

| Item | Estado |
|---|---|
| **CV-3** e o caso S6 `FORCED-TERMINATION TRAY CLEANUP` | **`NOT_RUN`.** Exigem terminar o processo à força. |
| Reinício real do Explorer → recuperação | **`NOT_RUN`.** Exige reiniciar o Explorer. |
| Ícone DPI após `WM_DPICHANGED` (M24) | **`NOT_RUN`.** Só é visível a olho. |
| Entrega real de `TaskbarCreated` (M25) | **`NOT_RUN`.** Exige reinício real do shell. |
| Aparência e clique da notificação de saída fail-safe | **`NOT_RUN`.** A emissão é provada; a entrega pelo Windows e o clique tardio são observação. |

**THEME-1** (persistência da preferência entre processos) continua **fora de âmbito**, por decisão. O que
esta entrega garante é que, dentro do processo, o Dashboard e o flyout resolvem a **mesma** preferência.

### A troca é completa — a prova

Não basta dizer que removi o caminho antigo. `TrayOwnershipCompletenessTests` afirma, contra o
**contentor real** construído por `App.ConfigureApplicationServices`:

1. o assembly declara **um** `ITrayIconAdapter` — `OwnedTrayIconAdapter`;
2. os únicos `ITrayAffordanceSource` são o adaptador e a própria `TrayStateMachine` a que ele reenvia,
   nomeados por identidade — um terceiro seria uma segunda resposta a «o utilizador tem saída?»;
3. **nenhum** tipo `WinUIExTray*` ou `PendingTrayAffordance*` sobrevive no assembly;
4. o contentor resolve `ITrayIconAdapter`, `ITrayAffordanceSource` e o tipo concreto para a **mesma
   instância** — verificado por resolução, não por leitura da forma do registo;
5. existe exatamente **um** descritor para cada um dos três.

M33 confirma que isto não é decorativo: registar uma segunda instância como fonte de afordância faz
falhar (4).

## 3. Matriz de mutação — CV-12

Uma mutação de cada vez, sempre contra o **código de produção**, com restauro e reconfirmação da baseline
entre cada uma. Filtro: `FullyQualifiedName~Tray` (M1–M25) e
`FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout` (M26–M35).
e `FullyQualifiedName~FailSafe|FullyQualifiedName~WindowsAppNotification|FullyQualifiedName~Notification`
(M36–M40). Baselines **95** e **82**, ambas 0 falhas.

**43 entradas · 42 corridas (a M21 está `SUPERSEDED`) · 39 mortas · 3 sobrevivem**, e cada uma das três é
uma limitação declarada, não uma alegação.

### Reprodução

```
# Núcleo da máquina e contrato do callback (M1–M18)
cd <scratchpad>
python mutate.py M1              # ou qualquer subconjunto de M1..M18

# CV-20 e fronteira nativa (M19–M25)
python mutate_t14.py M19         # ou qualquer subconjunto de M19..M25

# Ligação em DI, flyout, CV-9 e tema (M26–M35)
python mutate_wiring.py M26      # ou qualquer subconjunto de M26..M35

# Notificação de saída fail-safe, CV-17/CV-18 (M36–M40)
python mutate_notice.py M36      # ou qualquer subconjunto de M36..M40

# Prova diferencial da escalada CS8509
python cs8509_differential.py

# Cada corrida aplica a mutação, executa
#   ~/.dotnet/dotnet.exe test tests/ServerMonitor.App.Tests/ServerMonitor.App.Tests.csproj \
#     -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Tray"
# e restaura o ficheiro.
```

### Matriz

| # | Mutação | Invariante violado | Falhas | Estado |
|---|---|---|---|---|
| M1 | `Transition` pode emitir `Add` durante `Releasing` | dominância do Release | 3 | **morta** |
| M2 | um `Add` tardio anterior ao Release publica `Available` | `Available` = provadamente disponível | 1 | **morta** |
| M3 | um `Add` tardio não recebe `Delete` compensatório | conclusão compensada | 1 | **morta** |
| M4 | revalidação na entrega das notificações removida | Release domina as continuações | 1 | **morta** |
| M5 | um `Shell_NotifyIcon` falso é tratado como sucesso | a razão de ser da slice | 7 | **morta** |
| M6 | um `Shell_NotifyIcon` verdadeiro é tratado como falha | idem, direção oposta | 6 | **morta** |
| M7 | recuperação por `TaskbarCreated` removida | recuperação após reinício do Explorer | 1 | **morta** |
| M8 | um sucesso repõe o histórico de frequência | independência de A e B | 1 | **morta** |
| M9 | `Available` mantido após um `TaskbarCreated` admitido | `Recovering` em vez de mentir | 3 | **morta** |
| M10 | uma limpeza não verificável pode continuar a viver | CV-16 fail-closed | 1 | **morta** |
| M11 | acrescenta-se um braço `default` ao switch de efeitos | exaustividade do switch | 1 | **morta** |
| M12 | o RunOnce do fail-safe marca à entrada em vez de após retorno normal | uma exceção não consome o único disparo | 1 | **morta** |
| M13 | a ressalva CV-19 é removida | reconciliação de conclusões obsoletas | **0** | **SOBREVIVE — 3.1** |
| M14 | o passo de prazo do preâmbulo é removido | terminalização pelo prazo | 1 | **morta** |
| M15 | a verificação da identidade da mensagem é removida | CV-6b caso B | 1 | **morta** |
| M16 | a verificação do `uID` é removida | CV-6b caso C | 1 | **morta** |
| M17 | a lista fechada de eventos v4 é aberta | CV-1 ponto 3 | 2 | **morta** |
| M18 | a sanitização da âncora é removida | CV-1 ponto 5 | 1 | **morta** |
| M19 | o executor deixa de ser `private`-aninhado | CV-20, fecho do canal | 1 | **morta** |
| M20 | a máquina retém a capacidade num campo próprio | CV-20, detentor único | 2 | **morta** |
| M21 | a capacidade é registada no composition root | CV-20, fora do contentor | — | `SUPERSEDED BY` **M34** (a âncora era a antiga forma em lambda) |
| M22 | um closure captura a capacidade num campo **gerado pelo compilador** | CV-20 sem exclusão por categoria | 2 | **morta** |
| M23 | o tooltip deixa de ser ajustado ao buffer `szTip` | CV-5 | 1 | **morta** |
| M24 | o `HICON` antigo é libertado **antes** de `NIM_MODIFY` | regra DPI do Prism | **0** | **SOBREVIVE — 3.3** |
| M25 | a janela hospedeira passa a `HWND_MESSAGE` | entrega de `TaskbarCreated` | **0** | **SOBREVIVE — 3.3** |
| M26 | a porta CV-9 admite todos os pedidos | um só flyout de cada vez | 4 | **morta** |
| M27 | `Close` deixa de ser idempotente e dá um slot extra | CV-9 sob dupla notificação de fecho | 1 | **morta** |
| M28 | a ordem do menu põe `Sair` em primeiro | ordem fixada pelo produto | 4 | **morta** |
| M29 | um item desaparece do menu | idem | 3 | **morta** |
| M30 | um item resolve a chave de recurso errada | item vazio = app que não se fecha | 2 | **morta** |
| M31 | anexar um root de tema **substitui** o anterior | HIGH do Prism | 4 | **morta** |
| M32 | o tema é aplicado só ao root mais recente | idem | 1 | **morta** |
| M33 | a fonte de afordância é uma **segunda instância** | um só dono do ícone | 1 | **morta** |
| M34 | a capacidade é registada no contentor | CV-20 / T14c sobre descritores reais | 2 | **morta** |
| M35 | o adaptador reporta `Available` antes de haver registo | fail-closed antes do `Start()` | 1 | **morta** |
| M36 | a notificação é emitida quando o CAS foi **PERDIDO** | condição do Prism | 2 | **morta** |
| M37 | a guarda do controlador desaparece | a falha não pode impedir o Exit | 1 | **morta** |
| M37b | a guarda da própria notificação desaparece | idem, outra camada | 1 | **morta** |
| M38 | o vocabulário aceita qualquer ação sob `FailSafeExit` | CV-18, par literal | 2 | **morta** |
| M38b | o vocabulário aceita `OpenDashboard` sob qualquer kind | CV-18 / CI-1b | 3 | **morta** |
| M39 | a expiração passa a longa (30 dias) | CV-17, curta duração | 2 | **morta** |
| M39b | a expiração é removida | idem | 1 | **morta** |
| M40 | a fronteira deixa de ser fire-and-forget (devolve `Task`) | a saída não espera pela entrega | 5 | **morta** |

### 3.1 M13 sobrevive: a ressalva CV-19 está implementada mas **não provada**

A ressalva existe no código — `&& trayEvent.Kind != TrayEventKind.AddCompleted`, no passo 2 do preâmbulo
— e é exigida pela CV-19. Removê-la não faz falhar nenhum teste.

**A ligação em DI não a torna alcançável** — verifiquei em vez de assumir. Há três incrementos de geração
em `TrayStateMachine`: dois entram em `Releasing` (terminal, cortado pelo passo 1 do preâmbulo) e o
terceiro é o `BeginEpisode`, cujos eventos já levam a geração nova. A ligação não acrescenta nenhuma
fonte de incremento.

A causa não é um teste em falta que eu possa escrever: **o estado que a ressalva protege é hoje
inalcançável**. O passo 1 do preâmbulo (terminal) faz curto-circuito antes do passo 2, e todos os
incrementos de geração exceto o de `BeginEpisode` entram também num estado terminal. Um `AddCompleted`
obsoleto, com geração diferente, num estado **não terminal**, não tem caminho de execução.

Registo o que é: a ressalva fica como defesa em profundidade contra uma futura fonte de incremento de
geração, e **não é reclamada como provada**. Não a removo, porque é uma condição normativa; e não afirmo
cobertura que não tenho.

### 3.2 Prova diferencial da escalada `CS8509`

A mesma mutação — apagar o braço `EffectKind.ScheduleDeadline` do switch exaustivo — compilada duas
vezes, mudando **apenas** a escalada no `.csproj`:

| Compilação | Escalada | `Build succeeded` | `error CS8509` | `warning CS8509` |
|---|---|---|---|---|
| **C1** | aplicada (a árvore tal como é entregue) | **não** | **2** | 0 |
| **C2** | removida | sim | 0 | 4 |
| baseline | aplicada, sem mutação | sim | 0 | 0 |

**`CS8524` não é escalado, deliberadamente.** Dispara na árvore como a análise previa (2 avisos, visíveis
em qualquer build). Escalá-lo obrigaria ao braço `default` que o desenho proíbe — que é exatamente a
mutação M11. A escolha está registada em comentário no `.csproj`, não apenas aqui.

### 3.3 M24 e M25 sobrevivem: limitações de cobertura, não invariantes por defender

Ambas são regras corretas cuja violação **só um ambiente gráfico real revela**:

- **M24** (libertar o `HICON` antes de `NIM_MODIFY`) entrega ao Explorer um handle morto. O sintoma é um
  ícone corrompido ou em falta, visível a olho.
- **M25** (`HWND_MESSAGE`) faz o `TaskbarCreated` deixar de chegar, porque o shell só transmite para
  janelas de topo. O sintoma é a tray não voltar depois de reiniciar o Explorer.

Nenhuma é observável num teste sem desktop. Ficam ligadas aos casos S6 de QA humana, e não são
apresentadas como provadas. Os passos exatos para as verificar estão no relatório, secção P, casos 6 e 4.

### 3.4 M37 e M37b: uma propriedade defendida duas vezes tem de ser provada duas vezes

Na primeira passagem **ambas sobreviveram**, e a razão é instrutiva: «a falha da notificação nunca impede
o Exit» está defendida em duas camadas — a própria notificação engole a exceção, e o `RunStep` do
controlador engole o que passar. Removendo **uma** das duas, a outra ainda apanhava, e o teste
ponta-a-ponta continuava verde.

Defesa em profundidade é o que torna a mutação de uma camada invisível. Cada camada passou a ter o seu
teste direto: `The_notice_itself_never_lets_a_platform_failure_escape` (a notificação, isolada) e
`The_exit_path_survives_a_committed_hook_that_throws` (o controlador, com um gancho cru que rebenta, para
que a guarda da notificação não possa substituir a dele). Ambas mortas com 1 falha cada.

### 3.5 Secção 1, atualizada pela ligação

Acrescenta-se ao que estava provado:

### 3.5 continuação

11. **A notificação de saída fail-safe.** Emitida do ramo que **venceu** o CAS existente para `Exiting`,
    e só para `ExitReason.TrayCleanupUnverified`. Uma linha no `switch` de pares literais, ação
    `OpenDashboard` que já estava na allowlist, zero parâmetros, 30 minutos, fire-and-forget com guarda
    em cada camada.

7. **Um dono do ícone.** `OwnedTrayIconAdapter` substitui o `WinUIExTrayIconAdapter`, que foi **apagado**
   junto com o `PendingTrayAffordanceSource` — não guardado como recurso. Provado contra o contentor real
   (secção 2).
8. **O composition root é invocável.** Era um lambda, e por isso a CV-20 só podia ser verificada por
   leitura de texto. A primeira versão do T14c falhou por causa de um **comentário de documentação** que
   nomeava a capacidade: o melhor argumento possível contra a técnica. Agora inspeciona os
   `ServiceDescriptor` que o root produz mesmo.
9. **CV-9 é um tipo, não um `bool`.** Uma flag dentro de uma chamada para o XAML só se exercita com
   desktop; `FlyoutReentrancyGate` prova-se com seis testes e morre a duas mutações.
10. **O tema chega a todos os roots.** O serviço guardava um `FrameworkElement`; anexar o flyout teria
    feito o Dashboard deixar de seguir a preferência em silêncio. `ThemeRootSet` leva a contabilidade
    sobre objetos simples, e por isso a regressão é uma asserção em vez de estado de UI não testável.
