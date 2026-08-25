using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// Covers the transient per-server monitoring-state store the engine writes and the UI
/// observes. It never persists; unknown servers read as a fresh <c>Initial</c> state.
/// </summary>
public sealed class ServerMonitoringStateStoreTests
{
    [Fact]
    public void Get_UnknownServer_ReturnsInitialState()
    {
        var store = new ServerMonitoringStateStore();
        var id = Guid.NewGuid();

        var state = store.Get(id);

        Assert.Equal(id, state.ServerId);
        Assert.Equal(ServerHealth.Unknown, state.Health);
        Assert.False(state.IsRefreshing);
        Assert.Null(state.LastSuccessAt);
        Assert.False(state.HasEverSucceeded);
    }

    [Fact]
    public void Set_StoresStateRetrievableById()
    {
        var store = new ServerMonitoringStateStore();
        var id = Guid.NewGuid();
        var state = ServerMonitoringState.Initial(id) with { Health = ServerHealth.Healthy };

        store.Set(state);

        Assert.Same(state, store.Get(id));
        Assert.True(store.TryGet(id, out var explicitState));
        Assert.Same(state, explicitState);
    }

    [Fact]
    public void TryGet_UnknownServer_DoesNotCreateSyntheticState()
    {
        var store = new ServerMonitoringStateStore();

        Assert.False(store.TryGet(Guid.NewGuid(), out _));
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Set_RaisesStateChangedWithServerId()
    {
        var store = new ServerMonitoringStateStore();
        var id = Guid.NewGuid();
        Guid? raised = null;
        store.StateChanged += (_, changedId) => raised = changedId;

        store.Set(ServerMonitoringState.Initial(id));

        Assert.Equal(id, raised);
    }

    [Fact]
    public void Set_Null_Throws()
    {
        var store = new ServerMonitoringStateStore();

        Assert.Throws<ArgumentNullException>(() => store.Set(null!));
    }

    [Fact]
    public void GetAll_ReturnsOnlyExplicitlySetStates()
    {
        var store = new ServerMonitoringStateStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.Set(ServerMonitoringState.Initial(first));
        store.Set(ServerMonitoringState.Initial(second));

        var all = store.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, state => state.ServerId == first);
        Assert.Contains(all, state => state.ServerId == second);
    }

    [Fact]
    public void Remove_ExistingServer_ClearsStateAndRaisesChanged()
    {
        var store = new ServerMonitoringStateStore();
        var id = Guid.NewGuid();
        store.Set(ServerMonitoringState.Initial(id) with { Health = ServerHealth.Critical });
        Guid? raised = null;
        store.StateChanged += (_, changedId) => raised = changedId;

        store.Remove(id);

        Assert.Equal(id, raised);
        Assert.Empty(store.GetAll());
        // Reading a removed server falls back to a fresh Initial state.
        Assert.Equal(ServerHealth.Unknown, store.Get(id).Health);
    }

    [Fact]
    public void Remove_UnknownServer_DoesNotRaiseChanged()
    {
        var store = new ServerMonitoringStateStore();
        var raisedCount = 0;
        store.StateChanged += (_, _) => raisedCount++;

        store.Remove(Guid.NewGuid());

        Assert.Equal(0, raisedCount);
    }
}
