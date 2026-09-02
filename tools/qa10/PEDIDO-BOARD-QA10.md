# M13-QA-10 — medições no board real

**O que estamos a investigar:** clicar no widget do ServerAlyzer abre/restaura a app, mas o painel do
`Win+W` continua visualmente por cima e a app fica atrás.

Já se mediu o que se consegue medir fora do board. Sabe-se agora que a janela do painel
(`WindowsDashboard`) é **"sempre no topo"** (`WS_EX_TOPMOST`): isso significa que ela se desenha por cima
mesmo quando a app **já recebeu** o foco. Logo o problema não se resolve pedindo foco com mais força —
o painel tem de sair da frente por decisão dele. Estas medições servem para saber **se e quando** ele o faz.

**Não é preciso saber mais nada para executar este procedimento.**

---

## Duas regras que valem para tudo o que se segue

1. **O veredicto é o que os teus olhos veem.** O script mede janelas e PIDs, mas quem decide se "o painel
   saiu da frente" e se "a app ficou visível à frente" és tu, a olhar para o ecrã. Um PID em primeiro
   plano **não** prova que o painel se dispensou — foi exatamente essa confusão que atrasou o diagnóstico.
2. **O clique é teu, sempre.** Nada aqui automatiza `Win+W`, `Esc` ou o clique. O script é **read-only**:
   observa janelas e processos, não fecha o painel, não fecha nem mata processos, não sintetiza teclas
   nem rato, não mexe na ordem das janelas.

Cada execução guarda um ficheiro em `tools\qa10\logs\board-<TAG>.txt`. **Envia-me esses ficheiros** e,
por cada caso, uma linha a dizer o que viste.

---

# PARTE 1 — TESTE DE CONTROLO (widget que não é nosso). Faz esta parte PRIMEIRO.

Esta parte não usa nada nosso. Serve para responder a uma pergunta anterior a toda a investigação:
**quando um widget da Microsoft navega para fora do painel, o painel sai da frente?** Se nem os widgets
first-party o fizerem nesta máquina, a direção da correção muda por completo e a Parte 2 não interessa.

**Preparação:** nada. O ServerAlyzer pode estar aberto, fechado, tanto faz.

```
cd C:\Users\pfloy\OneDrive\Documentos\ServerMonitor-m13-qa10\tools\qa10
pwsh -File board-test.ps1 -Seconds 45 -Tag CONTROLO
```

**A tua ação:** `Win+W` → clicar num widget **da Microsoft** que abra uma app ou um site — uma **notícia**
do widget de Notícias, ou o widget do **Tempo**.

**Regista estes seis pontos** (o script apanha os que estão marcados com «script»; o resto é o que vês):

| | O que registar | Como |
|---|---|---|
| **A** | quem tinha o primeiro plano **antes** do clique | script (primeira linha `FG`) |
| **B** | o clique — em que widget e onde exatamente | teu, escreve-o |
| **C** | que app/janela **abriu** como destino | teu (+ `PROC` no script) |
| **D** | se a janela do painel (`WindowsDashboard`) **continua visível/topmost** depois | **teu, a olho** (+ `BOARD` no script) |
| **E** | que PID ficou em primeiro plano **depois** | script (`RESUMO 4`) |
| **F** | se o painel **se dispensou sozinho** (fechou-se sem tu carregares em `Esc` nem clicares fora) | **teu, a olho** |

**Diz-me também qual widget usaste.**

### O que cada resultado significa

- **O painel fecha-se sozinho e a app de destino fica visível à frente** → existe um comportamento
  suportado em que o painel sai da frente quando o widget navega para fora. A Parte 2 vai medir se o
  nosso widget consegue usar esse mesmo comportamento.
- **O painel também fica por cima e a app de destino também fica atrás** → o comportamento é igual para
  toda a gente nesta máquina; não é um defeito do nosso widget. **Diz-me já, e pára aqui** — a Parte 2
  não acrescenta nada e a correção terá de ser desenhada de outra maneira.

---

# PARTE 2 — EXPERIÊNCIA `Action.OpenUrl` (pacote descartável da spike)

Só depois da Parte 1. Aqui instala-se um pacote **descartável**, ao lado do ServerAlyzer normal, em que
**uma única ação** do widget foi mudada: clicar no **corpo** do cartão passa a entregar ao painel um
endereço `serveralyzer://dashboard` para ele próprio navegar, em vez de avisar o nosso programa de fundo
para ser ele a abrir a app. **Clicar numa linha de servidor continua a usar o caminho atual**, de
propósito: assim comparas os dois comportamentos no mesmo widget, na mesma sessão.

O pacote tem identidade própria (`ServerAlyzer.Dev`) e **não substitui** o ServerAlyzer instalado.

## 2.1 Instalar

**Fecha primeiro o ServerAlyzer normal** (ícone na bandeja → Sair). Os dois partilham a mesma pasta de
dados; com os dois a correr ao mesmo tempo a medição fica suja.

```
Add-AppxPackage -Register "C:\Users\pfloy\OneDrive\Documentos\ServerMonitor-m13-qa10\artifacts\qa10-spike\layout\AppxManifest.xml"
```

Não precisa de certificado nem de administrador: o Modo de Programador já está ligado nesta máquina, e é
assim que se instala um pacote em ficheiros soltos. Se der erro, manda-me a mensagem tal e qual.

Depois:

1. Abre o **ServerAlyzer (Dev)** pelo menu Iniciar uma vez, para ele escrever dados e ficar a correr.
2. `Win+W` → **Adicionar widgets** → afixa o widget chamado **"ServerAlyzer QA10 SPIKE"**.
   (O nome é diferente de propósito: o widget normal chama-se só "ServerAlyzer". Se afixares o errado
   estás a medir o comportamento antigo.)

## 2.2 Medir

```
cd C:\Users\pfloy\OneDrive\Documentos\ServerMonitor-m13-qa10\tools\qa10
pwsh -File board-test.ps1 -Seconds 45 -Tag SPIKE-dashboard
```

**A tua ação:** `Win+W` → clicar no **corpo** do widget SPIKE (não numa linha de servidor).

**Os 11 pontos.** O script apanha sozinho os marcados «script»; os outros são o que vês:

| | Ponto | Como |
|---|---|---|
| 1 | o cartão **desenha-se** normalmente no painel? | teu |
| 2 | a ação é **clicável** (o corpo reage ao rato)? | teu |
| 3 | o painel **aceitou** a ação, ou mostrou erro/nada aconteceu? | teu |
| 4 | o nosso programa de fundo foi chamado? | script (`RESUMO 5`) |
| 5 | houve mesmo uma ativação (a app foi acordada)? | script (`PROC`) |
| 6 | quantos processos ServerAlyzer existiram | script (`RESUMO 3`) |
| 7 | que PID ficou em primeiro plano | script (`RESUMO 4`) |
| 8 | o painel continuou **visível/topmost**? | **teu, a olho** (+ `BOARD`) |
| 9 | o painel **dispensou-se sozinho**? | **teu, a olho** |
| 10 | apareceu algum **seletor** ("Como quer abrir isto?", escolher aplicação)? | teu |
| 11 | alguma **consola preta**, flash de janela, erro do Windows, ou o foco a saltar? | teu |

**Repete o mesmo caso 2 ou 3 vezes** — quero saber se é consistente.

## 2.3 Só se o 2.2 funcionar: clique por servidor

Se — e só se — clicar no corpo abriu a app **e** o painel saiu da frente:

```
pwsh -File board-test.ps1 -Seconds 45 -Tag SPIKE-servidor
```

**A tua ação:** `Win+W` → clicar **numa linha de servidor** do widget SPIKE.

**Atenção:** esta ação é **de propósito** a do caminho antigo. Aqui interessa-me a comparação lado a lado:
a app veio à frente? o painel saiu? e o servidor certo ficou destacado? Diz-me **qual servidor clicaste**.

## 2.4 Desinstalar quando acabares

```
Get-AppxPackage ServerAlyzer.Dev | Remove-AppxPackage
```

Isto remove só o pacote da spike; o ServerAlyzer normal (`PedroLoy.ServerAlyzer`) fica intacto. Podes
voltar a abri-lo normalmente a seguir. Se quiseres, remove também o widget SPIKE do painel antes.

---

## O que me envias no fim

1. Os ficheiros de `tools\qa10\logs\` (CONTROLO, SPIKE-dashboard e, se chegaste lá, SPIKE-servidor).
2. Por cada caso, uma linha com o que viste: **o painel saiu da frente? a app ficou visível à frente?**
3. Qual widget first-party usaste no CONTROLO, e qual servidor clicaste em 2.3.
4. Qualquer coisa estranha do ponto 11 (consola, flash, erro, foco a saltar) — mesmo que pareça pequena.

Com isso decide-se GO/NO-GO. Se for NO-GO, volta a design review: **não vai haver truque nenhum para
forçar o painel a sair**.
