using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Web.Auth;

namespace WindowsGSH.Core.Web.Api;

public static class ServerStatusEndpoints
{
    public static void MapServerStatusEndpoints(this WebApplication app)
    {
        app.MapGet("/api/servers", GetServers).RequireAuthorization("FullAccess");
        app.MapGet("/api/servers/{id}", GetServer).RequireAuthorization("FullAccess");
        app.MapGet("/api/servers/{id}/logs", GetServerLogs).RequireAuthorization("FullAccess");
        app.MapGet("/api/servers/{id}/metrics", GetServerMetrics).RequireAuthorization("FullAccess");
        app.MapGet("/api/host/metrics", GetHostMetrics).RequireAuthorization("FullAccess");
    }

    private static IResult GetServers(HttpContext ctx)
    {
        var servers = WebServerState.GetServers();
        return Results.Ok(servers.Select(s => BuildSummary(s)).ToArray());
    }

    private static IResult GetServer(string id, HttpContext ctx)
    {
        var server = WebServerState.GetServers().FirstOrDefault(s => s.Id == id);
        if (server == null)
            return Results.NotFound();

        var role = GetCallerRole(ctx.User);
        var metrics = WebServerState.GetServerMetrics(id);
        var (lastStarted, lastStopped) = WebServerState.GetServerLifecycleTimes(id);

        return Results.Ok(new ServerDetailResponse(
            Id: server.Id,
            Name: server.Name,
            Module: server.ModuleId,
            Status: server.CurrentStatusText,
            Port: server.Port,
            Uptime: server.Uptime,
            CpuPercent: metrics?.CpuPercent,
            MemoryMb: ToMb(metrics?.MemoryBytes),
            CanStart: server.CanStart,
            CanStop: server.CanStop,
            InstallPath: role >= WebRole.Operator ? server.InstallPath : null,
            PlayerCount: NullIfEmpty(server.PlayerCount),
            Version: NullIfEmpty(server.LocalBuildId),
            LastStarted: lastStarted,
            LastStopped: lastStopped));
    }

    private static IResult GetServerLogs(string id, HttpContext ctx, int max = 100)
    {
        if (!WebServerState.GetServers().Any(s => s.Id == id))
            return Results.NotFound();

        var clampedMax = Math.Clamp(max, 1, 500);
        var lines = ServerConsoleService.GetLines(id)
            .TakeLast(clampedMax)
            .Select(l => new ConsoleLineResponse(l.Timestamp, SupportBundleService.RedactText(l.Text)))
            .ToArray();

        return Results.Ok(lines);
    }

    private static IResult GetServerMetrics(string id, HttpContext ctx)
    {
        if (!WebServerState.GetServers().Any(s => s.Id == id))
            return Results.NotFound();

        var s = WebServerState.GetServerMetrics(id);
        // PID combined with install path could assist process inspection; restrict to Operator+.
        var pid = GetCallerRole(ctx.User) >= WebRole.Operator ? s?.PrimaryProcessId : null;
        return Results.Ok(new ServerMetricsResponse(
            SampledAt: s?.SampledAt,
            CpuPercent: s?.CpuPercent,
            MemoryMb: ToMb(s?.MemoryBytes),
            UptimeSeconds: s?.Uptime?.TotalSeconds,
            ProcessId: pid));
    }

    private static IResult GetHostMetrics()
    {
        var s = WebServerState.GetHostMetrics();
        return Results.Ok(new HostMetricsResponse(
            SampledAt: s?.SampledAt,
            TotalCpuPercent: s?.TotalCpuPercent,
            TotalMemoryBytes: s?.TotalMemoryBytes,
            UsedMemoryBytes: s?.UsedMemoryBytes,
            UploadBytesPerSecond: s?.UploadBytesPerSecond,
            DownloadBytesPerSecond: s?.DownloadBytesPerSecond));
    }

    private static ServerSummaryResponse BuildSummary(InstalledServer s)
    {
        var metrics = WebServerState.GetServerMetrics(s.Id);
        return new ServerSummaryResponse(
            Id: s.Id,
            Name: s.Name,
            Module: s.ModuleId,
            Status: s.CurrentStatusText,
            Port: s.Port,
            Uptime: s.Uptime,
            CpuPercent: metrics?.CpuPercent,
            MemoryMb: ToMb(metrics?.MemoryBytes),
            CanStart: s.CanStart,
            CanStop: s.CanStop);
    }

    private static WebRole GetCallerRole(ClaimsPrincipal user)
    {
        var roleStr = user.FindFirst(ClaimTypes.Role)?.Value;
        return Enum.TryParse<WebRole>(roleStr, out var r) ? r : WebRole.Viewer;
    }

    private static double? ToMb(long? bytes)
        => bytes.HasValue ? Math.Round(bytes.Value / 1024.0 / 1024.0, 1) : null;

    private static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;
}

public sealed record ServerSummaryResponse(
    string Id, string Name, string Module, string Status, string Port,
    string Uptime, double? CpuPercent, double? MemoryMb,
    bool CanStart, bool CanStop);

public sealed record ServerDetailResponse(
    string Id, string Name, string Module, string Status, string Port,
    string Uptime, double? CpuPercent, double? MemoryMb,
    bool CanStart, bool CanStop,
    // InstallPath is omitted entirely (not just null) for Viewer-role callers.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstallPath,
    string? PlayerCount, string? Version,
    DateTimeOffset? LastStarted, DateTimeOffset? LastStopped);

public sealed record ConsoleLineResponse(DateTimeOffset Timestamp, string Line);

public sealed record ServerMetricsResponse(
    DateTimeOffset? SampledAt, double? CpuPercent, double? MemoryMb,
    double? UptimeSeconds, int? ProcessId);

public sealed record HostMetricsResponse(
    DateTimeOffset? SampledAt, double? TotalCpuPercent,
    long? TotalMemoryBytes, long? UsedMemoryBytes,
    double? UploadBytesPerSecond, double? DownloadBytesPerSecond);
