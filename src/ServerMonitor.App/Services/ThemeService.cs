using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Services;

/// <summary>
/// The single source of the session's theme preference, applied to EVERY XAML root.
/// <para>
/// <b>Why a collection and not one root (Prism HIGH, M13 S2-T).</b> <c>RequestedTheme</c> is a per-root
/// property: setting it on the main window says nothing about any other window. While the app had one
/// root that distinction was invisible. The tray flyout is a second root, so a single-root service would
/// have had exactly two failure modes, both real — the flyout renders in the system theme while the
/// Dashboard renders in the chosen one, or attaching the flyout REPLACES the Dashboard's root and the
/// Dashboard silently stops following the preference from then on.
/// </para>
/// <para>
/// Scope note: this is the preference <b>within the current process</b>. Persisting it across launches is
/// THEME-1 and deliberately not implemented here.
/// </para>
/// </summary>
public sealed class ThemeService(ILogger<ThemeService> logger) : IThemeService
{
    private readonly ThemeRootSet _roots = new();

    public AppThemePreference Current { get; private set; } = AppThemePreference.System;

    /// <summary>
    /// Registers a XAML root and brings it to the current preference immediately, so a root created
    /// after the user chose a theme never renders a frame in the wrong one.
    /// </summary>
    public void Attach(FrameworkElement rootElement)
    {
        ArgumentNullException.ThrowIfNull(rootElement);

        _roots.Add(rootElement);
        ApplyTo(rootElement);
    }

    /// <summary>
    /// Unregisters a root. Needed for roots that do not live as long as the process: without it the
    /// service would keep a closed window alive and go on writing to it.
    /// </summary>
    public void Detach(FrameworkElement rootElement)
    {
        ArgumentNullException.ThrowIfNull(rootElement);

        _roots.Remove(rootElement);
    }

    public void Apply(AppThemePreference preference)
    {
        Current = preference;

        // EVERY root, not the most recent one. ThemeRootSet owns the iteration so that claim is testable.
        _roots.ForEach(root => ApplyTo((FrameworkElement)root));

        logger.LogInformation("Application theme changed to {Theme}.", preference);
    }

    /// <summary>How many roots currently follow the preference. Diagnostic, and the seam the tests pin.</summary>
    internal int AttachedRootCount => _roots.Count;

    /// <summary>
    /// The preference-to-theme mapping, as a pure function so it is decidable without a XAML runtime.
    /// </summary>
    internal static ElementTheme ResolveElementTheme(AppThemePreference preference) => preference switch
    {
        AppThemePreference.Light => ElementTheme.Light,
        AppThemePreference.Dark => ElementTheme.Dark,
        AppThemePreference.System => ElementTheme.Default
        // No `_ =>` arm: CS8509 is an error here, so a new preference cannot be added without deciding
        // what it renders as.
    };

    private void ApplyTo(FrameworkElement root) => root.RequestedTheme = ResolveElementTheme(Current);
}
