using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.Health;
using WindowsGSH.Core.Metrics;
using WindowsGSH.Core.Servers;

namespace WindowsGSH;

public partial class HostHealthView : System.Windows.Controls.UserControl
{
    private const int ChartWidth = 900;
    private const int ChartHeight = 80;
    private const int ChartDisplaySamples = 900;
    private readonly StableItemCollection<string> _warnings = new();
    private readonly StableItemCollection<ServerResourceRow> _serverResources = new();

    private SystemMetricsSnapshot? _lastSnapshot;
    private IReadOnlyList<InstalledServer> _lastServers = [];
    private string _lastPublicIp = "";
    private bool _storageScanRunning;
    private bool _portScanRunning;

    public HostHealthView()
    {
        InitializeComponent();
        WarningsItemsControl.ItemsSource = _warnings.Items;
        ServerResourcesItemsControl.ItemsSource = _serverResources.Items;
        LoadStaticInfo();
    }

    public void UpdateMetrics(SystemMetricsSnapshot snapshot, IReadOnlyList<SystemMetricsSnapshot> history)
    {
        _lastSnapshot = snapshot;
        SummaryCpuTextBlock.Text = snapshot.TotalCpuText;
        SummaryCpuDetailTextBlock.Text =
            $"{snapshot.LogicalProcessorCount} logical processor(s) · {snapshot.CpuModel}";
        SummaryMemoryTextBlock.Text = snapshot.MemoryText;
        SummaryDriveTextBlock.Text = snapshot.DriveText;
        SummaryDriveDetailTextBlock.Text =
            $"{snapshot.DriveRoot} · {snapshot.DriveFormat} · {snapshot.DriveUsageText}";
        SummaryAppTextBlock.Text = snapshot.ProcessText;
        UpdatedTextBlock.Text = snapshot.Error == null
            ? $"Updated {snapshot.SampledAt.ToLocalTime():HH:mm:ss}"
            : $"Unavailable: {snapshot.Error}";
        CpuChartValueTextBlock.Text = snapshot.TotalCpuText;
        MemoryChartValueTextBlock.Text = snapshot.MemoryText;
        AppResourcesTextBlock.Text = snapshot.ProcessText;
        AppUptimeTextBlock.Text = FormatAppUptime();
        SystemUptimeTextBlock.Text = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));
        StorageDriveFreeTextBlock.Text = FormatBytes(snapshot.DriveFreeBytes);
        StorageDriveTotalTextBlock.Text = FormatBytes(snapshot.DriveTotalBytes);

        DownloadChartValueTextBlock.Text = snapshot.DownloadText;
        UploadChartValueTextBlock.Text = snapshot.UploadText;

        UpdateCpuChart(history);
        UpdateMemoryChart(snapshot, history);
        UpdateBandwidthChart(DownloadChartLine, DownloadChartMaxLabel, history, s => s.DownloadBytesPerSecond);
        UpdateBandwidthChart(UploadChartLine, UploadChartMaxLabel, history, s => s.UploadBytesPerSecond);
        UpdateWarnings(snapshot);
    }

    public void RefreshContext(IReadOnlyList<InstalledServer> servers, string lastPublicIp)
    {
        _lastServers = servers;
        _lastPublicIp = lastPublicIp;
        AppLastLogTextBlock.Text = AppLogService.Messages.Count > 0
            ? AppLogService.Messages[AppLogService.Messages.Count - 1]
            : "—";
        PublicIpTextBlock.Text = string.IsNullOrWhiteSpace(lastPublicIp) ? "Not yet checked" : lastPublicIp;
        NatWarningTextBlock.Text = DetectNatWarning(lastPublicIp);
        UpdateWarnings(_lastSnapshot);
        UpdateServerResourcesTable(servers);
    }

    private void UpdateServerResourcesTable(IReadOnlyList<InstalledServer> servers)
    {
        if (servers.Count == 0)
        {
            _serverResources.Update([]);
            NoServersTextBlock.Visibility = Visibility.Visible;
            return;
        }

        var rows = servers
            .Select(s => new ServerResourceRow(
                s.Name,
                s.CurrentStatusText,
                s.CpuUsage,
                s.MemoryUsage,
                s.Uptime,
                s.Status == ServerRuntimeStatus.Running,
                ParseMemoryBytes(s.MemoryUsage)))
            .OrderByDescending(r => r.IsRunning)
            .ThenByDescending(r => r.MemorySortBytes)
            .ToList();

        _serverResources.Update(rows);
        NoServersTextBlock.Visibility = Visibility.Collapsed;
    }

    private void LoadStaticInfo()
    {
        AppVersionTextBlock.Text = AppVersionInfo.DisplayVersion;
        AppInstallPathTextBlock.Text = TruncatePath(AppPaths.AppDirectory);
        AppServersFolderTextBlock.Text = TruncatePath(AppPaths.GetPath("servers"));
        AppLogsFolderTextBlock.Text = TruncatePath(AppPaths.GetPath("logs"));
        OsVersionTextBlock.Text = ReadOsDisplayVersion();
        AdminStatusTextBlock.Text = IsRunningAsAdmin() ? "Yes" : "No";
        SleepStatusTextBlock.Text = ReadSleepStatus();
        LongPathTextBlock.Text = ReadLongPathSupport();
        PendingRebootTextBlock.Text = CheckPendingReboot() ? "Yes — reboot recommended" : "No";
        LocalIpTextBlock.Text = GetLocalIpV4();
        StorageDriveTypeTextBlock.Text = ReadDriveType(AppPaths.AppDirectory);
    }

    private void UpdateWarnings(SystemMetricsSnapshot? snapshot)
    {
        var warnings = new List<string>();

        if (snapshot != null)
        {
            if (snapshot.DriveFreeBytes.HasValue && snapshot.DriveFreeBytes.Value < 20L * 1024 * 1024 * 1024)
            {
                warnings.Add($"⚠ App drive is low on space ({FormatBytes(snapshot.DriveFreeBytes)} free)");
            }

            if (snapshot.TotalMemoryBytes.HasValue && snapshot.UsedMemoryBytes.HasValue &&
                snapshot.TotalMemoryBytes.Value > 0)
            {
                var usedPercent = snapshot.UsedMemoryBytes.Value / (double)snapshot.TotalMemoryBytes.Value * 100;
                if (usedPercent > 85)
                {
                    warnings.Add($"⚠ Memory usage is high ({usedPercent:F0}%)");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_lastPublicIp) && IsPrivateIp(_lastPublicIp))
        {
            warnings.Add("⚠ Public IP appears to be private — possible CGNAT or double-NAT");
        }

        if (CheckPendingReboot())
        {
            warnings.Add("⚠ System has a pending reboot");
        }

        _warnings.Update(warnings);
        WarningsBorder.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCpuChart(IReadOnlyList<SystemMetricsSnapshot> history)
    {
        UpdateChart(CpuChartLine, history, s => s.TotalCpuPercent, maxValue: 100.0);
    }

    private static void UpdateBandwidthChart(
        Polyline chartLine,
        TextBlock maxLabel,
        IReadOnlyList<SystemMetricsSnapshot> history,
        Func<SystemMetricsSnapshot, double?> valueSelector)
    {
        if (history.Count == 0)
        {
            chartLine.Points = null;
            maxLabel.Text = "";
            return;
        }

        var recent = history.Count <= ChartDisplaySamples
            ? history
            : history.Skip(history.Count - ChartDisplaySamples).ToArray();

        var peak = recent
            .Select(s => valueSelector(s))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .DefaultIfEmpty(0)
            .Max();

        var maxValue = peak > 0 ? peak : 1.0;
        maxLabel.Text = FormatBandwidth(peak);

        UpdateChart(chartLine, recent, valueSelector, maxValue);
    }

    private static string FormatBandwidth(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
        {
            return $"{bytesPerSecond / (1024.0 * 1024):0.0} MB/s";
        }

        if (bytesPerSecond >= 1024)
        {
            return $"{bytesPerSecond / 1024.0:0.0} KB/s";
        }

        return $"{bytesPerSecond:0} B/s";
    }

    private void UpdateMemoryChart(SystemMetricsSnapshot snapshot, IReadOnlyList<SystemMetricsSnapshot> history)
    {
        double? totalBytes = snapshot.TotalMemoryBytes;
        if (totalBytes is null or <= 0)
        {
            UpdateChart(MemoryChartLine, history, _ => null, maxValue: 1.0);
            MemoryChartMaxLabel.Text = "";
            return;
        }

        var maxGb = totalBytes.Value / (1024.0 * 1024 * 1024);
        MemoryChartMaxLabel.Text = $"{maxGb:F1} GB";
        UpdateChart(MemoryChartLine, history, s => ComputeMemoryUsedPercent(s), maxValue: 100.0);
    }

    private static void UpdateChart(
        Polyline chartLine,
        IReadOnlyList<SystemMetricsSnapshot> history,
        Func<SystemMetricsSnapshot, double?> valueSelector,
        double maxValue)
    {
        if (history.Count == 0)
        {
            chartLine.Points = null;
            return;
        }

        var count = Math.Min(history.Count, ChartDisplaySamples);
        var recent = history.Count <= ChartDisplaySamples
            ? history
            : history.Skip(history.Count - ChartDisplaySamples).ToArray();

        var points = new PointCollection(count);
        for (var i = 0; i < recent.Count; i++)
        {
            var value = valueSelector(recent[i]);
            if (!value.HasValue)
            {
                continue; // leave a gap rather than drawing a false zero
            }

            var x = count == 1 ? ChartWidth / 2.0 : (double)i / (count - 1) * ChartWidth;
            var y = ChartHeight - Math.Clamp(value.Value / maxValue, 0.0, 1.0) * ChartHeight;
            points.Add(new System.Windows.Point(x, y));
        }

        chartLine.Points = points;
    }

    private async void RefreshStorageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageScanRunning)
        {
            return;
        }

        _storageScanRunning = true;
        RefreshStorageButton.IsEnabled = false;
        StorageScanStatusTextBlock.Text = "Scanning…";
        ServerSizesItemsControl.ItemsSource = null;

        try
        {
            var servers = _lastServers;
            var logsPath = AppPaths.GetPath("logs");

            var (serverSizes, logsSize) = await Task.Run(() => ScanSizes(servers, logsPath));

            StorageLogsSizeTextBlock.Text = FormatBytes(logsSize);
            ServerSizesItemsControl.ItemsSource = serverSizes;
            StorageScanStatusTextBlock.Text = serverSizes.Count > 0
                ? $"{serverSizes.Count} server(s) scanned"
                : "No servers found";
        }
        finally
        {
            _storageScanRunning = false;
            RefreshStorageButton.IsEnabled = true;
        }
    }

    private async void CheckPortsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_portScanRunning)
        {
            return;
        }

        _portScanRunning = true;
        CheckPortsButton.IsEnabled = false;
        PortScanStatusTextBlock.Text = "Checking…";
        PortDiagnosticsItemsControl.ItemsSource = null;

        try
        {
            var specs = BuildPortSpecs(_lastServers);
            var rows = await Task.Run(() => new PortDiagnosticsService().CheckPorts(specs));

            PortDiagnosticsItemsControl.ItemsSource = rows;
            PortScanStatusTextBlock.Text = rows.Count > 0
                ? $"{specs.Count} server port(s) checked — {rows.Count(r => r.IsWarning)} warning(s)"
                : "No servers with configured ports found.";
        }
        catch (Exception ex)
        {
            PortScanStatusTextBlock.Text = $"Check failed: {ex.Message}";
        }
        finally
        {
            _portScanRunning = false;
            CheckPortsButton.IsEnabled = true;
        }
    }

    private static (List<ServerSizeRow> Servers, long? LogsSize) ScanSizes(
        IReadOnlyList<InstalledServer> servers, string logsPath)
    {
        var rows = servers
            .Select(s =>
            {
                var size = TryGetDirectorySize(s.InstallPath);
                return new ServerSizeRow(s.Name, size.HasValue ? FormatBytes(size) : "–");
            })
            .OrderByDescending(r => r.Name)
            .ToList();

        var logsSize = TryGetDirectorySize(logsPath);
        return (rows, logsSize);
    }

    private static long? TryGetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch
        {
            return null;
        }
    }

    private void OpenInstallFolderButton_Click(object sender, RoutedEventArgs e)
        => OpenFolder(AppPaths.AppDirectory);

    private void OpenServersFolderButton_Click(object sender, RoutedEventArgs e)
        => OpenFolder(AppPaths.GetPath("servers"));

    private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
        => OpenFolder(AppPaths.GetPath("logs"));

    private void CopyReportButton_Click(object sender, RoutedEventArgs e)
    {
        var report = BuildHealthReport();
        System.Windows.Clipboard.SetText(report);
    }

    private string BuildHealthReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("WindowsGSH Health Report");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        sb.AppendLine("WindowsGSH:");
        sb.AppendLine($"  Version: {AppVersionInfo.DisplayVersion}");
        sb.AppendLine($"  App uptime: {FormatAppUptime()}");
        if (_lastSnapshot != null)
        {
            sb.AppendLine($"  CPU / Memory: {_lastSnapshot.ProcessText}");
        }

        sb.AppendLine($"  Install folder: {AppPaths.AppDirectory}");
        sb.AppendLine($"  Servers folder: {AppPaths.GetPath("servers")}");
        sb.AppendLine($"  Logs folder: {AppPaths.GetPath("logs")}");
        sb.AppendLine();

        sb.AppendLine("Host:");
        if (_lastSnapshot != null)
        {
            sb.AppendLine($"  CPU: {_lastSnapshot.TotalCpuText}");
            sb.AppendLine($"  Memory: {_lastSnapshot.MemoryText}");
            sb.AppendLine($"  App drive: {_lastSnapshot.DriveText} ({_lastSnapshot.DriveUsageText})");
        }

        sb.AppendLine($"  OS: {ReadOsDisplayVersion()}");
        sb.AppendLine($"  System uptime: {FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64))}");
        sb.AppendLine($"  Running as admin: {(IsRunningAsAdmin() ? "Yes" : "No")}");
        sb.AppendLine($"  Sleep/hibernate: {ReadSleepStatus()}");
        sb.AppendLine($"  Long path support: {ReadLongPathSupport()}");
        sb.AppendLine($"  Pending reboot: {(CheckPendingReboot() ? "Yes" : "No")}");
        sb.AppendLine();

        sb.AppendLine("Network:");
        sb.AppendLine($"  Local IP: {GetLocalIpV4()}");
        sb.AppendLine($"  Public IP: {(string.IsNullOrWhiteSpace(_lastPublicIp) ? "Not yet checked" : _lastPublicIp)}");
        if (!string.IsNullOrWhiteSpace(_lastPublicIp) && IsPrivateIp(_lastPublicIp))
        {
            sb.AppendLine("  NAT warning: Public IP appears private (possible CGNAT)");
        }

        sb.AppendLine();

        if (_lastServers.Count > 0)
        {
            sb.AppendLine("Servers:");
            foreach (var server in _lastServers)
            {
                sb.AppendLine($"  {server.Name}: {server.CurrentStatusText}, CPU {server.CpuUsage}, RAM {server.MemoryUsage}, Uptime {server.Uptime}");
            }
        }

        return sb.ToString();
    }

    private static string ReadOsDisplayVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                var product = key.GetValue("ProductName") as string;
                var displayVersion = key.GetValue("DisplayVersion") as string;
                if (!string.IsNullOrWhiteSpace(product))
                {
                    return string.IsNullOrWhiteSpace(displayVersion)
                        ? product
                        : $"{product} {displayVersion}";
                }
            }
        }
        catch
        {
        }

        return Environment.OSVersion.VersionString;
    }

    private static string ReadSleepStatus()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            if (key != null)
            {
                var hibernate = key.GetValue("HibernateEnabled");
                if (hibernate is int h && h == 0)
                {
                    return "Disabled";
                }
            }
        }
        catch
        {
        }

        return "Possibly enabled — check Power settings";
    }

    private static string ReadLongPathSupport()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            if (key != null)
            {
                var value = key.GetValue("LongPathsEnabled");
                if (value is int v)
                {
                    return v == 1 ? "Enabled" : "Disabled";
                }
            }
        }
        catch
        {
        }

        return "Unknown";
    }

    private static bool CheckPendingReboot()
    {
        try
        {
            using var wuKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wuKey != null)
            {
                return true;
            }

            using var cbsKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbsKey != null)
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string ReadDriveType(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return "Unknown";
            }

            var drive = new DriveInfo(root);
            return drive.DriveType switch
            {
                DriveType.Fixed => "Fixed (HDD/SSD/NVMe)",
                DriveType.Network => "Network drive",
                DriveType.Removable => "Removable",
                DriveType.CDRom => "Optical",
                DriveType.Ram => "RAM disk",
                _ => drive.DriveType.ToString()
            };
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string GetLocalIpV4()
    {
        // Primary: UDP connect lets the OS pick the routing interface without sending traffic.
        // Works reliably when the host has a default internet route. May return a VPN address
        // if a full-tunnel VPN is active, or fail entirely on offline/LAN-only hosts.
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            var ip = ((IPEndPoint)socket.LocalEndPoint!).Address;
            if (!IPAddress.IsLoopback(ip))
            {
                return ip.ToString();
            }
        }
        catch
        {
        }

        // Fallback: enumerate physical adapters for offline or LAN-only hosts.
        // Prefer Ethernet, then WiFi; skip loopback, tunnel, PPP, and slip.
        try
        {
            string? wireless = null;
            string? other = null;

            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Slip)
                {
                    continue;
                }

                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(addr.Address))
                    {
                        continue;
                    }

                    var ipText = addr.Address.ToString();
                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                    {
                        return ipText;
                    }

                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        wireless ??= ipText;
                    }
                    else
                    {
                        other ??= ipText;
                    }
                }
            }

            return wireless ?? other ?? "Unknown";
        }
        catch
        {
        }

        return "Unknown";
    }

    private static bool IsPrivateIp(string ipText)
    {
        if (!IPAddress.TryParse(ipText, out var ip))
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return (bytes[0] == 10) ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127); // CGNAT range
    }

    private static string DetectNatWarning(string publicIp)
    {
        if (string.IsNullOrWhiteSpace(publicIp))
        {
            return "—";
        }

        if (!IPAddress.TryParse(publicIp, out var ip))
        {
            return "—";
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4 && bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
        {
            return "CGNAT detected (100.64.0.0/10) — port forwarding may not work";
        }

        if (IsPrivateIp(publicIp))
        {
            return "Private IP detected — may be double-NAT";
        }

        return "Public IPv4 detected";
    }

    private static string FormatAppUptime()
    {
        try
        {
            var started = Process.GetCurrentProcess().StartTime;
            return FormatUptime(DateTime.Now - started);
        }
        catch
        {
            return "—";
        }
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        }

        if (uptime.TotalHours >= 1)
        {
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        }

        return $"{(int)uptime.TotalMinutes}m";
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string TruncatePath(string path)
    {
        return path.Length <= 40 ? path : "…" + path[^38..];
    }

    private static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return "—";
        }

        var value = bytes.Value;
        return value switch
        {
            >= 1L * 1024 * 1024 * 1024 * 1024 => $"{value / (1024.0 * 1024 * 1024 * 1024):F1} TB",
            >= 1024 * 1024 * 1024 => $"{value / (1024.0 * 1024 * 1024):F1} GB",
            >= 1024 * 1024 => $"{value / (1024.0 * 1024):F0} MB",
            >= 1024 => $"{value / 1024.0:F0} KB",
            _ => $"{value} B"
        };
    }

    private static double? ComputeMemoryUsedPercent(SystemMetricsSnapshot s)
    {
        if (!s.TotalMemoryBytes.HasValue || !s.UsedMemoryBytes.HasValue || s.TotalMemoryBytes.Value <= 0)
        {
            return null;
        }

        return s.UsedMemoryBytes.Value / (double)s.TotalMemoryBytes.Value * 100.0;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static long ParseMemoryBytes(string memoryText)
    {
        if (string.IsNullOrWhiteSpace(memoryText) || memoryText == "--")
        {
            return 0;
        }

        var parts = memoryText.Trim().Split(' ', 2);
        if (parts.Length < 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        return parts[1].Trim().ToUpperInvariant() switch
        {
            "TB" => (long)(value * 1024L * 1024 * 1024 * 1024),
            "GB" => (long)(value * 1024L * 1024 * 1024),
            "MB" => (long)(value * 1024 * 1024),
            "KB" => (long)(value * 1024),
            _ => 0
        };
    }

    private static List<PortCheckSpec> BuildPortSpecs(IReadOnlyList<InstalledServer> servers)
    {
        var specs = new List<PortCheckSpec>(servers.Count);
        foreach (var server in servers)
        {
            if (!int.TryParse(server.Port, out var port) || port is <= 0 or > 65535)
            {
                continue;
            }

            // Main game port. ExpectsTcpListener defaults to false — many game ports are
            // UDP-only and the protocol is only known via module config fields.
            // When module context is available at this call site, additional specs for
            // auxiliary ports (RCON, query, etc.) should be appended here using
            // module.GetConfigFields() filtered to ConfigFieldType.Port.
            specs.Add(new PortCheckSpec(server.Name, server.Id, port, server.IpAddress));
        }

        return specs;
    }

    private sealed record ServerSizeRow(string Name, string SizeText);

    private sealed record ServerResourceRow(
        string Name,
        string Status,
        string Cpu,
        string Memory,
        string Uptime,
        bool IsRunning,
        long MemorySortBytes);
}
