using System.IO.Compression;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.Health;
using WindowsGSH.Core.Operations;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(LocalStateRecoveryTestCollection.Name)]
public sealed class SupportBundleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH-support-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Export_includes_recovery_status_without_database_contents()
    {
        LocalStateRecoveryStatus.Clear();
        LocalStateRecoveryStatus.Report(
            "Database",
            "A pre-migration backup was created.",
            Path.Combine(_root, "recovery", "WindowsGSH.db.pre-migration-v4.bak"));
        var output = Path.Combine(_root, "recovery-support.zip");

        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            "Software",
            ReducedMotion: true));

        using var archive = ZipFile.OpenRead(output);
        var info = ReadEntry(archive.GetEntry("bundle-info.txt")!);
        Assert.Contains("pre-migration backup", info);
        Assert.Contains("WindowsGSH.db.pre-migration-v4.bak", info);
        Assert.Contains("Rendering Mode: Software", info);
        Assert.Contains("Reduced Motion: True", info);
        Assert.DoesNotContain("CREATE TABLE", info);
        LocalStateRecoveryStatus.Clear();
    }

    [Fact]
    public void Export_includes_expected_diagnostics_and_redacts_secrets()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs", "crashes"));
        Directory.CreateDirectory(Path.Combine(_root, "logs", "diagnostics"));
        Directory.CreateDirectory(Path.Combine(_root, "modules", "installed", "example"));
        Directory.CreateDirectory(Path.Combine(_root, "servers", "1"));
        Directory.CreateDirectory(Path.Combine(_root, "servers", "1", "files", "nested"));
        File.WriteAllText(
            Path.Combine(_root, "logs", "crashes", "app-crash.log"),
            "token=crash-secret https://discord.com/api/webhooks/123/crash-webhook");
        File.WriteAllText(
            Path.Combine(_root, "logs", "diagnostics", "runtime-2026-07-26.jsonl"),
            """{"WorkingSetBytes":1234,"ManagedHeapBytes":456}""");
        File.WriteAllText(
            Path.Combine(_root, "modules", "installed", "example", "module.json"),
            """{"id":"example","apiKey":"module-secret"}""");
        File.WriteAllText(
            Path.Combine(_root, "servers", "1", "ServerConfig.json"),
            """
            {
              "id": "private-id",
              "name": "Private Server Name",
              "moduleId": "paper",
              "runtime": "java",
              "installPath": "D:\\Private\\Game",
              "steam": { "appId": "12345", "branch": "private-beta" },
              "settings": {
                "password": "server-secret",
                "server.gslt": "json-gslt-secret",
                "port": 25565,
                "publicIp": "203.0.113.10",
                "discordId": "1234567890",
                "inviteCode": "private-invite",
                "endpoint": "https://private.example.test"
              },
              "discord": { "channelId": "9999999999" },
              "network": { "publicIp": "203.0.113.10" }
            }
            """);
        File.WriteAllText(
            Path.Combine(_root, "servers", "1", "files", "nested", "ServerConfig.json"),
            """{"server.gslt":"install-tree-secret"}""");

        var output = Path.Combine(_root, "support.zip");
        var operation = new ServerOperationSnapshot(
            "1",
            "Test",
            ServerOperationKind.Start,
            "Failed",
            DateTimeOffset.UtcNow,
            "authorization=operation-secret",
            IsActive: false,
            DateTimeOffset.UtcNow);

        var result = new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.2.3",
            "1.0",
            "password=app-secret server.gslt=log-gslt-secret",
            [operation]));

        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(7, result.EntryCount);

        using var archive = ZipFile.OpenRead(output);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("bundle-info.txt", entryNames);
        Assert.Contains("logs/app.log", entryNames);
        Assert.Contains("operations/history.json", entryNames);
        Assert.Contains("logs/crashes/app-crash.log", entryNames);
        Assert.Contains("logs/diagnostics/runtime-2026-07-26.jsonl", entryNames);
        Assert.Contains("modules/example/module.json", entryNames);
        Assert.Contains("servers/server-001/summary.json", entryNames);
        Assert.DoesNotContain(entryNames, name => name.EndsWith("ServerConfig.json", StringComparison.OrdinalIgnoreCase));

        var combined = string.Join(
            Environment.NewLine,
            archive.Entries.Select(ReadEntry));
        Assert.Contains("[REDACTED]", combined);
        Assert.DoesNotContain("app-secret", combined);
        Assert.DoesNotContain("crash-secret", combined);
        Assert.DoesNotContain("crash-webhook", combined);
        Assert.DoesNotContain("module-secret", combined);
        Assert.DoesNotContain("server-secret", combined);
        Assert.DoesNotContain("json-gslt-secret", combined);
        Assert.DoesNotContain("log-gslt-secret", combined);
        Assert.DoesNotContain("install-tree-secret", combined);
        Assert.DoesNotContain("operation-secret", combined);
        Assert.DoesNotContain("Private Server Name", combined);
        Assert.DoesNotContain(@"D:\Private\Game", combined);
        Assert.DoesNotContain("203.0.113.10", combined);
        Assert.DoesNotContain("1234567890", combined);
        Assert.DoesNotContain("9999999999", combined);
        Assert.DoesNotContain("private-invite", combined);
        Assert.DoesNotContain("private.example.test", combined);
        Assert.Contains("\"ModuleId\": \"paper\"", combined);
        Assert.Contains("\"Runtime\": \"java\"", combined);
        Assert.Contains("\"SteamAppId\": \"12345\"", combined);
        Assert.Contains("\"HasCustomSteamBranch\": true", combined);
        Assert.Contains("\"SettingsCount\": 7", combined);
        Assert.Contains("\"discord\"", combined);
        Assert.Contains("\"network\"", combined);
    }

    [Theory]
    [InlineData("""{"nested":{"webhookUrl":"https://example.invalid/secret"},"name":"safe"}""")]
    [InlineData("api_key=plain-secret")]
    [InlineData("server.gslt=steam-login-token")]
    [InlineData("""{"servergslt":"steam-login-token"}""")]
    [InlineData("serverpassword=plain-server-password")]
    [InlineData("rcon_password=rcon-secret")]
    [InlineData("adminPassword=\"admin secret with spaces\"")]
    [InlineData("password='password value with spaces'")]
    [InlineData("password=unquoted value with spaces port=25565")]
    [InlineData("mfa.abcdefghijklmnopqrstuvwxyz0123456789")]
    public void RedactText_removes_common_secret_formats(string input)
    {
        var output = SupportBundleService.RedactText(input);

        Assert.Contains("[REDACTED]", output);
        Assert.DoesNotContain("secret", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password value", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unquoted value", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.invalid", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789", output);
    }

    [Fact]
    public void Export_does_not_include_nested_install_tree_configs()
    {
        var serverRoot = Path.Combine(_root, "servers", "1");
        Directory.CreateDirectory(Path.Combine(serverRoot, "files", "nested"));
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");
        File.WriteAllText(
            Path.Combine(serverRoot, "files", "nested", "ServerConfig.json"),
            """{"name":"must-not-be-read"}""");

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            []));

        using var archive = ZipFile.OpenRead(output);
        Assert.Single(archive.Entries, entry => entry.FullName.StartsWith("servers/", StringComparison.Ordinal));
        Assert.DoesNotContain(
            archive.Entries.Select(ReadEntry),
            content => content.Contains("must-not-be-read", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_includes_health_report_for_a_server_matched_by_config_id()
    {
        var serverRoot = Path.Combine(_root, "servers", "1");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"id":"server-a","moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");

        var report = new ServerHealthReport(
            "server-a",
            "Server A",
            [new ServerHealthCheck("Network", "Listening", ServerHealthSeverity.Pass, "All configured ports are listening.")]);

        var output = Path.Combine(_root, "support.zip");
        var result = new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("servers/server-001/summary.json", entryNames);
        Assert.Contains("servers/server-001/health.json", entryNames);
        // health.json is one additional entry beyond the baseline three (bundle-info/app.log/
        // operations) plus the summary.json this same server already contributes.
        Assert.Equal(5, result.EntryCount);

        var healthJson = ReadEntry(archive.GetEntry("servers/server-001/health.json")!);
        Assert.Contains("\"OverallSeverity\": \"Pass\"", healthJson);
        Assert.Contains("\"Category\": \"Network\"", healthJson);
        Assert.Contains("\"Name\": \"Listening\"", healthJson);
        Assert.Contains("All configured ports are listening.", healthJson);
    }

    [Fact]
    public void Export_matches_health_report_by_folder_name_when_config_has_no_id()
    {
        // Mirrors InstalledServerLoader's own id-resolution fallback exactly (config "id" property,
        // then the server's own folder name) - a config with no "id" at all is a normal, supported
        // shape, not a corrupt one.
        var serverRoot = Path.Combine(_root, "servers", "folder-id");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");

        var report = new ServerHealthReport(
            "folder-id",
            "Folder Id Server",
            [new ServerHealthCheck("Process", "Running", ServerHealthSeverity.Info, "No matching process is currently running for this server.")]);

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        Assert.Contains("servers/server-001/health.json", archive.Entries.Select(entry => entry.FullName));
    }

    [Fact]
    public void Export_matches_health_report_when_config_id_is_a_json_number()
    {
        // Regression guard: InstalledServerLoader's own id resolution (GetString) tolerates a
        // non-string JSON "id" value - a number becomes its string representation, true/false become
        // "true"/"false" - falling back to the folder name only when the property is genuinely
        // missing or some other JSON kind entirely. InstalledServer.Id (and therefore
        // ServerHealthReport.ServerId) can legitimately be "42" for a config with `"id": 42`; a
        // narrower string-only read here would wrongly resolve this server's id as its folder name
        // instead, silently failing to match an available, correctly-keyed health report.
        var serverRoot = Path.Combine(_root, "servers", "numeric-id-folder");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"id":42,"moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");

        var report = new ServerHealthReport(
            "42",
            "Numeric Id Server",
            [new ServerHealthCheck("Process", "Running", ServerHealthSeverity.Pass, "Process is running.")]);

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        Assert.Contains("servers/server-001/health.json", archive.Entries.Select(entry => entry.FullName));
    }

    [Fact]
    public void Export_omits_health_json_when_no_report_matches_this_server()
    {
        var serverRoot = Path.Combine(_root, "servers", "1");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"id":"server-a","moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");

        var unrelatedReport = new ServerHealthReport(
            "some-other-server",
            "Other Server",
            [new ServerHealthCheck("Process", "Running", ServerHealthSeverity.Pass, "Process is running.")]);

        var output = Path.Combine(_root, "support.zip");
        var result = new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [unrelatedReport]));

        using var archive = ZipFile.OpenRead(output);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("servers/server-001/summary.json", entryNames);
        Assert.DoesNotContain(entryNames, name => name.EndsWith("health.json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, result.EntryCount);
    }

    [Fact]
    public void Export_redacts_secrets_embedded_in_health_check_messages()
    {
        var serverRoot = Path.Combine(_root, "servers", "1");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"id":"server-a","moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");

        var report = new ServerHealthReport(
            "server-a",
            "Server A",
            [new ServerHealthCheck(
                "Operations",
                "Recent history",
                ServerHealthSeverity.Warning,
                "The most recent operation failed: authorization=health-check-secret")]);

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        var healthJson = ReadEntry(archive.GetEntry("servers/server-001/health.json")!);
        Assert.Contains("[REDACTED]", healthJson);
        Assert.DoesNotContain("health-check-secret", healthJson);
    }

    [Fact]
    public void Export_redacts_app_root_and_user_profile_paths_from_health_check_messages()
    {
        var serverRoot = Path.Combine(_root, "servers", "1");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"id":"server-a","moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configPath = Path.Combine(_root, "servers", "1", "ServerConfig.json");
        var installPath = Path.Combine(userProfile, "Games", "MyServer");
        var report = new ServerHealthReport(
            "server-a",
            "Server A",
            [
                new ServerHealthCheck("Configuration", "Config file", ServerHealthSeverity.Fail, $"Config file is missing: {configPath}"),
                new ServerHealthCheck("Installation", "Install folder", ServerHealthSeverity.Pass, $"Install folder exists: {installPath}")
            ]);

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        var healthJson = ReadEntry(archive.GetEntry("servers/server-001/health.json")!);

        Assert.Contains("[APP_ROOT]", healthJson);
        Assert.Contains("[USER_PROFILE]", healthJson);
        Assert.DoesNotContain(_root, healthJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userProfile, healthJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_redacts_secrets_and_paths_embedded_in_module_controlled_check_names()
    {
        // Regression guard: ReadinessCheckResult.Name (AddModuleReadinessChecksAsync) and a backup
        // target's Label (AddBackupChecks) both flow directly into ServerHealthCheck.Name from
        // module-authored data, exactly like Message - a module setting either to a credential- or
        // path-shaped string must not leak it into an exported health.json just because only Message
        // used to get the redaction treatment.
        var serverRoot = Path.Combine(_root, "servers", "1");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"id":"server-a","moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");

        var report = new ServerHealthReport(
            "server-a",
            "Server A",
            [new ServerHealthCheck(
                "Module readiness",
                "authorization=name-secret-value",
                ServerHealthSeverity.Warning,
                "Module-specific readiness check failed.")]);

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        var healthJson = ReadEntry(archive.GetEntry("servers/server-001/health.json")!);
        Assert.Contains("[REDACTED]", healthJson);
        Assert.DoesNotContain("name-secret-value", healthJson);
    }

    [Fact]
    public void Export_includes_health_report_for_a_server_with_missing_config()
    {
        // Regression guard: InstalledServerLoader.CreateProblemServer keys a server with a missing
        // ServerConfig.json by its folder name, and ServerHealthService still produces a real
        // ("Config file is missing") report for it under that same id. The export path must not
        // silently drop that diagnosis just because there's no config left to build a summary.json
        // from - the folder itself still needs to exist so the directory scan finds it at all.
        var serverRoot = Path.Combine(_root, "servers", "broken-server");
        Directory.CreateDirectory(serverRoot);

        var report = new ServerHealthReport(
            "broken-server",
            "Server broken-server",
            [new ServerHealthCheck("Configuration", "Config file", ServerHealthSeverity.Fail, "Config file is missing.")]);

        var output = Path.Combine(_root, "support.zip");
        var result = new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("servers/server-001/health.json", entryNames);
        Assert.DoesNotContain(entryNames, name => name.EndsWith("summary.json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, result.EntryCount);
    }

    [Fact]
    public void Export_includes_health_report_for_a_server_with_unreadable_config()
    {
        // Same guard as the missing-config case above, but for a config file that exists yet fails
        // to parse as JSON at all - BuildServerSummary's own catch returns a null serverId in that
        // exact situation (unlike a parseable config with no "id" property, which still resolves to
        // the folder name via ResolveServerId), so this exercises a different code path than
        // Export_matches_health_report_by_folder_name_when_config_has_no_id.
        var serverRoot = Path.Combine(_root, "servers", "unreadable-server");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(Path.Combine(serverRoot, "ServerConfig.json"), "{ not valid json");

        var report = new ServerHealthReport(
            "unreadable-server",
            "Server unreadable-server",
            [new ServerHealthCheck("Configuration", "Config file", ServerHealthSeverity.Fail, "Configuration could not be read.")]);

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("servers/server-001/summary.json", entryNames);
        Assert.Contains("servers/server-001/health.json", entryNames);
    }

    [Fact]
    public void Export_includes_health_report_for_a_server_with_a_whitespace_only_id()
    {
        // Regression guard: InstalledServerLoader.GetString (and ResolveServerId, which mirrors it
        // exactly) preserves an empty/whitespace-only "id" JSON string verbatim rather than falling
        // back to the folder name - a JSON string value is never null even when it's "". A server
        // configured that way still produces a real ServerHealthReport keyed by that same
        // whitespace-ish id, and it must still be matchable, not silently dropped.
        var serverRoot = Path.Combine(_root, "servers", "1");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"id":"   ","moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");

        var report = new ServerHealthReport(
            "   ",
            "Server with a blank id",
            [new ServerHealthCheck("Configuration", "Config file", ServerHealthSeverity.Pass, "Server configuration parsed successfully.")]);

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        Assert.Contains("servers/server-001/health.json", archive.Entries.Select(entry => entry.FullName));
    }

    [Fact]
    public void Export_falls_back_to_folder_id_when_the_report_is_keyed_by_folder_name_despite_a_readable_config()
    {
        // Regression guard: InstalledServerLoader.TryLoadAsync's catch wraps its ENTIRE try block,
        // not just the JSON parse/read step - a config that parses perfectly fine (as this one does,
        // with a real "id") can still end up producing a report keyed by CreateProblemServer's
        // folder-name fallback, if some later, unrelated step (a module's GetDisplayInfo/
        // IsInstallValid, etc.) throws. BuildServerSummary/ResolveServerId only ever see the config
        // itself, so they resolve the configured id ("configured-id") - the export must still try the
        // folder name as a second key so this doesn't silently omit health.json.
        var serverRoot = Path.Combine(_root, "servers", "folder-fallback-server");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"id":"configured-id","moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");

        var report = new ServerHealthReport(
            "folder-fallback-server",
            "Server folder-fallback-server",
            [new ServerHealthCheck("Installation", "Module validation", ServerHealthSeverity.Fail, "This module's install validation could not be completed due to an internal error.")]);

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        Assert.Contains("servers/server-001/health.json", archive.Entries.Select(entry => entry.FullName));
    }

    [Fact]
    public void Export_uses_source_folder_identity_when_duplicate_configured_ids_exist()
    {
        var firstRoot = Path.Combine(_root, "servers", "first-folder");
        var secondRoot = Path.Combine(_root, "servers", "second-folder");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        const string config =
            """{"id":"duplicate-id","moduleId":"test","runtime":"native","installPath":"files","settings":{}}""";
        File.WriteAllText(Path.Combine(firstRoot, "ServerConfig.json"), config);
        File.WriteAllText(Path.Combine(secondRoot, "ServerConfig.json"), config);

        var firstReport = new ServerHealthReport(
            "duplicate-id",
            "First server",
            [new ServerHealthCheck("Configuration", "First server check", ServerHealthSeverity.Pass, "First server is healthy.")])
        {
            SourceFolderId = "first-folder"
        };
        var secondReport = new ServerHealthReport(
            "second-folder",
            "Second server",
            [new ServerHealthCheck("Installation", "Second server problem", ServerHealthSeverity.Fail, "Second server module failed.")])
        {
            SourceFolderId = "second-folder"
        };

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [firstReport, secondReport]));

        using var archive = ZipFile.OpenRead(output);
        var firstHealth = ReadEntry(archive.GetEntry("servers/server-001/health.json")!);
        var secondHealth = ReadEntry(archive.GetEntry("servers/server-002/health.json")!);

        Assert.Contains("First server check", firstHealth);
        Assert.DoesNotContain("Second server problem", firstHealth);
        Assert.Contains("Second server problem", secondHealth);
        Assert.DoesNotContain("First server check", secondHealth);
    }

    [Fact]
    public void Export_redacts_forward_slash_variants_of_app_root_and_user_profile_paths()
    {
        // Regression guard: appRoot/userProfile are always backslash-form on Windows, but a module-
        // controlled string (a readiness check's Name/Message) could render the same path using
        // forward slashes instead - a single literal, backslash-only replace would never match that
        // variant, leaving the account name visible.
        var serverRoot = Path.Combine(_root, "servers", "1");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "ServerConfig.json"),
            """{"id":"server-a","moduleId":"test","runtime":"native","installPath":"files","settings":{}}""");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appRootForwardSlash = _root.Replace('\\', '/');
        var userProfileForwardSlash = userProfile.Replace('\\', '/');
        var report = new ServerHealthReport(
            "server-a",
            "Server A",
            [
                new ServerHealthCheck("Configuration", "Config file", ServerHealthSeverity.Fail, $"Config file is missing: {appRootForwardSlash}/servers/1/ServerConfig.json"),
                new ServerHealthCheck("Installation", "Install folder", ServerHealthSeverity.Pass, $"Install folder exists: {userProfileForwardSlash}/Games/MyServer")
            ]);

        var output = Path.Combine(_root, "support.zip");
        new SupportBundleService().Export(new SupportBundleRequest(
            output,
            _root,
            "1.0.0",
            "1.0",
            string.Empty,
            [],
            ServerHealthReports: [report]));

        using var archive = ZipFile.OpenRead(output);
        var healthJson = ReadEntry(archive.GetEntry("servers/server-001/health.json")!);

        Assert.Contains("[APP_ROOT]", healthJson);
        Assert.Contains("[USER_PROFILE]", healthJson);
        Assert.DoesNotContain(appRootForwardSlash, healthJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userProfileForwardSlash, healthJson, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        LocalStateRecoveryStatus.Clear();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
