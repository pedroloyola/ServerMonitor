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
/// <b>THEME-1 (persistence).</b> The saved choice is read from <see cref="IThemePreferenceStore"/> in the
/// constructor, so <see cref="Current"/> is already the restored value before ANY root can be attached: every
/// root (MainWindow, and the native tray menu that reads <see cref="Current"/>) gets it from its first frame, on
/// a normal and on a headless <c>--background</c> start alike. A missing or unusable record restores System.
/// This service never writes the record - only an explicit choice in Settings does
/// (<c>SettingsViewModel.SelectedThemeIndex</c>). High Contrast is not a preference: Windows applies it over
/// <c>RequestedTheme</c>, nothing on that path calls <see cref="Apply"/>, and when it ends the saved choice is
/// what the roots still carry.
/// </para>
/// </summary>
public sealed class ThemeService(ILogger<ThemeService> logger, IThemePreferenceStore preferenceStore) : IThemeService
{
    private readonly ThemeRootSet _roots = new();

    public AppThemePreference Current { get; private set; } = Restore(preferenceStore, logger);

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

    /// <summary>
    /// The startup value: the saved choice, or System. Fail-safe by contract of the store, and again here so that a
    /// store that breaks it (an undefined value, an unexpected exception) cannot crash startup or render garbage.
    /// </summary>
    internal static AppThemePreference Restore(IThemePreferenceStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);

        AppThemePreference saved;
        try
        {
            saved = store.Read();
        }
        catch (Exception exception)
        {
            logger.LogWarning("The saved theme could not be read ({Type}); the theme follows Windows.", exception.GetType().Name);
            return AppThemePreference.System;
        }

        return Enum.IsDefined(saved) ? saved : AppThemePreference.System;
    }

    /// <summary>
    /// THEME-1: what <see cref="Attach"/> gives a root right now - the restored preference from the moment the service
    /// exists, before any root or any <see cref="Apply"/>. The seam the restore-before-attach tests pin without a XAML
    /// runtime: <see cref="ApplyTo"/> uses nothing else.
    /// </summary>
    internal ElementTheme ThemeForNewRoot => ResolveElementTheme(Current);

    private void ApplyTo(FrameworkElement root) => root.RequestedTheme = ThemeForNewRoot;
}
