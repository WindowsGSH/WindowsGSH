using System.Net;
using WindowsGSH.Discord;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordWebhookNotificationServiceTests
{
    private const string GlobalUrl = "https://discord.com/api/webhooks/100/global-secret";
    private const string ServerUrl = "https://discord.com/api/webhooks/200/server-secret";

    [Fact]
    public async Task Disabled_webhooks_do_not_send()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        using var service = CreateService(handler, enabled: false);

        await service.SendNotificationAsync("hello", server: null);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Message_is_rendered_as_discord_content_payload()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        using var service = CreateService(handler);

        await service.SendNotificationAsync("Server started.", server: null);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(GlobalUrl, request.Url);
        Assert.Contains("\"content\":\"Server started.\"", request.Body);
    }

    [Fact]
    public void Server_webhook_overrides_global_and_missing_override_falls_back()
    {
        using var service = CreateService(new RecordingHandler(HttpStatusCode.NoContent));

        Assert.Equal(ServerUrl, service.ResolveWebhook("1"));
        Assert.Equal(GlobalUrl, service.ResolveWebhook("2"));
        Assert.Equal(GlobalUrl, service.ResolveWebhook(null));
    }

    [Fact]
    public async Task Transient_failures_are_retried()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.NoContent);
        var delays = new List<TimeSpan>();
        using var service = CreateService(handler, delayAsync: delay =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await service.SendNotificationAsync("retry me", server: null);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Permanent_failure_is_logged_without_webhook_secret()
    {
        var logs = new List<string>();
        var handler = new RecordingHandler(HttpStatusCode.BadRequest);
        using var service = CreateService(handler, log: logs.Add);

        await service.SendNotificationAsync("bad request", server: null);

        Assert.Single(handler.Requests);
        Assert.DoesNotContain(logs, line => line.Contains("global-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Redaction_removes_discord_webhook_urls()
    {
        var redacted = DiscordWebhookNotificationService.RedactWebhookUrls("Failed " + GlobalUrl);

        Assert.DoesNotContain("global-secret", redacted);
        Assert.Contains("<discord-webhook-redacted>", redacted);
    }

    private static DiscordWebhookNotificationService CreateService(
        HttpMessageHandler handler,
        bool enabled = true,
        Func<TimeSpan, Task>? delayAsync = null,
        Action<string>? log = null)
    {
        return new DiscordWebhookNotificationService(
            () => enabled,
            () => GlobalUrl,
            serverId => serverId == "1" ? ServerUrl : null,
            new HttpClient(handler),
            delayAsync,
            log);
    }

    private sealed class RecordingHandler(params HttpStatusCode[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _responses = new(responses);

        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            var status = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return new HttpResponseMessage(status);
        }
    }
}
