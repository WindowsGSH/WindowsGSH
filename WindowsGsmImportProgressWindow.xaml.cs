using System.ComponentModel;
using System.Windows;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;

namespace WindowsGSH;

public partial class WindowsGsmImportProgressWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool _canClose;

    public WindowsGsmImportProgressWindow()
    {
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
    }

    public void UpdateProgress(WindowsGsmServerImportProgress progress)
    {
        PhaseTextBlock.Text = progress.Phase;
        CopyProgressBar.Value = progress.Percent;
        DetailTextBlock.Text = $"{progress.FilesCopied:N0} / {progress.TotalFiles:N0} files copied, {FormatBytes(progress.BytesCopied)} / {FormatBytes(progress.TotalBytes)}";
        CurrentFileTextBlock.Text = string.IsNullOrWhiteSpace(progress.CurrentPath)
            ? string.Empty
            : progress.CurrentPath;
    }

    public void MarkComplete(string message)
    {
        _canClose = true;
        PhaseTextBlock.Text = message;
        CopyProgressBar.Value = 100;
    }

    public void AllowClose()
    {
        _canClose = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_canClose)
        {
            e.Cancel = true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} {units[unit]}"
            : $"{value:N1} {units[unit]}";
    }
}
