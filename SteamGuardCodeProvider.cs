using WindowsGSH.Core.Steam;

namespace WindowsGSH;

/// <summary>
/// Shows a modal Steam Guard code entry dialog.
/// A static semaphore prevents two concurrent operations from opening overlapping dialogs.
/// </summary>
public sealed class SteamGuardCodeProvider(System.Windows.Window owner) : ISteamGuardCodeProvider
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<string?> RequestCodeAsync(SteamGuardChallengeRequest challenge, CancellationToken cancellationToken)
    {
        if (challenge.IsExpired)
        {
            return null;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (challenge.IsExpired)
            {
                return null;
            }

            string? code = null;
            SteamGuardCodeWindow? window = null;

            // Close the dialog if the operation is cancelled while it is open.
            // WPF's ShowDialog() runs its own message pump, so InvokeAsync callbacks
            // execute even while the dialog is modal.
            using var registration = cancellationToken.Register(() =>
                owner.Dispatcher.InvokeAsync(() => window?.CloseForOperation()));

            await owner.Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                window = new SteamGuardCodeWindow(challenge) { Owner = owner };
                if (window.ShowDialog() == true)
                {
                    code = window.EnteredCode;
                }
            });

            cancellationToken.ThrowIfCancellationRequested();
            return code;
        }
        finally
        {
            Gate.Release();
        }
    }
}
