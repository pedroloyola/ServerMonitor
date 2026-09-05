using ServerMonitor.ActivationContract;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// The launch-argument classifier itself (M13 S2 §8, corrected).
/// <para>
/// The previous round fed already-classified <see cref="ActivationOrigin"/> values into the dispatch
/// matrix, which proved the dispatch but never the grammar: the question that actually decides whether a
/// second <c>--background</c> launch surfaces someone's Dashboard is "what does this command line mean?",
/// and nothing tested it. These tests run real command lines through the production classifier, and then
/// through the production dispatch, so the whole path is covered rather than its second half.
/// </para>
/// </summary>
public sealed class ActivationOriginPolicyTests
{
    [Theory]
    // a person: no arguments, unknown flags, and anything that only resembles the switch
    [InlineData(null, ActivationOrigin.UserActivation)]
    [InlineData("", ActivationOrigin.UserActivation)]
    [InlineData("ServerMonitor.App.exe", ActivationOrigin.UserActivation)]
    [InlineData("ServerMonitor.App.exe --unknown-flag", ActivationOrigin.UserActivation)]
    [InlineData("ServerMonitor.App.exe --verbose --unknown", ActivationOrigin.UserActivation)]
    [InlineData("ServerMonitor.App.exe --background=1", ActivationOrigin.UserActivation)]
    [InlineData("ServerMonitor.App.exe --background:true", ActivationOrigin.UserActivation)]
    [InlineData("ServerMonitor.App.exe --backgroundx", ActivationOrigin.UserActivation)]
    [InlineData("ServerMonitor.App.exe -background", ActivationOrigin.UserActivation)]
    [InlineData("\"C:\\tools\\--background\\ServerMonitor.App.exe\"", ActivationOrigin.UserActivation)]
    // a background launch: the exact token, in any position, in any case, however many times
    [InlineData("ServerMonitor.App.exe --background", ActivationOrigin.BackgroundLaunch)]
    [InlineData("--background", ActivationOrigin.BackgroundLaunch)]
    [InlineData("ServerMonitor.App.exe --BACKGROUND", ActivationOrigin.BackgroundLaunch)]
    [InlineData("ServerMonitor.App.exe --background --background", ActivationOrigin.BackgroundLaunch)]
    [InlineData("ServerMonitor.App.exe --unknown --background", ActivationOrigin.BackgroundLaunch)]
    [InlineData("\"C:\\Program Files\\App\\ServerMonitor.App.exe\" --background", ActivationOrigin.BackgroundLaunch)]
    public void Real_command_lines_are_classified_by_the_production_policy(
        string? commandLine, ActivationOrigin expected) =>
        Assert.Equal(expected, ActivationOriginPolicy.FromLaunchCommandLine(commandLine));

    /// <summary>
    /// The closed codomain: the classifier can only ever produce the two approved origins, so no third
    /// launch shape can appear without this failing.
    /// </summary>
    [Fact]
    public void The_classifier_produces_only_the_two_approved_origins()
    {
        var produced = new[]
            {
                null, "", "app.exe", "app.exe --background", "app.exe --background=2", "--BACKGROUND",
                "app.exe --x --y --background --z", "app.exe --backgroundish"
            }
            .Select(ActivationOriginPolicy.FromLaunchCommandLine)
            .Distinct()
            .OrderBy(origin => origin)
            .ToArray();

        Assert.Equal([ActivationOrigin.UserActivation, ActivationOrigin.BackgroundLaunch], produced);
        Assert.Equal(2, Enum.GetValues<ActivationOrigin>().Length);
    }

    /// <summary>
    /// End to end through the PRODUCTION classifier and the PRODUCTION dispatch: the command line, not a
    /// hand-picked enum, is what decides whether the running instance's window is surfaced.
    /// </summary>
    [Theory]
    [InlineData("ServerMonitor.App.exe", 1)]
    [InlineData("ServerMonitor.App.exe --background", 0)]
    [InlineData("ServerMonitor.App.exe --background=1", 1)] // a value form is NOT the switch
    public void A_launch_command_line_decides_whether_the_window_is_surfaced(
        string commandLine, int expectedRestores)
    {
        var restores = 0;
        var dispatch = new ActivationDispatch(_ => { }, () => restores++);

        dispatch.Dispatch(intent: null, ActivationOriginPolicy.FromLaunchCommandLine(commandLine));

        Assert.Equal(expectedRestores, restores);
    }

    /// <summary>
    /// §6: a second background launch converges on the running primary. It executes no intent, surfaces
    /// no UI and — because it redirects rather than registering the key — never builds a second host. The
    /// intent the primary receives in that case is NONE, and that is the whole point.
    /// </summary>
    [Fact]
    public void A_second_background_launch_delivers_no_intent_and_surfaces_nothing()
    {
        var delivered = new List<ActivationIntent?>();
        var restores = 0;
        var dispatch = new ActivationDispatch(intent => delivered.Add(intent), () => restores++);

        dispatch.Dispatch(
            intent: null,
            ActivationOriginPolicy.FromLaunchCommandLine("ServerMonitor.App.exe --background"));

        Assert.Equal([null], delivered);
        Assert.Equal(0, restores);
    }
}
