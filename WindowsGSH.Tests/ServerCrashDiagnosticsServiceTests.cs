using System.Diagnostics;
using System.IO;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerCrashDiagnosticsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WindowsGSH.Tests",
        Guid.NewGuid().ToString("N"));

    public ServerCrashDiagnosticsServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void BuildSummary_captures_server_name_module_and_exit_code()
    {
        var service = new ServerCrashDiagnosticsService();
        var server = CreateServer();
        var module = new FakeModule("Test Module", "test-mod", null);
        var instance = CreateInstance();
        using var process = StartExitingProcess(5);

        var summary = service.BuildSummary(server, module, instance, process, recentConsoleOutput: "", reportPath: null);

        Assert.Equal("Test Server", summary.ServerName);
        Assert.Equal("test-mod", summary.ModuleId);
        Assert.Equal("Test Module", summary.ModuleName);
        Assert.Equal(5, summary.ExitCode);
    }

    [Fact]
    public void BuildSummary_captures_report_path_and_server_folder()
    {
        var service = new ServerCrashDiagnosticsService();
        var server = CreateServer();
        var module = new FakeModule("Test", "test", null);
        var instance = CreateInstance();
        using var process = StartExitingProcess(0);

        var summary = service.BuildSummary(server, module, instance, process, "", reportPath: @"C:\CrashLogs\report.log");

        Assert.Equal(@"C:\CrashLogs\report.log", summary.ReportPath);
        Assert.Equal(_root, summary.ServerFolder);
    }

    [Fact]
    public void BuildSummary_uses_game_log_excerpt_when_available()
    {
        var logPath = Path.Combine(_root, "latest.log");
        File.WriteAllLines(logPath, ["[Info] Server started", "[Warn] Low memory", "[Error] Disconnected"]);

        var service = new ServerCrashDiagnosticsService();
        var server = CreateServer();
        var module = new FakeModule("Test", "test", logPath);
        var instance = CreateInstance();
        using var process = StartExitingProcess(0);

        var summary = service.BuildSummary(server, module, instance, process, recentConsoleOutput: "console fallback", reportPath: null);

        Assert.NotNull(summary.RecentLogExcerpt);
        Assert.Contains("[Error] Disconnected", summary.RecentLogExcerpt);
    }

    [Fact]
    public void BuildSummary_falls_back_to_console_output_when_no_log_path()
    {
        var service = new ServerCrashDiagnosticsService();
        var server = CreateServer();
        var module = new FakeModule("Test", "test", null);
        var instance = CreateInstance();
        using var process = StartExitingProcess(0);

        var summary = service.BuildSummary(server, module, instance, process, recentConsoleOutput: "console output\nsecond line", reportPath: null);

        Assert.NotNull(summary.RecentLogExcerpt);
        Assert.Contains("second line", summary.RecentLogExcerpt);
    }

    [Fact]
    public void BuildSummary_falls_back_to_console_when_log_file_is_missing()
    {
        var service = new ServerCrashDiagnosticsService();
        var server = CreateServer();
        var module = new FakeModule("Test", "test", Path.Combine(_root, "does-not-exist.log"));
        var instance = CreateInstance();
        using var process = StartExitingProcess(0);

        var summary = service.BuildSummary(server, module, instance, process, recentConsoleOutput: "fallback line", reportPath: null);

        Assert.NotNull(summary.RecentLogExcerpt);
        Assert.Contains("fallback line", summary.RecentLogExcerpt);
    }

    [Fact]
    public void BuildSummary_returns_null_excerpt_when_no_log_and_no_console_output()
    {
        var service = new ServerCrashDiagnosticsService();
        var server = CreateServer();
        var module = new FakeModule("Test", "test", null);
        var instance = CreateInstance();
        using var process = StartExitingProcess(0);

        var summary = service.BuildSummary(server, module, instance, process, recentConsoleOutput: "", reportPath: null);

        Assert.Null(summary.RecentLogExcerpt);
    }

    [Fact]
    public void BuildSummary_uses_preReadGameLog_instead_of_reading_file_again()
    {
        var logPath = Path.Combine(_root, "game.log");
        File.WriteAllLines(logPath, ["line from file"]);

        var service = new ServerCrashDiagnosticsService();
        var server = CreateServer();
        var module = new FakeModule("Test", "test", logPath);
        var instance = CreateInstance();
        using var process = StartExitingProcess(0);

        // Provide a different pre-read log — should take priority over the file on disk.
        var summary = service.BuildSummary(server, module, instance, process, recentConsoleOutput: "", reportPath: null,
            preReadGameLog: "pre-read line A\npre-read line B");

        Assert.NotNull(summary.RecentLogExcerpt);
        Assert.Contains("pre-read line B", summary.RecentLogExcerpt);
        Assert.DoesNotContain("line from file", summary.RecentLogExcerpt);
    }

    [Fact]
    public void WriteUnexpectedExitReport_uses_preReadGameLog_instead_of_reading_file_again()
    {
        var serverFolder = Path.Combine(_root, "server-preread");
        Directory.CreateDirectory(serverFolder);
        var logPath = Path.Combine(_root, "game.log");
        File.WriteAllLines(logPath, ["line from file"]);

        var service = new ServerCrashDiagnosticsService();
        var server = CreateServer(serverFolder);
        var module = new FakeModule("Test", "test", logPath);
        var instance = CreateInstance();
        using var process = StartExitingProcess(0);

        var reportPath = service.WriteUnexpectedExitReport(server, module, instance, process, commandLine: "server.exe",
            recentConsoleOutput: "", preReadGameLog: "pre-read content");

        var content = File.ReadAllText(reportPath);
        Assert.Contains("pre-read content", content);
        Assert.DoesNotContain("line from file", content);
    }

    [Fact]
    public void WriteUnexpectedExitReport_creates_crash_report_file_containing_server_info()
    {
        var serverFolder = Path.Combine(_root, "server-1");
        Directory.CreateDirectory(serverFolder);
        var service = new ServerCrashDiagnosticsService();
        var server = CreateServer(serverFolder);
        var module = new FakeModule("Test", "test", null);
        var instance = CreateInstance();
        using var process = StartExitingProcess(1);

        var reportPath = service.WriteUnexpectedExitReport(server, module, instance, process, commandLine: "server.exe", recentConsoleOutput: "startup output");

        Assert.True(File.Exists(reportPath), "Crash report file should exist.");
        var content = File.ReadAllText(reportPath);
        Assert.Contains("Test Server", content);
        Assert.Contains("startup output", content);
        Assert.StartsWith(serverFolder + Path.DirectorySeparatorChar + "CrashLogs", reportPath, StringComparison.OrdinalIgnoreCase);
    }

    private InstalledServer CreateServer(string? serverFolder = null) =>
        new InstalledServer(
            Id: "1",
            Name: "Test Server",
            ModuleId: "test",
            Runtime: "native",
            ServerFolder: serverFolder ?? _root,
            InstallPath: Path.Combine(serverFolder ?? _root, "files"),
            ConfigPath: Path.Combine(serverFolder ?? _root, "ServerConfig.json"),
            IpAddress: "",
            Port: "",
            SteamAppId: "",
            SteamBranch: "",
            MaxPlayers: "",
            ProcessId: "--",
            CpuUsage: "--",
            MemoryUsage: "--",
            PlayerCount: "--",
            CurrentStatusText: "",
            Uptime: "--",
            IsOperationRunning: false,
            OperationText: "",
            LastOperationError: null,
            IsInstalled: true,
            Status: ServerRuntimeStatus.Offline,
            StatusText: "",
            StatusBrushKey: "",
            HasUpdateAvailable: false,
            LocalBuildId: "",
            RemoteBuildId: "",
            IgnoredBuildId: "",
            CanShowInfo: false,
            CanEditConfig: false,
            CanStart: false,
            CanStop: false);

    private static ServerInstance CreateInstance() =>
        new ServerInstance(
            "server-1",
            "Test Server",
            "test",
            "",
            "",
            "",
            new Dictionary<string, object?>());

    private static Process StartExitingProcess(int exitCode)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe", $"/c exit {exitCode}")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The test exit process did not finish within five seconds.");
        }
        return process;
    }

    private sealed class FakeModule(string name, string id, string? logPath) : IGameServerModule
    {
        public string Id => id;
        public string Name => name;
        public string Version => "1.0.0";

        public ModuleCapabilities Capabilities => new(
            SupportsInstall: false,
            SupportsUpdate: false,
            SupportsQuery: false,
            SupportsRcon: false,
            SupportsConsoleCommands: false,
            SupportsApiActions: false,
            SupportsBackups: false,
            SupportsDirectConnection: false);

        public ModuleRuntimeDefinition Runtime => new("server.exe", []);

        public string? GetConsoleLogPath(ServerInstance instance) => logPath;

        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => name;

        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");

        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public bool IsInstallValid(ServerInstance instance) => true;
    }
}
