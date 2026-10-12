using System.Text.RegularExpressions;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Controls;

/// <summary>
/// UI.12 C-L1 (Cortex UI.12A, P2). <c>ServerFormControl</c> detached from its view model on Unloaded and re-attached on
/// Loaded. A theme remount (SaThemeRefresh) can raise the new Loaded BEFORE the stale Unloaded (measured in UI.11C, see
/// SaLiveTree): Loaded's attach was a no-op (same view model) and the stale Unloaded then detached for good, because the
/// DataContext is set once and nothing re-attaches - the key picker, the route line and the saved-secret labels froze and the
/// key menu came up empty. The control is modelled here by its two handlers over <see cref="DeferredUnloadDetach"/>, with a
/// fake dispatcher queue and a fake liveness probe; both event orders and the real-unload case are covered. The control's
/// wiring (XAML type) is pinned by source. Runtime (editor + theme switch + key menu): NOT_RUN here, Beacon.
/// </summary>
public sealed partial class Ui12FormDetachOnRemountTests
{
    private const string ViewModel = "vm";

    [Fact]
    public void Remount_LoadedBeforeStaleUnloaded_StaysAttached()
    {
        var form = new FormModel();
        form.Loaded();                 // first mount
        form.IsLive = true;

        form.Loaded();                 // remount: the new Loaded first (attach is a no-op, same view model)
        form.Unloaded();               // ...then the stale Unloaded
        form.RunQueue();

        Assert.Equal(ViewModel, form.Attached);
    }

    [Fact]
    public void Remount_UnloadedThenLoaded_StaysAttached()
    {
        var form = new FormModel();
        form.Loaded();
        form.IsLive = true;

        form.Unloaded();
        form.Loaded();
        form.RunQueue();

        Assert.Equal(ViewModel, form.Attached);
    }

    [Fact]
    public void ARealUnload_StillDetaches_OneTurnLater()
    {
        var form = new FormModel();
        form.Loaded();

        form.IsLive = false;           // navigated away: not in the live tree any more
        form.Unloaded();
        Assert.Equal(ViewModel, form.Attached);
        form.RunQueue();

        Assert.Null(form.Attached);

        form.IsLive = true;            // mounted again later: Loaded re-attaches, as before
        form.Loaded();
        Assert.Equal(ViewModel, form.Attached);
    }

    [Fact]
    public void AQueueThatCannotPost_DetachesImmediately()
    {
        var form = new FormModel { CanPost = false };
        form.Loaded();

        form.Unloaded();

        Assert.Null(form.Attached);
    }

    [Fact]
    public void TheControl_DetachesThroughTheDeferredRule_AndNowhereSynchronously()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/ServerFormControl.xaml.cs");

        Assert.Matches(DeferredWiring(), code);
        Assert.Matches(UnloadedUsesIt(), code);
        Assert.DoesNotMatch(SynchronousDetachOnUnloaded(), code);
    }

    [GeneratedRegex(@"new DeferredUnloadDetach\(\s*isLive:\s*\(\)\s*=>\s*SaLiveTree\.IsLive\(this\),\s*post:\s*action\s*=>\s*DispatcherQueue\?\.TryEnqueue\(DispatcherQueuePriority\.Low,\s*\(\)\s*=>\s*action\(\)\)\s*==\s*true,\s*detach:\s*\(\)\s*=>\s*Attach\(null\)\)")]
    private static partial Regex DeferredWiring();

    [GeneratedRegex(@"Unloaded \+= \(_, _\) => unloadDetach\.OnUnloaded\(\);")]
    private static partial Regex UnloadedUsesIt();

    [GeneratedRegex(@"Unloaded \+= \(_, _\) => Attach\(null\);")]
    private static partial Regex SynchronousDetachOnUnloaded();

    /// <summary>ServerFormControl's attach state and its Loaded/Unloaded handlers, over the production rule.</summary>
    private sealed class FormModel
    {
        private readonly Queue<Action> _queue = new();
        private readonly DeferredUnloadDetach _detach;

        public FormModel()
        {
            _detach = new DeferredUnloadDetach(
                () => IsLive,
                action =>
                {
                    if (!CanPost)
                    {
                        return false;
                    }

                    _queue.Enqueue(action);
                    return true;
                },
                () => Attached = null);
        }

        public string? Attached { get; private set; }

        public bool IsLive { get; set; } = true;

        public bool CanPost { get; init; } = true;

        public void Loaded() => Attached = ViewModel; // Attach(DataContext) - DataContext is set once

        public void Unloaded() => _detach.OnUnloaded();

        public void RunQueue()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
