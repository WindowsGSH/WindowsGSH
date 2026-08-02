using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsGSH.Core.Health;

public sealed class SystemMetricsService
{
    private readonly ISystemMetricsProvider _provider;
    private readonly SystemMetricsIdentity _identity;
    private readonly object _sync = new();
    private SystemMetricsRawSample? _previous;

    public SystemMetricsService(ISystemMetricsProvider? provider = null)
    {
        _provider = provider ?? new WindowsSystemMetricsProvider();
        _identity = SafeReadIdentity(_provider);
    }

    public Task<SystemMetricsSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Sample(cancellationToken), cancellationToken);
    }

    public SystemMetricsSnapshot Sample(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var current = _provider.ReadSample();
            cancellationToken.ThrowIfCancellationRequested();
            SystemMetricsRawSample? previous;
            lock (_sync)
            {
                previous = _previous;
                _previous = current;
            }

            var totalCpu = previous == null
                ? null
                : CalculateTotalCpuPercent(previous, current);
            var processCpu = previous == null
                ? null
                : CalculateProcessCpuPercent(previous, current, _identity.LogicalProcessorCount);
            long? usedMemory = current.TotalMemoryBytes.HasValue && current.AvailableMemoryBytes.HasValue
                ? Math.Max(0, current.TotalMemoryBytes.Value - current.AvailableMemoryBytes.Value)
                : null;

            return new SystemMetricsSnapshot(
                current.SampledAt,
                _identity.CpuModel,
                _identity.LogicalProcessorCount,
                totalCpu,
                current.TotalMemoryBytes,
                usedMemory,
                _identity.DriveRoot,
                _identity.DriveFormat,
                current.DriveTotalBytes,
                current.DriveFreeBytes,
                CalculateUsagePercent(current.DriveTotalBytes, current.DriveFreeBytes),
                processCpu,
                current.ProcessPrivateWorkingSetBytes,
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SystemMetricsSnapshot.Unavailable(_identity, ex.Message);
        }
    }

    public static double? CalculateUsagePercent(long? totalBytes, long? freeBytes)
    {
        if (!totalBytes.HasValue || !freeBytes.HasValue || totalBytes <= 0)
        {
            return null;
        }

        return Math.Clamp((totalBytes.Value - freeBytes.Value) / (double)totalBytes.Value * 100d, 0, 100);
    }

    public static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue || bytes < 0)
        {
            return "--";
        }

        var value = (double)bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public static string SelectDriveRoot(string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        return Path.GetPathRoot(Path.GetFullPath(appDirectory)) ?? Path.GetFullPath(appDirectory);
    }

    private static double? CalculateTotalCpuPercent(SystemMetricsRawSample previous, SystemMetricsRawSample current)
    {
        if (!previous.SystemIdleTime.HasValue ||
            !previous.SystemKernelTime.HasValue ||
            !previous.SystemUserTime.HasValue ||
            !current.SystemIdleTime.HasValue ||
            !current.SystemKernelTime.HasValue ||
            !current.SystemUserTime.HasValue)
        {
            return null;
        }

        var idleDelta = current.SystemIdleTime.Value - previous.SystemIdleTime.Value;
        var totalDelta =
            (current.SystemKernelTime.Value - previous.SystemKernelTime.Value) +
            (current.SystemUserTime.Value - previous.SystemUserTime.Value);
        if (totalDelta <= 0)
        {
            return null;
        }

        return Math.Clamp((totalDelta - idleDelta) / (double)totalDelta * 100d, 0, 100);
    }

    private static double? CalculateProcessCpuPercent(
        SystemMetricsRawSample previous,
        SystemMetricsRawSample current,
        int processorCount)
    {
        if (!previous.ProcessTotalProcessorTime.HasValue || !current.ProcessTotalProcessorTime.HasValue)
        {
            return null;
        }

        var elapsed = current.SampledAt - previous.SampledAt;
        if (elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var processDelta = current.ProcessTotalProcessorTime.Value - previous.ProcessTotalProcessorTime.Value;
        return Math.Clamp(
            processDelta.TotalMilliseconds /
            (elapsed.TotalMilliseconds * Math.Max(1, processorCount)) *
            100d,
            0,
            100);
    }

    private static SystemMetricsIdentity SafeReadIdentity(ISystemMetricsProvider provider)
    {
        try
        {
            return provider.ReadIdentity();
        }
        catch
        {
            return new SystemMetricsIdentity("Unavailable", Math.Max(1, Environment.ProcessorCount), "--", "--");
        }
    }
}

public interface ISystemMetricsProvider
{
    SystemMetricsIdentity ReadIdentity();

    SystemMetricsRawSample ReadSample();
}

public sealed record SystemMetricsIdentity(
    string CpuModel,
    int LogicalProcessorCount,
    string DriveRoot,
    string DriveFormat);

public sealed record SystemMetricsRawSample(
    DateTimeOffset SampledAt,
    ulong? SystemIdleTime,
    ulong? SystemKernelTime,
    ulong? SystemUserTime,
    long? TotalMemoryBytes,
    long? AvailableMemoryBytes,
    long? DriveTotalBytes,
    long? DriveFreeBytes,
    TimeSpan? ProcessTotalProcessorTime,
    long? ProcessPrivateWorkingSetBytes);

public sealed record SystemMetricsSnapshot(
    DateTimeOffset SampledAt,
    string CpuModel,
    int LogicalProcessorCount,
    double? TotalCpuPercent,
    long? TotalMemoryBytes,
    long? UsedMemoryBytes,
    string DriveRoot,
    string DriveFormat,
    long? DriveTotalBytes,
    long? DriveFreeBytes,
    double? DriveUsedPercent,
    double? ProcessCpuPercent,
    long? ProcessMemoryBytes,
    string? Error,
    double? UploadBytesPerSecond = null,
    double? DownloadBytesPerSecond = null)
{
    public string TotalCpuText => TotalCpuPercent.HasValue ? $"{TotalCpuPercent:0.0}%" : "--";
    public string MemoryText => TotalMemoryBytes.HasValue && UsedMemoryBytes.HasValue
        ? $"{SystemMetricsService.FormatBytes(UsedMemoryBytes)} / {SystemMetricsService.FormatBytes(TotalMemoryBytes)}"
        : "--";
    public string DriveText => DriveTotalBytes.HasValue && DriveFreeBytes.HasValue
        ? $"{SystemMetricsService.FormatBytes(DriveFreeBytes)} free / {SystemMetricsService.FormatBytes(DriveTotalBytes)}"
        : "--";
    public string DriveUsageText => DriveUsedPercent.HasValue ? $"{DriveUsedPercent:0.0}% used" : "--";
    public string ProcessText => ProcessCpuPercent.HasValue
        ? $"{ProcessCpuPercent:0.0}% CPU · {SystemMetricsService.FormatBytes(ProcessMemoryBytes)}"
        : $"-- CPU · {SystemMetricsService.FormatBytes(ProcessMemoryBytes)}";
    public string UploadText => FormatBandwidth(UploadBytesPerSecond);
    public string DownloadText => FormatBandwidth(DownloadBytesPerSecond);

    public static SystemMetricsSnapshot Unavailable(SystemMetricsIdentity identity, string error)
    {
        return new SystemMetricsSnapshot(
            DateTimeOffset.UtcNow,
            identity.CpuModel,
            identity.LogicalProcessorCount,
            null,
            null,
            null,
            identity.DriveRoot,
            identity.DriveFormat,
            null,
            null,
            null,
            null,
            null,
            error);
    }

    private static string FormatBandwidth(double? bytesPerSecond)
    {
        if (!bytesPerSecond.HasValue)
        {
            return "--";
        }

        var bps = bytesPerSecond.Value;
        if (bps >= 1024 * 1024)
        {
            return $"{bps / (1024.0 * 1024):0.0} MB/s";
        }

        if (bps >= 1024)
        {
            return $"{bps / 1024.0:0.0} KB/s";
        }

        return $"{bps:0} B/s";
    }
}

internal sealed class WindowsSystemMetricsProvider : ISystemMetricsProvider
{
    private readonly string _driveRoot = ResolveDriveRoot();

    public SystemMetricsIdentity ReadIdentity()
    {
        var cpuModel = "Unavailable";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                writable: false);
            cpuModel = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unavailable";
        }
        catch
        {
        }

        var driveFormat = "--";
        try
        {
            driveFormat = new DriveInfo(_driveRoot).DriveFormat;
        }
        catch
        {
        }

        return new SystemMetricsIdentity(
            cpuModel,
            Math.Max(1, Environment.ProcessorCount),
            _driveRoot,
            driveFormat);
    }

    public SystemMetricsRawSample ReadSample()
    {
        ulong? idle = null;
        ulong? kernel = null;
        ulong? user = null;
        if (GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            idle = ToUInt64(idleTime);
            kernel = ToUInt64(kernelTime);
            user = ToUInt64(userTime);
        }

        long? totalMemory = null;
        long? availableMemory = null;
        var memory = new MemoryStatusEx();
        if (GlobalMemoryStatusEx(memory))
        {
            totalMemory = checked((long)memory.TotalPhysical);
            availableMemory = checked((long)memory.AvailablePhysical);
        }

        long? driveTotal = null;
        long? driveFree = null;
        try
        {
            var drive = new DriveInfo(_driveRoot);
            if (drive.IsReady)
            {
                driveTotal = drive.TotalSize;
                driveFree = drive.AvailableFreeSpace;
            }
        }
        catch
        {
        }

        TimeSpan? processCpu = null;
        long? processMemory = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            processCpu = process.TotalProcessorTime;
            processMemory = TryGetPrivateWorkingSetBytes(process)
                ?? process.PrivateMemorySize64;
        }
        catch
        {
        }

        return new SystemMetricsRawSample(
            DateTimeOffset.UtcNow,
            idle,
            kernel,
            user,
            totalMemory,
            availableMemory,
            driveTotal,
            driveFree,
            processCpu,
            processMemory);
    }

    private static long? TryGetPrivateWorkingSetBytes(Process process)
    {
        var counters = new ProcessMemoryCountersEx2
        {
            Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>()
        };
        return GetProcessMemoryInfo(process.Handle, ref counters, counters.Size) &&
               counters.PrivateWorkingSetSize > 0
            ? checked((long)counters.PrivateWorkingSetSize)
            : null;
    }

    private static string ResolveDriveRoot()
    {
        return SystemMetricsService.SelectDriveRoot(AppPaths.AppDirectory);
    }

    private static ulong ToUInt64(FileTime time)
    {
        return ((ulong)(uint)time.High << 32) | (uint)time.Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr process,
        ref ProcessMemoryCountersEx2 counters,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public nuint SharedCommitUsage;
    }
}
