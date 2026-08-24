# ADR-005 — Aplicação standard, modo compacto e widget oficial

Estado: **aceite para preparação arquitetural; apenas a primeira etapa está exposta**.

## Contexto

O Server Monitor precisa de funcionar hoje como uma utility WinUI 3 standard, mantendo aberta a possibilidade de uma apresentação mais compacta e, futuramente, de integração com o Windows Widgets Board. Estas superfícies têm dimensões, lifecycle e restrições de processo diferentes. Tratar as três como a mesma janela criaria dependências de layout nos ViewModels e misturaria a UI desktop com contratos específicos de um provider.

No Milestone 2.5 não existe monitorização, fonte de dados para widgets nem Windows Widget Provider. A decisão define apenas fronteiras que evitam retrabalho.

## Decisão

A evolução será feita em três etapas explícitas:

1. **Standard App** — aplicação WinUI 3 atual, com shell e `ServerFullCard`.
2. **Compact Widget Mode** — apresentação in-process futura, baseada em `ServerCompactCard`, usando os mesmos modelos, serviços e `ServerCardViewModel`. A View escolhe a apresentação; o ViewModel não recebe dimensões da janela.
3. **Windows Widget Provider** — integração oficial futura num componente/processo próprio. Poderá reutilizar contratos e modelos estáveis do Core, mas não controlos XAML nem ViewModels específicos da aplicação standard.

As ações comuns dos cartões ficam num componente reutilizável. O modo compacto não é exposto no Milestone 2.5 e não é persistido como preferência.

## Consequências

- domínio, persistência e estado de servidores não são duplicados;
- a `MainWindow` pode impor o tamanho mínimo da aplicação standard sem condicionar o modo compacto futuro;
- nenhum pacote, manifesto, provider, cache cross-process ou schema de widget é introduzido agora;
- sincronização, lifecycle, packaging e segurança do provider exigirão uma ADR própria quando houver dados reais para apresentar;
- testes de domínio e persistência continuam independentes da superfície visual.

## Alternativas rejeitadas

- **Redimensionar a MainWindow e chamar-lhe widget:** não representa as restrições nem o lifecycle de um widget oficial.
- **Duplicar ViewModels por tamanho:** cria duas fontes de estado e acopla apresentação ao domínio.
- **Criar já um Windows Widget Provider vazio:** acrescenta packaging e complexidade sem dados úteis, ultrapassando o Milestone 2.5.
