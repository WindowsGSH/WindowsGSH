using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;
using WpfMessageBox = System.Windows.MessageBox;

namespace WindowsGSH;

public partial class ExistingServerImportWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ModuleRegistry _moduleRegistry = new();
    private readonly ExistingServerImportService _importService = new();
    private readonly WindowsGsmServerImportService _windowsGsmImportService = new();
    private readonly WindowsFirewallService _firewallService = new();
    private ExistingServerImportPreview? _preview;
    private WindowsGsmServerImportPreview? _windowsGsmPreview;
    private bool _isImporting;
    private bool _isScanning;
    private bool _allowCloseAfterImport;
    private int _scanVersion;
    private int _activeScanVersion;

    public ObservableCollection<ExistingImportRowViewModel> ImportRows { get; } = [];

    private IGameServerModule? SelectedModule => ModuleComboBox.SelectedItem as IGameServerModule;

    public ExistingServerImportWindow()
    {
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        DataContext = this;
        var modules = _moduleRegistry.GetModules()
            .ToArray();
        ModuleComboBox.ItemsSource = modules;
        ModuleComboBox.IsEnabled = modules.Length > 0;
        if (modules.Length > 0)
        {
            ModuleComboBox.SelectedIndex = 0;
        }
        else
        {
            StatusTextBlock.Text = "No modules installed. Import a module before importing an existing server.";
        }

        RefreshActionState();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select an existing dedicated server folder",
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

        var scanVersion = ++_scanVersion;
        _activeScanVersion = scanVersion;
        var sourcePath = ServerFolderTextBox.Text.Trim();
        var useWindowsGsmImport = IsWindowsGsmServerFolder(sourcePath);
        _isScanning = true;
        SetScanControlsEnabled(false);
        StatusTextBlock.Text = "Scanning existing server...";
        ImportRows.Clear();
        _preview = null;
        _windowsGsmPreview = null;
        CreateFirewallRulesCheckBox.Visibility = Visibility.Collapsed;
        RefreshActionState();

        try
        {
            if (useWindowsGsmImport)
            {
                var preview = await _windowsGsmImportService.PreviewAsync(module, sourcePath);
                if (!IsCurrentScan(scanVersion, module, sourcePath))
                {
                    return;
                }

                _windowsGsmPreview = preview;
                foreach (var row in preview.Rows)
                {
                    AddRow(new ExistingImportRowViewModel(row));
                }

                CreateFirewallRulesCheckBox.Visibility = Visibility.Visible;
                WarningsTextBlock.Text = string.Join(Environment.NewLine, preview.Warnings);
                WarningsBorder.Visibility = preview.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                StatusTextBlock.Text = $"Detected WindowsGSM server. Loaded {ImportRows.Count} fields from {Path.GetFileName(preview.SourceServerFolder)}.";
                FooterTextBlock.Text = preview.SourceGame.Length > 0
                    ? $"WindowsGSM game: {preview.SourceGame}"
                    : "Review imported values before continuing.";
            }
            else
            {
                var preview = await _importService.PreviewAsync(module, sourcePath);
                if (!IsCurrentScan(scanVersion, module, sourcePath))
                {
                    return;
                }

                _preview = preview;
                foreach (var row in preview.Rows)
                {
                    AddRow(new ExistingImportRowViewModel(row));
                }

                WarningsTextBlock.Text = string.Join(Environment.NewLine, preview.Warnings);
                WarningsBorder.Visibility = preview.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                StatusTextBlock.Text = $"Loaded {ImportRows.Count} fields from {preview.SourceName}.";
                FooterTextBlock.Text = "Review imported values before continuing.";
            }
        }
        catch (Exception ex)
        {
            if (!IsCurrentScan(scanVersion, module, sourcePath))
            {
                return;
            }

            StatusTextBlock.Text = "Import scan failed.";
            WpfMessageBox.Show(this, ex.Message, "Existing Server Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (_activeScanVersion == scanVersion)
            {
                _isScanning = false;
                SetScanControlsEnabled(true);
            }

            RefreshActionState();
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var message = _windowsGsmPreview != null
            ? "Copy mode copies the WindowsGSM serverfiles folder into WindowsGSH-managed storage. The original WindowsGSM server folder will be left unchanged. Continue?"
            : "Copy mode copies the existing server files into WindowsGSH-managed storage. The original folder will be left unchanged. Continue?";
        var result = WpfMessageBox.Show(
            this,
            message,
            "Copy Existing Server",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.OK)
        {
            _ = ImportAsync(copy: true);
        }
    }

    private void AdoptButton_Click(object sender, RoutedEventArgs e)
    {
        var message = _windowsGsmPreview != null
            ? "Adopt mode leaves server files in the WindowsGSM folder and points WindowsGSH at them. WindowsGSH will write its own ServerConfig.json, but both apps would be able to touch the same server files. Continue?"
            : "Adopt mode leaves server files in their current folder and points WindowsGSH at them. Continue?";
        var result = WpfMessageBox.Show(
            this,
            message,
            "Adopt Existing Server",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.OK)
        {
            _ = ImportAsync(copy: false);
        }
    }

    private async Task ImportAsync(bool copy)
    {
        var module = SelectedModule;
        if (module == null || (_preview == null && _windowsGsmPreview == null))
        {
            return;
        }

        var fieldValues = ImportRows
            .Select(row => new ExistingServerImportFieldValue(row.Key, row.Value))
            .ToArray();
        var previousCursor = Cursor;
        WindowsGsmImportProgressWindow? progressWindow = null;
        _isImporting = true;
        _allowCloseAfterImport = false;
        SetImportControlsEnabled(false);

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            if (copy)
            {
                progressWindow = new WindowsGsmImportProgressWindow
                {
                    Owner = this
                };
                progressWindow.Show();
            }

            var result = _windowsGsmPreview != null
                ? await ImportWindowsGsmAsync(module, fieldValues, copy, progressWindow)
                : await ImportGenericExistingAsync(module, fieldValues, copy, progressWindow);

            progressWindow?.MarkComplete("Copy complete.");
            var firewallMessage = _windowsGsmPreview != null
                ? CreateFirewallRulesIfRequested(module, result)
                : string.Empty;
            StatusTextBlock.Text = string.IsNullOrWhiteSpace(firewallMessage)
                ? $"Imported {result.ServerName}."
                : $"Imported {result.ServerName}. {firewallMessage}";
            progressWindow?.Close();
            WpfMessageBox.Show(
                this,
                string.IsNullOrWhiteSpace(firewallMessage)
                    ? $"Imported {result.ServerName} as server {result.ServerId}."
                    : $"Imported {result.ServerName} as server {result.ServerId}.{Environment.NewLine}{Environment.NewLine}{firewallMessage}",
                "Existing Server Import Complete",
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
            WpfMessageBox.Show(this, ex.Message, "Existing Server Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            ClearPreviewAfterFailedImport();
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
        BrowseButton.IsEnabled = enabled;
        ScanButton.IsEnabled = enabled;
        CopyButton.IsEnabled = enabled;
        AdoptButton.IsEnabled = enabled;
        FieldsDataGrid.IsEnabled = enabled;
        CreateFirewallRulesCheckBox.IsEnabled = enabled;
    }

    private void ClearPreviewAfterFailedImport()
    {
        _scanVersion++;
        _preview = null;
        _windowsGsmPreview = null;
        ImportRows.Clear();
        WarningsBorder.Visibility = Visibility.Collapsed;
        CreateFirewallRulesCheckBox.Visibility = Visibility.Collapsed;
        FooterTextBlock.Text = "Import failed. Scan the folder again before retrying.";
        StatusTextBlock.Text = "Import failed. Rescan required.";
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
        _scanVersion++;
        _preview = null;
        _windowsGsmPreview = null;
        ImportRows.Clear();
        WarningsBorder.Visibility = Visibility.Collapsed;
        CreateFirewallRulesCheckBox.Visibility = Visibility.Collapsed;
        FooterTextBlock.Text = string.Empty;
        RefreshActionState();
    }

    private void RefreshActionState()
    {
        foreach (var row in ImportRows)
        {
            row.RefreshStatus();
        }

        var hasPreview = _preview != null || _windowsGsmPreview != null;
        var ready = hasPreview &&
            ImportRows.Count > 0 &&
            ImportRows.All(row => row.Status is not ExistingServerImportFieldStatus.Missing and not ExistingServerImportFieldStatus.Review);
        CopyButton.IsEnabled = ready;
        AdoptButton.IsEnabled = ready;
        ScanButton.IsEnabled = !_isScanning && SelectedModule != null && Directory.Exists(ServerFolderTextBox.Text.Trim());

        if (!hasPreview)
        {
            CopyButton.IsEnabled = false;
            AdoptButton.IsEnabled = false;
        }

        var missing = ImportRows.Count(row => row.Status == ExistingServerImportFieldStatus.Missing);
        var review = ImportRows.Count(row => row.Status == ExistingServerImportFieldStatus.Review);
        if (hasPreview)
        {
            var imported = ImportRows.Count(row => row.Status == ExistingServerImportFieldStatus.Imported);
            var defaulted = ImportRows.Count(row => row.Status == ExistingServerImportFieldStatus.Defaulted);
            var summary = $"Imported: {imported} | Defaulted: {defaulted} | Missing: {missing} | Needs Review: {review}";
            FooterTextBlock.Text = missing > 0 || review > 0
                ? $"{summary}. Resolve missing and review fields before continuing."
                : $"{summary}. Ready to copy or adopt.";
        }
    }

    private void AddRow(ExistingImportRowViewModel viewModel)
    {
        viewModel.PropertyChanged += (_, _) => RefreshActionState();
        ImportRows.Add(viewModel);
    }

    private void SetScanControlsEnabled(bool enabled)
    {
        ModuleComboBox.IsEnabled = enabled && ModuleComboBox.Items.Count > 0;
        ServerFolderTextBox.IsEnabled = enabled;
        BrowseButton.IsEnabled = enabled;
        ScanButton.IsEnabled = enabled;
    }

    private bool IsCurrentScan(int scanVersion, IGameServerModule module, string sourcePath)
    {
        return scanVersion == _scanVersion &&
            ReferenceEquals(module, SelectedModule) &&
            string.Equals(sourcePath, ServerFolderTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<UnifiedImportResult> ImportGenericExistingAsync(
        IGameServerModule module,
        IReadOnlyList<ExistingServerImportFieldValue> fieldValues,
        bool copy,
        WindowsGsmImportProgressWindow? progressWindow)
    {
        var preview = _preview ?? throw new InvalidOperationException("Scan an existing server before importing.");
        IProgress<ExistingServerImportProgress>? progress = progressWindow == null
            ? null
            : new Progress<ExistingServerImportProgress>(update =>
                progressWindow.UpdateProgress(new WindowsGsmServerImportProgress(
                    update.Phase,
                    update.CurrentPath,
                    update.FilesCopied,
                    update.TotalFiles,
                    update.BytesCopied,
                    update.TotalBytes)));
        var mode = copy ? ExistingServerImportMode.Copy : ExistingServerImportMode.Adopt;
        var result = await Task.Run(() => _importService.Import(module, preview, fieldValues, mode, progress));
        return new UnifiedImportResult(result.ServerId, result.ServerName, result.ServerFolder, result.InstallPath, result.ConfigPath, result.Settings);
    }

    private async Task<UnifiedImportResult> ImportWindowsGsmAsync(
        IGameServerModule module,
        IReadOnlyList<ExistingServerImportFieldValue> fieldValues,
        bool copy,
        WindowsGsmImportProgressWindow? progressWindow)
    {
        var preview = _windowsGsmPreview ?? throw new InvalidOperationException("Scan a WindowsGSM server before importing.");
        var windowsGsmValues = fieldValues
            .Select(row => new WindowsGsmServerImportFieldValue(row.Key, row.Value))
            .ToArray();
        IProgress<WindowsGsmServerImportProgress>? progress = progressWindow == null
            ? null
            : new Progress<WindowsGsmServerImportProgress>(progressWindow.UpdateProgress);
        var mode = copy ? WindowsGsmServerImportMode.Copy : WindowsGsmServerImportMode.Adopt;
        var result = await Task.Run(() => _windowsGsmImportService.Import(module, preview, windowsGsmValues, mode, progress));
        return new UnifiedImportResult(result.ServerId, result.ServerName, result.ServerFolder, result.InstallPath, result.ConfigPath, result.Settings);
    }

    private string CreateFirewallRulesIfRequested(IGameServerModule module, UnifiedImportResult result)
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

    private static bool IsWindowsGsmServerFolder(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        var serverFolder = Path.GetFullPath(sourcePath);
        return File.Exists(Path.Combine(serverFolder, "configs", "WindowsGSM.cfg")) &&
               Directory.Exists(Path.Combine(serverFolder, "serverfiles"));
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isImporting)
        {
            WpfMessageBox.Show(this, "Import is still running. Please wait for it to finish.", "Import In Progress", MessageBoxButton.OK, MessageBoxImage.Information);
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
        WpfMessageBox.Show(this, "Import is still running. Please wait for it to finish.", "Import In Progress", MessageBoxButton.OK, MessageBoxImage.Information);
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

public sealed class ExistingImportRowViewModel : INotifyPropertyChanged
{
    private string _value;
    private ExistingServerImportFieldStatus _status;

    public ExistingImportRowViewModel(ExistingServerImportRow row)
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

    public ExistingImportRowViewModel(WindowsGsmServerImportRow row)
    {
        Key = row.Key;
        Label = row.Label;
        Type = row.Type;
        Required = row.Required;
        _value = row.Value;
        Source = row.Source;
        _status = row.Status switch
        {
            WindowsGsmImportFieldStatus.Missing => ExistingServerImportFieldStatus.Missing,
            WindowsGsmImportFieldStatus.Review => ExistingServerImportFieldStatus.Review,
            WindowsGsmImportFieldStatus.Defaulted => ExistingServerImportFieldStatus.Defaulted,
            _ => ExistingServerImportFieldStatus.Imported
        };
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

    public ExistingServerImportFieldStatus Status
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

    public string StatusDisplay => Status == ExistingServerImportFieldStatus.Review ? "Needs Review" : Status.ToString();

    public void RefreshStatus()
    {
        if (Required && string.IsNullOrWhiteSpace(Value))
        {
            Status = ExistingServerImportFieldStatus.Missing;
            return;
        }

        if (!string.IsNullOrWhiteSpace(Value) &&
            string.Equals(Type, "Boolean", StringComparison.OrdinalIgnoreCase) &&
            !ImportFieldValueConverter.IsValidBooleanText(Value))
        {
            Status = ExistingServerImportFieldStatus.Review;
            return;
        }

        if (!string.IsNullOrWhiteSpace(Value) &&
            (string.Equals(Type, "Number", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Type, "Port", StringComparison.OrdinalIgnoreCase)) &&
            !double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            Status = ExistingServerImportFieldStatus.Review;
            return;
        }

        if (!string.IsNullOrWhiteSpace(Value) &&
            string.Equals(Type, "Port", StringComparison.OrdinalIgnoreCase) &&
            (!int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535))
        {
            Status = ExistingServerImportFieldStatus.Review;
            return;
        }

        if (Status is ExistingServerImportFieldStatus.Missing or ExistingServerImportFieldStatus.Review)
        {
            Status = ExistingServerImportFieldStatus.Imported;
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record UnifiedImportResult(
    string ServerId,
    string ServerName,
    string ServerFolder,
    string InstallPath,
    string ConfigPath,
    IReadOnlyDictionary<string, object?> Settings);
