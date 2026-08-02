using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Windows;

namespace WindowsGSH;

public partial class BulkActionsWindow : Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{
    private readonly BulkServerActionPlanner _planner = new();
    private readonly BulkServerActionExecutor _executor = new();
    private readonly Func<BulkServerAction, string, CancellationToken, Task<ServerActionExecutionOutcome>> _executeAsync;
    private readonly Action<IReadOnlyList<string>> _cancelActive;
    private readonly Action<bool> _runningStateChanged;
    private CancellationTokenSource? _cancellation;
    private BulkServerActionPlan? _currentPlan;
    private bool _running;
    private bool _canSelectServers = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    // DataGrid.IsReadOnly only prevents cell *editing* - it does not disable controls embedded
    // in a DataGridTemplateColumn's DataTemplate, so the "Use" column's CheckBox would otherwise
    // stay fully interactive while a batch is running. Bound (not just set once) so it stays
    // correct for row containers the DataGrid virtualizes/recreates after SetRunningState runs.
    public bool CanSelectServers
    {
        get => _canSelectServers;
        private set
        {
            if (_canSelectServers == value)
            {
                return;
            }

            _canSelectServers = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSelectServers)));
        }
    }

    public BulkActionsWindow(
        IReadOnlyList<BulkActionServerRow> rows,
        Func<BulkServerAction, string, CancellationToken, Task<ServerActionExecutionOutcome>> executeAsync,
        Action<IReadOnlyList<string>> cancelActive,
        Action<bool> runningStateChanged)
    {
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        Rows = new ObservableCollection<BulkActionServerRow>(rows);
        _executeAsync = executeAsync;
        _cancelActive = cancelActive;
        _runningStateChanged = runningStateChanged;
        DataContext = this;
        ActionComboBox.ItemsSource = BulkActionChoice.All;
        ActionComboBox.SelectedIndex = 0;
        RefreshPlan();
    }

    public ObservableCollection<BulkActionServerRow> Rows { get; }

    private BulkServerAction SelectedAction =>
        (ActionComboBox.SelectedItem as BulkActionChoice)?.Action ?? BulkServerAction.StartSelected;

    private void ActionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshPlan();
    }

    private void ServerSelectionCheckBox_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        RefreshPlan();
    }

    private void RefreshPlan()
    {
        if (!IsInitialized || _running)
        {
            return;
        }

        _currentPlan = _planner.Plan(SelectedAction, Rows.Select(row => row.ToCandidate()).ToArray());
        var byId = _currentPlan.Items.ToDictionary(item => item.ServerId, StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            var item = byId[row.ServerId];
            row.PlanState = item.ShouldExecute ? "Run" : "Skip";
            row.Detail = item.Reason;
        }

        PlanSummaryTextBlock.Text =
            $"{_currentPlan.Executable.Count} will run • {_currentPlan.Skipped.Count} will be skipped";
        RunButton.IsEnabled = _currentPlan.Executable.Count > 0;
        StatusTextBlock.Text = _currentPlan.RequiresConfirmation
            ? "This action changes running state or server files and will require confirmation."
            : "Review the plan, then run the action.";
    }

    private async void RunButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_running || _currentPlan == null || _currentPlan.Executable.Count == 0)
        {
            return;
        }

        if (_currentPlan.RequiresConfirmation)
        {
            var answer = System.Windows.MessageBox.Show(
                this,
                $"Run {GetActionLabel(_currentPlan.Action)} for {_currentPlan.Executable.Count} server(s)?",
                "Confirm Bulk Action",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }
        }

        SetRunningState(true);
        _cancellation = new CancellationTokenSource();
        var executableIds = _currentPlan.Executable.Select(item => item.ServerId).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Rows.Where(row => executableIds.Contains(row.ServerId)))
        {
            row.PlanState = "Queued";
            row.Detail = "Waiting to start.";
        }

        var progress = new Progress<BulkServerActionProgress>(item =>
        {
            ProgressBar.Maximum = Math.Max(1, item.Total);
            ProgressBar.Value = item.Completed;
            StatusTextBlock.Text = $"Completed {item.Completed} of {item.Total}: {item.ServerName}";
        });

        try
        {
            var action = _currentPlan.Action;
            var summary = await _executor.ExecuteAsync(
                _currentPlan,
                maximumConcurrency: 2,
                async (item, token) =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var row = Rows.First(candidate => candidate.ServerId == item.ServerId);
                        row.PlanState = "Running";
                        row.Detail = GetActionLabel(action);
                    });
                    var outcome = await _executeAsync(action, item.ServerId, token);
                    return new BulkServerActionResult(
                        item.ServerId,
                        item.ServerName,
                        MapStatus(outcome.Status),
                        outcome.Message);
                },
                progress,
                _cancellation.Token);

            foreach (var result in summary.Results)
            {
                var row = Rows.First(candidate => candidate.ServerId == result.ServerId);
                row.PlanState = result.Status.ToString();
                row.Detail = result.Message;
            }

            StatusTextBlock.Text =
                $"Finished: {summary.Succeeded} succeeded, {summary.Failed} failed, " +
                $"{summary.Cancelled} cancelled, {summary.Skipped} skipped.";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetRunningState(false);
        }
    }

    private void CancelBatchButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_running || _cancellation == null)
        {
            return;
        }

        CancelBatchButton.IsEnabled = false;
        StatusTextBlock.Text = "Cancelling queued and active bulk operations...";
        _cancellation.Cancel();
        _cancelActive(_currentPlan?.Executable.Select(item => item.ServerId).ToArray() ?? []);
    }

    private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            StatusTextBlock.Text = "Cancel the active batch before closing this window.";
            return;
        }

        base.OnClosing(e);
    }

    private void SetRunningState(bool running)
    {
        _running = running;
        _runningStateChanged(running);
        ActionComboBox.IsEnabled = !running;
        ServersGrid.IsReadOnly = running;
        CanSelectServers = !running;
        RunButton.IsEnabled = !running && _currentPlan?.Executable.Count > 0;
        CloseButton.IsEnabled = !running;
        CancelBatchButton.Visibility = running
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        CancelBatchButton.IsEnabled = running;
        ProgressBar.Visibility = running
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        if (!running)
        {
            ProgressBar.Value = 0;
        }
    }

    private static BulkServerActionResultStatus MapStatus(ServerActionExecutionStatus status) =>
        status switch
        {
            ServerActionExecutionStatus.Succeeded => BulkServerActionResultStatus.Succeeded,
            ServerActionExecutionStatus.Failed => BulkServerActionResultStatus.Failed,
            ServerActionExecutionStatus.Cancelled => BulkServerActionResultStatus.Cancelled,
            _ => BulkServerActionResultStatus.Skipped
        };

    private static string GetActionLabel(BulkServerAction action) =>
        BulkActionChoice.All.First(choice => choice.Action == action).Label;
}

public sealed class BulkActionServerRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _planState = string.Empty;
    private string _detail = string.Empty;

    public required string ServerId { get; init; }
    public required string ServerName { get; init; }
    public required string CurrentState { get; init; }
    public required bool ConfigExists { get; init; }
    public required bool CanStart { get; init; }
    public required bool CanStop { get; init; }
    public required bool IsBusy { get; init; }
    public required bool AutoStartEnabled { get; init; }
    public required bool SupportsUpdate { get; init; }
    public required bool SupportsBackups { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string PlanState
    {
        get => _planState;
        set => SetField(ref _planState, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BulkServerActionCandidate ToCandidate() =>
        new(
            ServerId,
            ServerName,
            IsSelected,
            ConfigExists,
            CanStart,
            CanStop,
            IsBusy,
            AutoStartEnabled,
            SupportsUpdate,
            SupportsBackups);

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record BulkActionChoice(BulkServerAction Action, string Label)
{
    public static IReadOnlyList<BulkActionChoice> All { get; } =
    [
        new(BulkServerAction.StartSelected, "Start selected"),
        new(BulkServerAction.StartAutoStart, "Start Auto Start servers"),
        new(BulkServerAction.StopSelected, "Stop selected"),
        new(BulkServerAction.StopAllRunning, "Stop all running"),
        new(BulkServerAction.RestartSelected, "Restart selected"),
        new(BulkServerAction.UpdateSelectedOffline, "Update selected offline"),
        new(BulkServerAction.BackupSelected, "Back up selected")
    ];
}
