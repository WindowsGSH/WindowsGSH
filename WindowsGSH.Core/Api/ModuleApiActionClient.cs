using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Api;

public sealed class ModuleApiActionClient
{
    private static readonly Regex PlaceholderPattern = new(@"\{(?<scope>setting|param):(?<key>[^}]+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Shared, not per-call: a weeks-uptime process calling this on every server action would
    // otherwise exhaust ephemeral ports under load (each HttpClient owns its own connection pool
    // that a per-call `using` immediately tears down). PooledConnectionLifetime bounds how long a
    // pooled connection is reused so a long-lived process still picks up DNS/address changes for
    // module API hosts that aren't the 127.0.0.1 default. Auth varies per call (different servers,
    // different credentials) so it's set on each HttpRequestMessage below, never on this client's
    // DefaultRequestHeaders - that would race across concurrent calls for different servers.
    // UseCookies = false: SocketsHttpHandler's default cookie container is shared by every request
    // through this one client, and cookie matching ignores port - a Set-Cookie from one server's
    // module API on 127.0.0.1:portA would otherwise be replayed against a different server's API
    // on 127.0.0.1:portB despite each call carrying its own distinct Basic-auth credentials (the
    // only auth this client actually uses). Per-call HttpClient instances never had this problem
    // since each one's cookie state was thrown away with it.
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public async Task<ModuleApiActionResult> ExecuteAsync(
        ServerInstance instance,
        ModuleApiConnectionDefinition connection,
        ModuleApiActionDefinition action,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(connection.EnabledKey) && !IsTruthy(GetSetting(instance.Settings, connection.EnabledKey, false)))
        {
            throw new InvalidOperationException("The module API is not enabled for this server.");
        }

        var port = GetRequiredSetting(instance.Settings, connection.PortKey);
        var password = GetRequiredSetting(instance.Settings, connection.PasswordKey);
        var username = string.IsNullOrWhiteSpace(connection.UsernameKey)
            ? string.Empty
            : GetSetting(instance.Settings, connection.UsernameKey, string.Empty)?.ToString() ?? string.Empty;
        var host = string.IsNullOrWhiteSpace(connection.Host) ? "127.0.0.1" : connection.Host;
        var baseUrl = $"{connection.Scheme}://{host}:{port}";
        var path = ResolveTemplate(action.Path, instance.Settings, parameters, forJson: false);
        var url = baseUrl + (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path);

        using var request = new HttpRequestMessage(new HttpMethod(action.Method), url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password))
        {
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        if (!string.IsNullOrWhiteSpace(action.BodyTemplate))
        {
            var body = ResolveTemplate(action.BodyTemplate, instance.Settings, parameters, forJson: true);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await SharedClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}");
        }

        return new ModuleApiActionResult(
            response.StatusCode,
            string.IsNullOrWhiteSpace(responseBody) ? "OK" : PrettyJson(responseBody));
    }

    private static string ResolveTemplate(
        string template,
        IReadOnlyDictionary<string, object?> settings,
        IReadOnlyDictionary<string, object?> parameters,
        bool forJson)
    {
        return PlaceholderPattern.Replace(template, match =>
        {
            var scope = match.Groups["scope"].Value;
            var key = match.Groups["key"].Value;
            var value = scope.Equals("param", StringComparison.OrdinalIgnoreCase)
                ? GetSetting(parameters, key, string.Empty)
                : GetSetting(settings, key, string.Empty);

            return forJson
                ? JsonSerializer.Serialize(value)
                : Uri.EscapeDataString(value?.ToString() ?? string.Empty);
        });
    }

    private static object? GetSetting(IReadOnlyDictionary<string, object?> values, string key, object? fallback)
    {
        return values.TryGetValue(key, out var value) && value != null && !string.IsNullOrWhiteSpace(value.ToString())
            ? value
            : fallback;
    }

    private static string GetRequiredSetting(IReadOnlyDictionary<string, object?> values, string key)
    {
        return values.TryGetValue(key, out var value) && value != null && !string.IsNullOrWhiteSpace(value.ToString())
            ? value.ToString()!.Trim()
            : throw new InvalidOperationException($"API setting '{key}' is required.");
    }

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool boolean => boolean,
            string text => bool.TryParse(text, out var parsed) ? parsed : !string.IsNullOrWhiteSpace(text),
            int number => number != 0,
            long number => number != 0,
            double number => Math.Abs(number) > double.Epsilon,
            _ => value != null
        };
    }

    private static string PrettyJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return text;
        }
    }
}

public sealed record ModuleApiActionResult(System.Net.HttpStatusCode StatusCode, string Body);
