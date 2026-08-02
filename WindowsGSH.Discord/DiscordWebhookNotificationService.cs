using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Discord;

// This is a pure send transport: it does not subscribe to server events itself.
// A single shared DiscordEventAlertService (see MainWindow) decides whether to alert and renders the
// message once, then fans out to this and/or the bot transport. Do not give this its own
// DiscordEventAlertService again - that previously caused every alert to be evaluated and sent twice
// whenever both a bot Alert Channel and a webhook were configured for the same server.
public sealed class DiscordWebhookNotificationService : IDisposable
{
    private const int MaxAttempts = 3;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string?> _resolveGlobalWebhook;
    private readonly Func<string, string?> _resolveServerWebhook;
    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, Task> _delayAsync;
    private readonly Action<string> _log;
    private readonly bool _disposeHttpClient;

    public DiscordWebhookNotificationService(
        Func<bool> isEnabled,
        Func<string?> resolveGlobalWebhook,
        Func<string, string?> resolveServerWebhook,
        HttpClient? httpClient = null,
        Func<TimeSpan, Task>? delayAsync = null,
        Action<string>? log = null)
    {
        _isEnabled = isEnabled;
        _resolveGlobalWebhook = resolveGlobalWebhook;
        _resolveServerWebhook = resolveServerWebhook;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _disposeHttpClient = httpClient == null;
        _delayAsync = delayAsync ?? Task.Delay;
        _log = log ?? (_ => { });
    }

    public Task SendTestAlertAsync(InstalledServer? server = null)
    {
        return SendNotificationAsync("WindowsGSH Discord webhook test alert.", server);
    }

    public async Task SendNotificationAsync(string message, InstalledServer? server)
    {
        if (!_isEnabled() || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var webhookUrl = ResolveWebhook(server?.Id);
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            _log(server == null
                ? "Discord webhook alert was not sent because no global webhook is configured."
                : $"Discord webhook alert for server {server.Id} was not sent because no server or global webhook is configured.");
            return;
        }

        var payload = JsonSerializer.Serialize(new { content = Truncate(message.Trim()) });
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(webhookUrl, content).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                if (!IsTransient(response.StatusCode) || attempt == MaxAttempts)
                {
                    _log($"Discord webhook send failed with HTTP {(int)response.StatusCode} after {attempt} attempt(s).");
                    return;
                }

                await _delayAsync(GetRetryDelay(response, attempt)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt == MaxAttempts)
                {
                    _log("Discord webhook send failed after retries: " + RedactWebhookUrls(ex.Message));
                    return;
                }

                await _delayAsync(TimeSpan.FromSeconds(attempt)).ConfigureAwait(false);
            }
        }
    }

    public string? ResolveWebhook(string? serverId)
    {
        if (!string.IsNullOrWhiteSpace(serverId))
        {
            var serverWebhook = _resolveServerWebhook(serverId);
            if (!string.IsNullOrWhiteSpace(serverWebhook))
            {
                return serverWebhook;
            }
        }

        return _resolveGlobalWebhook();
    }

    public static string RedactWebhookUrls(string value)
    {
        return Regex.Replace(
            value ?? string.Empty,
            @"https://(?:[\w-]+\.)?discord(?:app)?\.com/api/webhooks/\d+/[^\s""']+",
            "<discord-webhook-redacted>",
            RegexOptions.IgnoreCase);
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
               (int)statusCode >= 500;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        return response.Headers.RetryAfter?.Delta is { } retryAfter && retryAfter > TimeSpan.Zero
            ? retryAfter
            : TimeSpan.FromSeconds(attempt);
    }

    private static string Truncate(string value)
    {
        const int maxLength = 1900;
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }
}
