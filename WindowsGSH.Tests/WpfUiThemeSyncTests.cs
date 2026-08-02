using WindowsGSH.Services;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class WpfUiThemeSyncTests
{
    // WpfUiThemeSync.Apply itself is not covered here: it constructs/mutates the live WPF-UI
    // theme dictionary via System.Windows.Application, and WPF only allows one Application
    // instance per process for its lifetime - once created, it cannot be recreated or torn down,
    // so a shared xUnit process running 1000+ other tests cannot safely own that singleton for a
    // single regression test without risking every other test that happens to run afterward.
    // The runtime-construction scenario this class exists to prevent (Wpf.Ui.Controls.SymbolIcon
    // throwing when WPF-UI's theme/accent resources were never populated correctly) was instead
    // verified via a throwaway, isolated console
    // harness during development, not as part of this suite. Only the pure, Application-free
    // mapping logic is unit-tested below.
    [Theory]
    [InlineData("Light", Wpf.Ui.Appearance.ApplicationTheme.Light)]
    [InlineData("light", Wpf.Ui.Appearance.ApplicationTheme.Light)]
    [InlineData("LIGHT", Wpf.Ui.Appearance.ApplicationTheme.Light)]
    [InlineData("Dark", Wpf.Ui.Appearance.ApplicationTheme.Dark)]
    [InlineData("dark", Wpf.Ui.Appearance.ApplicationTheme.Dark)]
    public void MapTheme_maps_the_known_values_case_insensitively(string settingValue, Wpf.Ui.Appearance.ApplicationTheme expected)
    {
        Assert.Equal(expected, WpfUiThemeSync.MapTheme(settingValue));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Solarized")]
    [InlineData("system")]
    public void MapTheme_fails_closed_to_dark_for_anything_unrecognized(string settingValue)
    {
        // "Dark" is this app's own documented default (AppSettings.Theme's own default value),
        // so an unrecognized/corrupted setting should land on the same value a fresh install
        // would, not silently produce Light for anything that merely isn't the literal "Light".
        Assert.Equal(Wpf.Ui.Appearance.ApplicationTheme.Dark, WpfUiThemeSync.MapTheme(settingValue));
    }
}
