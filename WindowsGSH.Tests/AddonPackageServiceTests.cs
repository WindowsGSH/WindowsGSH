using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class AddonPackageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.AddonTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Zip_install_tracks_provenance_and_remove_restores_original_files()
    {
        var packagePath = CreateZip(
            ("config.cfg", "addon-value"),
            ("addons/plugin.txt", "plugin"));
        var instance = CreateInstance();
        Directory.CreateDirectory(instance.InstallPath);
        File.WriteAllText(Path.Combine(instance.InstallPath, "config.cfg"), "original-value");
        var addon = CreateAddon(AddonPackageKind.Zip, sourceName: "Example Catalog", sourceVersion: "2.1");
        var service = new AddonPackageService(new LocalDownloader(packagePath));

        await service.InstallAsync(instance, addon, CancellationToken.None);

        Assert.Equal("addon-value", File.ReadAllText(Path.Combine(instance.InstallPath, "config.cfg")));
        Assert.Equal("plugin", File.ReadAllText(Path.Combine(instance.InstallPath, "addons", "plugin.txt")));
        var status = service.GetStatus(instance, addon);
        Assert.True(status.IsInstalled);
        Assert.True(status.IsEnabled);
        Assert.Contains("Example Catalog", status.StatusText);
        Assert.True(File.Exists(Path.Combine(instance.ServerFolder, "addons", addon.Id, "installed.json")));

        await service.RemoveAsync(instance, addon.Id, CancellationToken.None);

        Assert.Equal("original-value", File.ReadAllText(Path.Combine(instance.InstallPath, "config.cfg")));
        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "addons", "plugin.txt")));
        Assert.False(service.GetStatus(instance, addon).IsInstalled);
    }

    [Fact]
    public async Task Tar_gz_install_extracts_safe_entries()
    {
        var packagePath = CreateTarGz(("package/plugins/example.jar", "jar-data"));
        var instance = CreateInstance();
        var addon = CreateAddon(
            AddonPackageKind.TarGz,
            package: new AddonPackageDefinition(
                AddonPackageKind.TarGz,
                "https://example.invalid/addon.tar.gz",
                "server",
                StripComponents: 1,
                RequiredMarkers: ["plugins/example.jar"]));
        var service = new AddonPackageService(new LocalDownloader(packagePath));

        await service.InstallAsync(instance, addon, CancellationToken.None);

        Assert.Equal(
            "jar-data",
            File.ReadAllText(Path.Combine(instance.InstallPath, "server", "plugins", "example.jar")));
    }

    [Fact]
    public async Task Tar_install_extracts_safe_entries()
    {
        var packagePath = CreateTar(("bundle/addons/example.txt", "tar-data"));
        var instance = CreateInstance();
        var addon = CreateAddon(
            AddonPackageKind.Tar,
            package: new AddonPackageDefinition(
                AddonPackageKind.Tar,
                "https://example.invalid/addon.tar",
                ".",
                StripComponents: 1));
        var service = new AddonPackageService(new LocalDownloader(packagePath));

        await service.InstallAsync(instance, addon, CancellationToken.None);

        Assert.Equal(
            "tar-data",
            File.ReadAllText(Path.Combine(instance.InstallPath, "addons", "example.txt")));
    }

    [Fact]
    public async Task Direct_file_install_places_java_plugin_in_plugin_folder()
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, "plugin.jar");
        await File.WriteAllTextAsync(packagePath, "plugin-data");
        var instance = CreateInstance();
        var addon = CreateAddon(
            AddonPackageKind.File,
            package: CommonAddonPackages.JavaPlugin(
                new Uri("https://example.invalid/plugin.jar"),
                "example.jar"));
        var service = new AddonPackageService(new LocalDownloader(packagePath));

        await service.InstallAsync(instance, addon, CancellationToken.None);

        Assert.Equal(
            "plugin-data",
            File.ReadAllText(Path.Combine(instance.InstallPath, "plugins", "example.jar")));
    }

    [Fact]
    public async Task Http_source_url_is_rejected()
    {
        var instance = CreateInstance();
        var addon = CreateAddon(
            AddonPackageKind.Zip,
            package: new AddonPackageDefinition(AddonPackageKind.Zip, "http://example.invalid/addon.zip", "."));
        var service = new AddonPackageService(new LocalDownloader(CreateZip(("a.txt", "x"))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(instance, addon, CancellationToken.None));

        Assert.Contains("HTTPS", exception.Message);
    }

    [Fact]
    public async Task Expected_hash_mismatch_is_rejected()
    {
        var packagePath = CreateZip(("a.txt", "x"));
        var instance = CreateInstance();
        var addon = CreateAddon(
            AddonPackageKind.Zip,
            package: new AddonPackageDefinition(
                AddonPackageKind.Zip,
                "https://example.invalid/addon.zip",
                ".",
                ExpectedSha256: "0000000000000000000000000000000000000000000000000000000000000000"));
        var service = new AddonPackageService(new LocalDownloader(packagePath));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(instance, addon, CancellationToken.None));

        Assert.Contains("hash", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Expected_hash_match_allows_install()
    {
        var packagePath = CreateZip(("a.txt", "x"));
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        var instance = CreateInstance();
        var addon = CreateAddon(
            AddonPackageKind.Zip,
            package: new AddonPackageDefinition(
                AddonPackageKind.Zip,
                "https://example.invalid/addon.zip",
                ".",
                ExpectedSha256: expectedHash));
        var service = new AddonPackageService(new LocalDownloader(packagePath));

        await service.InstallAsync(instance, addon, CancellationToken.None);

        Assert.Equal("x", File.ReadAllText(Path.Combine(instance.InstallPath, "a.txt")));
    }

    [Fact]
    public async Task Archive_with_too_many_entries_is_rejected()
    {
        var packagePath = CreateZip(("a.txt", "x"), ("b.txt", "y"), ("c.txt", "z"));
        var instance = CreateInstance();
        var addon = CreateAddon(AddonPackageKind.Zip);
        var service = new AddonPackageService(
            new LocalDownloader(packagePath),
            new AddonPackageLimits(MaxDownloadBytes: 10_000, MaxExtractedBytes: 10_000, MaxEntries: 2));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(instance, addon, CancellationToken.None));

        Assert.Contains("too many entries", exception.Message);
        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "a.txt")));
    }

    [Fact]
    public async Task Entries_filtered_out_by_archive_subpath_still_count_against_the_entry_limit()
    {
        var packagePath = CreateZip(
            ("skip/a.txt", "1"),
            ("skip/b.txt", "2"),
            ("keep/c.txt", "3"));
        var instance = CreateInstance();
        var addon = CreateAddon(
            AddonPackageKind.Zip,
            package: new AddonPackageDefinition(AddonPackageKind.Zip, "https://example.invalid/addon.zip", ".", ArchiveSubpath: "keep"));
        var service = new AddonPackageService(
            new LocalDownloader(packagePath),
            new AddonPackageLimits(MaxDownloadBytes: 10_000, MaxExtractedBytes: 10_000, MaxEntries: 2));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(instance, addon, CancellationToken.None));

        Assert.Contains("too many entries", exception.Message);
        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "c.txt")));
    }

    [Fact]
    public async Task Zip_directory_entries_count_against_the_entry_limit()
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("dir1/");
            archive.CreateEntry("dir2/");
            using var writer = new StreamWriter(archive.CreateEntry("dir2/a.txt").Open());
            writer.Write("x");
        }
        var instance = CreateInstance();
        var addon = CreateAddon(AddonPackageKind.Zip);
        var service = new AddonPackageService(
            new LocalDownloader(packagePath),
            new AddonPackageLimits(MaxDownloadBytes: 10_000, MaxExtractedBytes: 10_000, MaxEntries: 2));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(instance, addon, CancellationToken.None));

        Assert.Contains("too many entries", exception.Message);
        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "dir2", "a.txt")));
    }

    [Fact]
    public async Task Tar_directory_entries_count_against_the_entry_limit()
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tar");
        using (var output = File.Create(packagePath))
        using (var writer = new TarWriter(output))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "dir1/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "dir2/"));
            var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("x"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "dir2/a.txt") { DataStream = data });
        }
        var instance = CreateInstance();
        var addon = CreateAddon(AddonPackageKind.Tar);
        var service = new AddonPackageService(
            new LocalDownloader(packagePath),
            new AddonPackageLimits(MaxDownloadBytes: 10_000, MaxExtractedBytes: 10_000, MaxEntries: 2));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(instance, addon, CancellationToken.None));

        Assert.Contains("too many entries", exception.Message);
        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "dir2", "a.txt")));
    }

    [Fact]
    public async Task Archive_that_extracts_too_much_data_is_rejected()
    {
        var packagePath = CreateZip(("a.txt", "this content is longer than the tiny limit allows"));
        var instance = CreateInstance();
        var addon = CreateAddon(AddonPackageKind.Zip);
        var service = new AddonPackageService(
            new LocalDownloader(packagePath),
            new AddonPackageLimits(MaxDownloadBytes: 10_000, MaxExtractedBytes: 4, MaxEntries: 100));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(instance, addon, CancellationToken.None));

        Assert.Contains("too much data", exception.Message);
        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "a.txt")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("nested/../../escape.txt")]
    [InlineData(@"C:\escape.txt")]
    [InlineData(@"\\server\share\escape.txt")]
    [InlineData("nested\\..\\../escape.txt")]
    public async Task Archive_traversal_is_rejected_without_writing_outside_install(string entryName)
    {
        var packagePath = CreateZip((entryName, "bad"));
        var instance = CreateInstance();
        var addon = CreateAddon(AddonPackageKind.Zip);
        var service = new AddonPackageService(new LocalDownloader(packagePath));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(instance, addon, CancellationToken.None));

        Assert.Contains("unsafe", exception.Message);
        Assert.False(File.Exists(Path.Combine(instance.ServerFolder, "escape.txt")));
    }

    [Fact]
    public async Task Failed_install_rolls_back_files_copied_before_failure()
    {
        var packagePath = CreateZip(
            ("a.txt", "new"),
            ("conflict", "cannot replace a directory"));
        var instance = CreateInstance();
        Directory.CreateDirectory(Path.Combine(instance.InstallPath, "conflict"));
        var addon = CreateAddon(AddonPackageKind.Zip);
        var service = new AddonPackageService(new LocalDownloader(packagePath));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.InstallAsync(instance, addon, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "a.txt")));
        Assert.True(Directory.Exists(Path.Combine(instance.InstallPath, "conflict")));
        Assert.False(File.Exists(Path.Combine(instance.ServerFolder, "addons", addon.Id, "installed.json")));
    }

    [Fact]
    public async Task Repair_validation_failure_leaves_existing_addon_state_untouched()
    {
        var originalPackage = CreateZip(
            ("existing.txt", "existing"),
            ("missing.txt", "will be deleted"));
        var invalidReplacement = CreateZip(("replacement.txt", "replacement"));
        var instance = CreateInstance();
        var addon = CreateAddon(AddonPackageKind.Zip);
        await new AddonPackageService(new LocalDownloader(originalPackage))
            .InstallAsync(instance, addon, CancellationToken.None);
        File.Delete(Path.Combine(instance.InstallPath, "missing.txt"));
        var metadataPath = Path.Combine(instance.ServerFolder, "addons", addon.Id, "installed.json");
        var originalMetadata = File.ReadAllText(metadataPath);
        var replacementAddon = CreateAddon(
            AddonPackageKind.Zip,
            package: addon.Package! with { RequiredMarkers = ["required.txt"] });
        var service = new AddonPackageService(new LocalDownloader(invalidReplacement));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(instance, replacementAddon, CancellationToken.None));

        Assert.Equal("existing", File.ReadAllText(Path.Combine(instance.InstallPath, "existing.txt")));
        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "missing.txt")));
        Assert.Equal(originalMetadata, File.ReadAllText(metadataPath));
    }

    [Fact]
    public async Task Repair_apply_failure_restores_existing_addon_state()
    {
        var originalPackage = CreateZip(
            ("existing.txt", "existing"),
            ("missing.txt", "will be deleted"));
        var failingReplacement = CreateZip(
            ("existing.txt", "replacement"),
            ("conflict", "cannot replace a directory"));
        var instance = CreateInstance();
        var addon = CreateAddon(AddonPackageKind.Zip);
        await new AddonPackageService(new LocalDownloader(originalPackage))
            .InstallAsync(instance, addon, CancellationToken.None);
        File.Delete(Path.Combine(instance.InstallPath, "missing.txt"));
        Directory.CreateDirectory(Path.Combine(instance.InstallPath, "conflict"));
        var metadataPath = Path.Combine(instance.ServerFolder, "addons", addon.Id, "installed.json");
        var originalMetadata = File.ReadAllText(metadataPath);
        var service = new AddonPackageService(new LocalDownloader(failingReplacement));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.InstallAsync(instance, addon, CancellationToken.None));

        Assert.Equal("existing", File.ReadAllText(Path.Combine(instance.InstallPath, "existing.txt")));
        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "missing.txt")));
        Assert.True(Directory.Exists(Path.Combine(instance.InstallPath, "conflict")));
        Assert.Equal(originalMetadata, File.ReadAllText(metadataPath));
        var status = service.GetStatus(instance, addon);
        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
    }

    [Fact]
    public async Task Successful_repair_preserves_pre_addon_files_for_later_remove()
    {
        var originalPackage = CreateZip(
            ("config.cfg", "addon-v1"),
            ("missing.txt", "will be deleted"));
        var replacementPackage = CreateZip(
            ("config.cfg", "addon-v2"),
            ("missing.txt", "replacement"));
        var instance = CreateInstance();
        File.WriteAllText(Path.Combine(instance.InstallPath, "config.cfg"), "server-original");
        var addon = CreateAddon(AddonPackageKind.Zip);
        await new AddonPackageService(new LocalDownloader(originalPackage))
            .InstallAsync(instance, addon, CancellationToken.None);
        File.Delete(Path.Combine(instance.InstallPath, "missing.txt"));
        var service = new AddonPackageService(new LocalDownloader(replacementPackage));

        await service.InstallAsync(instance, addon, CancellationToken.None);

        Assert.Equal("addon-v2", File.ReadAllText(Path.Combine(instance.InstallPath, "config.cfg")));
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(instance.InstallPath, "missing.txt")));

        await service.RemoveAsync(instance, addon.Id, CancellationToken.None);

        Assert.Equal("server-original", File.ReadAllText(Path.Combine(instance.InstallPath, "config.cfg")));
        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "missing.txt")));
    }

    [Fact]
    public void Common_helpers_create_expected_source_and_java_layouts()
    {
        var sourceMod = CommonAddonPackages.SourceMod(new Uri("https://example.invalid/sourcemod.tar.gz"), "tf");
        var metaMod = CommonAddonPackages.MetaModSource(new Uri("https://example.invalid/metamod.tar.gz"), "tf");
        var plugin = CommonAddonPackages.JavaPlugin(new Uri("https://example.invalid/plugin.jar"), "plugin.jar");

        Assert.Equal("tf", sourceMod.InstallPath);
        Assert.Contains("addons/sourcemod", sourceMod.RequiredMarkers!);
        Assert.Contains("addons/metamod", metaMod.RequiredMarkers!);
        Assert.Equal(AddonPackageKind.File, plugin.Kind);
        Assert.Equal("plugins", plugin.InstallPath);
        Assert.Equal("plugin.jar", plugin.FileName);
    }

    private ServerInstance CreateInstance()
    {
        var serverFolder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var installPath = Path.Combine(serverFolder, "files");
        Directory.CreateDirectory(installPath);
        return new ServerInstance(
            "server-1",
            "Server",
            "test",
            serverFolder,
            installPath,
            Path.Combine(serverFolder, "ServerConfig.json"),
            new Dictionary<string, object?>());
    }

    private static ServerAddonDefinition CreateAddon(
        AddonPackageKind kind,
        AddonPackageDefinition? package = null,
        string? sourceName = null,
        string? sourceVersion = null)
    {
        return new ServerAddonDefinition(
            "example-addon",
            "Example Addon",
            "Test addon",
            new ModuleCapabilities(false, false, false, false, false, false, false, false),
            [],
            Package: package ?? new AddonPackageDefinition(
                kind,
                "https://example.invalid/addon",
                "."),
            SourceName: sourceName,
            SourceVersion: sourceVersion);
    }

    private string CreateZip(params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(file.Path);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(file.Content);
        }
        return path;
    }

    private string CreateTarGz(params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tar.gz");
        using var output = File.Create(path);
        using var gzip = new GZipStream(output, CompressionMode.Compress);
        using var writer = new TarWriter(gzip);
        foreach (var file in files)
        {
            var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(file.Content));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, file.Path) { DataStream = data });
        }
        return path;
    }

    private string CreateTar(params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tar");
        using var output = File.Create(path);
        using var writer = new TarWriter(output);
        foreach (var file in files)
        {
            var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(file.Content));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, file.Path) { DataStream = data });
        }
        return path;
    }

    public void Dispose()
    {
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

    private sealed class LocalDownloader(string sourcePath) : IAddonPackageDownloader
    {
        public async Task<AddonDownloadResult> DownloadAsync(Uri source, string targetPath, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
            await using var stream = File.OpenRead(targetPath);
            return new AddonDownloadResult(
                targetPath,
                Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
        }
    }
}
