# M13-QA-10 — medições no board real · **FECHADO**

> **RESULTADO (decisão do humano, board real).** QA-10 foi **reclassificada e fechada**. O sintoma
> original — o painel do `Win+W` ficar por cima — **não é defeito nosso**: é comportamento de UX do host,
> reproduzido também com o **Microsoft Phone Link**. A experiência `Action.OpenUrl` foi **tecnicamente
> validada** no board: dispensa o painel e traz o ServerAlyzer à frente, sem nenhum hack. Foi mesmo assim
> **NO-GO de UX**, porque o Windows mostra a confirmação *"Queria mesmo mudar de aplicação?"* em **todas**
> as ativações — teste repetido confirmou que não é um aviso de primeira utilização. A navegação de
> produção **mantém-se `Action.Execute`**. O teste por servidor foi saltado por decisão do humano.
>
> A spike foi revertida; o pacote descartável já não se constrói a partir desta branch. O que fica é a
> ferramenta de medição (`board-test.ps1`, read-only e reutilizável) e este registo do procedimento que
> foi executado.

---

## O que se sabia antes de medir (e continua a valer)

A janela do painel (`WidgetBoard`, classe `WindowsDashboard`) é **"sempre no topo"** (`WS_EX_TOPMOST`):
desenha-se por cima mesmo quando a nossa app **já tem** o foco. Logo o sintoma não se resolve pedindo
foco com mais força — o painel tem de sair da frente por decisão dele. É por isso que a investigação
mediu o comportamento do host em vez de escalar contornos de foreground do nosso lado.

---

## Ferramenta: `board-test.ps1`

Continua válida e usa-se em qualquer medição futura do painel.

```
cd C:\Users\pfloy\OneDrive\Documentos\ServerMonitor-m13-qa10\tools\qa10
pwsh -File board-test.ps1 -Seconds 45 -Tag <nome>
```

**READ-ONLY:** observa janelas e processos; não fecha o painel, não fecha nem mata processos, não
sintetiza teclas nem rato, não mexe na ordem das janelas. Regista, ao milissegundo, quem tinha o primeiro
plano, que processos nasceram, que janelas o painel e a app têm (visível/oculta/TOPMOST), e imprime no
fim quatro linhas de resumo. Guarda tudo em `tools\qa10\logs\board-<TAG>.txt`.

**Duas regras que valeram para todas as medições e valem para as próximas:**

1. **O veredicto é o que os olhos veem.** Um PID em primeiro plano **não** prova que o painel se
   dispensou. Foi precisamente essa confusão que atrasou o diagnóstico.
2. **O clique é sempre humano.** Nada aqui automatiza `Win+W`, `Esc` ou o clique.

---

## Procedimento executado (registo)

**Parte 1 — controlo com widget first-party.** `Win+W` → clicar numa notícia / no widget do Tempo,
registando: (A) quem tinha o primeiro plano antes, (B) o clique, (C) a app/janela de destino, (D) se o
`WindowsDashboard` continuou visível/topmost, (E) que PID ficou em primeiro plano depois, (F) se o painel
se dispensou sozinho. **Resultado:** o comportamento reproduz-se fora do nosso widget (Phone Link), o que
estabeleceu que o sintoma é do host.

**Parte 2 — experiência `Action.OpenUrl`** (pacote descartável `ServerAlyzer.Dev`, ao lado do
ServerAlyzer normal, com uma única ação mudada: o corpo do cartão entregava `serveralyzer://dashboard` ao
painel para ser ele a navegar). Os 11 pontos: cartão desenha · ação clicável · host aceita · o nosso
programa de fundo é chamado · houve ativação · número de processos · PID em primeiro plano · painel
continua visível/topmost · painel dispensa-se · aparece algum seletor · consola/flash/WER/foco a saltar.
**Resultado:** funcionou tecnicamente — painel dispensado, app à frente — mas o ponto 10 apanhou a
confirmação *"Queria mesmo mudar de aplicação?"* **em todas as ativações**, e isso é NO-GO de UX para a
navegação primária.

**Parte 3 — clique por servidor:** saltada por decisão do humano.
