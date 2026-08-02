using System.IO.Compression;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Data.Security;
using WindowsGSH.Services;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(LocalStateRecoveryTestCollection.Name)]
public sealed class DepotDownloaderSessionStoreTests
{
    [Fact]
    public void CaptureAndProtect_RemovesPlaintextAndRestoresItForNextRun()
    {
        var root = CreateTempDirectory();
        try
        {
            var statePath = Path.Combine(root, "session.json");
            var accountConfigPath = Path.Combine(root, "tool", "account.config");
            var store = new DepotDownloaderSessionStore(statePath, accountConfigPath);
            var expected = new byte[] { 1, 4, 9, 16, 25 };
            Directory.CreateDirectory(Path.GetDirectoryName(accountConfigPath)!);
            File.WriteAllBytes(accountConfigPath, expected);

            Assert.True(store.CaptureAndProtect());
            Assert.True(store.HasProtectedSession);
            Assert.False(File.Exists(accountConfigPath));

            Assert.True(store.Restore());
            Assert.Equal(expected, File.ReadAllBytes(accountConfigPath));

            store.Clear();
            Assert.False(store.HasProtectedSession);
            Assert.False(File.Exists(accountConfigPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Restore_RejectsStateForDifferentAccountConfigPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var statePath = Path.Combine(root, "session.json");
            var accountConfigPath = Path.Combine(root, "tool", "account.config");
            File.WriteAllText(
                statePath,
                """
                {
                  "AccountConfigPath": "C:\\outside\\account.config",
                  "ProtectedAccountConfig": "not-used",
                  "UpdatedUtc": "2026-01-01T00:00:00Z"
                }
                """);
            var store = new DepotDownloaderSessionStore(statePath, accountConfigPath);

            Assert.False(store.HasProtectedSession);
            Assert.False(store.Restore());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Restore_LeavesCorruptEncryptedStateUnchangedAndRequestsFreshLogin()
    {
        var root = CreateTempDirectory();
        LocalStateRecoveryStatus.Clear();
        try
        {
            var statePath = Path.Combine(root, "session.json");
            var accountConfigPath = Path.Combine(root, "tool", "account.config");
            Directory.CreateDirectory(Path.GetDirectoryName(accountConfigPath)!);
            File.WriteAllText(
                statePath,
                $$"""
                {
                  "AccountConfigPath": "{{accountConfigPath.Replace("\\", "\\\\")}}",
                  "ProtectedAccountConfig": "not-valid-base64",
                  "UpdatedUtc": "2026-01-01T00:00:00Z"
                }
                """);
            var originalState = File.ReadAllText(statePath);
            var store = new DepotDownloaderSessionStore(statePath, accountConfigPath);

            Assert.False(store.Restore());
            Assert.Equal(originalState, File.ReadAllText(statePath));
            Assert.False(File.Exists(accountConfigPath));
        }
        finally
        {
            LocalStateRecoveryStatus.Clear();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Constructor_RejectsNonAccountConfigTarget()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                new DepotDownloaderSessionStore(
                    Path.Combine(root, "session.json"),
                    Path.Combine(root, "tool", "credentials.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData(@"C:\outside.txt")]
    [InlineData(@"\\server\share\outside.txt")]
    [InlineData("nested\\..\\../outside.txt")]
    [InlineData(".")]
    public void ExtractArchiveSafely_RejectsPathTraversal(string entryName)
    {
        var root = CreateTempDirectory();
        try
        {
            var archivePath = Path.Combine(root, "unsafe.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(entryName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("unsafe");
            }

            Assert.Throws<InvalidDataException>(() =>
                DepotDownloaderToolManager.ExtractArchiveSafely(
                    archivePath,
                    Path.Combine(root, "extract")));
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExtractArchiveSafely_extracts_directories_and_overwrites_existing_files()
    {
        var root = CreateTempDirectory();
        try
        {
            var archivePath = Path.Join(root, "tool.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntry("bin/");
                var entry = archive.CreateEntry("bin/tool.exe");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("new");
            }

            var destination = Path.Join(root, "extract");
            Directory.CreateDirectory(Path.Join(root, "extract", "bin"));
            File.WriteAllText(Path.Join(root, "extract", "bin", "tool.exe"), "old");

            DepotDownloaderToolManager.ExtractArchiveSafely(archivePath, destination);

            Assert.Equal("new", File.ReadAllText(Path.Join(root, "extract", "bin", "tool.exe")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildArguments_UsesRememberedLoginWithoutPuttingPasswordOnCommandLine()
    {
        var arguments = DepotDownloaderClient.BuildArguments(
            @"D:\servers\5\files",
            "2329680",
            "steam-user",
            "beta",
            "branch-secret",
            validate: true);

        Assert.Equal(
            [
                "-app", "2329680",
                "-dir", Path.GetFullPath(@"D:\servers\5\files"),
                "-os", "windows",
                "-username", "steam-user",
                "-remember-password",
                "-branch", "beta",
                "-branchpassword", "branch-secret",
                "-validate"
            ],
            arguments);
        Assert.DoesNotContain("-password", arguments);
    }

    [Theory]
    [InlineData("Enter account password for \"steam-user\": ")]
    [InlineData(" enter ACCOUNT password for steam-user:")]
    public void PasswordPrompt_IsRecognized(string line)
    {
        Assert.True(DepotDownloaderClient.IsDepotDownloaderPasswordPrompt(line));
    }

    [Theory]
    [InlineData("Access token was rejected (Expired).")]
    [InlineData("Access token was rejected (Revoked).")]
    [InlineData("Access token was rejected (InvalidPassword).")]
    public void RejectedRememberedSession_IsRecognized(string line)
    {
        Assert.True(DepotDownloaderClient.IsRejectedAccessToken(line));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
