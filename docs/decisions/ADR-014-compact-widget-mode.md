# ADR-014 — Compact Widget Mode (M9)

Estado: **aceite**. Implementa a etapa 2 da ADR-005 (Compact Widget Mode in-process). A etapa 3
(Windows Widget Provider oficial) permanece reservada para o M13 e **não** é introduzida aqui.

## Contexto

O Server Monitor precisa de uma forma discreta e persistente de vigiar os servidores no desktop —
um pequeno widget flutuante com o estado essencial — sem abrir a aplicação completa. A ADR-005 já
tinha fixado a fronteira: o modo compacto é uma segunda *apresentação* da mesma aplicação, nunca uma
segunda aplicação, e os ViewModels de servidor não conhecem a largura da janela.

## Decisão

### Uma janela, dois modos de apresentação

Existe uma única `MainWindow`, uma única aplicação, um único `MonitoringEngine`, `DiscoveryService`,
`ServerMetricsStore` e tray. A `MainWindow` contém duas raízes de apresentação (`StandardRoot`,
`CompactRoot`) e alterna a visibilidade e a região de arrasto do título entre elas. O Compact reutiliza
integralmente `DashboardViewModel.VisibleServers` e `ServerCardViewModel` — os mesmos ViewModels, o
mesmo estado ao vivo do M6. Nenhum estado é duplicado; não há segundo processo nem segunda janela.

### WindowModeCoordinator + fronteiras testáveis

Toda a lógica de janela vive na camada App (nunca no domínio/persistência/VMs de servidor):

- **Modelo WinUI-free e testável** (`App/Windowing/`): `WindowMode`, `WindowBounds`,
  `DisplayWorkArea`, `WindowSizeConstraints`, `WindowPlacement`.
- **`WindowPlacementResolver`** — função pura que transforma bounds persistidos (input não confiável)
  numa rectângulo seguro para a topologia de ecrãs atual: recuperação de monitor removido, bounds
  totalmente fora do ecrã, coordenadas negativas, reescala por DPI e rejeição de valores
  absurdos/corruptos.
- **`WindowModeCoordinator`** — sequencia cada transição Standard ⇄ Compact de forma determinística:
  captura os bounds do modo que sai → configura o presenter do modo que entra → resolve e aplica
  bounds recuperados → aplica always-on-top → persiste → anuncia `ModeChanged`. Nunca deixa a janela
  meio-transitada (topmost em Standard, ou redimensionada antes de trocar o conteúdo).
- **`IWindowPlacementAdapter`** (`AppWindowPlacementAdapter`) — única fronteira nativa
  (AppWindow / OverlappedPresenter / DisplayArea / P/Invoke de DPI por monitor). Fakeável; mantém o
  P/Invoke fora dos ViewModels.

O code-behind da `MainWindow` permanece fino: só reage a `ModeChanged` para trocar a apresentação e
a região de arrasto, e faz debounce da persistência de bounds em move/minimize/close.

### Always-on-top

Propriedade **exclusiva da experiência Compact**, via `OverlappedPresenter.IsAlwaysOnTop` (mecanismo
oficial suportado — sem polling, sem timer de z-order). Default `false`, persistido. Ao voltar a
Standard o topmost é sempre removido; ao voltar a Compact a preferência é reaplicada.

### Persistência de placement

`WindowPlacementSettings` (mode; bounds+DPI por modo; compact always-on-top) é persistido em
`%LOCALAPPDATA%\ServerMonitor\window-placement.json` com o mesmo padrão atómico e resiliente das
notification-settings (write-through + move, malformed/oversized → default, bounded). Os bounds são
guardados **por modo**, para que alternar não sobrescreva a geometria do outro modo. Uma instalação
pré-M9 (sem ficheiro) abre em Standard.

### Multi-monitor e DPI

Os bounds físicos são guardados com o factor de escala do monitor em que foram medidos. No restauro,
o resolver escolhe o monitor com maior interseção; se nenhum interseta (monitor desligado), recupera
para o primário; reescala o tamanho para preservar o tamanho *lógico* quando o DPI do monitor-alvo
difere do guardado; e faz clamp para caber totalmente na área de trabalho. Coordenadas negativas
(monitores à esquerda/acima) são suportadas.

### Integração tray / minimize / close / notificações (M8 mantém-se autoritativo)

- Minimize → tray, em ambos os modos; restore devolve a **mesma** janela no **mesmo** modo.
- X/Alt+F4/Exit → encerra (M8 inalterado; não é close-to-tray).
- Novo item de tray "Modo compacto" → `IApplicationWindowController.ToggleCompactMode` (restaura,
  ativa e alterna, sempre na mesma janela).
- Click numa notificação enquanto Compact minimizado → restaura a mesma janela no modo Compact (o
  modo nunca é alterado no restore). `AlertCoordinator` inalterado.

### Discovery excluído do Compact

O Compact mostra apenas servidores configurados visíveis (a coleção `VisibleServers`): hidden e
discovered-only nunca aparecem. O M7 continua ativo em background; a descoberta permanece no
Standard.

## M9 ≠ Windows Widget (M13)

- **M9 Compact Widget Mode:** janela desktop WinUI da própria aplicação (esta ADR).
- **M13 Official Windows Widget:** Windows Widget Provider / Widget Board, processo/integração
  próprios. **Não** é adicionado nenhum package/manifesto/provider de Widgets neste milestone.

## Consequências

- Domínio, persistência e ViewModels de servidor permanecem agnósticos ao modo de janela.
- A lógica de recuperação (multi-monitor/DPI/off-screen/corrupção) é pura e coberta por testes
  unitários, independente de hardware.
- QA real multi-monitor/DPI pode ficar NOT_RUN quando o hardware não está disponível, desde que a
  implementação seja testável e a limitação reportada — nunca marcada como PASS.

## Alternativas rejeitadas

- **Segunda top-level window para o widget:** duplicaria lifecycle/estado e violaria a ADR-005; a
  arquitetura de uma janela com duas apresentações é suficiente e mais simples.
- **Manter z-order com timer/polling:** rejeitado a favor do `IsAlwaysOnTop` suportado.
- **Guardar apenas dimensões físicas sem DPI:** quebraria o tamanho lógico ao mover entre monitores
  de DPI diferente; guardamos o factor de escala e reescalamos no restauro.
- **Widget Provider oficial agora:** é o M13; adicionaria packaging e um contrato de processo sem
  pertencer a este milestone.
