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

- Uma sonda é código compilado e registado na composição: id, sistema operativo, um script constante, linhas separadas
  por LF, um exec; limite de output, timeout e intervalo mínimo. Tudo é lido e validado uma vez no arranque. O script
  aceita só LF como quebra de linha: CR, NUL, TAB, ESC, qualquer outro carácter de controlo e os separadores Unicode
  U+2028/U+2029 são recusados. Uma sonda inválida fica de fora e o arranque regista um aviso por cada uma ("A remote
  probe was left out: …"), só com a posição e o motivo, nunca com o comando. Nenhum texto de utilizador, configuração,
  host ou UI entra num comando.
- **Dialeto fixo.** Cada sonda corre como um único exec do comando constante `sh -s`, com o script enviado pelo stdin
  (stream de entrada do `SshCommand` do SSH.NET). A shell de login do utilizador (bash, zsh, fish, csh…) só vê
  `sh -s`, por isso o script é sempre lido por um `sh` POSIX, e nenhum carácter do script (aspas, `$`) chega à linha de
  comando. Não foi preciso o recurso `sh -c '<script>'`, e por isso `'` não é recusado.
- **Stdin dentro do script.** Como o próprio script é o stdin do `sh`, um comando do script que leia o stdin (`cat`
  sem ficheiro, `read`, `ssh`, `docker exec -i`, `docker run -i`…) consome o resto do script, e a sonda termina mais
  cedo sem erro visível. Esses comandos têm de redirecionar com `</dev/null`. A regra está no `IRemoteProbe.Command`.
- **Escrita no stdin que falha.** Se a escrita do script falhar (o canal fechou porque o `sh` já saiu, ou
  cancelamento), a sessão cancela o comando e espera pelas suas tarefas antes de deixar a falha subir. A sonda fica
  `Failed`, e a falha não leva o script nem o output.
- Limites fixos (`RemoteProbeLimits`): até 4 sondas por recolha, orçamento total de output de 1 MiB, até 256 KiB por
  sonda, timeout máximo de 30 s e script com até 8 KiB (8192 caracteres).
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
- **Gravar depois de uma limpeza.** Os resultados de uma geração anterior são descartados antes de chegarem à sonda,
  mas uma limpeza pode cair entre essa verificação e a escrita da própria sonda. Uma sonda que guarde dados tem de
  voltar a comparar `RemoteProbeOutcome.HistoryGeneration` com `HistoryGeneration.Current` no momento em que escreve,
  e descartar o resultado se já não for igual.
- **Sessões e tetos.** A fase 2 fica fora do limitador global de recolhas: este é libertado no fim da fase 1.
  - Por servidor há no máximo uma fase 2 (uma reserva por servidor). Por isso há no máximo 2 sessões ao mesmo
    servidor: a fase 2 em curso e a fase 1 da recolha seguinte.
  - Entre servidores, a fase 2 não é limitada. Com N servidores, o pior caso é 2×N sessões abertas ao mesmo tempo.
  - Uma fase 2 dura no máximo 4 × 30 s = 120 s. Uma sonda que ignore o token é abandonada quando o seu timeout passa.
  - A credencial da sessão (chave ou palavra-passe em memória) vive até ao fim da fase 2, ou seja até 120 s a mais do
    que antes, e é libertada no mesmo `finally` de sempre.
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
- O limitador global de recolhas concorrentes do motor é libertado no fim da fase 1, não da fase 2 (ver "Sessões e
  tetos").
- QA manual: um servidor cuja shell de login é fish (e, se possível, csh/tcsh), com uma sonda multi-linha com `if` e
  `for`. Esperado: o resultado chega à sonda igual ao de um servidor com bash. Fazer o mesmo também com um servidor
  com bash, antes de ligar a primeira sonda real, porque o caminho do stdin do SSH.NET não tem teste contra um servidor
  SSH.
- A regra da composição que rejeita um módulo condicional que registe um tipo de serviço já registado continua em
  vigor; um segundo módulo condicional que registe `IRemoteProbe` ou `IHistoryParticipant` exige uma decisão própria.
