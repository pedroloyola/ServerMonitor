# ADR-027 — Sondas remotas, secções do detalhe e participantes do histórico (M16.1)

Estado: **aceite**. Acrescenta três pontos de extensão genéricos para módulos compostos: sondas remotas que correm na
mesma sessão SSH de cada recolha, secções na página de detalhe de um servidor e participantes na limpeza do histórico.
Sem nenhum módulo registado, o comportamento, a interface e o número de comandos por recolha ficam exatamente como
antes. Constrói sobre o transporte SSH (ADR-006), os pipelines Linux/macOS (ADR-008/ADR-010), o motor de
monitorização (ADR-011) e o histórico local (ADR-015).

## Contexto

Os pipelines de métricas usam um catálogo fechado de comandos e não expõem nenhuma API que execute texto arbitrário. Um
módulo que precise de um dado a mais de cada servidor teria de abrir uma segunda sessão SSH por recolha, ou de alterar o
catálogo. Do mesmo modo, a página de detalhe e a limpeza do histórico só conheciam o que vem de origem.

## Decisão

### 1. Sondas remotas (`IRemoteProbe`)

- Uma sonda é código compilado e registado na composição: id, sistema operativo, um comando de uma linha, limite de
  output, timeout e intervalo mínimo. Tudo é lido e validado uma vez no arranque; uma sonda inválida fica de fora e é
  registada no log só pela posição e pelo motivo, nunca pelo comando. Nenhum texto de utilizador, configuração, host
  ou UI entra num comando.
- Limites fixos (`RemoteProbeLimits`): número máximo de sondas por recolha, orçamento total de output, limite por sonda
  e timeout máximo.
- **Duas fases na mesma sessão.** A sessão corre os comandos de origem, constrói o resultado e entrega-o uma vez a um
  callback (o que o callback lançar é ignorado). Só depois corre as sondas devidas e entrega cada resultado à sua sonda.
  Por fim desliga, como antes. A posse da sessão, o túnel por jump host, a eliminação das credenciais e o `Disconnect`
  não mudam de sítio. A Task devolvida passa a significar "fase 2 terminada".
- O motor publica frescura, saúde, histórico e UI a partir do callback, e não espera pela fase 2 para dar a recolha
  por concluída. Enquanto a fase 2 de um servidor ainda corre, a recolha seguinte corre só os comandos de origem.
  Podem sobrepor-se brevemente duas sessões ao mesmo servidor.
- Se a ligação ou um comando de origem falhar, não há callback e o resultado de falha segue o caminho de sempre. Se o
  callback já foi chamado, o resultado devolvido só fecha a fase 2 e nunca é publicado segunda vez.
- **Cancelamento.** Cada fase 2 tem o seu próprio token, ligado ao token de monitorização do servidor. Remover o
  servidor, parar a monitorização ou limpar/repor o histórico cancela-a.
- O output bruto de uma sonda nunca vai para o log e nunca sai do computador.

### 2. Secções do detalhe do servidor (`ServerDetailSection`)

Um módulo pode contribuir secções para a página de detalhe. A vista é criada para o id do servidor, de novo em cada
visita, nunca guardada em cache. Ordem, ids reservados e duplicados seguem a mesma regra das secções das Definições.
Uma fábrica que lança ou que não devolve uma vista apresentável é deixada de fora e registada só pelo id e pelo tipo da
exceção; a página é construída sem ela.

### 3. Participantes do histórico (`IHistoryParticipant`) e geração (`HistoryGeneration`)

Limpar ou repor o histórico avança primeiro a geração, depois limpa o histórico de origem e por fim chama cada
participante registado. Um participante que falha ou lança torna o resultado "indisponível", sem impedir os outros.
Resultados de sondas iniciados numa geração anterior são descartados, para que nada recolhido antes de uma limpeza
apareça depois dela.

## Consequências

- Sem módulos registados: zero comandos extra, nenhuma secção nova, limpeza do histórico igual.
- O limitador global de recolhas concorrentes do motor é libertado no fim da fase 1, não da fase 2.
- A regra da composição que rejeita um módulo condicional que registe um tipo de serviço já registado continua em
  vigor; um segundo módulo condicional que registe `IRemoteProbe` ou `IHistoryParticipant` exige uma decisão própria.
