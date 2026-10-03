using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// UI.5 fix round 4: the clock every test passes to a notice / toast owner (SettingsViewModel, ServersViewModel) - now a
/// REQUIRED constructor parameter. A stopped FakeTimeProvider: a countdown started here never fires unless the test
/// advances it.
/// </summary>
internal static class TestClock
{
    public static PresentationClock Fake() => new(new FakeTimeProvider(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero)));
}
