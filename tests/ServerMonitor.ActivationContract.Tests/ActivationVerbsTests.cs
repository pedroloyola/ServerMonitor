using ServerMonitor.ActivationContract;

namespace ServerMonitor.ActivationContract.Tests;

public sealed class ActivationVerbsTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void For_intent_maps_to_verb()
    {
        Assert.Equal(ActivationVerbs.OpenDashboard, ActivationVerbs.ForIntent(ActivationIntent.Dashboard));
        Assert.Equal(ActivationVerbs.OpenServer, ActivationVerbs.ForIntent(ActivationIntent.Server(Id)));
    }

    [Fact]
    public void Open_dashboard_verb_maps_to_intent()
    {
        Assert.Equal(ActivationIntent.Dashboard, ActivationVerbs.TryToIntent(ActivationVerbs.OpenDashboard, null));
    }

    [Fact]
    public void Open_server_verb_needs_a_valid_id()
    {
        var intent = ActivationVerbs.TryToIntent(ActivationVerbs.OpenServer, Id);
        Assert.Equal(Id, intent!.ServerId);

        Assert.Null(ActivationVerbs.TryToIntent(ActivationVerbs.OpenServer, null));       // no id
        Assert.Null(ActivationVerbs.TryToIntent(ActivationVerbs.OpenServer, Guid.Empty)); // empty id
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("openurl")]
    [InlineData("run")]
    [InlineData("OpenServer")] // case-sensitive verb
    public void Unknown_verb_is_null(string? verb)
    {
        Assert.Null(ActivationVerbs.TryToIntent(verb, Id));
    }
}
