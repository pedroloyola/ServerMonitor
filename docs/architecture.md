# Arquitetura — Milestone 1

```text
ServerMonitor.App  ──→ ServerMonitor.Core

ServerMonitor.Infrastructure ──→ ServerMonitor.Core
ServerMonitor.Collectors     ──→ ServerMonitor.Core
```

`ServerMonitor.App` contém a composição por dependency injection, a shell WinUI 3, Views, ViewModels, serviços estritamente relacionados com apresentação, recursos de localização e design tokens.

`ServerMonitor.Core` contém apenas modelos e regras independentes de UI. `Infrastructure` e `Collectors` existem como limites de projeto, mas não possuem implementações funcionais neste marco.

## Decisões do bootstrap

- `.NET 10` e Windows App SDK `2.4.0`;
- aplicação unpackaged x64 no Milestone 1;
- MVVM simples, sem framework MVVM adicional;
- `Microsoft.Extensions.Hosting` para DI e lifecycle;
- `Microsoft.Extensions.Logging.Debug` para logging sem ficheiros ou dados sensíveis;
- recursos RESW externos e `pt-BR` como idioma padrão/fallback;
- Mica para o backdrop da janela e Acrylic adaptável ao tema para superfícies glass.
