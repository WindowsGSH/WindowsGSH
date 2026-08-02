using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WindowsGSH.Core.Health;
using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Diagnostics;

public sealed partial class SupportBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SupportBundleResult Export(SupportBundleRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppRoot);

        var outputPath = Path.GetFullPath(request.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Support bundle output directory could not be determined.");
        Directory.CreateDirectory(outputDirectory);

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var entries = 0;
        using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            AddText(archive, "bundle-info.txt", BuildBundleInfo(request));
            entries++;

            AddText(archive, "logs/app.log", RedactText(request.AppLogText));
            entries++;

            AddText(archive, "operations/history.json", BuildOperationHistory(request.OperationHistory));
            entries++;

            entries += AddFiles(
                archive,
                Path.Combine(request.AppRoot, "logs", "crashes"),
                "logs/crashes",
                "*.log",
                SearchOption.TopDirectoryOnly);
            entries += AddFiles(
                archive,
                Path.Combine(request.AppRoot, "logs", "diagnostics"),
                "logs/diagnostics",
                "runtime-*.jsonl",
                SearchOption.TopDirectoryOnly);
            entries += AddFiles(
                archive,
                Path.Combine(request.AppRoot, "modules", "installed"),
                "modules",
                "module.json",
                SearchOption.AllDirectories);
            entries += AddServerSummaries(
                archive,
                Path.Combine(request.AppRoot, "servers"),
                BuildHealthReportIndex(request.ServerHealthReports),
                request.AppRoot);
        }

        return new SupportBundleResult(outputPath, entries);
    }

    // SourceFolderId is the authoritative correlation key when the caller can provide it. Configured
    // server ids are retained as a compatibility fallback for older/caller-created reports, but they
    // are not unique enough to safely identify reports from broken duplicate-id configurations.
    private static HealthReportIndex BuildHealthReportIndex(
        IReadOnlyList<ServerHealthReport>? reports)
    {
        if (reports == null || reports.Count == 0)
        {
            return new HealthReportIndex(
                new Dictionary<string, ServerHealthReport>(),
                new Dictionary<string, ServerHealthReport>());
        }

        var byId = new Dictionary<string, ServerHealthReport>(StringComparer.OrdinalIgnoreCase);
        var bySourceFolder = new Dictionary<string, ServerHealthReport>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in reports)
        {
            // Deliberately not filtered by IsNullOrWhiteSpace - InstalledServerLoader.GetString (and
            // ResolveServerId below, which mirrors it exactly) preserves an empty or whitespace-only
            // "id" value verbatim rather than falling back to the folder name, since a JSON string
            // value is never null even when it's empty. A server configured that way still produces
            // a real ServerHealthReport keyed by that same whitespace-ish ServerId; discarding it here
            // would mean it could never be correlated back to its summary.json, even though
            // ResolveServerId resolves to the exact same key independently.
            if (report.ServerId != null)
            {
                byId[report.ServerId] = report;
            }

            if (!string.IsNullOrWhiteSpace(report.SourceFolderId))
            {
                bySourceFolder[report.SourceFolderId] = report;
            }
        }

        return new HealthReportIndex(byId, bySourceFolder);
    }

    private sealed record HealthReportIndex(
        IReadOnlyDictionary<string, ServerHealthReport> ByServerId,
        IReadOnlyDictionary<string, ServerHealthReport> BySourceFolder);

    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = text;
        try
        {
            var node = JsonNode.Parse(text);
            if (node != null)
            {
                RedactJson(node);
                redacted = node.ToJsonString(JsonOptions);
            }
        }
        catch (JsonException)
        {
        }

        redacted = SecretAssignmentRegex().Replace(redacted, match =>
        {
            var quote = match.Groups["double"].Success
                ? "\""
                : match.Groups["single"].Success
                    ? "'"
                    : string.Empty;
            return $"{match.Groups["prefix"].Value}{quote}[REDACTED]{quote}";
        });
        redacted = DiscordWebhookRegex().Replace(redacted, "https://discord.com/api/webhooks/[REDACTED]");
        redacted = DiscordTokenRegex().Replace(redacted, "[REDACTED]");
        return redacted;
    }

    private static void RedactJson(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (IsSecretKey(property.Key))
                {
                    jsonObject[property.Key] = "[REDACTED]";
                }
                else if (property.Value != null)
                {
                    RedactJson(property.Value);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                if (item != null)
                {
                    RedactJson(item);
                }
            }
        }
    }

    private static bool IsSecretKey(string key)
    {
        var normalized = Regex.Replace(key, "[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase);
        return normalized.Contains("password", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("token", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("webhook", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("gslt", StringComparison.OrdinalIgnoreCase);
    }

    private static int AddServerSummaries(
        ZipArchive archive,
        string serversRoot,
        HealthReportIndex healthReports,
        string appRoot)
    {
        if (!Directory.Exists(serversRoot))
        {
            return 0;
        }

        var count = 0;
        var serverIndex = 0;
        foreach (var serverDirectory in Directory.EnumerateDirectories(serversRoot)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var configPath = Path.Combine(serverDirectory, "ServerConfig.json");

            // Mirrors InstalledServerLoader.CreateProblemServer's own id derivation for a missing or
            // unreadable ServerConfig.json (Path.GetFileName(serverFolder)) - a "problem server" is
            // still evaluated by ServerHealthService, producing a real "Config file is
            // missing"/"could not be read" ServerHealthReport keyed by that same folder-name id.
            // That diagnosis is exactly what a support bundle exists to carry; it must not be
            // silently dropped just because there's no readable config left to summarise.
            var folderId = Path.GetFileName(serverDirectory);

            if (!File.Exists(configPath))
            {
                // No summary.json is possible here (there's no config to read at all), but the
                // health report - which exists independently of this file scan - can still be
                // included on its own.
                if (TryGetHealthReport(healthReports, null, folderId, out var missingConfigReport))
                {
                    serverIndex++;
                    AddText(
                        archive,
                        $"servers/server-{serverIndex:D3}/health.json",
                        BuildServerHealthJson(missingConfigReport, appRoot));
                    count++;
                }

                continue;
            }

            serverIndex++;
            var (serverId, summaryJson) = BuildServerSummary(configPath, serverDirectory);
            AddText(
                archive,
                $"servers/server-{serverIndex:D3}/summary.json",
                summaryJson);
            count++;

            // Prefer the source-folder identity supplied by the live loader. Configured ids are only
            // a compatibility fallback: they may be duplicated or may differ from a problem card's
            // folder-name id when a later module operation fails.
            if (TryGetHealthReport(healthReports, serverId, folderId, out var report))
            {
                AddText(
                    archive,
                    $"servers/server-{serverIndex:D3}/health.json",
                    BuildServerHealthJson(report, appRoot));
                count++;
            }
        }

        return count;
    }

    private static bool TryGetHealthReport(
        HealthReportIndex healthReports,
        string? serverId,
        string folderId,
        out ServerHealthReport report)
    {
        if (healthReports.BySourceFolder.TryGetValue(folderId, out report!))
        {
            return true;
        }

        if (serverId != null && healthReports.ByServerId.TryGetValue(serverId, out report!))
        {
            return true;
        }

        return healthReports.ByServerId.TryGetValue(folderId, out report!);
    }

    private static (string? ServerId, string SummaryJson) BuildServerSummary(string configPath, string serverDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            var settingsCount = root.TryGetProperty("settings", out var settings) &&
                settings.ValueKind == JsonValueKind.Object
                    ? settings.EnumerateObject().Count()
                    : 0;
            var installPath = ReadString(root, "installPath");
            var steam = root.TryGetProperty("steam", out var steamElement) &&
                steamElement.ValueKind == JsonValueKind.Object
                    ? steamElement
                    : default;
            var summary = new
            {
                SummaryVersion = 1,
                ConfigStatus = "Valid",
                ModuleId = ReadString(root, "moduleId"),
                Runtime = ReadString(root, "runtime"),
                UsesDefaultInstallPath = string.IsNullOrWhiteSpace(installPath) ||
                    string.Equals(installPath, "files", StringComparison.OrdinalIgnoreCase),
                SteamAppId = ReadString(steam, "appId"),
                HasCustomSteamBranch = !string.IsNullOrWhiteSpace(ReadString(steam, "branch")) &&
                    !string.Equals(ReadString(steam, "branch"), "public", StringComparison.OrdinalIgnoreCase),
                SettingsCount = settingsCount,
                ConfiguredSections = KnownServerConfigSections
                    .Where(section => root.TryGetProperty(section, out var value) &&
                        value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                    .ToArray()
            };
            var serverId = ResolveServerId(root, serverDirectory);
            return (serverId, JsonSerializer.Serialize(summary, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            var unreadableSummary = JsonSerializer.Serialize(new
            {
                SummaryVersion = 1,
                ConfigStatus = "Unreadable",
                ErrorType = ex.GetType().Name
            }, JsonOptions);
            return (null, unreadableSummary);
        }
    }

    // Mirrors InstalledServerLoader.GetString's own id-resolution *exactly*, not just its "read a
    // string property" shape - InstalledServer.Id (and therefore ServerHealthReport.ServerId, which
    // this value must correlate against) can resolve from a JSON number/true/false "id" value too,
    // via that same tolerant conversion, not only a JSON string. A narrower string-only read would
    // silently fall back to the folder name for exactly the shapes InstalledServerLoader itself
    // still accepts as a real id - a real, if narrow, correlation bug for any server whose "id"
    // happens to be stored as something other than a JSON string.
    private static string ResolveServerId(JsonElement root, string serverDirectory)
    {
        var fallback = Path.GetFileName(serverDirectory);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback
        };
    }

    // ServerHealthCheck.Message is already vetted, across many rounds of review on ServerHealthService
    // itself, to never surface a raw module/resolver exception or config value verbatim - but this is
    // still an external-ish, occasionally-module-influenced source of text (e.g. a module's own
    // provenance warning), so it gets the same RedactText defense-in-depth pass every other bundle
    // section applies, rather than being trusted solely on that history.
    //
    // Several checks (config-missing, install-folder-exists, etc.) deliberately do include the
    // server's absolute config/install path in their message, because that's genuinely useful
    // diagnostic detail - but an absolute path under the current user's profile directory also
    // carries that user's Windows account name. RedactText only targets credential-shaped values, so
    // it never touches these paths; RedactPaths below is a separate, additive pass that swaps out the
    // app-root and user-profile portions specifically, in line with 5.6's "remove home-directory
    // names where practical" requirement.
    //
    // check.Name gets the identical treatment, not just check.Message - it isn't always an app-chosen
    // literal. AddModuleReadinessChecksAsync copies a module's own ReadinessCheckResult.Name directly
    // into it, and AddBackupChecks does the same with a module's own GetBackupTargets() target.Label -
    // both are module-controlled strings a poorly-behaved or malicious module could set to a
    // credential-shaped or path-containing value, exactly like Message. Redacting an ordinary app
    // literal like "Config file" is a no-op (nothing in it matches either pass), so this is free
    // defense-in-depth for the literals and a real fix for the module-controlled cases.
    private static string BuildServerHealthJson(ServerHealthReport report, string appRoot)
    {
        var checks = report.Checks.Select(check => new
        {
            check.Category,
            Name = RedactText(RedactPaths(check.Name, appRoot)),
            Severity = check.Severity.ToString(),
            Message = RedactText(RedactPaths(check.Message, appRoot))
        });
        var health = new
        {
            SummaryVersion = 1,
            OverallSeverity = report.OverallSeverity.ToString(),
            Checks = checks
        };
        return JsonSerializer.Serialize(health, JsonOptions);
    }

    private static string RedactPaths(string text, string appRoot)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = text;
        redacted = ReplacePathVariants(redacted, appRoot, "[APP_ROOT]");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        redacted = ReplacePathVariants(redacted, userProfile, "[USER_PROFILE]");

        return redacted;
    }

    // appRoot/userProfile are always backslash-form here (from Path.Combine/
    // Environment.GetFolderPath on Windows), but the text being redacted can be module-controlled
    // (a readiness check's Name/Message, a backup target's Label) - a module could render the same
    // path using forward slashes instead (e.g. "C:/Users/Alice/..."), which a single literal,
    // backslash-only replace would never match, leaving the account name visible. Replacing both
    // concrete variants explicitly - rather than normalising the arbitrary module text itself, which
    // could corrupt unrelated content that happens to contain a slash - catches both without that risk.
    private static string ReplacePathVariants(string text, string path, string placeholder)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return text;
        }

        var forwardSlashVariant = path.Replace('\\', '/');
        return text
            .Replace(path, placeholder, StringComparison.OrdinalIgnoreCase)
            .Replace(forwardSlashVariant, placeholder, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static int AddFiles(
        ZipArchive archive,
        string root,
        string destinationRoot,
        string searchPattern,
        SearchOption searchOption)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var count = 0;
        foreach (var path in Directory.EnumerateFiles(root, searchPattern, searchOption)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            AddFile(archive, path, $"{destinationRoot}/{relative}");
            count++;
        }

        return count;
    }

    private static void AddFile(ZipArchive archive, string path, string entryName)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = $"File could not be read: {ex.Message}";
        }

        AddText(archive, entryName, RedactText(text));
    }

    private static string BuildBundleInfo(SupportBundleRequest request)
    {
        var recoveryEvents = LocalStateRecoveryStatus.Snapshot();
        var recoverySummary = recoveryEvents.Count == 0
            ? "None reported this session."
            : string.Join(
                Environment.NewLine,
                recoveryEvents.Select(item =>
                    $"- {item.CreatedUtc:O} [{item.Area}] {item.Message}" +
                    (string.IsNullOrWhiteSpace(item.RecoveryPath)
                        ? string.Empty
                        : $" Recovery file: {Path.GetFileName(item.RecoveryPath)}")));
        return $"""
            WindowsGSH Support Bundle
            =========================
            Created UTC: {DateTimeOffset.UtcNow:O}
            App Version: {request.AppVersion}
            Module API Version: {request.ModuleApiVersion}
            OS: {Environment.OSVersion}
            Framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}
            Rendering Mode: {request.RenderingMode}
            Reduced Motion: {request.ReducedMotion}

            Local State Recovery
            --------------------
            {recoverySummary}

            This archive contains redacted diagnostics. Review it before sharing.
            """;
    }

    private static string BuildOperationHistory(IReadOnlyList<ServerOperationSnapshot> operations)
    {
        var rows = operations.Select(operation => new
        {
            operation.ServerId,
            operation.ServerName,
            Kind = operation.Kind.ToString(),
            operation.Status,
            StartedUtc = operation.StartedAt.ToUniversalTime(),
            FinishedUtc = operation.FinishedAt?.ToUniversalTime(),
            LastError = RedactText(operation.LastError)
        });
        return JsonSerializer.Serialize(rows, JsonOptions);
    }

    private static void AddText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static readonly string[] KnownServerConfigSections =
    [
        "automation",
        "backup",
        "discord",
        "java",
        "network",
        "runtimeSettings",
        "schedules",
        "addons"
    ];

    [GeneratedRegex(
        @"(?im)(?<prefix>\b(?:server[_-]?password|rcon[_-]?password|admin[_-]?password|password|secret|token|api[_-]?key|webhook(?:url)?|credential|authorization|(?:server[._-]?)?gslt)\b\s*[:=]\s*)(?:""(?<double>[^""\r\n]*)""|'(?<single>[^'\r\n]*)'|(?<plain>[^\r\n,;}]*?)(?=\s+\b[A-Za-z][\w.-]*\s*[:=]|\s*$|[,;}]))")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"https://(?:discord(?:app)?\.com)/api/webhooks/[^\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex DiscordWebhookRegex();

    [GeneratedRegex(@"\b(?:mfa\.[A-Za-z0-9_-]{20,}|[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{20,})\b")]
    private static partial Regex DiscordTokenRegex();
}

public sealed record SupportBundleRequest(
    string OutputPath,
    string AppRoot,
    string AppVersion,
    string ModuleApiVersion,
    string AppLogText,
    IReadOnlyList<ServerOperationSnapshot> OperationHistory,
    string RenderingMode = "Unknown",
    bool ReducedMotion = false,
    // Freshly-evaluated Server Doctor results, one per server the caller could successfully assess
    // at export time - optional and additive (default null) so existing callers/tests keep compiling
    // unchanged. A server with no matching entry here (not supplied at all, or its own evaluation
    // failed) still gets its summary.json; it just has no accompanying health.json.
    IReadOnlyList<ServerHealthReport>? ServerHealthReports = null);

public sealed record SupportBundleResult(string OutputPath, int EntryCount);
