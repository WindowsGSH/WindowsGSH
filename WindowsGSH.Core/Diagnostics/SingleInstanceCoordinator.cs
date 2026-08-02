using System.Security.Cryptography;
using System.Text;

namespace WindowsGSH.Core.Diagnostics;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _marker;
    private readonly EventWaitHandle _activationEvent;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim? _listenerReady;
    private readonly Thread? _activationListener;
    private bool _disposed;

    public SingleInstanceCoordinator(
        string applicationKey,
        Action? activationRequested = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationKey);
        InstanceName = CreateInstanceName(applicationKey);
        _marker = new Mutex(initiallyOwned: false, $@"Local\{InstanceName}-Marker", out var createdNew);
        IsPrimaryInstance = createdNew;
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            $@"Local\{InstanceName}-Activate");

        if (IsPrimaryInstance && activationRequested != null)
        {
            _listenerReady = new ManualResetEventSlim();
            _activationListener = new Thread(() => ListenForActivation(
                activationRequested,
                _cancellation.Token,
                _listenerReady))
            {
                IsBackground = true,
                Name = "WindowsGSH single-instance activation listener"
            };
            _activationListener.Start();
            _listenerReady.Wait();
        }
    }

    public string InstanceName { get; }

    public bool IsPrimaryInstance { get; }

    public void SignalPrimaryInstance()
    {
        if (!IsPrimaryInstance)
        {
            _activationEvent.Set();
        }
    }

    public static string CreateInstanceName(string applicationKey)
    {
        var normalized = applicationKey.Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $"WindowsGSH-{hash[..24]}";
    }

    private void ListenForActivation(
        Action activationRequested,
        CancellationToken cancellationToken,
        ManualResetEventSlim listenerReady)
    {
        var handles = new WaitHandle[] { _activationEvent, cancellationToken.WaitHandle };
        listenerReady.Set();
        while (!cancellationToken.IsCancellationRequested)
        {
            var signalled = WaitHandle.WaitAny(handles);
            if (signalled != 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                activationRequested();
            }
            catch
            {
                // A failed activation request must not terminate the primary app.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _activationEvent.Set();
        try
        {
            _activationListener?.Join(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        _listenerReady?.Dispose();
        _activationEvent.Dispose();
        _marker.Dispose();
        _cancellation.Dispose();
    }
}
