using System.Windows;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Windows;
using WindowsGSH.Data;

namespace WindowsGSH;

public partial class OperationHistoryWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ServerOperationManager _operationManager = ServerOperationManager.Shared;
    private readonly OperationHistoryComposer _historyComposer = new();

    public OperationHistoryWindow()
    {
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        RefreshOperations();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshOperations();
    }

    private void CancelSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (OperationsGrid.SelectedItem is not ServerOperationSnapshot operation || !operation.IsActive)
        {
            StatusTextBlock.Text = "Select an active operation to cancel.";
            return;
        }

        StatusTextBlock.Text = _operationManager.Cancel(operation.ServerId)
            ? $"Cancellation requested for {operation.ServerName}."
            : "Operation is no longer active.";
        RefreshOperations();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshOperations()
    {
        OperationsGrid.ItemsSource = _historyComposer.Compose(
            _operationManager.GetActive(),
            OperationHistoryRepository.GetRecent());
        StatusTextBlock.Text = "Showing active operations and recent history.";
    }
}
