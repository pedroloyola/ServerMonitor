using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// The headless switch is the only new externally-controlled input the S2 slice adds, so its surface is
/// pinned here (Vigil C4): pure, stateless, exactly two outcomes, and one exact token.
/// </summary>
public sealed class LaunchModePolicyTests
{
    [Theory]
    [InlineData("--background")]
    [InlineData("--BACKGROUND")]
    [InlineData("--Background")]
    public void The_exact_switch_selects_background(string argument) =>
        Assert.Equal(LaunchMode.Background, LaunchModePolicy.Resolve([argument]));

    /// <summary>
    /// Everything that is merely LIKE the switch is a foreground launch. There is no value grammar, no
    /// prefix matching and no alias, so an attacker-controlled command line has nothing to steer.
    /// </summary>
    [Theory]
    [InlineData("--background=1")]
    [InlineData("--background:true")]
    [InlineData("--backgroundx")]
    [InlineData("-background")]
    [InlineData("background")]
    [InlineData("/background")]
    [InlineData("--back")]
    [InlineData("--background ")]
    [InlineData(" --background")]
    [InlineData("")]
    public void Anything_that_is_not_the_exact_switch_stays_foreground(string argument) =>
        Assert.Equal(LaunchMode.Foreground, LaunchModePolicy.Resolve([argument]));

    [Fact]
    public void No_arguments_at_all_is_foreground()
    {
        Assert.Equal(LaunchMode.Foreground, LaunchModePolicy.Resolve([]));
        Assert.Equal(LaunchMode.Foreground, LaunchModePolicy.Resolve(null));
    }

    [Fact]
    public void The_switch_is_matched_by_value_not_by_position()
    {
        Assert.Equal(
            LaunchMode.Background,
            LaunchModePolicy.Resolve([@"C:\Program Files\ServerAlyzer\ServerMonitor.App.exe", "--background"]));
        Assert.Equal(
            LaunchMode.Background,
            LaunchModePolicy.Resolve(["--background", "--something-else"]));
    }

    /// <summary>A redirected launch carries a raw command line, and it is classified the same way.</summary>
    [Theory]
    [InlineData("\"C:\\App\\ServerMonitor.App.exe\" --background", LaunchMode.Background)]
    [InlineData("ServerMonitor.App.exe --background", LaunchMode.Background)]
    [InlineData("ServerMonitor.App.exe", LaunchMode.Foreground)]
    [InlineData("ServerMonitor.App.exe --background=1", LaunchMode.Foreground)]
    [InlineData("C:\\tools\\--background\\app.exe", LaunchMode.Foreground)]
    [InlineData("", LaunchMode.Foreground)]
    [InlineData(null, LaunchMode.Foreground)]
    public void A_raw_command_line_is_classified_the_same_way(string? commandLine, LaunchMode expected) =>
        Assert.Equal(expected, LaunchModePolicy.ResolveFromCommandLine(commandLine));

    /// <summary>
    /// The codomain is the security contract: two values. A third mode, a parameter or a second flag
    /// reopens Vigil's opinion, so it must not be possible to add one without this failing.
    /// </summary>
    [Fact]
    public void The_launch_mode_has_exactly_two_values()
    {
        Assert.Equal(
            [LaunchMode.Foreground, LaunchMode.Background],
            Enum.GetValues<LaunchMode>());
    }
}
