# QA — Network Discovery harness

O harness visual do M7 permite validar a secção **Encontrados na rede** de forma determinística, sem depender de multicast, SSH ou equipamentos reais.

## Executar

O harness existe apenas em builds Debug:

```powershell
dotnet run --project src/ServerMonitor.App/ServerMonitor.App.csproj -c Debug -- --qa-discovery
```

O modo `--qa-discovery` substitui a data plane por doubles inertes e apresenta dois anúncios `_ssh._tcp` em memória:

- `Mac Studio` (`mac-studio.local:22`);
- `Raspberry Pi` (`raspberrypi.local:22`).

Não abre sockets mDNS, não lê persistência, credenciais ou host-trust, não executa SSH e não inicia o `MonitoringEngine`. Os mesmos ViewModels, controlos, recursos e diálogo de Add usados em produção continuam ativos.

## Cenários visuais

1. confirmar a hierarquia secundária, contador `2`, cards e scroll em Light e Dark;
2. clicar **Ignorar** num item e confirmar contador `1` e independência do outro;
3. ignorar ambos e confirmar que a secção desaparece;
4. em **Configurações**, usar **Redefinir dispositivos ignorados** e confirmar que os dois regressam;
5. clicar **Adicionar** e confirmar `Name`, `Host`, `Port = 22` e `OperatingSystem = Auto`; cancelar sem guardar;
6. repetir na largura mínima suportada e com navegação por teclado.

O Ignore do harness é deliberadamente só em memória. A durabilidade entre restarts é coberta pelo `JsonIgnoredDeviceStore` em testes e deve ser validada no modo normal com um anúncio real ou responder externo, restaurando o estado local no fim.

## Release boundary

`ServerMonitor.App.csproj` remove `Qa/**/*.cs` de configurações não-Debug. O flag e os dados fake não são compilados no artefacto Release. O build Release é um gate obrigatório.

## Limitações de rede real

- mDNS só alcança o segmento multicast visível;
- macOS normalmente anuncia `_ssh._tcp` quando Remote Login já está ativo, mas o QA não o deve ativar;
- Linux pode necessitar de Avahi ou outro anúncio compatível; não instalar nem configurar isso automaticamente;
- VPNs, scan de subnet e scan de portas ficam fora do M7.
