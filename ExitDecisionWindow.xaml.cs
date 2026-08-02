using System.Windows;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;

namespace WindowsGSH;

public partial class ExitDecisionWindow : Wpf.Ui.Controls.FluentWindow
{
    public ExitDecisionWindow(int runningServerCount, int activeOperationCount)
    {
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;

        // Mica is deliberately still not enabled here, regardless of SupportsMica.
        // MainWindow.Settings.cs's ApplyTheme now does keep the WPF-UI theme resources synced
        // with the user's actual theme selection, via Services/WpfUiThemeSync.cs (which calls
        // Wpf.Ui.Appearance.ApplicationThemeManager.Apply and merges a matching
        // Wpf.Ui.Markup.ThemesDictionary into Application.Resources by code - App.xaml itself
        // declares no static ui:ThemesDictionary and must not; see WpfUiThemeSync.cs for why),
        // so the specific dark-backdrop-in-light-mode risk this comment used to describe is
        // resolved. What's still unverified is simpler: nobody has visually confirmed this
        // window's current (Mica-off) appearance yet, in either theme, and turning Mica on in the
        // same pass as a still-unverified theme-sync change would stack two unseen visual changes
        // together. Re-enable once both are separately confirmed to look right.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        var serverText = runningServerCount switch
        {
            0 => "No managed game servers are currently detected as running",
            1 => "One managed game server is still running",
            _ => $"{runningServerCount} managed game servers are still running"
        };
        var operationText = activeOperationCount switch
        {
            0 => "no WindowsGSH operations are active",
            1 => "one WindowsGSH operation is active and will be cancelled before exit",
            _ => $"{activeOperationCount} WindowsGSH operations are active and will be cancelled before exit"
        };
        SummaryTextBlock.Text = $"{serverText}, and {operationText}. Choose exactly what WindowsGSH should do.";
    }

    public ApplicationExitChoice Choice { get; private set; } = ApplicationExitChoice.Cancel;

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = ApplicationExitChoice.Cancel;
        DialogResult = false;
    }

    private void LeaveRunningButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = ApplicationExitChoice.LeaveServersRunningAndExit;
        DialogResult = true;
    }

    private void StopServersButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = ApplicationExitChoice.StopServersAndExit;
        DialogResult = true;
    }
}
