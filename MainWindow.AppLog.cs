using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using WindowsGSH.Core.Servers;

namespace WindowsGSH;

public partial class MainWindow
{
    private readonly AppLogViewState _appLogViewState = new();
    private readonly NotifyCollectionChangedEventHandler _appLogCollectionChangedHandler;
    private bool _appLogRefreshQueued;

    private void QueueAppLogRefresh()
    {
        if (LogsView.Visibility != Visibility.Visible)
        {
            return;
        }

        if (_appLogRefreshQueued)
        {
            return;
        }

        _appLogRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _appLogRefreshQueued = false;
            if (LogsView.Visibility == Visibility.Visible)
            {
                RefreshAppLogText();
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void LogFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshAppLogText();
    }

    private void RefreshAppLogText()
    {
        var firstVisibleLine = AppLogTextBox.GetFirstVisibleLineIndex();
        var selectionStart = AppLogTextBox.SelectionStart;
        var selectionLength = AppLogTextBox.SelectionLength;
        var source = (LogFilterComboBox.SelectedItem as ServerLogFilterItem)?.Source;
        AppLogTextBox.Text = _appLogViewState.BuildVisibleText(AppLogService.Messages, source);
        if (_appLogViewState.AutoScrollEnabled)
        {
            AppLogTextBox.ScrollToEnd();
            return;
        }

        if (firstVisibleLine >= 0 && AppLogTextBox.LineCount > 0)
        {
            AppLogTextBox.ScrollToLine(Math.Min(firstVisibleLine, AppLogTextBox.LineCount - 1));
        }

        var safeSelectionStart = Math.Min(selectionStart, AppLogTextBox.Text.Length);
        var safeSelectionLength = Math.Min(selectionLength, AppLogTextBox.Text.Length - safeSelectionStart);
        AppLogTextBox.Select(safeSelectionStart, safeSelectionLength);
    }

    private void PauseLogAutoScrollButton_Click(object sender, RoutedEventArgs e)
    {
        _appLogViewState.ToggleAutoScroll();
        PauseLogAutoScrollButton.Content = _appLogViewState.AutoScrollEnabled
            ? "Pause Auto-scroll"
            : "Resume Auto-scroll";
        if (_appLogViewState.AutoScrollEnabled)
        {
            AppLogTextBox.ScrollToEnd();
        }
    }

    private void CopyVisibleLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(AppLogTextBox.Text))
            {
                System.Windows.Clipboard.SetText(AppLogTextBox.Text);
            }
        }
        catch (Exception ex)
        {
            AppLogService.Add("Could not copy visible log: " + ex.Message);
        }
    }

    private void ClearLogViewButton_Click(object sender, RoutedEventArgs e)
    {
        _appLogViewState.ClearView(AppLogService.Messages.Count);
        RefreshAppLogText();
    }

    private void RefreshLogFilters(ServerListViewState state)
    {
        var current = LogFilterComboBox.Items.OfType<ServerLogFilterItem>().ToArray();
        if (current.Length == state.LogFilters.Count &&
            current.Zip(state.LogFilters).All(pair => pair.First.Source == pair.Second.Source && pair.First.Label == pair.Second.Label))
        {
            return;
        }

        LogFilterComboBox.Items.Clear();
        foreach (var item in state.LogFilters)
        {
            LogFilterComboBox.Items.Add(item);
        }

        LogFilterComboBox.SelectedItem = LogFilterComboBox.Items
            .OfType<ServerLogFilterItem>()
            .FirstOrDefault(item => item.Source == state.SelectedLogFilter.Source) ?? LogFilterComboBox.Items[0];
    }
}
