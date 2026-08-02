namespace WindowsGSH;

public partial class MainWindow
{
    private bool _themeSelectorReady;

    private void ThemeSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_themeSelectorReady)
        {
            return;
        }

        ApplySelectedTheme();
    }

    private void ApplySelectedTheme()
    {
        var selected = (ThemeSelector.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Dark";
        _settings.Theme = selected;
        try
        {
            _settings.Save();
        }
        catch (System.IO.IOException)
        {
            // Non-fatal: losing the theme preference on this write is acceptable.
            // This path is hit when a crash-restart spawns the new instance before the
            // previous one has released the settings file lock.
        }

        switch (selected)
        {
            case "Dark":
                ApplyDarkTheme();
                break;
            case "Light":
                ApplyLightTheme();
                break;
        }
    }

    private void DismissFirstRunChecklistButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _settings.FirstRunChecklistDismissed = true;
        _settings.Save();
        UpdateFirstRunChecklist();
    }

    private void UpdateFirstRunChecklist()
    {
        var shouldShow = _serversViewIsEmpty &&
                         !_settings.FirstRunChecklistDismissed;
        FirstRunChecklistBorder.Visibility = shouldShow
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        if (!shouldShow)
        {
            return;
        }

        FirstRunChecklistItemsControl.ItemsSource = _firstRunChecklistComposer.Compose(
            _latestReadinessResults,
            _settings.DiscordBotEnabled,
            _discordTokenStore.HasToken);
    }

    private void ApplyLightTheme()
    {
        ApplyTheme(AppTheme.Light);
    }

    private void ApplyDarkTheme()
    {
        ApplyTheme(AppTheme.Dark);
    }

    private void ApplyTheme(AppTheme theme)
    {
        // Applies WPF-UI's own theme resources - App.xaml.cs's OnStartup calls the same
        // WpfUiThemeSync.Apply for the app's very first launch, before MainWindow (or any WPF-UI
        // control) is constructed at all, since that timing matters and this method only ever
        // runs after MainWindow already exists. Both call sites share this one helper so they
        // cannot diverge on the theme-name-to-ApplicationTheme mapping or the Apply(...)
        // arguments - see WpfUiThemeSync's own doc comment for why updateAccent is passed as true
        // and for a known, currently-harmless gap in what it actually accomplishes today. This
        // app's own PrimaryBrush, set by SetBrush below, is confirmed untouched either way.
        //
        // Previously also called ControlzEx.Theming.ThemeManager.Current.ChangeTheme(...) here to
        // swap MahApps' own theme-color dictionary (Light.Teal/Dark.Blue) - removed as dead code
        // once Tier 4's last window (ServerConfigEditorWindow, Sub-chunk 18) migrated off
        // MetroWindow: confirmed via grep that no XAML in the app resolves any MahApps.Brushes.*
        // key via DynamicResource anymore (the SetBrush("MahApps.Brushes...", ...) calls below were
        // removed for the same reason), so swapping MahApps' theme dictionary had no observable
        // effect left to preserve. The MahApps.Metro package reference and App.xaml's own merged
        // Controls.xaml/Fonts.xaml/Light.Teal.xaml dictionaries have since been removed too (Tier 4,
        // following the WPF-UI theme-engine migration.
        Services.WpfUiThemeSync.Apply(theme == AppTheme.Dark ? "Dark" : "Light");

        if (theme == AppTheme.Light)
        {
            ApplyLightPalette();
        }
        else
        {
            ApplyDarkPalette();
        }

    }

    private void ApplyLightPalette()
    {
        SetBrush("TitleBarBrush", "#45B2DC");
        SetWindowChromeBrushes("#45B2DC", "#45B2DC");
        SetBrush("TopBarBrush", "#45B2DC");
        SetBrush("TopBarIconBrush", "#38A8D3");
        SetBrush("WorkspaceBrush", "#F3F5F8");
        SetBrush("SidebarBrush", "#FBFCFE");
        SetBrush("PanelBrush", "#FFFFFF");
        SetBrush("PanelAltBrush", "#F5F7FA");
        SetBrush("BorderBrushSoft", "#D8DEE8");
        SetBrush("TextBrush", "#17202A");
        SetBrush("MutedTextBrush", "#697586");
        SetBrush("NavSelectedBrush", "#EAF3FF");
        SetBrush("NavHoverBrush", "#D7ECFF");
        SetBrush("TopBarTextBrush", "#FFFFFF");
        SetBrush("PrimaryBrush", "#2F7BDF");
        SetBrush("ActionBrush", "#334155");
        SetBrush("NormalMetricBackground", "#F5F7FA");
        SetBrush("NormalMetricBorder", "#E0E6EF");
        SetBrush("NormalMetricText", "#17202A");
        SetBrush("GoodMetricBackground", "#ECFFF2");
        SetBrush("GoodMetricBorder", "#BCE6C8");
        SetBrush("GoodMetricText", "#176C32");
        SetBrush("BadMetricBackground", "#FFF3F3");
        SetBrush("BadMetricBorder", "#F3C9CC");
        SetBrush("BadMetricText", "#9F1D26");
        SetBrush("WarnMetricBackground", "#FFF9EA");
        SetBrush("WarnMetricBorder", "#F0DBA5");
        SetBrush("WarnMetricText", "#7A5309");
    }

    private void ApplyDarkPalette()
    {
        SetBrush("TitleBarBrush", "#0E2442");
        SetWindowChromeBrushes("#0E2442", "#0E2442");
        SetBrush("TopBarBrush", "#0E2442");
        SetBrush("TopBarIconBrush", "#143256");
        SetBrush("WorkspaceBrush", "#101218");
        SetBrush("SidebarBrush", "#151821");
        SetBrush("PanelBrush", "#1B1F2A");
        SetBrush("PanelAltBrush", "#202635");
        SetBrush("BorderBrushSoft", "#2C3444");
        SetBrush("TextBrush", "#F2F5FA");
        SetBrush("MutedTextBrush", "#A3ADBC");
        SetBrush("NavSelectedBrush", "#202B44");
        SetBrush("NavHoverBrush", "#2B3A5E");
        SetBrush("TopBarTextBrush", "#FFFFFF");
        SetBrush("PrimaryBrush", "#57B8E6");
        SetBrush("ActionBrush", "#33415F");
        SetBrush("NormalMetricBackground", "#242B38");
        SetBrush("NormalMetricBorder", "#344154");
        SetBrush("NormalMetricText", "#F2F5FA");
        SetBrush("GoodMetricBackground", "#173023");
        SetBrush("GoodMetricBorder", "#2F6F48");
        SetBrush("GoodMetricText", "#A9F0C0");
        SetBrush("BadMetricBackground", "#3A2028");
        SetBrush("BadMetricBorder", "#743340");
        SetBrush("BadMetricText", "#FFADB6");
        SetBrush("WarnMetricBackground", "#342A16");
        SetBrush("WarnMetricBorder", "#6E5726");
        SetBrush("WarnMetricText", "#FFD98A");
    }

    private void SetBrush(string key, string color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        Resources[key] = brush;
        System.Windows.Application.Current.Resources[key] = brush;
    }

    private void SetWindowChromeBrushes(string activeColor, string inactiveColor)
    {
        SetBrush("WindowChromeBrush", activeColor);
        SetBrush("WindowChromeInactiveBrush", inactiveColor);
        // GlowBrush/NonActiveGlowBrush (MahApps' MetroWindow window-glow effect) and the four
        // MahApps.Brushes.Border.* keys were removed here - confirmed via grep that none of the
        // five were ever referenced by any XAML/code in this app (GlowBrush/NonActiveGlowBrush
        // were apparently never wired into the implicit MetroWindow style or any window's own XAML
        // either, dead even before this migration; the Border.* keys stopped mattering once the
        // last MetroWindow migrated away in Sub-chunk 18). See ApplyTheme's own comment for the
        // matching MahApps.Brushes.Accent/AccentBase removal.
    }

    private enum AppTheme
    {
        Light,
        Dark
    }
}
