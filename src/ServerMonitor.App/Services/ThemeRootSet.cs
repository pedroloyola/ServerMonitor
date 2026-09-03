namespace ServerMonitor.App.Services;

/// <summary>
/// The set of XAML roots that follow the theme preference.
/// <para>
/// Split out of <see cref="ThemeService"/> for one reason: it makes the Prism HIGH provable. The defect
/// being guarded against is that attaching a second root <b>replaces</b> the first, so the Dashboard
/// silently stops following the preference the moment the tray flyout exists. Expressed as a
/// <c>FrameworkElement</c> field that could only be exercised with a live XAML runtime, that behaviour
/// is untestable; expressed here over plain objects, "add, never replace" is one assertion.
/// </para>
/// </summary>
internal sealed class ThemeRootSet
{
    private readonly List<object> _roots = [];
    private readonly object _sync = new();

    internal int Count
    {
        get { lock (_sync) { return _roots.Count; } }
    }

    /// <summary>Adds a root. Idempotent by reference identity: attaching twice must not apply twice.</summary>
    /// <returns>True when the root was not already present.</returns>
    internal bool Add(object root)
    {
        ArgumentNullException.ThrowIfNull(root);

        lock (_sync)
        {
            if (_roots.Contains(root))
            {
                return false;
            }

            _roots.Add(root);
            return true;
        }
    }

    /// <returns>True when the root was present and has been removed.</returns>
    internal bool Remove(object root)
    {
        ArgumentNullException.ThrowIfNull(root);

        lock (_sync)
        {
            return _roots.Remove(root);
        }
    }

    /// <summary>
    /// A snapshot to iterate. Taken under the lock so applying a theme to every root cannot race an
    /// attach — a window created mid-apply either gets the new theme from <c>Attach</c> or from this
    /// pass, never neither.
    /// </summary>
    internal object[] Snapshot()
    {
        lock (_sync)
        {
            return [.. _roots];
        }
    }
}
