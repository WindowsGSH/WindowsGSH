namespace WindowsGSH.Services;

/// <summary>
/// The single place that applies WPF-UI's theme/accent resources, so App.xaml.cs's startup path
/// and MainWindow.Settings.cs's user-triggered theme switch can never diverge on how the app's
/// "Light"/"Dark" setting maps to WPF-UI's own ApplicationTheme enum or which Apply(...) overload
/// gets called. Must run before any WPF-UI control is constructed anywhere in the process -
/// App.xaml.cs calls this before constructing MainWindow specifically so a future migration of
/// MainWindow itself (or any control loaded during its XAML parsing) doesn't reintroduce the
/// SymbolIcon resource-construction failure this pairing was found to fix during the WPF-UI
/// migration.
/// </summary>
internal static class WpfUiThemeSync
{
    private static System.Windows.ResourceDictionary? _themeColorsDictionary;

    public static void Apply(string themeSettingValue)
    {
        var theme = MapTheme(themeSettingValue);

        // updateAccent must be true - see MainWindow.Settings.cs's ApplyTheme for the full
        // reasoning (WPF-UI's own controls, e.g. ToggleSwitch/ProgressRing/Appearance="Primary"
        // buttons, would depend on the SystemAccentColorPrimary resource family this is meant to
        // populate; this app's own PrimaryBrush is confirmed untouched either way).
        //
        // KNOWN GAP (confirmed via harness + ilspycmd decompile): this does NOT actually populate
        // Application.Current.Resources today.
        // WPF-UI's ApplicationAccentColorManager writes through an internal, [ThreadStatic]-cached
        // Wpf.Ui.UiApplication wrapper that only binds to the real Application if a
        // Source="...wpf.ui;..."-tagged dictionary was already merged at its first access - this
        // app's ThemesDictionary merge below is deliberately Source-less (a Source-tagged static
        // merge is what crashes SymbolIcon/FontIcon, see the ApplyTheme comment and App.xaml), so
        // every accent write lands in a disconnected, unread ResourceDictionary instead. No
        // currently-used control depends on those keys (confirmed: no ToggleSwitch/ProgressRing/
        // Appearance="Primary" button/rendered SymbolIcon anywhere, and the Button/CheckBox/
        // ComboBox templates this app does use reference none of the accent-family keys), so this
        // is a real but currently harmless defect - revisit before adopting any WPF-UI control
        // whose default template does reference SystemAccentColor*/AccentFillColor*.
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            theme,
            Wpf.Ui.Controls.WindowBackdropType.None,
            updateAccent: true);

        // ApplicationThemeManager.Apply(...) above does NOT actually make WPF-UI's theme-color
        // keys (e.g. ComboBoxDropDownBackground, CheckBoxBackground, WindowBackground - the
        // brush/color resources every ui:ControlsDictionary template's DynamicResource lookups
        // depend on) resolvable anywhere - confirmed by direct testing: enumerating
        // Application.Resources before/after calling it shows zero new keys added, in every
        // configuration tried. Those keys only ever live in WPF-UI's own <ui:ThemesDictionary>,
        // which nothing in this app has merged anywhere since Sub-chunk 7 removed the static
        // App.xaml declaration (to fix the SymbolIcon/FontIcon crash - see below). The practical
        // effect: every already-migrated window's ComboBox/CheckBox/Popup surfaces silently
        // render with fully transparent backgrounds (DynamicResource simply finds nothing),
        // which is invisible in a harness that never actually LOOKS at the rendered window, but
        // is a real, visually-confirmed defect in the real running app (backup preview icon bug's
        // sibling: a resource-resolution gap that never throws, so builds/tests can't catch it).
        //
        // Fix: merge a real Wpf.Ui.Markup.ThemesDictionary into Application.Resources ourselves,
        // via code only - never via static XAML. That distinction matters: a *static* XAML
        // <ui:ThemesDictionary> merged in App.xaml, present before ApplicationThemeManager.Apply()
        // ever runs, is the exact configuration Sub-chunk 7 found breaks SymbolIcon/FontIcon's
        // constructor (throws ArgumentException("'' is not a valid value for property
        // 'FontSize'")) - confirmed still true today, reproduced identically in isolation.
        // Merging the same dictionary via code, with no prior static declaration, does not
        // reproduce that crash in any configuration tested (global Application.Resources merge,
        // or a window's own local Resources merge - both verified safe); only the "static XAML
        // present from the start" shape is the actual problem.
        //
        // Add the replacement dictionary *before* removing the previous one (not the reverse) -
        // on a live theme switch (MainWindow.Settings.cs's ApplyTheme, not just startup), any
        // currently-open window using ui:ControlsDictionary is still resolving DynamicResource
        // lookups against Application.Resources for as long as this method is running. Removing
        // the old dictionary first leaves a real, if brief, gap where those keys resolve to
        // nothing - the exact class of resource-gap defect (WindowBackground, icon sizes,
        // ComboBox brushes) this whole mechanism exists to prevent, just self-inflicted and
        // momentary instead of permanent.
        var app = System.Windows.Application.Current;
        if (app != null)
        {
            var previous = _themeColorsDictionary;
            var replacement = new Wpf.Ui.Markup.ThemesDictionary { Theme = theme };

            app.Resources.MergedDictionaries.Add(replacement);
            if (previous != null)
            {
                app.Resources.MergedDictionaries.Remove(previous);
            }

            _themeColorsDictionary = replacement;
        }
    }

    /// <summary>
    /// Pure mapping from the app's own "Light"/"Dark" setting string to WPF-UI's
    /// <see cref="Wpf.Ui.Appearance.ApplicationTheme"/> enum, extracted so this specific piece is
    /// directly unit-testable without needing a live WPF <see cref="System.Windows.Application"/>
    /// (which <see cref="Apply"/> itself does, and which a shared test process can only safely
    /// construct once per run - see the accompanying test file for why the rest of this class
    /// isn't covered by an automated regression test the same way).
    /// </summary>
    internal static Wpf.Ui.Appearance.ApplicationTheme MapTheme(string themeSettingValue)
    {
        return string.Equals(themeSettingValue, "Light", StringComparison.OrdinalIgnoreCase)
            ? Wpf.Ui.Appearance.ApplicationTheme.Light
            : Wpf.Ui.Appearance.ApplicationTheme.Dark;
    }
}
