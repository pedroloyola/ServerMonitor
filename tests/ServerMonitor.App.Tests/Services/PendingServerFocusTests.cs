using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

public sealed class PendingServerFocusTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Resolves_when_the_server_is_already_loaded()
    {
        var focus = new PendingServerFocus();
        focus.Request(A);
        Assert.Equal(A, focus.TryResolve(new[] { A, B }));
        Assert.False(focus.HasPending);                 // cleared after focusing once
        Assert.Null(focus.TryResolve(new[] { A, B }));  // does not re-fire
    }

    [Fact]
    public void Stays_pending_until_the_server_loads()
    {
        var focus = new PendingServerFocus();
        focus.Request(A);
        Assert.Null(focus.TryResolve(Array.Empty<Guid>())); // not loaded yet
        Assert.True(focus.HasPending);
        Assert.Equal(A, focus.TryResolve(new[] { A }));      // resolves after load
    }

    [Fact]
    public void Dashboard_clears_an_older_server_request()
    {
        // Server(A) then a newer Dashboard intent: the dashboard must win — A never focuses (§M-3).
        var focus = new PendingServerFocus();
        focus.Request(A);
        focus.Clear();
        Assert.False(focus.HasPending);
        Assert.Null(focus.TryResolve(new[] { A }));
    }

    [Fact]
    public void A_newer_server_request_replaces_an_older_one()
    {
        // Server(A) then Server(B) before load: B wins (§28/§M-3).
        var focus = new PendingServerFocus();
        focus.Request(A);
        focus.Request(B);
        Assert.Null(focus.TryResolve(Array.Empty<Guid>()));
        Assert.Equal(B, focus.TryResolve(new[] { A, B }));
    }

    [Fact]
    public void A_removed_server_never_resolves()
    {
        var focus = new PendingServerFocus();
        focus.Request(A);
        Assert.Null(focus.TryResolve(new[] { B }));   // A was removed
        Assert.Null(focus.TryResolve(new[] { B }));
        Assert.True(focus.HasPending);                // harmless: it just never focuses (§11)
    }

    [Fact]
    public void No_request_resolves_to_null()
    {
        Assert.Null(new PendingServerFocus().TryResolve(new[] { A }));
    }
}
