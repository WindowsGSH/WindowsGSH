using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;

namespace WindowsGSH;

public partial class ServerCrashSummaryWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ServerCrashSummary _summary;

    public ServerCrashSummaryWindow(ServerCrashSummary summary)
    {
        _summary = summary;
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        // Window.Title drives the taskbar/Alt+Tab text; ui:TitleBar.Title is a separate property
        // for the custom-drawn chrome text and does not read from Window.Title automatically, so
        // both need to be set together wherever the title becomes server-specific.
        Title = $"Crash Detected — {summary.ServerName}";
        CrashSummaryTitleBar.Title = Title;
        HeaderTextBlock.Text = $"{summary.ServerName} exited unexpectedly";

        ModuleTextBlock.Text = string.IsNullOrWhiteSpace(summary.ModuleName)
            ? summary.ModuleId
            : $"{summary.ModuleName} ({summary.ModuleId})";

        DetectedAtTextBlock.Text = summary.DetectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        ExitCodeTextBlock.Text = summary.ExitCode?.ToString() ?? "unknown";

        if (!string.IsNullOrWhiteSpace(summary.ReportPath))
        {
            ReportPathTextBlock.Text = summary.ReportPath;
            ReportPathTextBlock.ToolTip = summary.ReportPath;
        }
        else
        {
            ReportPathTextBlock.Text = "(not written)";
        }

        LogExcerptTextBlock.Text = !string.IsNullOrWhiteSpace(summary.RecentLogExcerpt)
            ? summary.RecentLogExcerpt
            : "(no recent log output captured)";

        OpenReportButton.IsEnabled = !string.IsNullOrWhiteSpace(summary.ReportPath) &&
            File.Exists(summary.ReportPath);
    }

    private void OpenReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_summary.ReportPath) || !File.Exists(_summary.ReportPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_summary.ReportPath) { UseShellExecute = true });
            ClearError();
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_summary.ReportPath}\"")
                {
                    UseShellExecute = true
                });
                ClearError();
            }
            catch (Exception ex)
            {
                ShowError($"Could not open the report file: {ex.Message}");
            }
        }
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var redacted = SupportBundleService.RedactText(BuildDiagnosticsText());
            System.Windows.Clipboard.SetText(redacted);
            ClearError();
        }
        catch (Exception ex)
        {
            ShowError($"Could not copy to clipboard: {ex.Message}");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorTextBlock.Visibility = Visibility.Collapsed;
        ErrorTextBlock.Text = string.Empty;
    }

    private string BuildDiagnosticsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("WindowsGSH Crash Summary");
        sb.AppendLine("========================");
        sb.AppendLine($"Server:       {_summary.ServerName}");
        sb.AppendLine($"Module:       {_summary.ModuleName} ({_summary.ModuleId})");
        sb.AppendLine($"Detected:     {_summary.DetectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Exit code:    {_summary.ExitCode?.ToString() ?? "unknown"}");
        sb.AppendLine($"Report:       {_summary.ReportPath ?? "(not written)"}");
        sb.AppendLine($"Server folder:{_summary.ServerFolder ?? "(unknown)"}");

        if (!string.IsNullOrWhiteSpace(_summary.RecentLogExcerpt))
        {
            sb.AppendLine();
            sb.AppendLine("Recent log:");
            sb.AppendLine(_summary.RecentLogExcerpt);
        }

        return sb.ToString();
    }
}
