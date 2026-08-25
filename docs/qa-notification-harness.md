# QA — Notification transition harness

O harness do M8 permite validar notificações Windows de forma determinística, sem SSH,
servidores reais, persistência, credenciais, host trust ou discovery.

## Executar

O modo existe apenas em builds Debug:

```powershell
dotnet run --project src/ServerMonitor.App/ServerMonitor.App.csproj -c Debug -- --qa-notifications
```

A composição DI cria um único servidor in-memory com hostname `.invalid`, estabelece um
baseline Healthy e emite a sequência:

```text
Healthy → Warning → Critical → Healthy → Offline → Healthy
```

Isto produz, quando `AppNotificationManager` está suportado e habilitado no Windows, uma
notificação Warning, uma Critical, uma Recovery, uma Offline e uma Recovery final. O conteúdo
usa os mesmos recursos localizados e a mesma sanitização da produção. Nenhuma transição executa
SSH ou toca em Credential Manager/known-hosts.

## Validação

- confirmar título e corpo sem IP, username, port, fingerprint, path ou erro SSH;
- confirmar uma notificação por transição e ausência de repetição por polling;
- clicar numa notificação e confirmar que a mesma janela é restaurada;
- repetir em Light e Dark apenas para a secção de Settings; banners/Notification Center seguem
  o visual nativo do Windows;
- fechar via X ou Exit e confirmar remoção do tray e ausência de processo residual.

## Release boundary

`ServerMonitor.App.csproj` remove `Qa/**/*.cs` de configurações não-Debug. O build Release e a
inspeção do assembly são gates: `QaNotification`, `--qa-notifications`, `QaHealth` e
`QaDiscovery` não podem aparecer no binário de produção.

## Limitações do Windows

A app é unpackaged e self-contained. App notifications dependem do suporte real do Windows App
SDK/Singleton e das definições do utilizador ou da organização. Focus Assist/Do Not Disturb pode
suprimir o banner e ainda manter a aplicação funcional. Um resultado indisponível é registado
como limitação de QA, nunca convertido em PASS.
