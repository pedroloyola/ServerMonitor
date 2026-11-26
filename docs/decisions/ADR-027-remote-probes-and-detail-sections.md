# ADR-027 — Sondas remotas, secções do detalhe e participantes do histórico (M16.1)

Estado: **aceite**; emendado em 2026-11-26 com o §4 (leituras registadas de um argumento). Acrescenta três pontos de extensão genéricos para módulos compostos: sondas remotas que correm na
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
- **Sondas só enquanto vistas (M18).** Uma sonda pode declarar `RunsOnlyWhileWatched` (por omissão `false`, lido uma
  vez como o resto). Essa sonda só fica devida num servidor enquanto o `RemoteProbeWatch` (um singleton da composição)
  tiver um watch dela para esse servidor: uma página aberta pega num watch e liberta-o quando sai. Fora disso nunca corre,
  nem em segundo plano. Sem `RemoteProbeWatch` passado ao agendador, nunca corre. Enquanto não é vista não é marcada como
  corrida, por isso fica devida na primeira recolha depois de o watch começar, e o seu intervalo mínimo aplica-se
  enquanto é vista. Os watches são contados por servidor e sonda, e libertar o mesmo duas vezes conta uma. O comando, os
  limites e os tetos acima não mudam; só muda quando corre.
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

### 4. Leituras registadas de um argumento (`IRemoteRead`) — emenda

Uma leitura é como uma sonda, com duas diferenças: corre uma vez, a pedido (por exemplo, quando uma página a pede), e
recebe **um** argumento escolhido de uma lista que a própria app recolheu. Não é uma sonda: não tem agendador, nem watch,
nem geração do histórico, e nunca conta no orçamento de uma recolha.

- **Registo.** Uma leitura é código compilado e registado na composição: id, sistema operativo, script constante, tipo
  de argumento (`SystemdUnit` ou `ContainerId`), limite de output e timeout. O `RemoteReadRegistry` lê tudo uma vez no
  arranque e valida-o com as mesmas regras de uma sonda (id, um sistema operativo, script só com LF e até 8 KiB, limites
  dentro de `RemoteProbeLimits`), mais estas:
  - o script usa `"$1"` pelo menos uma vez;
  - não usa outro parâmetro posicional (`$@`, `$*`, `$2` a `$9`, as formas `${…}` destes, nem `${10}` e acima);
  - não usa as palavras `shift`, `set` nem `eval`;
  - uma leitura `SystemdUnit` corre só em Linux;
  - o id não é usado por outra leitura nem por uma sonda.

  Uma leitura inválida fica de fora com um aviso no arranque ("A remote read was left out: …"), só com a posição e o
  motivo, nunca com o script. A composição de origem não regista nenhuma leitura.
- **Forma do exec.** Uma leitura corre como um único exec do mesmo comando constante `sh -s`. O stdin leva primeiro uma
  linha fixa, `set -- '<argumento>'`, e depois o script registado, byte a byte e sem edição. A shell de login e a
  lista de processos do servidor só veem `sh -s`. O argumento nunca entra no texto do comando nem no script.
- **Regras do argumento** (`RemoteReadArgument`), verificadas no início e outra vez imediatamente antes de construir o
  stdin:
  - `ContainerId`: exatamente 64 caracteres hexadecimais minúsculos;
  - `SystemdUnit`: `[A-Za-z0-9@_:]` seguido de `[A-Za-z0-9@._:-]`, até 200 caracteres, a terminar em `.service`.

  Nenhuma das regras admite aspas, espaços, `$`, `;`, barras, quebras de linha nem caracteres fora de ASCII, e um
  argumento nunca começa por `-` nem `.`. Por isso, entre as aspas simples o `sh` lê-o literalmente: `$#` é 1 e `"$1"` é
  o argumento.
- **Verificações antes de qualquer trabalho SSH**, por esta ordem; a primeira que falha recusa a leitura:
  1. o id está registado;
  2. o servidor existe, é monitorizado e tem credencial, e o seu sistema operativo é o da leitura (um servidor `Auto`
     usa o do último snapshot de serviços: systemd é Linux, launchd é macOS; sem snapshot, é recusado);
  3. o argumento cumpre a regra do seu tipo;
  4. o argumento está no último snapshot de workloads desse servidor, comparado ordinalmente: o id completo de um
     contentor, ou a unidade numa lista recolhida pelo systemd;
  5. não há outra leitura a correr nesse servidor.
- **Id completo do contentor.** O `docker ps` já usa `--no-trunc`. O parser passa a guardar o id bruto em
  `ContainerInfo.FullContainerId` quando é exatamente 64 hexadecimais minúsculos (senão fica `null`). Este id nunca é
  mostrado; `ContainerId` continua a ser o id curto de 12 caracteres.
- **Sessão.** Cada leitura abre a sua própria sessão pelo caminho de sempre: verificação da chave do host, jump host e
  credencial libertada no mesmo `finally`. Tem 15 s para ligar, mais o timeout da própria leitura. O stderr é
  lido até ao fim e descartado, seja qual for o tamanho, por isso um script que escreve muito no stderr (como o
  `docker logs`) continua a devolver o seu output. O output é limitado ao limite da leitura e devolvido só a quem a pediu.
- **Resultado** (`RemoteReadOutcome`): `Completed`, `Refused` (com o motivo), `OutputExceededLimit`, `TimedOut` ou
  `Failed`. O `ToString` do resultado, do exec e do registo nunca inclui o argumento, o script nem o output, e nada no
  caminho de uma leitura os escreve no log.

## Consequências

- Sem módulos registados: zero comandos extra, nenhuma secção nova, limpeza do histórico igual.
- Sem leituras registadas, o executor de leituras recusa todos os pedidos com `UnknownRead` e nunca abre uma sessão.
  Guardar `FullContainerId` não muda nada do que é mostrado.
- O limitador global de recolhas concorrentes do motor é libertado no fim da fase 1, não da fase 2 (ver "Sessões e
  tetos").
- QA manual: um servidor cuja shell de login é fish (e, se possível, csh/tcsh), com uma sonda multi-linha com `if` e
  `for`. Esperado: o resultado chega à sonda igual ao de um servidor com bash. Fazer o mesmo também com um servidor
  com bash, antes de ligar a primeira sonda real, porque o caminho do stdin do SSH.NET não tem teste contra um servidor
  SSH.
- A regra da composição que rejeita um módulo condicional que registe um tipo de serviço já registado continua em
  vigor; um segundo módulo condicional que registe `IRemoteProbe` ou `IHistoryParticipant` exige uma decisão própria.
