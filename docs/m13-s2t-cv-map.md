# M13 S2-T — MAPA DE CONDIÇÕES CV

**Cumprimento da CV-15.** Condição → secção de desenho → **implementação de produção** → testes →
evidência de mutação → estado.

**Regra:** nenhuma condição desaparece em silêncio. Remover redação durante condensação **não** revoga
uma condição. Uma condição só sai marcada `SUPERSEDED BY <regra>`, com justificação.

**Fontes normativas:** `.boss/tmp/m13-s2t-vigil-conditions.md` (condições) ·
`docs/m13-s2t-linearizable-state-machine.md` (desenho) · `.boss/BOSS.md` §9 e §10.

**Base de medição:** worktree `ServerMonitor-m13-s2t`, ramo `agent/m13-s2t-tray`.
Baseline dos testes filtrados por `Tray`: **68 passam, 0 falham**.
Gates completos: **Debug 1763/1763**, **Release 1728/1728**. A diferença de 35 vem de um
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
| **CV-9** | reentrância com flyout aberto | **NÃO IMPLEMENTADA** — depende do flyout | — | — | **ABERTA** — ver secção 2 |
| **CV-10** | acoplamento limitador ↔ custo de UI | `EpisodeFrequencyLimiter.DefaultCapacity = 5 / 60 s` | `T4` | M8 morta | **FECHADA** |
| **CV-11** | residual de admissão suprimida (LOW, aceite) | ordem das guardas em `Transition` | `T4` | — | **FECHADA · residual escrito** |
| **CV-12** | evidência de mutação na entrega | — | matriz da secção 3 | 25 mutações corridas | **FECHADA com limitações declaradas** (M13, M24, M25) |
| **CV-13** | só um episódio ADMITIDO por B pode expirar | `BeginEpisode`, só depois de `TryBeginEpisode` | `CV13` | M14 morta | **FECHADA** |
| **CV-14** | B não limita tentativas dentro de um episódio | `EpisodeFrequencyLimiter` com **um** método | `CV14` ×2 (inclui teste de arquitetura por reflexão) | M8 morta | **FECHADA** |
| **CV-15** | integridade do documento normativo | — | este ficheiro | — | **ATIVA · este mapa é o cumprimento** |
| **CV-16** | `CleanupVerified` fail-closed | `HandleCleanupCompleted` · `NativeTrayRegistration.Delete` devolve o BOOL real | `T5` | M10 morta | **FECHADA** |
| **CV-17** | notificação informativa antes da saída fail-safe | **NÃO IMPLEMENTADA** | — | — | **ABERTA** — ver secção 2 |
| **CV-18** | contrato fechado da ação da notificação | **NÃO IMPLEMENTADA** | — | — | **ABERTA** — ver secção 2 |
| **CV-19** | ressalva do passo 2 para conclusões de efeito | `Transition`, passo 2 | `T11` | **M13 SOBREVIVE** | **IMPLEMENTADA, NÃO PROVADA** — ver 3.1 |
| **CV-20** | canal de efeitos fechado por construção | tipos `private` aninhados em `TrayStateMachine`; capacidade retida só por `EffectExecutor._native` | `TrayCapabilityBoundaryTests` (T14a/b/c) | M11, M19–M22 mortas | **FECHADA com uma imprecisão declarada em T14c** |
| **CV-21** | exceção do sink não consome o único disparo | **NÃO IMPLEMENTADA AQUI** | — | — | **com o Cortex** (`ServerMonitor-m13-s2`) |
| **CI-1b** | grafias numéricas de enum em payloads hostis | herdada da S2 | — | — | **REFERENCIADA**, dívida da S2 |

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

Estes pontos **não** estão entregues. Nenhum é uma dificuldade técnica por resolver; todos partilham a
mesma razão: só são verificáveis por observação humana num ambiente que esta sessão não pode usar.

| Item | Porque não está feito |
|---|---|
| Ligação em DI (`ITrayAffordanceSource` → `TrayStateMachine`), substituindo `PendingTrayAffordanceSource` e `WinUIExTrayIconAdapter` | Trocar a fonte de afordância remove o único ícone de tray da aplicação. O ícone novo não pode ser observado nesta sessão. A troca deixaria uma tray por verificar no caminho para `main`, e a que existe hoje funciona. |
| Janela XAML do flyout (ordem: Abrir o ServerAlyzer · Modo compacto · Atualizar todos · Definições · Sair do ServerAlyzer) | Acoplada à troca acima: quem for dono do ícone tem de ser dono do menu, sob pena de haver dois ícones. Requer também a resolução multi-root de tema (HIGH do Prism) e QA visual. |
| **CV-9** — reentrância com o flyout aberto | Sem flyout não há o que reentrar. |
| **CV-17 / CV-18** — notificação informativa antes da saída fail-safe | Depende das strings RESW finais do Prism e da entrega real da notificação, que é QA humana. O sink `requestAuthoritativeExit` já existe e já é invocado; falta a notificação que o precede. |
| **CV-3** e o caso S6 `FORCED-TERMINATION TRAY CLEANUP` | Exigem reiniciar o Explorer ou terminar o processo à força — fora do que esta sessão pode fazer. |

### Imprecisão declarada em T14c

O Atlas exige que T14c inspecione a `IServiceCollection` **realmente produzida** pelo composition root. O
composition root é hoje um lambda dentro de `App.xaml.cs`, sem costura que permita invocá-lo a partir de
um teste. T14c afirma por isso sobre o **texto** da composição — a mesma técnica dos
`WatchdogOwnershipBoundaryTests` já aceites. Mata a mutação de registo (M21), mas é texto e não a coleção
real. Fica registado como imprecisão em aberto, não como condição satisfeita.

Nota atenuante, medida e não inferida: M21 é morta **duas vezes** — por T14c e também por T14b, porque um
registo por fábrica produz um método gerado cujo tipo de retorno é a capacidade. A forma
`AddSingleton<INativeTrayRegistration, Concreto>()` seria apanhada só por T14c.

## 3. Matriz de mutação — CV-12

Uma mutação de cada vez, sempre contra o **código de produção**, com restauro e reconfirmação da baseline
entre cada uma. Filtro em todas: `FullyQualifiedName~Tray`. Baseline **68 passam / 0 falham**.

### Reprodução

```
# Núcleo da máquina e contrato do callback (M1–M18)
cd <scratchpad>
python mutate.py M1              # ou qualquer subconjunto de M1..M18

# CV-20 e fronteira nativa (M19–M25)
python mutate_t14.py M19         # ou qualquer subconjunto de M19..M25

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
| M21 | a capacidade é registada no composition root | CV-20, fora do contentor | 2 | **morta** |
| M22 | um closure captura a capacidade num campo **gerado pelo compilador** | CV-20 sem exclusão por categoria | 2 | **morta** |
| M23 | o tooltip deixa de ser ajustado ao buffer `szTip` | CV-5 | 1 | **morta** |
| M24 | o `HICON` antigo é libertado **antes** de `NIM_MODIFY` | regra DPI do Prism | **0** | **SOBREVIVE — 3.3** |
| M25 | a janela hospedeira passa a `HWND_MESSAGE` | entrega de `TaskbarCreated` | **0** | **SOBREVIVE — 3.3** |

### 3.1 M13 sobrevive: a ressalva CV-19 está implementada mas **não provada**

A ressalva existe no código — `&& trayEvent.Kind != TrayEventKind.AddCompleted`, no passo 2 do preâmbulo
— e é exigida pela CV-19. Removê-la não faz falhar nenhum teste.

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
apresentadas como provadas.
