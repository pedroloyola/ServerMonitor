using ServerMonitor.Infrastructure.Collectors.Linux;
using ServerMonitor.Infrastructure.Collectors.MacOS;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

public sealed class SshApiBoundaryTests
{
    [Fact]
    public void Low_level_sessions_are_not_public_api()
    {
        var exportedNames = typeof(SshConnectionService).Assembly
            .ExportedTypes
            .Select(type => type.Name)
            .ToArray();

        Assert.DoesNotContain("ISshSession", exportedNames);
        Assert.DoesNotContain("ISshSessionFactory", exportedNames);
        Assert.DoesNotContain("SshNetSessionFactory", exportedNames);
    }

    [Fact]
    public void Linux_metrics_port_accepts_no_command_text()
    {
        var methods = typeof(ILinuxMetricsRemoteSource).GetMethods();

        Assert.NotEmpty(methods);
        Assert.All(
            methods.SelectMany(method => method.GetParameters()),
            parameter => Assert.NotEqual(typeof(string), parameter.ParameterType));
    }

    [Fact]
    public void MacOs_metrics_port_accepts_no_command_text()
    {
        var methods = typeof(IMacOsMetricsRemoteSource).GetMethods();

        Assert.NotEmpty(methods);
        Assert.All(
            methods.SelectMany(method => method.GetParameters()),
            parameter => Assert.NotEqual(typeof(string), parameter.ParameterType));
    }
}
