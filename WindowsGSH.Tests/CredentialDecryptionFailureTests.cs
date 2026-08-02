using System.Text.Json;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.Steam;
using WindowsGSH.Data.Security;
using WindowsGSH.Discord;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(LocalStateRecoveryTestCollection.Name)]
public sealed class CredentialDecryptionFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WindowsGSH.Tests",
        Guid.NewGuid().ToString("N"));

    public CredentialDecryptionFailureTests()
    {
        Directory.CreateDirectory(_root);
        LocalStateRecoveryStatus.Clear();
    }

    public void Dispose()
    {
        LocalStateRecoveryStatus.Clear();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void SteamCredentialStore_Load_reports_failure_when_dpapi_data_is_corrupted()
    {
        var path = Path.Combine(_root, "steam-credentials.json");
        File.WriteAllText(path, """
            {
                "Username": "testuser",
                "ProtectedPassword": "AAAA",
                "UpdatedUtc": "2024-01-01T00:00:00Z"
            }
            """);

        var store = new SteamCredentialStore(path);
        var result = store.Load();

        Assert.Null(result);
        var events = LocalStateRecoveryStatus.Snapshot();
        Assert.Contains(events, e => e.Area == "Steam credentials" && e.IsFailure);
    }

    [Fact]
    public void SteamCredentialStore_Load_returns_null_without_event_when_file_missing()
    {
        var path = Path.Combine(_root, "does-not-exist.json");

        var store = new SteamCredentialStore(path);
        var result = store.Load();

        Assert.Null(result);
        Assert.Empty(LocalStateRecoveryStatus.Snapshot());
    }

    [Fact]
    public void DiscordBotTokenStore_Load_reports_failure_when_dpapi_data_is_corrupted()
    {
        var path = Path.Combine(_root, "discord-token.json");
        File.WriteAllText(path, """
            {
                "ProtectedToken": "AAAA",
                "UpdatedUtc": "2024-01-01T00:00:00Z"
            }
            """);

        var store = new DiscordBotTokenStore(path);
        var result = store.Load();

        Assert.Null(result);
        var events = LocalStateRecoveryStatus.Snapshot();
        Assert.Contains(events, e => e.Area == "Discord bot token" && e.IsFailure);
    }

    [Fact]
    public void DiscordBotTokenStore_Load_returns_null_without_event_when_file_missing()
    {
        var path = Path.Combine(_root, "does-not-exist.json");

        var store = new DiscordBotTokenStore(path);
        var result = store.Load();

        Assert.Null(result);
        Assert.Empty(LocalStateRecoveryStatus.Snapshot());
    }

    [Fact]
    public void DiscordWebhookStore_Load_reports_failure_when_dpapi_data_is_corrupted()
    {
        var path = Path.Combine(_root, "discord-webhooks.json");
        File.WriteAllText(path, """
            {
                "ProtectedData": "AAAA",
                "UpdatedUtc": "2024-01-01T00:00:00Z"
            }
            """);

        var store = new DiscordWebhookStore(path);
        var result = store.LoadGlobal();

        Assert.Null(result);
        var events = LocalStateRecoveryStatus.Snapshot();
        Assert.Contains(events, e => e.Area == "Discord webhooks" && e.IsFailure);
    }

    [Fact]
    public void DiscordWebhookStore_Load_returns_empty_without_event_when_file_missing()
    {
        var path = Path.Combine(_root, "does-not-exist.json");

        var store = new DiscordWebhookStore(path);
        var result = store.LoadGlobal();

        Assert.Null(result);
        Assert.Empty(LocalStateRecoveryStatus.Snapshot());
    }

    [Fact]
    public void SteamCredentialStore_does_not_report_duplicate_events_on_repeated_load_failures()
    {
        var path = Path.Combine(_root, "steam-creds-dedup.json");
        File.WriteAllText(path, """{"Username": "user", "ProtectedPassword": "AAAA", "UpdatedUtc": "2024-01-01T00:00:00Z"}""");

        var store = new SteamCredentialStore(path);
        store.Load();
        store.Load();
        store.Load();

        var events = LocalStateRecoveryStatus.Snapshot();
        Assert.Equal(1, events.Count(e => e.Area == "Steam credentials"));
    }

    [Fact]
    public void DiscordBotTokenStore_does_not_report_duplicate_events_on_repeated_load_failures()
    {
        var path = Path.Combine(_root, "discord-token-dedup.json");
        File.WriteAllText(path, """{"ProtectedToken": "AAAA", "UpdatedUtc": "2024-01-01T00:00:00Z"}""");

        var store = new DiscordBotTokenStore(path);
        store.Load();
        store.Load();
        store.Load();

        var events = LocalStateRecoveryStatus.Snapshot();
        Assert.Equal(1, events.Count(e => e.Area == "Discord bot token"));
    }

    [Fact]
    public void DiscordWebhookStore_does_not_report_duplicate_events_on_repeated_load_failures()
    {
        var path = Path.Combine(_root, "discord-webhooks-dedup.json");
        File.WriteAllText(path, """{"ProtectedData": "AAAA", "UpdatedUtc": "2024-01-01T00:00:00Z"}""");

        var store = new DiscordWebhookStore(path);
        store.LoadGlobal();
        store.LoadGlobal();
        store.HasGlobalWebhook.ToString();  // triggers another Load()

        var events = LocalStateRecoveryStatus.Snapshot();
        Assert.Equal(1, events.Count(e => e.Area == "Discord webhooks"));
    }

    [Fact]
    public void DiscordWebhookStore_SaveGlobal_backs_up_original_file_when_load_fails()
    {
        var path = Path.Combine(_root, "discord-webhooks-overwrite.json");
        const string corruptContent = """{"ProtectedData": "AAAA", "UpdatedUtc": "2024-01-01T00:00:00Z"}""";
        File.WriteAllText(path, corruptContent);
        var backupPath = path + ".bak";

        var store = new DiscordWebhookStore(path);
        store.SaveGlobal("https://discord.com/api/webhooks/1234567890/faketoken_abc123");

        Assert.True(File.Exists(backupPath), "Backup file should have been created.");
        Assert.Equal(corruptContent, File.ReadAllText(backupPath));
        Assert.True(File.Exists(path), "New store file should exist.");
        Assert.NotEqual(corruptContent, File.ReadAllText(path));
        Assert.Equal(
            "https://discord.com/api/webhooks/1234567890/faketoken_abc123",
            store.LoadGlobal());
    }

    [Fact]
    public void DiscordWebhookStore_ClearGlobal_backs_up_original_file_before_deleting()
    {
        var path = Path.Combine(_root, "discord-webhooks-clear.json");
        const string corruptContent = """{"ProtectedData": "AAAA", "UpdatedUtc": "2024-01-01T00:00:00Z"}""";
        File.WriteAllText(path, corruptContent);
        var backupPath = path + ".bak";

        var store = new DiscordWebhookStore(path);
        store.ClearGlobal();

        Assert.True(File.Exists(backupPath), "Backup file should have been created.");
        Assert.Equal(corruptContent, File.ReadAllText(backupPath));
        Assert.False(File.Exists(path), "Main store file should be deleted after clear.");
        Assert.False(store.HasGlobalWebhook);
    }
}

[Collection(SteamBranchPasswordStoreTestCollection.Name)]
public sealed class SteamBranchPasswordDecryptionFailureTests : IDisposable
{
    private readonly string? _existingStoreContent;
    private readonly bool _storeExistedBefore;

    public SteamBranchPasswordDecryptionFailureTests()
    {
        _storeExistedBefore = File.Exists(SteamBranchPasswordStore.StorePath);
        _existingStoreContent = _storeExistedBefore
            ? File.ReadAllText(SteamBranchPasswordStore.StorePath)
            : null;
        LocalStateRecoveryStatus.Clear();
    }

    public void Dispose()
    {
        LocalStateRecoveryStatus.Clear();

        try
        {
            if (_storeExistedBefore && _existingStoreContent != null)
            {
                File.WriteAllText(SteamBranchPasswordStore.StorePath, _existingStoreContent);
            }
            else if (!_storeExistedBefore && File.Exists(SteamBranchPasswordStore.StorePath))
            {
                File.Delete(SteamBranchPasswordStore.StorePath);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Load_reports_recovery_status_when_dpapi_data_is_corrupted()
    {
        const string serverId = "decrypt-fail-test";
        var corruptedStore = new
        {
            Passwords = new Dictionary<string, object>
            {
                [serverId] = new { ProtectedPassword = "AAAA", UpdatedUtc = "2024-01-01T00:00:00Z" }
            }
        };

        var dir = Path.GetDirectoryName(SteamBranchPasswordStore.StorePath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(
            SteamBranchPasswordStore.StorePath,
            JsonSerializer.Serialize(corruptedStore, new JsonSerializerOptions { WriteIndented = true }));

        var store = new SteamBranchPasswordStore();
        var result = store.Load(serverId);

        Assert.Equal(string.Empty, result);
        var events = LocalStateRecoveryStatus.Snapshot();
        Assert.Contains(events, e => e.Area == "Steam branch password" && e.IsFailure);
    }

    [Fact]
    public void Load_does_not_report_duplicate_events_for_repeated_calls()
    {
        const string serverId = "dedup-test";
        var corruptedStore = new
        {
            Passwords = new Dictionary<string, object>
            {
                [serverId] = new { ProtectedPassword = "AAAA", UpdatedUtc = "2024-01-01T00:00:00Z" }
            }
        };

        var dir = Path.GetDirectoryName(SteamBranchPasswordStore.StorePath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(
            SteamBranchPasswordStore.StorePath,
            JsonSerializer.Serialize(corruptedStore, new JsonSerializerOptions { WriteIndented = true }));

        var store = new SteamBranchPasswordStore();
        store.Load(serverId);
        store.Load(serverId);

        Assert.Equal(1, LocalStateRecoveryStatus.Snapshot().Count(e => e.Area == "Steam branch password"));
    }

    [Fact]
    public void Save_throws_rather_than_overwriting_corrupt_store()
    {
        const string corrupt = "this is not valid json";
        var dir = Path.GetDirectoryName(SteamBranchPasswordStore.StorePath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(SteamBranchPasswordStore.StorePath, corrupt);

        var store = new SteamBranchPasswordStore();
        Assert.ThrowsAny<Exception>(() => store.Save("server-1", "password"));

        Assert.Equal(corrupt, File.ReadAllText(SteamBranchPasswordStore.StorePath));
    }

    [Fact]
    public void Clear_silently_skips_when_store_file_is_corrupt()
    {
        const string corrupt = "this is not valid json";
        var dir = Path.GetDirectoryName(SteamBranchPasswordStore.StorePath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(SteamBranchPasswordStore.StorePath, corrupt);

        var store = new SteamBranchPasswordStore();
        store.Clear("server-1");

        Assert.Equal(corrupt, File.ReadAllText(SteamBranchPasswordStore.StorePath));
    }
}
