using System.ComponentModel;
using System.Windows.Threading;
using WindowsGSH.Core.Steam;
using WindowsGSH.Core.Windows;

namespace WindowsGSH;

public partial class SteamGuardCodeWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTimeOffset _expiresAt;
    private bool _closingForOperation;

    public string? EnteredCode { get; private set; }
    public bool UserCancelled { get; private set; }

    public SteamGuardCodeWindow(SteamGuardChallengeRequest challenge)
    {
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        _expiresAt = challenge.DetectedAt + SteamGuardChallengeRequest.Lifetime;

        DescriptionTextBlock.Text = challenge.Kind switch
        {
            SteamGuardChallengeKind.Email =>
                "A Steam Guard code has been sent to your email. Enter it below, or this window closes automatically once Steam authenticates.",
            SteamGuardChallengeKind.MobileAuthenticator =>
                "Enter the code from your Steam authenticator app, or approve the push notification on your phone — this window closes automatically.",
            _ =>
                "SteamCMD needs a Steam Guard code. Enter it below, or approve on your phone — this window closes automatically when done."
        };

        AccountTextBlock.Text = string.IsNullOrWhiteSpace(challenge.SteamAccount)
            ? string.Empty
            : $"Account: {challenge.SteamAccount}";

        _countdownTimer.Tick += CountdownTimer_Tick;
        _countdownTimer.Start();
        UpdateStatus();
        CodeTextBox.Focus();
        Closed += (_, _) => _countdownTimer.Stop();
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        var remaining = _expiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _countdownTimer.Stop();
            // Code window expired — disable code entry but keep window open.
            // SteamCMD may be waiting for push auth; processExitCts will close this
            // window automatically when authentication completes.
            CodeTextBox.IsEnabled = false;
            SubmitButton.IsEnabled = false;
            StatusTextBlock.Text = "Authenticator code expired. Approve on your phone, or click Cancel update to abort.";
            return;
        }

        StatusTextBlock.Text = $"Authenticator code expires in {(int)remaining.TotalSeconds}s";
    }

    private void SubmitButton_Click(object sender, System.Windows.RoutedEventArgs e) => Submit();

    private void CancelButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        UserCancelled = true;
        _countdownTimer.Stop();
        DialogResult = false;
        Close();
    }

    public void CloseForOperation()
    {
        _closingForOperation = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ShouldTreatCloseAsUserCancellation(_closingForOperation, DialogResult == true))
        {
            // Treat the title-bar close button and Alt+F4 exactly like Cancel update.
            UserCancelled = true;
        }

        base.OnClosing(e);
    }

    internal static bool ShouldTreatCloseAsUserCancellation(bool closingForOperation, bool submitted)
    {
        return !closingForOperation && !submitted;
    }

    private void CodeTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            Submit();
        }
    }

    private void Submit()
    {
        var code = CodeTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        _countdownTimer.Stop();
        EnteredCode = code;
        CodeTextBox.Clear();
        DialogResult = true;
        Close();
    }
}
