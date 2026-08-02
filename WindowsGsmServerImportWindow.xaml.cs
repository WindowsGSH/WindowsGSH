using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;
using WpfMessageBox = System.Windows.MessageBox;

namespace WindowsGSH;

public partial class WindowsGsmServerImportWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ModuleRegistry _moduleRegistry = new();
    private readonly WindowsGsmServerImportService _importService = new();
    private readonly WindowsFirewallService _firewallService = new();
    private WindowsGsmServerImportPreview? _preview;
    private bool _isImporting;
    private bool _allowCloseAfterImport;

    public ObservableCollection<ImportRowViewModel> ImportRows { get; } = [];

    private IGameServerModule? SelectedModule => ModuleComboBox.SelectedItem as IGameServerModule;

    public WindowsGsmServerImportWindow()
    {
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        DataContext = this;
        var modules = _moduleRegistry.GetModules();
        ModuleComboBox.ItemsSource = modules;
        ModuleComboBox.IsEnabled = modules.Count > 0;
        if (modules.Count > 0)
        {
            ModuleComboBox.SelectedIndex = 0;
        }
        else
        {
            StatusTextBlock.Text = "No modules installed. Import a module before importing a WindowsGSM server.";
        }

        RefreshActionState();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a WindowsGSM server folder, for example WindowsGSM\\servers\\1",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ServerFolderTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var module = SelectedModule;
        if (module == null)
        {
            StatusTextBlock.Text = "Select a module.";
            return;
        }

        ScanButton.IsEnabled = false;
        StatusTextBlock.Text = "Scanning WindowsGSM server...";
        ImportRows.Clear();
        _preview = null;
        RefreshActionState();

        try
        {
            var preview = await _importService.PreviewAsync(module, ServerFolderTextBox.Text.Trim());
            _preview = preview;
            foreach (var row in preview.Rows)
            {
                var viewModel = new ImportRowViewModel(row);
                viewModel.PropertyChanged += (_, _) => RefreshActionState();
                ImportRows.Add(viewModel);
            }

            WarningsTextBlock.Text = string.Join(Environment.NewLine, preview.Warnings);
            WarningsBorder.Visibility = preview.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusTextBlock.Text = $"Loaded {ImportRows.Count} fields from {Path.GetFileName(preview.SourceServerFolder)}.";
            FooterTextBlock.Text = preview.SourceGame.Length > 0
                ? $"WindowsGSM game: {preview.SourceGame}"
                : "Review imported values before continuing.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Import scan failed.";
            WpfMessageBox.Show(this, ex.Message, "WindowsGSM Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ScanButton.IsEnabled = true;
            RefreshActionState();
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var result = WpfMessageBox.Show(
            this,
            "Copy mode copies the WindowsGSM serverfiles folder into WindowsGSH-managed storage. The original WindowsGSM server folder will be left unchanged. Continue?",
            "Copy WindowsGSM Server",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        _ = ImportAsync(WindowsGsmServerImportMode.Copy);
    }

    private void AdoptButton_Click(object sender, RoutedEventArgs e)
    {
        var result = WpfMessageBox.Show(
            this,
            "Adopt mode leaves server files in the WindowsGSM folder and points WindowsGSH at them. WindowsGSH will write its own ServerConfig.json, but both apps would be able to touch the same server files. Continue?",
            "Adopt WindowsGSM Server",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        _ = ImportAsync(WindowsGsmServerImportMode.Adopt);
    }

    private async Task ImportAsync(WindowsGsmServerImportMode mode)
    {
        var module = SelectedModule;
        if (module == null || _preview == null)
        {
            return;
        }

        var fieldValues = ImportRows
            .Select(row => new WindowsGsmServerImportFieldValue(row.Key, row.Value))
            .ToArray();
        var previousCursor = Cursor;
        WindowsGsmImportProgressWindow? progressWindow = null;
        _isImporting = true;
        _allowCloseAfterImport = false;
        SetImportControlsEnabled(false);

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            IProgress<WindowsGsmServerImportProgress>? progress = null;
            if (mode == WindowsGsmServerImportMode.Copy)
            {
                progressWindow = new WindowsGsmImportProgressWindow
                {
                    Owner = this
                };
                progress = new Progress<WindowsGsmServerImportProgress>(progressWindow.UpdateProgress);
                progressWindow.Show();
            }

            var preview = _preview;
            var result = await Task.Run(() => _importService.Import(
                module,
                preview,
                fieldValues,
                mode,
                progress));

            progressWindow?.MarkComplete("Copy complete.");
            var firewallMessage = CreateFirewallRulesIfRequested(module, result);
            StatusTextBlock.Text = string.IsNullOrWhiteSpace(firewallMessage)
                ? $"Imported {result.ServerName}."
                : $"Imported {result.ServerName}. {firewallMessage}";
            progressWindow?.Close();
            WpfMessageBox.Show(
                this,
                string.IsNullOrWhiteSpace(firewallMessage)
                    ? $"Imported {result.ServerName} as server {result.ServerId}."
                    : $"Imported {result.ServerName} as server {result.ServerId}.{Environment.NewLine}{Environment.NewLine}{firewallMessage}",
                "WindowsGSM Import Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _allowCloseAfterImport = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            progressWindow?.AllowClose();
            progressWindow?.Close();
            WpfMessageBox.Show(this, ex.Message, "WindowsGSM Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isImporting = false;
            Cursor = previousCursor;
            SetImportControlsEnabled(true);
            RefreshActionState();
        }
    }

    private void SetImportControlsEnabled(bool enabled)
    {
        ModuleComboBox.IsEnabled = enabled && ModuleComboBox.Items.Count > 0;
        ServerFolderTextBox.IsEnabled = enabled;
        ScanButton.IsEnabled = enabled;
        CopyButton.IsEnabled = enabled;
        AdoptButton.IsEnabled = enabled;
        FieldsDataGrid.IsEnabled = enabled;
        CreateFirewallRulesCheckBox.IsEnabled = enabled;
    }

    private string CreateFirewallRulesIfRequested(IGameServerModule module, WindowsGsmServerImportResult result)
    {
        if (CreateFirewallRulesCheckBox.IsChecked != true)
        {
            return string.Empty;
        }

        if (!IsRunningAsAdministrator())
        {
            return "Firewall rules require administrator rights.";
        }

        try
        {
            var instance = new ServerInstance(
                result.ServerId,
                result.ServerName,
                module.Id,
                result.ServerFolder,
                result.InstallPath,
                result.ConfigPath,
                result.Settings);
            var created = _firewallService.CreateMissingRules(instance, module);
            return created.Count == 0
                ? "No firewall rules needed to be created."
                : $"Created {created.Count} Windows Firewall rule(s).";
        }
        catch (Exception ex)
        {
            return "Could not create Windows Firewall rules: " + FormatException(ex);
        }
    }

    private void FieldsDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(RefreshActionState));
    }

    private void FieldsDataGrid_CurrentCellChanged(object? sender, EventArgs e)
    {
        RefreshActionState();
    }

    private void Input_Changed(object sender, EventArgs e)
    {
        _preview = null;
        ImportRows.Clear();
        WarningsBorder.Visibility = Visibility.Collapsed;
        FooterTextBlock.Text = string.Empty;
        RefreshActionState();
    }

    private void RefreshActionState()
    {
        foreach (var row in ImportRows)
        {
            row.RefreshStatus();
        }

        var ready = _preview != null &&
            ImportRows.Count > 0 &&
            ImportRows.All(row => row.Status is not WindowsGsmImportFieldStatus.Missing and not WindowsGsmImportFieldStatus.Review);
        CopyButton.IsEnabled = ready;
        AdoptButton.IsEnabled = ready;
        ScanButton.IsEnabled = SelectedModule != null && Directory.Exists(ServerFolderTextBox.Text.Trim());

        if (_preview == null)
        {
            CopyButton.IsEnabled = false;
            AdoptButton.IsEnabled = false;
        }

        var missing = ImportRows.Count(row => row.Status == WindowsGsmImportFieldStatus.Missing);
        var review = ImportRows.Count(row => row.Status == WindowsGsmImportFieldStatus.Review);
        if (_preview != null)
        {
            var imported = ImportRows.Count(row => row.Status == WindowsGsmImportFieldStatus.Imported);
            var defaulted = ImportRows.Count(row => row.Status == WindowsGsmImportFieldStatus.Defaulted);
            var summary = $"Imported: {imported} | Defaulted: {defaulted} | Missing: {missing} | Needs Review: {review}";
            FooterTextBlock.Text = missing > 0 || review > 0
                ? $"{summary}. Resolve missing and review fields before continuing."
                : $"{summary}. Ready to copy or adopt.";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isImporting)
        {
            WpfMessageBox.Show(
                this,
                "WindowsGSM import is still running. Please wait for the import to finish.",
                "Import In Progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isImporting || _allowCloseAfterImport)
        {
            return;
        }

        e.Cancel = true;
        WpfMessageBox.Show(
            this,
            "WindowsGSM import is still running. Please wait for the import to finish.",
            "Import In Progress",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string FormatException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void ContentGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || WpfInteractionHelper.IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

}

public sealed class ImportRowViewModel : INotifyPropertyChanged
{
    private string _value;
    private WindowsGsmImportFieldStatus _status;

    public ImportRowViewModel(WindowsGsmServerImportRow row)
    {
        Key = row.Key;
        Label = row.Label;
        Type = row.Type;
        Required = row.Required;
        _value = row.Value;
        Source = row.Source;
        _status = row.Status;
        RefreshStatus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string Label { get; }

    public string Type { get; }

    public bool Required { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            OnPropertyChanged(nameof(Value));
            RefreshStatus();
        }
    }

    public string Source { get; }

    public WindowsGsmImportFieldStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    public string StatusDisplay => Status == WindowsGsmImportFieldStatus.Review ? "Needs Review" : Status.ToString();

    public void RefreshStatus()
    {
        if (Required && string.IsNullOrWhiteSpace(Value))
        {
            Status = WindowsGsmImportFieldStatus.Missing;
            return;
        }

        if (!string.IsNullOrWhiteSpace(Value) &&
            (string.Equals(Type, "Number", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Type, "Port", StringComparison.OrdinalIgnoreCase)) &&
            !double.TryParse(Value, out _))
        {
            Status = WindowsGsmImportFieldStatus.Review;
            return;
        }

        if (!string.IsNullOrWhiteSpace(Value) &&
            string.Equals(Type, "Port", StringComparison.OrdinalIgnoreCase) &&
            (!int.TryParse(Value, out var port) || port is < 1 or > 65535))
        {
            Status = WindowsGsmImportFieldStatus.Review;
            return;
        }

        if (Status is WindowsGsmImportFieldStatus.Missing or WindowsGsmImportFieldStatus.Review)
        {
            Status = WindowsGsmImportFieldStatus.Imported;
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
