using System.Diagnostics;
using System.IO;
using WindowsGSH.Core.Events;
using WindowsGSH.Core.Metrics;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public sealed class ServerMetricsService : IServerMetricsService
{
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<IGameServerModule?, ServerInstance, IReadOnlyList<ProcessMetricsSample>> _processSampler;
    private readonly int _processorCount;
    private readonly int _maxSamplesPerServer;
    private readonly int _maxServers;
    private readonly IWindowsGshEventBus _events;
    private readonly Dictionary<string, CpuSample> _cpuSamples = [];
    private readonly Dictionary<string, MetricHistoryBuffer<ServerMetricsSnapshot>> _history = [];

    public ServerMetricsService(
        Func<DateTimeOffset>? utcNow = null,
        Func<IGameServerModule?, ServerInstance, IReadOnlyList<ProcessMetricsSample>>? processSampler = null,
        int? processorCount = null,
        IWindowsGshEventBus? events = null,
        int maxSamplesPerServer = 900,
        int maxServers = 50)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _processSampler = processSampler ?? SampleProcesses;
        _processorCount = Math.Max(1, processorCount ?? Environment.ProcessorCount);
        _maxSamplesPerServer = Math.Max(1, maxSamplesPerServer);
        _maxServers = Math.Max(1, maxServers);
        _events = events ?? WindowsGshEventBus.Shared;
    }

    public ServerMetricsSnapshot Sample(IGameServerModule? module, ServerInstance instance, int? maxPlayers = null)
    {
        var sampledAt = _utcNow();
        var processes = module == null
            ? []
            : _processSampler(module, instance);

        var snapshot = processes.Count == 0
            ? new ServerMetricsSnapshot(instance.Id, instance.Name, instance.ModuleId, sampledAt, null, null, null, null, null, null, maxPlayers)
            : CreateSnapshot(instance, processes, sampledAt, maxPlayers);

        AddHistory(snapshot);
        _events.Publish(new ServerMetricSampledEvent(
            snapshot.SampledAt,
            snapshot.ServerId,
            snapshot.ModuleId,
            snapshot.ServerName,
            snapshot.PrimaryProcessId,
            snapshot.PrimaryProcessName,
            snapshot.CpuPercent,
            snapshot.MemoryBytes,
            snapshot.Uptime,
            snapshot.ThreadCount));
        return snapshot;
    }

    public ServerMetricsSnapshot? GetLatest(string serverId)
    {
        lock (_sync)
        {
            if (!_history.TryGetValue(serverId, out var buffer))
            {
                return null;
            }

            var list = buffer.ToReadOnlyList();
            return list.Count > 0 ? list[list.Count - 1] : null;
        }
    }

    public IReadOnlyList<ServerMetricsSnapshot> GetHistory(string serverId)
    {
        lock (_sync)
        {
            return _history.TryGetValue(serverId, out var buffer)
                ? buffer.ToReadOnlyList()
                : [];
        }
    }

    public void RemoveSeries(string serverId)
    {
        lock (_sync)
        {
            _history.Remove(serverId);
            _cpuSamples.Remove(serverId);
        }
    }

    public void PruneStale(DateTimeOffset olderThan)
    {
        lock (_sync)
        {
            var toRemove = new List<string>();
            foreach (var (serverId, buffer) in _history)
            {
                var list = buffer.ToReadOnlyList();
                if (list.Count == 0 || list[list.Count - 1].SampledAt < olderThan)
                {
                    toRemove.Add(serverId);
                }
            }

            foreach (var serverId in toRemove)
            {
                _history.Remove(serverId);
                _cpuSamples.Remove(serverId);
            }
        }
    }

    public static double CalculateCpuPercent(
        TimeSpan previousTotalProcessorTime,
        DateTimeOffset previousTimestamp,
        TimeSpan currentTotalProcessorTime,
        DateTimeOffset currentTimestamp,
        int processorCount)
    {
        var elapsed = currentTimestamp - previousTimestamp;
        if (elapsed.TotalMilliseconds <= 0)
        {
            return 0;
        }

        var cpuDelta = currentTotalProcessorTime - previousTotalProcessorTime;
        var percent = cpuDelta.TotalMilliseconds / (elapsed.TotalMilliseconds * Math.Max(1, processorCount)) * 100d;
        return Math.Clamp(percent, 0, 100);
    }

    private ServerMetricsSnapshot CreateSnapshot(
        ServerInstance instance,
        IReadOnlyList<ProcessMetricsSample> processes,
        DateTimeOffset sampledAt,
        int? maxPlayers)
    {
        var totalProcessorTime = processes.Aggregate(TimeSpan.Zero, (total, process) => total + process.TotalProcessorTime);
        var cpuPercent = GetCpuPercent(instance.Id, totalProcessorTime, sampledAt);
        var memoryBytes = processes.Sum(process => process.WorkingSetBytes);
        var uptime = sampledAt - processes.Min(process => process.StartTime);
        var threadCount = processes.Sum(process => process.ThreadCount);
        var primaryProcess = processes
            .OrderByDescending(process => process.WorkingSetBytes)
            .First();

        return new ServerMetricsSnapshot(
            instance.Id,
            instance.Name,
            instance.ModuleId,
            sampledAt,
            primaryProcess.ProcessId,
            primaryProcess.ProcessName,
            cpuPercent,
            memoryBytes,
            uptime,
            threadCount,
            maxPlayers);
    }

    private double? GetCpuPercent(string serverId, TimeSpan totalProcessorTime, DateTimeOffset sampledAt)
    {
        lock (_sync)
        {
            if (!_cpuSamples.TryGetValue(serverId, out var previous))
            {
                _cpuSamples[serverId] = new CpuSample(totalProcessorTime, sampledAt);
                return 0;
            }

            _cpuSamples[serverId] = new CpuSample(totalProcessorTime, sampledAt);
            return CalculateCpuPercent(previous.TotalProcessorTime, previous.Timestamp, totalProcessorTime, sampledAt, _processorCount);
        }
    }

    private void AddHistory(ServerMetricsSnapshot snapshot)
    {
        lock (_sync)
        {
            if (!_history.TryGetValue(snapshot.ServerId, out var buffer))
            {
                if (_history.Count >= _maxServers)
                {
                    PruneOldestSeries();
                }

                buffer = new MetricHistoryBuffer<ServerMetricsSnapshot>(_maxSamplesPerServer);
                _history[snapshot.ServerId] = buffer;
            }

            buffer.Add(snapshot);
        }
    }

    private void PruneOldestSeries()
    {
        string? oldestId = null;
        var oldestTime = DateTimeOffset.MaxValue;

        foreach (var (serverId, buffer) in _history)
        {
            var list = buffer.ToReadOnlyList();
            var lastSeen = list.Count > 0 ? list[list.Count - 1].SampledAt : DateTimeOffset.MinValue;
            if (lastSeen < oldestTime)
            {
                oldestTime = lastSeen;
                oldestId = serverId;
            }
        }

        if (oldestId != null)
        {
            _history.Remove(oldestId);
            _cpuSamples.Remove(oldestId);
        }
    }

    private static IReadOnlyList<ProcessMetricsSample> SampleProcesses(IGameServerModule? module, ServerInstance instance)
    {
        if (module == null)
        {
            return [];
        }

        var processes = ServerProcessLocator.FindProcesses(module, instance.InstallPath);
        try
        {
            return processes
                .Where(process => !process.HasExited)
                .Select(TrySampleProcess)
                .OfType<ProcessMetricsSample>()
                .ToArray();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static ProcessMetricsSample? TrySampleProcess(Process process)
    {
        try
        {
            process.Refresh();
            return new ProcessMetricsSample(
                process.Id,
                process.ProcessName,
                process.TotalProcessorTime,
                process.WorkingSet64,
                new DateTimeOffset(process.StartTime),
                process.Threads.Count);
        }
        catch
        {
            return null;
        }
    }

    private sealed record CpuSample(TimeSpan TotalProcessorTime, DateTimeOffset Timestamp);
}
