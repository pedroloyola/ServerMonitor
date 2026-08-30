using ServerMonitor.ActivationContract;

namespace ServerMonitor.ActivationContract.Tests;

public sealed class ActivationUriTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Formats_dashboard()
    {
        Assert.Equal("serveralyzer://dashboard", ActivationUri.Format(ActivationIntent.Dashboard));
    }

    [Fact]
    public void Formats_server()
    {
        Assert.Equal($"serveralyzer://server/{Id:D}", ActivationUri.Format(ActivationIntent.Server(Id)));
    }

    [Fact]
    public void Round_trips_dashboard_and_server()
    {
        Assert.Equal(ActivationIntent.Dashboard, ActivationUri.TryParse(ActivationUri.Format(ActivationIntent.Dashboard)));
        var server = ActivationUri.TryParse(ActivationUri.Format(ActivationIntent.Server(Id)));
        Assert.Equal(ActivationIntentKind.OpenServer, server!.Kind);
        Assert.Equal(Id, server.ServerId);
    }

    [Theory]
    [InlineData("serveralyzer://dashboard")]
    [InlineData("SERVERALYZER://DASHBOARD")]   // scheme + host case-insensitive
    [InlineData("serveralyzer://Dashboard")]
    public void Parses_dashboard(string uri)
    {
        Assert.Equal(ActivationIntent.Dashboard, ActivationUri.TryParse(uri));
    }

    [Fact]
    public void Parses_server_case_insensitive_host_and_guid()
    {
        var upper = $"serveralyzer://SERVER/{Id.ToString("D").ToUpperInvariant()}";
        var result = ActivationUri.TryParse(upper);
        Assert.Equal(Id, result!.ServerId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a uri")]
    [InlineData("http://dashboard")]                     // wrong scheme
    [InlineData("serveralyzer://settings")]              // unknown host
    [InlineData("serveralyzer://run?cmd=rm")]            // query/command
    [InlineData("serveralyzer://ssh/host")]              // unknown host
    [InlineData("serveralyzer://dashboard/extra")]       // dashboard must have no path
    [InlineData("serveralyzer://dashboard?x=1")]         // query on dashboard
    [InlineData("serveralyzer://dashboard#frag")]        // fragment
    [InlineData("serveralyzer://server")]                // missing id
    [InlineData("serveralyzer://server/")]               // empty id
    [InlineData("serveralyzer://server/not-a-guid")]     // bad guid
    [InlineData("serveralyzer://server/00000000-0000-0000-0000-000000000000")] // empty guid rejected
    [InlineData("serveralyzer://server/11111111-2222-3333-4444-555555555555/extra")] // extra segment
    [InlineData("serveralyzer://server/11111111-2222-3333-4444-555555555555?x=1")]   // query
    [InlineData("serveralyzer://server/11111111-2222-3333-4444-555555555555#f")]     // fragment
    [InlineData("serveralyzer://user@server/11111111-2222-3333-4444-555555555555")]  // userinfo
    [InlineData("serveralyzer://server:8080/11111111-2222-3333-4444-555555555555")]  // explicit port
    [InlineData("serveralyzer://server/11111111%2f2222")] // encoded slash
    public void Rejects_invalid(string? uri)
    {
        Assert.Null(ActivationUri.TryParse(uri));
    }

    [Fact]
    public void Rejects_oversized()
    {
        var huge = "serveralyzer://server/" + new string('a', ActivationUri.MaxUriLength);
        Assert.Null(ActivationUri.TryParse(huge));
    }

    [Fact]
    public void Rejects_braces_and_curly_guid()
    {
        Assert.Null(ActivationUri.TryParse("serveralyzer://server/{11111111-2222-3333-4444-555555555555}"));
        Assert.Null(ActivationUri.TryParse("serveralyzer://server/11111111222233334444555555555555")); // "N" format not accepted
    }
}
