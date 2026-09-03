using ServerMonitor.App.Shell.Tray;

namespace ServerMonitor.App.Tests.Shell;

/// <summary>
/// The tray menu order is product-fixed and not negotiable, so it is pinned here rather than living only
/// in the order of five <c>Add</c> calls inside a method no test can reach without a desktop.
/// </summary>
public sealed class TrayFlyoutMenuTests
{
    [Fact]
    public void The_menu_order_is_exactly_the_agreed_one()
    {
        // Abrir o ServerAlyzer · Modo compacto · Atualizar todos · Definições · Sair do ServerAlyzer
        Assert.Equal(
            [
                TrayCommand.Open,
                TrayCommand.ToggleCompact,
                TrayCommand.RefreshAll,
                TrayCommand.Settings,
                TrayCommand.Exit
            ],
            TrayFlyoutMenu.Order);
    }

    [Fact]
    public void Every_command_is_in_the_menu_exactly_once()
    {
        // Guards the two ways an order array goes wrong without changing its length in an obvious way:
        // a duplicated entry, or a command that quietly stops being reachable.
        var all = Enum.GetValues<TrayCommand>();

        Assert.Equal(all.Length, TrayFlyoutMenu.Order.Length);
        Assert.Equal(all.Length, TrayFlyoutMenu.Order.Distinct().Count());
        Assert.All(all, command => Assert.Contains(command, TrayFlyoutMenu.Order));
    }

    [Fact]
    public void Exit_is_last_because_a_destructive_item_is_not_placed_where_a_slip_lands_on_it()
    {
        Assert.Equal(TrayCommand.Exit, TrayFlyoutMenu.Order[^1]);
    }

    [Fact]
    public void Each_command_resolves_the_key_the_previous_adapter_used()
    {
        // The swap changes the OWNER of the icon and nothing the user reads: same keys, same strings.
        // A [Theory] would be the natural shape, but TrayCommand is internal and InlineData cannot carry
        // it out to a public test signature, so the cases live in the body instead.
        (TrayCommand Command, string Key)[] cases =
        [
            (TrayCommand.Open, "TrayOpenMenuItem"),
            (TrayCommand.ToggleCompact, "TrayCompactModeMenuItem"),
            (TrayCommand.RefreshAll, "TrayRefreshAllMenuItem"),
            (TrayCommand.Settings, "TraySettingsMenuItem"),
            (TrayCommand.Exit, "TrayExitMenuItem")
        ];

        Assert.Equal(TrayFlyoutMenu.Order.Length, cases.Length);
        Assert.All(cases, c => Assert.Equal(c.Key, TrayFlyoutMenu.ResourceKeyFor(c.Command)));
    }

    [Fact]
    public void Every_resource_key_is_distinct()
    {
        var keys = TrayFlyoutMenu.Order.Select(TrayFlyoutMenu.ResourceKeyFor).ToArray();

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void Every_menu_key_and_the_tooltip_exist_in_every_localization()
    {
        // Without this, the key assertions above compare a constant to itself. This is what makes them
        // mean something: a key the RESW does not define renders as an empty menu item at runtime, and
        // an empty item in the exit menu is an app the user cannot quit.
        var required = TrayFlyoutMenu.Order
            .Select(TrayFlyoutMenu.ResourceKeyFor)
            .Append("TrayToolTip")
            .ToArray();

        var resources = Directory.GetFiles(
            Path.Combine(RepositoryRoot(), "src", "ServerMonitor.App", "Resources"),
            "Resources.resw",
            SearchOption.AllDirectories);

        Assert.NotEmpty(resources);

        foreach (var file in resources)
        {
            var content = File.ReadAllText(file);
            foreach (var key in required)
            {
                Assert.Contains($"name=\"{key}\"", content, StringComparison.Ordinal);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
