using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WindowsGSH.Core.Api;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;
using WindowsGSH.Services;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace WindowsGSH;

public partial class ApiActionsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly InstalledServer _server;
    private readonly IGameServerModule _module;
    private readonly IModuleApiActionCapability _api;
    private readonly ServerInstance _instance;
    private readonly ModuleApiConnectionDefinition _connection;
    private readonly ModuleApiActionClient _client = new();
    private readonly Dictionary<string, FrameworkElement> _parameterControls = [];

    public ApiActionsWindow(InstalledServer server, IGameServerModule module, ServerInstance instance)
    {
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        _server = server;
        _module = module;
        _api = module as IModuleApiActionCapability
            ?? throw new InvalidOperationException("This module does not define API actions.");
        _instance = instance;
        _connection = _api.GetApiConnection() ?? throw new InvalidOperationException("This module does not define an API connection.");

        Title = $"{server.Name} API";
        ApiActionsTitleBar.Title = Title;
        TitleTextBlock.Text = $"{server.Name} API";
        SubtitleTextBlock.Text = $"{module.Name} module actions";
        ActionsListBox.ItemsSource = _api.GetApiActions();
        ActionsListBox.SelectedIndex = _api.GetApiActions().Count > 0 ? 0 : -1;
        RunButton.IsEnabled = ActionsListBox.SelectedItem != null;
    }

    private ModuleApiActionDefinition? SelectedAction => ActionsListBox.SelectedItem as ModuleApiActionDefinition;

    private void ActionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderSelectedAction();
    }

    private void RenderSelectedAction()
    {
        _parameterControls.Clear();
        ParametersPanel.Children.Clear();
        var action = SelectedAction;
        if (action == null)
        {
            ActionTitleTextBlock.Text = string.Empty;
            ActionDescriptionTextBlock.Text = string.Empty;
            RunButton.IsEnabled = false;
            return;
        }

        ActionTitleTextBlock.Text = $"{action.Label}  {action.Method} {action.Path}";
        ActionDescriptionTextBlock.Text = action.Description ?? string.Empty;
        foreach (var parameter in action.Parameters)
        {
            AddParameterRow(parameter);
        }

        RunButton.IsEnabled = true;
    }

    private void AddParameterRow(ConfigFieldDefinition parameter)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = parameter.Required ? parameter.Label + " *" : parameter.Label,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        };
        label.SetResourceReference(ForegroundProperty, "TextBrush");
        grid.Children.Add(label);

        var control = CreateParameterControl(parameter);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        _parameterControls[parameter.Key] = control;
        ParametersPanel.Children.Add(grid);
    }

    private FrameworkElement CreateParameterControl(ConfigFieldDefinition parameter)
    {
        if (parameter.Type == ConfigFieldType.Boolean)
        {
            var checkBox = new WpfCheckBox
            {
                IsChecked = parameter.DefaultValue is bool boolean && boolean,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("AppCheckBox")
            };
            return checkBox;
        }

        if (parameter.Type == ConfigFieldType.Select && parameter.Options is { Count: > 0 } options)
        {
            return new WpfComboBox
            {
                ItemsSource = options,
                SelectedItem = parameter.DefaultValue?.ToString() ?? options[0],
                Height = 32
            };
        }

        return new WpfTextBox
        {
            Text = parameter.DefaultValue?.ToString() ?? string.Empty,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        var action = SelectedAction;
        if (action == null)
        {
            return;
        }

        if (!TryGetParameters(action, out var parameters, out var error))
        {
            StatusTextBlock.Text = error;
            return;
        }

        if (action.Destructive)
        {
            var message = string.IsNullOrWhiteSpace(action.ConfirmMessage)
                ? $"Run {action.Label}?"
                : action.ConfirmMessage;
            if (WpfMessageBox.Show(this, message, action.Label, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }
        }

        RunButton.IsEnabled = false;
        StatusTextBlock.Text = $"Running {action.Label}...";
        OutputTextBox.Text = string.Empty;
        try
        {
            if (_module != null && !ServerProcessLocator.IsRunning(_module, _server.InstallPath))
            {
                throw new InvalidOperationException("Server process is offline. Start the server before running API actions.");
            }

            ServerConsoleService.Add(_server.Id, $"> api {action.Key}");
            var result = await _client.ExecuteAsync(_instance, _connection, action, parameters, CancellationToken.None);
            OutputTextBox.Text = result.Body;
            StatusTextBlock.Text = $"{action.Label} completed ({(int)result.StatusCode}).";
            ServerConsoleService.Add(_server.Id, $"API {action.Label} completed ({(int)result.StatusCode}).");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"{action.Label} failed: {ex.Message}";
            OutputTextBox.Text = ex.Message;
            ServerConsoleService.Add(_server.Id, $"API {action.Label} failed: {ex.Message}");
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private bool TryGetParameters(
        ModuleApiActionDefinition action,
        out IReadOnlyDictionary<string, object?> parameters,
        out string error)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in action.Parameters)
        {
            var value = GetControlValue(parameter);
            if (parameter.Required && string.IsNullOrWhiteSpace(value?.ToString()))
            {
                parameters = values;
                error = $"{parameter.Label} is required.";
                return false;
            }

            values[parameter.Key] = value;
        }

        parameters = values;
        error = string.Empty;
        return true;
    }

    private object? GetControlValue(ConfigFieldDefinition parameter)
    {
        if (!_parameterControls.TryGetValue(parameter.Key, out var control))
        {
            return parameter.DefaultValue;
        }

        return control switch
        {
            WpfCheckBox checkBox => checkBox.IsChecked == true,
            WpfComboBox comboBox => comboBox.SelectedItem?.ToString() ?? string.Empty,
            WpfTextBox textBox when parameter.Type is ConfigFieldType.Number or ConfigFieldType.Port =>
                double.TryParse(textBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : textBox.Text.Trim(),
            WpfTextBox textBox => textBox.Text.Trim(),
            _ => parameter.DefaultValue
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
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
