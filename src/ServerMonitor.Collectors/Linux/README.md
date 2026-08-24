# Linux

`LinuxMetricsCollector` (M4) implementa `IServerMetricsCollector` para servidores Linux,
consumindo `ILinuxMetricsRemoteSource` (Infrastructure) e devolvendo um `ServerMetricsSnapshot`.

- Rejeita servidores não-Linux sem executar comandos remotos.
- `Parsing/` contém parsers puros e determinísticos (sem I/O) para cada fonte fixa:
  `/proc/stat` (CPU), `/proc/meminfo` (RAM), `df -P -B1 /` (disco), `/proc/uptime`,
  hostname e `/etc/os-release`.
- Um valor `null` significa "desconhecido"; zero real é preservado como zero.
- `LinuxMetricsCollectorOptions` define o intervalo de amostragem de CPU (500 ms) e o timeout.
