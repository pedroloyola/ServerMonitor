using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// UI.11 F09 (Cortex §4). The Reduced Motion source with a fake platform setting and a fake UI dispatcher: the value is
/// read at start, a change raised on any thread is marshalled to the UI dispatcher BEFORE it is applied, Changed fires
/// only for a real change, the QA pin (--qa-reduced-motion) wins over the system, and the template gate follows it.
/// </summary>
public sealed class ReducedMotionSourceTests
{
    private sealed class FakeSettings : IAnimationSettings
    {
        public bool AnimationsEnabled { get; set; } = true;

        public int Subscribers => AnimationsEnabledChanged?.GetInvocationList().Length ?? 0;

        public event EventHandler? AnimationsEnabledChanged;

        public void Raise() => AnimationsEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeDispatcher
    {
        public Queue<Action> Pending { get; } = new();

        public bool Post(Action action)
        {
            Pending.Enqueue(action);
            return true;
        }

        public void RunAll()
        {
            while (Pending.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReadsTheSystemSettingAtStart(bool animationsEnabled, bool reduced)
    {
        var source = new SystemReducedMotionSource(new FakeSettings { AnimationsEnabled = animationsEnabled }, new FakeDispatcher().Post, forceReduced: false);

        Assert.Equal(reduced, source.IsReduced);
    }

    [Fact]
    public void AChange_IsAppliedOnlyOnTheUiDispatcher_ThenRaisedOnce()
    {
        var settings = new FakeSettings();
        var dispatcher = new FakeDispatcher();
        var source = new SystemReducedMotionSource(settings, dispatcher.Post, forceReduced: false);
        var raised = 0;
        source.Changed += (_, _) => raised++;

        settings.AnimationsEnabled = false;
        settings.Raise(); // the worker-thread notification

        Assert.False(source.IsReduced); // nothing applied off the UI thread
        Assert.Equal(0, raised);
        Assert.Single(dispatcher.Pending);

        dispatcher.RunAll();

        Assert.True(source.IsReduced);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ANotificationWithoutARealChange_DoesNotRaise()
    {
        var settings = new FakeSettings();
        var dispatcher = new FakeDispatcher();
        var source = new SystemReducedMotionSource(settings, dispatcher.Post, forceReduced: false);
        var raised = 0;
        source.Changed += (_, _) => raised++;

        settings.Raise();
        settings.Raise();
        dispatcher.RunAll();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void TurningAnimationsBackOn_RaisesAgain()
    {
        var settings = new FakeSettings { AnimationsEnabled = false };
        var dispatcher = new FakeDispatcher();
        var source = new SystemReducedMotionSource(settings, dispatcher.Post, forceReduced: false);
        var observed = new List<bool>();
        source.Changed += (_, _) => observed.Add(source.IsReduced);

        settings.AnimationsEnabled = true;
        settings.Raise();
        dispatcher.RunAll();
        settings.AnimationsEnabled = false;
        settings.Raise();
        dispatcher.RunAll();

        Assert.Equal([false, true], observed);
    }

    [Fact]
    public void TheQaPin_WinsOverTheSystemSetting()
    {
        var settings = new FakeSettings { AnimationsEnabled = true };
        var dispatcher = new FakeDispatcher();
        var source = new SystemReducedMotionSource(settings, dispatcher.Post, forceReduced: true);
        var raised = 0;
        source.Changed += (_, _) => raised++;

        settings.Raise();
        dispatcher.RunAll();

        Assert.True(source.IsReduced);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void TheGalleryOverride_RaisesLikeALiveChange_AndClears()
    {
        var source = new SystemReducedMotionSource(new FakeSettings(), new FakeDispatcher().Post, forceReduced: false);
        var observed = new List<bool>();
        source.Changed += (_, _) => observed.Add(source.IsReduced);

        source.SetOverride(true);
        source.SetOverride(null);

        Assert.Equal([true, false], observed);
    }

    [Fact]
    public void Dispose_Unsubscribes_AndIgnoresLateNotifications()
    {
        var settings = new FakeSettings();
        var dispatcher = new FakeDispatcher();
        var source = new SystemReducedMotionSource(settings, dispatcher.Post, forceReduced: false);
        settings.AnimationsEnabled = false;
        settings.Raise(); // queued before the dispose

        source.Dispose();
        dispatcher.RunAll();

        Assert.Equal(0, settings.Subscribers);
        Assert.False(source.IsReduced);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void TheTemplateGate_RunsTransitionsOnlyWhenRequestedAndNotReduced(bool requested, bool reduced, bool expected)
    {
        Assert.Equal(expected, MotionVisualStateManager.UseTransitions(requested, reduced));
    }

    [Fact]
    public void TheHighlightFalloff_MatchesTheCrescentItReplaces()
    {
        // The XAML BorderThickness="0,1,0,0" crescent thins as (1 - y/r)^2 down the corner arc and is 0 on the sides.
        var stops = IndicatorHighlight.Falloff;

        Assert.Equal((0f, 1f), stops[0]);
        Assert.Equal((1f, 0f), stops[^1]);
        foreach (var (offset, alpha) in stops)
        {
            Assert.Equal(Math.Pow(1 - offset, 2), alpha, precision: 4);
        }
    }

    private sealed class DisposableSource : IReducedMotionSource, IDisposable
    {
        public bool Disposed { get; private set; }

        public bool IsReduced => false;

        public event EventHandler? Changed { add { } remove { } }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Cortex R-4: installing a new source releases the one it replaces (never twice the same one).</summary>
    [Fact]
    public void Install_DisposesTheSourceItReplaces()
    {
        var first = new DisposableSource();
        var second = new DisposableSource();

        MotionPolicy.Install(first);
        MotionPolicy.Install(first);
        Assert.False(first.Disposed);
        MotionPolicy.Install(second);

        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.Same(second, MotionPolicy.Source);
    }
}
