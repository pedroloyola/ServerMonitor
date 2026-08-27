using ServerMonitor.Infrastructure.Collectors.Docker;
using ServerMonitor.Infrastructure.Collectors.Launchd;
using ServerMonitor.Infrastructure.Collectors.Systemd;

namespace ServerMonitor.Infrastructure.Tests.SSH;

public sealed class WorkloadCommandCatalogTests
{
    // Any of these appearing in a catalog command would mean the read-only invariant was broken.
    private static readonly string[] ForbiddenTokens =
    [
        "start", "stop", "restart", "kill", "exec", " rm", "rm ", "update", "pause",
        "unpause", "compose", "enable", "disable", "mask", "bootstrap", "bootout",
        "kickstart", "sudo", "su ", "pkill", "reboot", "shutdown", "systemctl set"
    ];

    [Fact]
    public void Docker_catalog_is_the_two_reviewed_literal_commands()
    {
        Assert.Equal(
            [
                "docker version --format '{{.Server.Version}}'",
                "docker ps -a --no-trunc --format '{{json .}}'"
            ],
            DockerCommandCatalog.All);
    }

    [Fact]
    public void Systemd_catalog_is_the_two_reviewed_literal_commands()
    {
        Assert.Equal(
            [
                "LC_ALL=C systemctl list-units --type=service --no-legend --no-pager --plain",
                "LC_ALL=C systemctl list-unit-files --type=service --no-legend --no-pager"
            ],
            SystemdCommandCatalog.All);
    }

    [Fact]
    public void Launchd_catalog_is_the_single_system_domain_command()
    {
        Assert.Equal(["launchctl print system"], LaunchdCommandCatalog.All);
    }

    [Fact]
    public void No_catalog_command_contains_a_state_changing_token()
    {
        var everyCommand = DockerCommandCatalog.All
            .Concat(SystemdCommandCatalog.All)
            .Concat(LaunchdCommandCatalog.All);

        Assert.All(everyCommand, command =>
        {
            foreach (var forbidden in ForbiddenTokens)
            {
                Assert.False(
                    command.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"Command '{command}' contains forbidden token '{forbidden}'.");
            }
        });
    }
}
