using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Windows;

namespace WindowsGSH;

public partial class ReadinessCheckWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ReadinessCheckService _readinessCheckService;
    private readonly Action<ReadinessAction>? _navigationCallback;

    // Preserves the original public constructor's own signature/metadata rather than folding the new
    // dependency into an optional parameter on it - optional arguments are resolved at compile time
    // on the caller's side, so an already-compiled consumer of this constructor's old shape would hit
    // a MissingMethodException at runtime against a version of this assembly whose only constructor
    // has a different parameter list.
    public ReadinessCheckWindow(Action<ReadinessAction>? navigationCallback = null)
        : this(new ReadinessCheckService(), navigationCallback)
    {
    }

    // Accepts an existing ReadinessCheckService so a caller that already owns one (MainWindow) can
    // share it - and, transitively, its InstalledServerLoader - across every window opened during the
    // app's lifetime, instead of each newly opened window creating its own loader with its own,
    // independent hang-protection dedup dictionary. A permanently hung module would otherwise gain a
    // fresh, independently-stuck worker every time this window is reopened. Internal rather than
    // public - MainWindow is the only real caller that has a shared instance to pass in, and it's in
    // the same assembly.
    internal ReadinessCheckWindow(ReadinessCheckService readinessCheckService, Action<ReadinessAction>? navigationCallback = null)
    {
        _readinessCheckService = readinessCheckService;
        _navigationCallback = navigationCallback;
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        Loaded += async (_, _) => await RunChecksAsync();
    }

    private async void RunAgainButton_Click(object sender, RoutedEventArgs e)
    {
        await RunChecksAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not ReadinessCheckResult result || result.Action is null)
            return;

        bool shouldClose;
        try
        {
            shouldClose = await ExecuteReadinessActionAsync(result);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Readiness action failed: {ex.Message}";
            return;
        }

        if (shouldClose)
        {
            Close();
            return;
        }

        await RunChecksAsync();
    }

    private async Task<bool> ExecuteReadinessActionAsync(ReadinessCheckResult result)
    {
        switch (result.Action)
        {
            case ReadinessAction.OpenServersFolder:
                OpenFolder(Core.AppPaths.GetPath("servers"));
                return false;

            case ReadinessAction.OpenModuleStorage:
                OpenFolder(result.ActionPath ?? Core.Modules.ModuleStoragePaths.ModuleRoot);
                return false;

            case ReadinessAction.OpenSteamCmdFolder:
                var steamDir = System.IO.Path.GetDirectoryName(new Core.Steam.SteamCmdManager().ExePath);
                if (!string.IsNullOrEmpty(steamDir))
                    OpenFolder(steamDir);
                return false;

            case ReadinessAction.OpenWindowsFirewall:
                Process.Start(new ProcessStartInfo("control.exe", "firewall.cpl") { UseShellExecute = true });
                return false;

            case ReadinessAction.OpenJavaSettings:
            case ReadinessAction.OpenModuleManagement:
                _navigationCallback?.Invoke(result.Action.Value);
                return true;

            case ReadinessAction.RetrySteamCmdSetup:
                StatusTextBlock.Text = "Retrying SteamCMD setup...";
                await new Core.Steam.SteamCmdManager()
                    .EnsureInstalledAsync(new Progress<string>(message => StatusTextBlock.Text = message));
                return false;

            default:
                return false;
        }
    }

    private static void OpenFolder(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Process.Start(CreateOpenFolderStartInfo(path));
    }

    internal static ProcessStartInfo CreateOpenFolderStartInfo(string path)
        => new("explorer.exe", $"\"{path}\"") { UseShellExecute = true };

    private async Task RunChecksAsync()
    {
        StatusTextBlock.Text = "Running checks...";
        var results = (await _readinessCheckService.RunAsync()).ToList();
        AppendRenderingModeResult(results);
        ChecksGrid.ItemsSource = results;
        StatusTextBlock.Text = "Readiness check complete.";
    }

    private static void AppendRenderingModeResult(List<ReadinessCheckResult> results)
    {
        var mode = RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly
            ? "Software rendering (forced via Accessibility settings — takes effect on launch)"
            : "Hardware rendering (GPU acceleration active)";
        results.Add(ReadinessCheckResult.Info("Rendering mode", mode));
    }
}
