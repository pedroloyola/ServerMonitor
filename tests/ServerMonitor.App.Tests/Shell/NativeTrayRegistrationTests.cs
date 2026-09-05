using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Shell.Tray;

namespace ServerMonitor.App.Tests.Shell;

/// <summary>
/// The parts of the native boundary that are decidable without a desktop.
/// <para>
/// Everything else in <c>NativeTrayRegistration</c> is a <c>Shell_NotifyIcon</c> call whose behaviour is
/// the operating system's, not ours, and is covered by the S6 human QA cases rather than pretended to be
/// covered here.
/// </para>
/// </summary>
public sealed class NativeTrayRegistrationTests
{
    [Fact]
    public void A_tooltip_that_fits_is_carried_through_unchanged()
    {
        const string tip = "ServerAlyzer";
        Assert.Equal(tip, NativeTrayRegistration.FitTooltip(tip));
    }

    [Fact]
    public void A_tooltip_longer_than_the_buffer_is_truncated_rather_than_failing_the_registration()
    {
        var tip = new string('a', 400);
        var fitted = NativeTrayRegistration.FitTooltip(tip);

        Assert.Equal(NativeTrayRegistration.MaxTooltipLength, fitted.Length);
    }

    [Fact]
    public void The_truncation_leaves_room_for_the_terminator_in_a_128_character_buffer()
    {
        // The buffer is 128 wide chars INCLUDING the terminator. Off-by-one here is a marshalling
        // exception at NIM_ADD, i.e. a tray that never appears.
        Assert.Equal(127, NativeTrayRegistration.MaxTooltipLength);
    }

    [Fact]
    public void A_missing_tooltip_becomes_empty_rather_than_null()
    {
        Assert.Equal(string.Empty, NativeTrayRegistration.FitTooltip(null));
        Assert.Equal(string.Empty, NativeTrayRegistration.FitTooltip(string.Empty));
    }

    [Fact]
    public void A_registration_without_a_host_window_is_refused_at_construction()
    {
        // The callback target cannot be late: an icon registered against HWND 0 can never deliver a
        // click, and the failure would only surface as an unresponsive tray.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeTrayRegistration(0, "irrelevant.ico", "tip", NullLogger.Instance));
    }

    [Fact]
    public void A_missing_icon_asset_is_refused_at_construction()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"serveralyzer-{Guid.NewGuid():N}.ico");

        Assert.Throws<FileNotFoundException>(() =>
            new NativeTrayRegistration(1, missing, "tip", NullLogger.Instance));
    }
}
