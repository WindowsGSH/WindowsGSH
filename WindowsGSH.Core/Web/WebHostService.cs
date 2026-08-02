using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WindowsGSH.Core.Web;

/// <summary>
/// P3-03 (fixed after review — was a WebHostService static, which is process-wide mutable state:
/// two WebHostService instances started with different AllowLegacyWebSocketQueryStringAuth values
/// in the same process — as happens under test, and would happen for any future multi-host use —
/// would have the second Start silently overwrite the policy for the first's already-open
/// connections). Registered per-host in that host's own DI container by
/// <see cref="WebHostService.TryStartAsync"/>, so each running host keeps its own policy no matter
/// how many others start or stop around it.
/// </summary>
public sealed record WebSocketAuthPolicy(bool AllowLegacyQueryStringAuth);

public sealed class WebHostService : IAsyncDisposable
{
    private WebApplication? _app;

    public static bool IsRunning { get; private set; }
    public static int? ActivePort { get; private set; }
    public static string? ActiveBindAddress { get; private set; }
    public static string? LastStartError { get; private set; }

    /// <param name="configureServices">Called before Build() to register DI services.</param>
    /// <param name="configurePipeline">Called after Build() to add middleware and map endpoints.</param>
    public async Task<bool> TryStartAsync(
        WebHostOptions options,
        Action<WebApplicationBuilder>? configureServices = null,
        Action<WebApplication>? configurePipeline = null,
        CancellationToken cancellationToken = default)
    {
        if (_app != null)
            return true;

        var bindAddress = IsValidBindAddress(options.BindAddress) ? options.BindAddress : "127.0.0.1";

        var builder = WebApplication.CreateSlimBuilder();
        // Configure the URL via the configuration system — the only way to set
        // Kestrel bindings on the slim builder without full IWebHostBuilder extensions.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["urls"] = $"http://{FormatBindAddress(bindAddress)}:{options.Port}"
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new WebSocketAuthPolicy(options.AllowLegacyWebSocketQueryStringAuth));

        configureServices?.Invoke(builder);

        var app = builder.Build();

        configurePipeline?.Invoke(app);

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        try
        {
            await app.StartAsync(cancellationToken);
            _app = app;
            IsRunning = true;
            ActivePort = options.Port;
            ActiveBindAddress = bindAddress;
            LastStartError = null;
            WebLog.Add($"Web server started on http://localhost:{options.Port}");
            if (!IsLoopbackAddress(bindAddress))
                WebLog.Add($"Security warning: web server is bound to {bindAddress} — " +
                           $"the API is reachable from other devices on the network over unencrypted HTTP. " +
                           $"For Cloudflare Tunnel, keep the bind address as 127.0.0.1 and point cloudflared at http://127.0.0.1:{options.Port}.");
            return true;
        }
        catch (Exception ex)
        {
            await app.DisposeAsync();
            LastStartError = $"Failed to bind port {options.Port}: {ex.Message}";
            WebLog.Add($"Web server failed to start on port {options.Port}: {ex.Message}");
            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app == null)
            return;

        var app = _app;
        _app = null;
        IsRunning = false;
        ActivePort = null;
        ActiveBindAddress = null;

        try
        {
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    public static bool IsLoopbackAddress(string address)
    {
        if (address.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            address == "127.0.0.1" ||
            address == "::1")
            return true;
        return IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Returns true when <paramref name="address"/> is a concrete IP address or
    /// "localhost". Wildcards (* +) and full URLs are rejected to prevent the web
    /// server failing silently on the next launch.
    /// </summary>
    public static bool IsValidBindAddress(string address) =>
        !string.IsNullOrWhiteSpace(address) &&
        (address.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         IPAddress.TryParse(address, out _));

    // IPv6 literals must be wrapped in brackets when used in a URL (RFC 2732).
    // "::1" → "[::1]", "127.0.0.1" → "127.0.0.1".
    internal static string FormatBindAddress(string address) =>
        address.Contains(':') ? $"[{address}]" : address;
}
