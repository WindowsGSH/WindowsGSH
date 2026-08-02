using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Api;
using WindowsGSH.Core.Web.Auth;
using WindowsGSH.Data;
using WindowsGSH.Core.Windows;
using WindowsGSH.Services;

namespace WindowsGSH;

public partial class App : System.Windows.Application
{
    private static int _fatalRecoveryStarted;
    private int _activationRequested;
    private SingleInstanceCoordinator? _singleInstance;
    private MainWindow? _mainWindow;
    private TrayIconService? _trayIcon;
    private WebHostService? _webHostService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        _singleInstance = new SingleInstanceCoordinator(
            $"{AppPaths.AppDirectory}|{Environment.UserName}",
            () => Dispatcher.BeginInvoke(ActivateMainWindow));
        if (!_singleInstance.IsPrimaryInstance)
        {
            _singleInstance.SignalPrimaryInstance();
            Shutdown(0);
            return;
        }

        ApplySoftwareRenderingIfRequired();

        try
        {
            EnsureRuntimeFolders();
            AppDatabase.Initialize();
            ModuleLog.Sink = AppLogService.Add;
            WebLog.Sink = AppLogService.Add;
            OperationHistoryLog.Sink = operation => OperationHistoryRepository.Add(operation);
            WebServerState.SetLifecycleQuery(serverId =>
            {
                var times = OperationHistoryRepository.GetServerLifecycleTimes(serverId);
                return (times.LastStarted, times.LastStopped);
            });
            ReconcileStartupRegistration();
        }
        catch (Exception ex)
        {
            HandleFatalException(ex, "Startup exception", canShowMessage: true);
            throw;
        }

        // Migrate the DPAPI-protected JWT signing key from the old settings JSON to its
        // dedicated file before MainWindow is constructed. MainWindow's constructor saves
        // settings, and [JsonIgnore] on JwtSigningKeyProtected means that save would silently
        // drop the legacy field before JwtKeyStore.Load() has a chance to read it.
        JwtKeyStore.MigrateIfNeeded();

        // Must run before constructing MainWindow (or any other window/control), not just before
        // showing it - WPF-UI's own controls need their theme/accent resources to already exist
        // the moment their XAML is parsed and their templates are first instantiated during
        // InitializeComponent(), not merely before the window becomes visible. Harmless today
        // since MainWindow.xaml has no WPF-UI controls yet, but load-bearing the moment it (or
        // any control constructed during its XAML loading) does.
        WpfUiThemeSync.Apply(AppSettings.Load().Theme);

        SessionEnding += App_SessionEnding;
        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        var router = new DesktopCommandRouter(
            () => _mainWindow.RestoreAndActivate(),
            () => _mainWindow.OpenDiscordSettingsFromTray(),
            () => _mainWindow.StopAllServersFromTrayAsync(),
            () => _mainWindow.RequestSafeExitFromTray());
        try
        {
            _trayIcon = new TrayIconService(Dispatcher, router);
        }
        catch (Exception ex)
        {
            AppLogService.Add("Windows tray integration could not be started: " + ex.Message);
        }

        _mainWindow.SetTrayAvailability(_trayIcon != null);
        _mainWindow.Show();
        _mainWindow.ApplyInitialDesktopState();
        ShowLocalStateRecoveryWarning();
        _ = CheckDatabaseIntegrityAsync();
        StartWebHostIfEnabled();
        if (Interlocked.Exchange(ref _activationRequested, 0) == 1)
        {
            ActivateMainWindow();
        }
    }

    private async Task CheckDatabaseIntegrityAsync()
    {
        var result = await AppDatabase.CheckIntegrityAsync();
        if (result.IsHealthy)
        {
            AppLogService.Add("SQLite integrity check passed.");
            return;
        }

        AppLogService.Add($"SQLite integrity check failed: {result.Details}");
        try
        {
            System.Windows.MessageBox.Show(
                _mainWindow,
                "WindowsGSH detected a problem while checking its local database. " +
                "The database has not been deleted or reset. See the app log and readiness checks for details.",
                "Database Integrity Warning",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        catch
        {
        }
    }

    private void ShowLocalStateRecoveryWarning()
    {
        var recovery = LocalStateRecoveryStatus.Snapshot()
            .Where(item => item.IsFailure)
            .ToArray();
        if (recovery.Length == 0)
        {
            return;
        }

        var details = string.Join(
            Environment.NewLine + Environment.NewLine,
            recovery.Select(item =>
                item.Message +
                (string.IsNullOrWhiteSpace(item.RecoveryPath)
                    ? string.Empty
                    : $"{Environment.NewLine}Recovery location: {item.RecoveryPath}")));
        System.Windows.MessageBox.Show(
            _mainWindow,
            details,
            "WindowsGSH Recovered Local State",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SessionEnding -= App_SessionEnding;
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_webHostService != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // Task.Run moves StopAsync off the WPF UI thread so its continuations can
                // complete on the thread pool. GetResult() here would otherwise deadlock
                // because the WPF SynchronizationContext would be captured and any
                // dispatcher.Invoke inside the stop task would wait for the UI thread
                // that is already blocked here.
                Task.Run(() => _webHostService.StopAsync(cts.Token)).GetAwaiter().GetResult();
                WebLog.Add("Web server stopped.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebHostService.StopAsync] {ex.Message}");
            }
            finally
            {
                _webHostService = null;
            }
        }
        try
        {
            AppLogService.FlushAndStopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppLogService.FlushAndStopAsync] {ex.Message}");
        }
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    private static void ReconcileStartupRegistration()
    {
        var recoveryCount = LocalStateRecoveryStatus.Snapshot().Count;
        var settings = AppSettings.Load();
        if (LocalStateRecoveryStatus.Snapshot()
            .Skip(recoveryCount)
            .Any(item => string.Equals(item.Area, "App settings", StringComparison.OrdinalIgnoreCase) && item.IsFailure))
        {
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        try
        {
            new StartupRegistrationService().Reconcile(settings.StartWithWindows, executablePath);
        }
        catch (Exception ex)
        {
            AppLogService.Add("Could not reconcile current-user startup registration: " + ex.Message);
        }
    }

    private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _mainWindow?.HandleWindowsSessionEnding(e.ReasonSessionEnding);
    }

    private void ActivateMainWindow()
    {
        var window = _mainWindow;
        if (window == null)
        {
            Interlocked.Exchange(ref _activationRequested, 1);
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private static void EnsureRuntimeFolders()
    {
        Directory.CreateDirectory(AppPaths.GetPath("servers"));
        Directory.CreateDirectory(AppPaths.GetPath("steamcmd"));
        Directory.CreateDirectory(AppPaths.GetPath("logs", "crashes"));
        ModuleStoragePaths.EnsureCreated();
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleFatalException(e.Exception, "WPF dispatcher exception", canShowMessage: true);
        e.Handled = false;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            HandleFatalException(exception, "Unhandled app domain exception", canShowMessage: false);
        }
        else
        {
            HandleFatalDetails(e.ExceptionObject?.ToString() ?? "Unknown fatal exception", "Unhandled app domain exception", canShowMessage: false);
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(new AppCrashLogRequest(
            "Unobserved task exception",
            IsFatal: false,
            Exception: e.Exception,
            RecentAppLog: SafeRecentAppLog()));
        e.SetObserved();
    }

    private static void HandleFatalException(Exception exception, string context, bool canShowMessage)
    {
        var result = WriteCrashLog(new AppCrashLogRequest(
            context,
            IsFatal: true,
            Exception: exception,
            RecentAppLog: SafeRecentAppLog()));
        var decision = TryPlanAndStartRecovery();
        ShowFatalMessage(result.Path, decision, canShowMessage);
    }

    private static void HandleFatalDetails(string details, string context, bool canShowMessage)
    {
        var result = WriteCrashLog(new AppCrashLogRequest(
            context,
            IsFatal: true,
            Details: details,
            RecentAppLog: SafeRecentAppLog()));
        var decision = TryPlanAndStartRecovery();
        ShowFatalMessage(result.Path, decision, canShowMessage);
    }

    private static AppCrashLogResult WriteCrashLog(AppCrashLogRequest request)
    {
        try
        {
            return new AppCrashLogService().Write(request);
        }
        catch
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, $"WindowsGSH-fatal-{DateTime.Now:yyyyMMdd-HHmmssfff}.log");
            File.WriteAllText(fallback, request.Exception?.ToString() ?? request.Details ?? request.Context);
            return new AppCrashLogResult(fallback);
        }
    }

    private static AppCrashRecoveryDecision TryPlanAndStartRecovery()
    {
        if (Interlocked.Exchange(ref _fatalRecoveryStarted, 1) == 1)
        {
            return new AppCrashRecoveryDecision(false, "Fatal recovery was already attempted.", 0);
        }

        try
        {
            var settings = AppSettings.Load();
            var decision = new AppCrashRecoveryService().RecordCrashAndPlan(new AppCrashRecoveryOptions(
                settings.RestartAppOnCrash,
                Math.Max(1, settings.AppCrashRestartMaxAttempts),
                TimeSpan.FromMinutes(Math.Max(1, settings.AppCrashRestartWindowMinutes))));
            if (decision.ShouldRestart)
            {
                ReleaseSingleInstanceForRestart();
                TryRestartProcess();
            }

            return decision;
        }
        catch (Exception ex)
        {
            return new AppCrashRecoveryDecision(false, "Crash recovery planning failed: " + ex.Message, 0);
        }
    }

    private static void TryRestartProcess()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = true
        });
    }

    private static void ReleaseSingleInstanceForRestart()
    {
        if (Current is not App app)
        {
            return;
        }

        app._singleInstance?.Dispose();
        app._singleInstance = null;
        app._trayIcon?.Dispose();
        app._trayIcon = null;
    }

    private static void ShowFatalMessage(string path, AppCrashRecoveryDecision decision, bool canShowMessage)
    {
        if (!canShowMessage)
        {
            return;
        }

        try
        {
            var restart = decision.ShouldRestart
                ? "\n\nWindowsGSH is restarting."
                : $"\n\n{decision.Message}";
            System.Windows.MessageBox.Show(
                $"WindowsGSH hit an unexpected fatal error and wrote a crash log:\n{path}{restart}",
                "WindowsGSH Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
        }
    }

    private static string SafeRecentAppLog()
    {
        try
        {
            return AppLogService.GetRecentText(160);
        }
        catch
        {
            return string.Empty;
        }
    }

    private void StartWebHostIfEnabled()
    {
        var settings = AppSettings.Load();
        if (!settings.WebEnabled)
            return;

        var signingKey = EnsureJwtSigningKey();
        var userRepo = new WebUserRepository();
        var options = new WebHostOptions(
            settings.WebPort,
            BindAddress: settings.WebBindAddress,
            TrustForwardedHeaders: settings.WebTrustForwardedHeaders,
            AllowLegacyWebSocketQueryStringAuth: settings.AllowLegacyWebSocketQueryStringAuth);
        var (configureServices, configurePipeline, tokenService) = WebHostSetup.CreateAuth(
            userRepo,
            signingKey,
            options.TrustForwardedHeaders);

        _webHostService = new WebHostService();
        _ = _webHostService.TryStartAsync(options, configureServices, configurePipeline);
        _ = EnsureDefaultAdminAndShowPasswordAsync(tokenService);
    }

    private async Task EnsureDefaultAdminAndShowPasswordAsync(WebTokenService tokenService)
    {
        var password = await tokenService.EnsureDefaultAdminAsync().ConfigureAwait(false);
        if (password is null)
            return;

        await Dispatcher.InvokeAsync(() =>
        {
            System.Windows.MessageBox.Show(
                $"WindowsGSH web first run\n\n" +
                $"A default admin account has been created.\n\n" +
                $"Username: admin\n" +
                $"Password: {password}\n\n" +
                $"Please change this password after your first login.",
                "Web First Run — Save Your Password",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        });
    }

    private static byte[] EnsureJwtSigningKey()
    {
        const string entropy = "WindowsGSH.JwtSigningKey.v1";
        var entropyBytes = System.Text.Encoding.UTF8.GetBytes(entropy);

        var stored = JwtKeyStore.Load();
        if (!string.IsNullOrEmpty(stored))
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(stored);
                return ProtectedData.Unprotect(protectedBytes, entropyBytes, DataProtectionScope.CurrentUser);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                AppLogService.Add("Web JWT signing key could not be decrypted; generating a new one. All active web sessions will be invalidated.");
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectedData.Protect(key, entropyBytes, DataProtectionScope.CurrentUser);
        JwtKeyStore.Save(Convert.ToBase64String(protectedKey));
        return key;
    }

    private static void ApplySoftwareRenderingIfRequired()
    {
        try
        {
            var settings = AppSettings.Load();
            AccessibilityVisualState.ApplyToApplication(settings);
            if (settings.SoftwareRendering)
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — logging not yet available at this startup stage, proceed with default hardware rendering.
            System.Diagnostics.Debug.WriteLine($"[ApplySoftwareRenderingIfRequired] {ex}");
        }
    }
}
