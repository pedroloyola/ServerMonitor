# UI.3 — Histórico + Serviços e containers (primeira adoção em produção)

Estado: **IMPLEMENTADO no branch `ui/ui3-history-services`** (base `main` 358fa688940ca3f873122e6193d7f3b31413cc73), PR #16 (draft). Merge **não autorizado** — aguarda GO humano. UI.4 não iniciado.
Data: 2026-10-01 → 2026-10-02. Autoridade visual: Figma `Qvk5dUFgsWf4UOYzfAkiDV` (04 Manual da marca; 05 Free secção 03 "Detalhe, histórico e serviços"; 06 Pro só extensibilidade). Precedência UI.0 §0.3: invariantes/segurança > a11y/plataforma > Figma.

## 1. Gates obrigatórios (fechados ANTES de qualquer migração de ecrã)

| Gate | Resultado | Evidência / review |
|---|---|---|
| **PERF-UI2-DICT** | **CLOSED** — ETW `Microsoft-Windows-XAML` (carga de XBF por URI) atribui à camada de dicionários UI.2 **+8,3 ms** (Styles/** 1,9 → 10,2 ms; 11 → 76 cargas XBF por merges aninhados T-2) ≈ **0,6 %** do FirstFrame. Relógio (ABAB e contrabalançado, n=25–40, Debug e `-p:Optimize=true`) dominado por ruído da máquina; contrabalançado não significativo (IC95 até +2,25 % Debug / +5,7 % otimizado). ≤ 5 % ⇒ sem otimização. Warm apenas (cold NOT_RUN: sem admin para flush da standby list). | Relay; `tools/perf/startup-perf.ps1`, `tools/perf/xaml-trace.ps1` |
| **UI-RATCHET-GEOMETRY** | **CLOSED** — `GeometryLiteralRatchetTests`: literais `CornerRadius`/`Padding` (atributo, Setter, property-element, recurso local, VisualState Setter `Target`, keyframes) fora de `Styles/Tokens/**` e `Styles/Components/**` (política G-2). Baseline versionado só desce; totais pinados no C# (subir exige editar o teste); allowlist exata para `Qa/Gallery` (Debug-only). | Beacon; Vigil R1 APPROVED_WITH_NITS |
| **TEST-REALDATA-AUDIT** | **CLOSED** — auditoria dos 9 ficheiros com composição completa (3 liam `notification-/background-settings.json` reais na construção; nenhum escrevia). `IsolatedAppComposition` + `RealDataIsolationGuard` estrutural (descobre `*Options` por reflexão, constrói o data plane e percorre o grafo; tudo sob raízes de teste; Credential Manager real nunca resolvido); `ProductionDescriptors()` só leitura; cercas para construtores com raiz real por defeito, `SpecialFolder.*` e Credential Manager; teste do Credential Manager real só com `SERVERMONITOR_RUN_REAL_CREDMAN_TESTS=1` (ligado só no job CI `debug-test`). | Cortex; Vigil R1 APPROVED |

Isolamento dos harnesses QA (achado durante 1A, corrigido no mesmo gate; Vigil R2 APPROVED): `QaStartupIsolation` re-raiz por processo + `VerifyOrThrow` fail-closed antes do host; parser estrito único de `--qa*` (qualquer forma mal formada ou modificador sem harness ⇒ exit 3); launcher obrigatório **`tools/qa/Start-QaApp.ps1`** (recusa antes de `Start-Process`: harness exato, caminho canónico final sob `<worktree>\src\ServerMonitor.App\bin\x64\Debug\…`, metadados do dll Debug + `QaStartupIsolation`).

## 2. Histórico
Título/subtítulo, seletor de servidor (`SaSelectorRichStyle`, ordem do Dashboard, subtítulo só com dados reais), intervalo 1 h/6 h/24 h/7 dias/30 dias (`SaSegmentedRect*` + `SaGroupNavigation`), cartões CPU/Memória/Disco (valor atual 38 Light + unidade, "Atual", "Pico no período" = `HistorySeries.Maximum` bruto, "—" quando desconhecido), `HistoryChart` evoluído (linha 2.4 **neutra** `SaChartLineBrush` — D `#D2D6D4` / L `#565C59` / HC WindowText, conforme Figma 05 Free; área em gradiente da cor da linha .18→.01, marcador final, grelha 3 linhas tracejadas, eixo Y 100/50/0, eixo X com marcas em limites redondos no X real; `HistoryChartGeometry.BuildSegments` intacto; sem biblioteca externa), rodapé período + "Guardado neste dispositivo durante 30 dias." (copy amarrada por teste ao default de retenção). Estados: loading (esqueleto), "O histórico começa aqui" (empty-ever), "Sem leituras neste período" (CTA 30 dias), indisponível, aviso offline. Exatamente um estado visível; troca de servidor nunca mostra valores do anterior. Cartões de gráfico focáveis só de leitura (`SaFocusableCard`, nome = resumo atual + pico + período).

## 3. Serviços e containers
Breadcrumb (pai → Dashboard até UI.5), título, contexto servidor · SO · "Atualizado há N s / N min / N h / N d" (< 1 s "agora mesmo"; sem timer, D-UI3-9), Atualizar, pesquisa global case-insensitive (nome/imagem/descrição), filtro global "Todos · N" / "Com problemas · N" (problema = severidade Negative), cartões Containers (resumo Docker por ciclo de vida, saúde: Saudável / Não saudável / Sem verificação / —) e Serviços (resumo systemd, Ativo/Falhou/Inativo, arranque Automático/Estático/Manual/Bloqueado), badge "N problemas", linhas compactas h64 com ponto de estado (`SaStatusDotOnlyStyle`), rodapé "Monitorização em modo de leitura · As ações nos serviços são feitas no servidor." **Read-only**: sem start/stop/restart. Estados: loading, nada para apresentar, indisponível, pesquisa sem resultados ("Limpar pesquisa" repõe Todos), falha só de uma secção, desatualizado, truncado. Listas: um tab stop por lista, setas percorrem as linhas, semântica UIA `List`/`ListItem` com PositionInSet/SizeOfSet; virtualização contra o scroll da página.

## 4. Componentes
Primitivas UI.2 reutilizadas. Adições (galeria `--qa-components` + manifesto `--qa-tokens` 259 chaves): tokens `SaSpace20/40`, `SaChartLineBrush` (+ `SaColorChartLineDark/Light`), `SaRadiusSelector`, `SaChartLineThickness`, `SaChartValueTextStyle` (+ unidade), `SaPageSubtitleTextStyle`, `SaChartAxisTextStyle`; variantes `SaTableHeaderRegularTextStyle` (cabeçalhos de coluna Workloads 10 Regular), `SaSelectorRichStyle`, `SaDataTableCompactRowStyle`, `SaDataTableCompactHeaderStyle`, `SaSegmentedRectFilterItemStyle`, `SaStatusDotOnlyStyle`; primitivos `SaDataTableList`, `SaFocusableCard`. Primitivos UI.2 alterados (Prism §7 aprovado): `SaStatusIndicator` (`IsLabelVisible`/`LabelStates`), `SaGroupNavigation` (entrada de fora aterra no item marcado; seleção só por seta), `SaSelectorFieldStyle` (anel de foco), `SaEmptyState` (peer nomeado pelo título, `TitleHeadingLevel`), `SaDataTableRow` (focável, `BringIntoView`). Removidos: API legada por secção do `WorkloadsViewModel`, conversores/estilos `WorkloadSeverityTo*`/`WorkloadStateText*`.

## 5. Decisões (D-UI3-1..13) e diferenças deliberadas
Ver lista aceite pelo Prism (R3). Ronda final de fidelidade (2026-10-02, decisão humana):
- **D-UI3-1 (substituída)**: as linhas dos gráficos do Histórico são **neutras conforme o Figma** (05 Free 112:2299 / 112:2476): 2.4 round, `SaChartLineBrush` D `#D2D6D4` / L `#565C59` / HC `SystemColorWindowTextColor`; área = gradiente vertical da cor da linha .18→.01; marcador final 7×7 na cor da linha; grelha inalterada. `SaCpuBrush`/`SaMemoryBrush`/`SaDiskBrush` não mudam e continuam no design system e noutros contextos. A série é identificada pelo título visível "CPU/Memória/Disco" e pelo nome acessível do cartão (nunca só cor).
- **D-UI3-9 (atualizada)**: "Atualizado há" com segundos — < 1 s "Atualizado agora mesmo"; 1–59 s "Atualizado há N s" (floor, chave `WorkloadUpdatedSecondsFormat` pt-PT/pt-BR/en-US); ≥ 60 s minutos/horas/dias como antes. **Sem timer**: o texto só é recalculado quando chega snapshot novo / Atualizar / Load (teste com `FakeTimeProvider` + guarda de fonte).

Diferenças deliberadas que ficam:
- filtro "Em execução" removido (todos os dados continuam em "Todos") — **ACEITE**;
- falhas de workloads em âmbar/atenção (não vermelho) — **ACEITE**;
- sem sidebar — **temporário até UI.6**;
- item do menu do Dashboard ainda "Workloads" — **temporário, renomear no UI.4**.

Corrigido antes do merge (decisão humana): os cabeçalhos de coluna dos Workloads ("NOME / IMAGEM", "ESTADO / SAÚDE", "NOME / DESCRIÇÃO", "ESTADO / ARRANQUE") passam a **10 Regular** secundário, maiúsculas, como Figma 112:2647/2648 (`SaTableHeaderRegularTextStyle`, BasedOn `SaTableHeaderTextStyle`, só o peso muda). A tabela de servidores/Dashboard mantém 10 Medium (112:1434). Alinhamento do cabeçalho direito e título de secção 19 ficam como estão.

Restantes notas (não alteram elementos desenhados): eixo X em limites redondos no X real; responsivo e foco DERIVED (o Figma só tem 1440 e não desenha foco).

## 6. Reviews e QA
Prism (fidelidade): R1 CHANGES_REQUIRED → R2 → **R3 APPROVED**; R4 primitivos §7 → **APPROVED** (9a00731). Cortex (arquitetura): R1 CHANGES_REQUIRED → **R2 APPROVED**, R3 APPROVED. Beacon (runtime/teclado/a11y): R1 CHANGES_REQUIRED (3 MUST WCAG 3.2.1 / 2.4.7 / 2.1.1) → **R2 APPROVED_WITH_NITS**, re-check F3 em 9a00731 PASS. Vigil: gates 1A/1B/1C (alteração fora da apresentação e risco de dados reais). Relay §15: sem regressão perceptível; 1.º frame da página +20–100 ms e 1 frame de ~60 ms no 1.º salto de scroll de lista grande (causas identificadas, não bloqueantes); 2048 serviços em Narrow realizam 43 linhas.

## 7. NOT_RUN
High Contrast real, DPI 150/200 %, Narrator real (UIA verificado), troca de tema com a página aberta, arranque a frio (perf).

## 8. Incidente
**INC-UI3-1** (2026-10-01 22:36): um script ad-hoc de medição lançou a app de produção sem `--qa` ~7 s; mudou só `history.db-wal`/`-shm` e `widget-state.json` (amostras genuínas); configuração, `~/.ssh` e Credential Manager inalterados. Decisão humana: manter os dados. Mitigação permanente: launcher obrigatório + parser estrito (secção 1).

## 9. Backlog (não bloqueante)
Home/End nas listas; Shift+Tab entra pela última linha; contadores do filtro vs pesquisa (produto); rodapé fixo tapa o fundo do cartão focado a 560×640; `HistoryChart` re-render agregado e estratégia de marca (Pro); `HistoryAxisTick` DTO no namespace ViewModels; registos por tipo de `PrivateKeyFilePicker` fora da cerca default-root; harnesses QaProxyJump/QaSshConfig/QaWindowPlacement ainda sobre raiz de produção (só construção); ratchet de geometria em C# / Margin / BorderThickness; consolidação T-2 dos merges (~5 ms).
