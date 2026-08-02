using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Auth;

namespace WindowsGSH.Core.Web.Api;

public static class ServerConsoleEndpoints
{
    private const int MaxConnectionsPerServer = 10;
    private const int MaxPendingAuthConnectionsPerServer = 10;
    private const int DefaultHistoryLines = 100;
    private const int MaxHistoryLines = 500;

    // Thread-safe count of active WebSocket connections per server ID.
    internal static readonly ConcurrentDictionary<string, int> ActiveConnections =
        new(StringComparer.Ordinal);

    internal static readonly ConcurrentDictionary<string, int> PendingAuthConnections =
        new(StringComparer.Ordinal);

    public static void MapServerConsoleEndpoints(this WebApplication app)
    {
        // WebSocket: auth is handled manually via ?token= because browsers cannot send
        // Authorization headers during the WebSocket upgrade handshake.
        app.Map("/api/servers/{id}/console/stream", HandleStreamRequestAsync);

        app.MapGet("/api/servers/{id}/console/history", GetHistory)
            .RequireAuthorization("FullAccess");

        app.MapPost("/api/servers/{id}/console/input", SendInput)
            .RequireAuthorization("OperatorAccess");
    }

    // --- WebSocket stream ---

    private static async Task HandleStreamRequestAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var id = ctx.Request.RouteValues["id"]?.ToString() ?? string.Empty;
        var tokenService = ctx.RequestServices.GetRequiredService<WebTokenService>();
        // Resolved per-host from this request's own DI container, not process-wide static state -
        // see WebSocketAuthPolicy's doc comment for why that distinction matters here specifically.
        var authPolicy = ctx.RequestServices.GetRequiredService<WebSocketAuthPolicy>();
        string? username = null;

        // Two supported auth paths for the WebSocket upgrade:
        //
        //  A) ?token= present in the query string — validated before the upgrade.
        //     P3-03 (deprecated, opt-out via AppSettings.AllowLegacyWebSocketQueryStringAuth):
        //     the token ends up in server/proxy access logs, browser history, and Referer
        //     headers wherever this URL is shared. Kept only for test clients and non-browser
        //     API consumers that can't do path B's first-frame handshake; the static web UI
        //     never uses it. Do not point new browser-facing code at this path. When the
        //     setting is off, a query token is treated the same as no token at all — the
        //     connection falls through to path B instead of being rejected outright, so a
        //     client that also implements the first-frame handshake keeps working either way.
        //
        //  B) No query token — the upgrade is accepted first, then the client is
        //     expected to send {"token":"<accessToken>"} as its very first frame.
        //     This keeps the access token out of server/proxy access logs and the
        //     browser's URL history. Used by the static web UI.
        var queryToken = ctx.Request.Query["token"].FirstOrDefault() ?? string.Empty;
        var authFromQuery = !string.IsNullOrEmpty(queryToken) && authPolicy.AllowLegacyQueryStringAuth;

        if (authFromQuery)
        {
            if (!tokenService.TryValidateAccessToken(queryToken, out _, out username, out _, out var fpc) || fpc)
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            WebLog.Add($"WebSocket console auth via deprecated ?token= query string: user='{username}', server='{id}'.");
        }

        if (!WebServerState.GetServers().Any(s => s.Id == id))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        // Path A: limit is enforced BEFORE accepting so we can return an HTTP 503.
        // Path B: the limit is enforced AFTER reading the auth frame so unauthenticated
        //         connections (which never send a token) cannot consume authenticated
        //         slots and starve legitimate console users.
        if (authFromQuery)
        {
            var count = ActiveConnections.AddOrUpdate(id, 1, (_, v) => v + 1);
            if (count > MaxConnectionsPerServer)
            {
                ActiveConnections.AddOrUpdate(id, 0, (_, v) => Math.Max(0, v - 1));
                ctx.Response.StatusCode = 503;
                return;
            }
        }
        else
        {
            var pendingCount = PendingAuthConnections.AddOrUpdate(id, 1, (_, v) => v + 1);
            if (pendingCount > MaxPendingAuthConnectionsPerServer)
            {
                PendingAuthConnections.AddOrUpdate(id, 0, (_, v) => Math.Max(0, v - 1));
                ctx.Response.StatusCode = 503;
                return;
            }
        }

        bool counted = authFromQuery;
        bool pendingCounted = !authFromQuery;
        WebSocket ws;
        try
        {
            ws = await ctx.WebSockets.AcceptWebSocketAsync();
        }
        catch
        {
            if (counted) ActiveConnections.AddOrUpdate(id, 0, (_, v) => Math.Max(0, v - 1));
            if (pendingCounted) PendingAuthConnections.AddOrUpdate(id, 0, (_, v) => Math.Max(0, v - 1));
            throw;
        }

        if (!authFromQuery)
        {
            (bool Succeeded, string? Username) authResult = (false, null);
            try
            {
                // Path B: read auth from the first WebSocket frame sent by the browser client.
                authResult = await ReceiveAuthMessageAsync(ws, tokenService, ctx.RequestAborted);
            }
            finally
            {
                if (pendingCounted)
                {
                    PendingAuthConnections.AddOrUpdate(id, 0, (_, v) => Math.Max(0, v - 1));
                    pendingCounted = false;
                }
            }

            if (!authResult.Succeeded)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", closeCts.Token); }
                catch { }
                return;
            }
            username = authResult.Username;

            // Auth confirmed — now check the per-server connection limit.
            var count = ActiveConnections.AddOrUpdate(id, 1, (_, v) => v + 1);
            if (count > MaxConnectionsPerServer)
            {
                ActiveConnections.AddOrUpdate(id, 0, (_, v) => Math.Max(0, v - 1));
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Too many connections", closeCts.Token); }
                catch { }
                return;
            }
            counted = true;
        }

        var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        WebLog.Add($"WebSocket connect: user='{username}', server='{id}', remote={remote}.");
        try
        {
            await StreamAsync(ws, id, ctx.RequestAborted);
        }
        finally
        {
            if (counted) ActiveConnections.AddOrUpdate(id, 0, (_, v) => Math.Max(0, v - 1));
            WebLog.Add($"WebSocket disconnect: user='{username}', server='{id}', remote={remote}.");
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // CloseOutputAsync only sends the close frame — it does not wait for the peer
                // to echo. CloseAsync would block waiting for the peer's echo, which can deadlock
                // if the peer is already gone. The peer's close was already consumed by DrainIncomingAsync.
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", closeCts.Token); }
                catch { }
            }
        }
    }

    // Waits for the browser client's first WebSocket message, which must be
    // {"token":"<accessToken>"}. Accumulates fragments until EndOfMessage so that
    // large JWTs (e.g., long usernames) that arrive across multiple frames are
    // handled correctly. Returns the validated username on success.
    private static async Task<(bool Succeeded, string? Username)> ReceiveAuthMessageAsync(
        WebSocket ws, WebTokenService tokenService, CancellationToken requestAborted)
    {
        const int MaxAuthBytes = 8192; // generous upper bound; rejects oversized messages fast
        var chunk = new byte[2048];
        using var ms = new MemoryStream();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(chunk), timeoutCts.Token);
                if (result.MessageType != WebSocketMessageType.Text)
                    return (false, null);
                ms.Write(chunk, 0, result.Count);
                if (ms.Length > MaxAuthBytes)
                    return (false, null);
            } while (!result.EndOfMessage);

            ms.Position = 0;
            using var doc = JsonDocument.Parse(ms);
            if (!doc.RootElement.TryGetProperty("token", out var tokenEl))
                return (false, null);

            var token = tokenEl.GetString() ?? string.Empty;
            if (!tokenService.TryValidateAccessToken(token, out _, out var username, out _, out var fpc) || fpc)
                return (false, null);

            return (true, username);
        }
        catch
        {
            return (false, null);
        }
    }

    private static async Task StreamAsync(WebSocket ws, string serverId, CancellationToken requestAborted)
    {
        // One channel per connection so each connection gets its own delivery queue.
        var channel = Channel.CreateBounded<ServerConsoleLine>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

        // streamCts is cancelled by either party: requestAborted (HTTP-level disconnect) or
        // DrainIncomingAsync (client sent a WebSocket close frame). Without the drain task the
        // ReadAllAsync loop would block forever waiting for a new console line, causing
        // ws.CloseAsync on the client to deadlock waiting for the server's echo.
        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);

        Action<string, ServerConsoleLine> handler = (sid, line) =>
        {
            if (string.Equals(sid, serverId, StringComparison.Ordinal))
                channel.Writer.TryWrite(line);
        };
        ServerConsoleService.NewLineAdded += handler;

        // Drain incoming frames (including the client's close frame) concurrently.
        var drainTask = DrainIncomingAsync(ws, streamCts);
        try
        {
            var server = WebServerState.GetServers().FirstOrDefault(s => s.Id == serverId);
            var isRunning = server?.Status is ServerRuntimeStatus.Running or ServerRuntimeStatus.Warning;

            if (!isRunning)
            {
                // Signal offline state; keep connection open so lines resume when server starts.
                await SendJsonAsync(ws, new { status = "offline" }, streamCts.Token);
            }
            else
            {
                // Replay the last 100 buffered lines immediately on connect.
                var history = ServerConsoleService.GetLines(serverId);
                foreach (var line in history.TakeLast(DefaultHistoryLines))
                {
                    if (streamCts.Token.IsCancellationRequested) break;
                    await SendJsonAsync(ws, ToLineMessage(line), streamCts.Token);
                }
            }

            // Forward live lines until the socket closes or the request is aborted.
            await foreach (var line in channel.Reader.ReadAllAsync(streamCts.Token))
            {
                if (ws.State != WebSocketState.Open) break;
                await SendJsonAsync(ws, ToLineMessage(line), streamCts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            streamCts.Cancel();
            ServerConsoleService.NewLineAdded -= handler;
            channel.Writer.Complete();
            await drainTask;
            streamCts.Dispose();
        }
    }

    // Reads incoming WebSocket frames so the connection drains normally.
    // Cancels streamCts when the client sends a close frame (or if the socket faults).
    private static async Task DrainIncomingAsync(WebSocket ws, CancellationTokenSource streamCts)
    {
        var buffer = new byte[64];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), streamCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            streamCts.Cancel();
        }
    }

    // --- History ---

    private static IResult GetHistory(string id, int count = DefaultHistoryLines)
    {
        if (!WebServerState.GetServers().Any(s => s.Id == id))
            return Results.NotFound();

        var lines = ServerConsoleService.GetLines(id);
        var take = Math.Clamp(count, 1, MaxHistoryLines);
        return Results.Ok(lines.TakeLast(take).Select(ToLineMessage).ToArray());
    }

    // --- Console input ---

    private static async Task<IResult> SendInput(string id, ConsoleInputBody body, HttpContext ctx)
    {
        if (!WebServerState.GetServers().Any(s => s.Id == id))
            return Results.NotFound();

        if (string.IsNullOrWhiteSpace(body?.Command))
            return Results.BadRequest(new { error = "Command cannot be blank." });

        var username = ctx.User.FindFirst(ClaimTypes.Name)?.Value ?? "web";

        if (!ServerConsoleService.CanSendCommand(id))
            return Results.Conflict(new { error = "Server is not running or does not support console input." });

        try
        {
            // SendCommand can block its calling thread for up to its internal timeout (5s by
            // default) if the server has stopped reading its console input - Task.Run keeps that
            // off this request thread instead of tying it up, matching the same fix already
            // applied to the desktop UI path (ServerInfoWindow's ExecuteModuleCommandAsync call).
            await Task.Run(
                () => ServerConsoleService.SendCommand(id, body.Command),
                ctx.RequestAborted);
            WebLog.Add($"Web console input: user='{username}', server='{id}'.");
            return Results.Ok(new { sent = true });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (TimeoutException ex)
        {
            // SendCommand can now throw TimeoutException (see ServerConsoleService) in addition to
            // InvalidOperationException - both are fixed, generic messages with no settings/secret
            // data in them, safe to return as-is.
            return Results.Conflict(new { error = ex.Message });
        }
    }

    // --- Helpers ---

    private static object ToLineMessage(ServerConsoleLine line) =>
        new { timestamp = line.Timestamp.ToString("O"), line = line.Text };

    private static async Task SendJsonAsync(WebSocket ws, object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}

public sealed record ConsoleInputBody(string? Command);
