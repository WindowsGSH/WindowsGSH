using WindowsGSH.Core.Health;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class SystemMetricsServiceTests
{
    [Theory]
    [InlineData(100, 25, 75)]
    [InlineData(100, 120, 0)]
    [InlineData(100, -10, 100)]
    public void CalculateUsagePercent_clamps_expected_values(long total, long free, double expected)
    {
        Assert.Equal(expected, SystemMetricsService.CalculateUsagePercent(total, free));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1073741824, "1.0 GB")]
    public void FormatBytes_formats_binary_units(long bytes, string expected)
    {
        Assert.Equal(expected, SystemMetricsService.FormatBytes(bytes));
    }

    [Fact]
    public void SelectDriveRoot_uses_application_drive()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory))!;

        Assert.Equal(root, SystemMetricsService.SelectDriveRoot(AppContext.BaseDirectory));
    }

    [Fact]
    public async Task SampleAsync_updates_cpu_memory_drive_and_process_values()
    {
        var provider = new FakeProvider(
            new SystemMetricsIdentity("Test CPU", 4, "C:\\", "NTFS"),
            [
                Sample(0, 100, 200, 100, 16_000, 6_000, 100_000, 25_000, 0, 1_000),
                Sample(1, 120, 260, 140, 16_000, 4_000, 100_000, 20_000, 2, 2_000)
            ]);
        var service = new SystemMetricsService(provider);

        _ = await service.SampleAsync();
        var snapshot = await service.SampleAsync();

        Assert.Equal(80, snapshot.TotalCpuPercent);
        Assert.Equal(12_000, snapshot.UsedMemoryBytes);
        Assert.Equal(80, snapshot.DriveUsedPercent);
        Assert.Equal(50, snapshot.ProcessCpuPercent);
        Assert.Equal(2_000, snapshot.ProcessMemoryBytes);
        Assert.Equal("Test CPU", snapshot.CpuModel);
    }

    [Fact]
    public async Task SampleAsync_returns_unavailable_snapshot_when_provider_fails()
    {
        var service = new SystemMetricsService(new ThrowingProvider());

        var snapshot = await service.SampleAsync();

        Assert.NotNull(snapshot.Error);
        Assert.Equal("--", snapshot.TotalCpuText);
        Assert.Equal("Unavailable", snapshot.CpuModel);
    }

    [Fact]
    public async Task SampleAsync_honors_cancellation()
    {
        var service = new SystemMetricsService(new FakeProvider(
            new SystemMetricsIdentity("CPU", 1, "C:\\", "NTFS"),
            [Sample(0, 0, 0, 0, 1, 1, 1, 1, 0, 1)]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SampleAsync(cancellation.Token));
    }

    private static SystemMetricsRawSample Sample(
        int seconds,
        ulong idle,
        ulong kernel,
        ulong user,
        long totalMemory,
        long availableMemory,
        long driveTotal,
        long driveFree,
        int processCpuSeconds,
        long processMemory)
    {
        return new SystemMetricsRawSample(
            DateTimeOffset.UnixEpoch.AddSeconds(seconds),
            idle,
            kernel,
            user,
            totalMemory,
            availableMemory,
            driveTotal,
            driveFree,
            TimeSpan.FromSeconds(processCpuSeconds),
            processMemory);
    }

    private sealed class FakeProvider(
        SystemMetricsIdentity identity,
        IReadOnlyList<SystemMetricsRawSample> samples) : ISystemMetricsProvider
    {
        private int _index;

        public SystemMetricsIdentity ReadIdentity() => identity;

        public SystemMetricsRawSample ReadSample()
        {
            var index = Math.Min(_index, samples.Count - 1);
            _index++;
            return samples[index];
        }
    }

    private sealed class ThrowingProvider : ISystemMetricsProvider
    {
        public SystemMetricsIdentity ReadIdentity() => throw new UnauthorizedAccessException("identity blocked");

        public SystemMetricsRawSample ReadSample() => throw new InvalidOperationException("sample blocked");
    }
}
