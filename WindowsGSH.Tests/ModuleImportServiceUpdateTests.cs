using System.IO.Compression;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(LocalStateRecoveryTestCollection.Name)]
public sealed class ModuleImportServiceUpdateTests
{
    [Fact]
    public void UpdateFromFolder_replaces_files_and_refreshes_hash()
    {
        var moduleId = "update.test." + Guid.NewGuid().ToString("N")[..8];
        using var workspace = new TestWorkspace();
        var service = new ModuleImportService();

        var v1Folder = workspace.CreateModuleFolder(moduleId, version: "1.0", extraFile: "v1only.txt");
        service.ImportFromFolder(v1Folder);
        try
        {
            var before = service.GetInstalledModules().First(m => m.Id == moduleId);

            var v2Folder = workspace.CreateModuleFolder(moduleId, version: "2.0", extraFile: "v2only.txt");
            var updated = service.UpdateFromFolder(moduleId, v2Folder);

            Assert.Equal(moduleId, updated.Id);
            Assert.Equal("2.0", updated.Version);
            Assert.False(updated.HasChangedSinceImport);
            Assert.NotEqual(before.CurrentHash, updated.CurrentHash);
            Assert.False(File.Exists(Path.Combine(updated.Path, "v1only.txt")));
            Assert.True(File.Exists(Path.Combine(updated.Path, "v2only.txt")));
        }
        finally
        {
            DeleteModuleIfExists(moduleId);
        }
    }

    [Fact]
    public void ResolveModuleFolder_finds_module_json_in_a_nested_subdirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "SomeModule.mod");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "module.json"), "{}");
        try
        {
            var resolved = ModuleImportService.ResolveModuleFolder(root);

            Assert.Equal(nested, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveModuleFolder_returns_null_when_no_module_json_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(ModuleImportService.ResolveModuleFolder(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateFromFolder_throws_when_module_id_does_not_match()
    {
        var moduleId = "update.mismatch." + Guid.NewGuid().ToString("N")[..8];
        var otherId = "other.module." + Guid.NewGuid().ToString("N")[..8];
        using var workspace = new TestWorkspace();
        var service = new ModuleImportService();

        var v1Folder = workspace.CreateModuleFolder(moduleId, version: "1.0");
        service.ImportFromFolder(v1Folder);
        try
        {
            var wrongFolder = workspace.CreateModuleFolder(otherId, version: "1.0");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                service.UpdateFromFolder(moduleId, wrongFolder));

            Assert.Contains("mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteModuleIfExists(moduleId);
            DeleteModuleIfExists(otherId);
        }
    }

    [Fact]
    public void UpdateFromZip_replaces_files_and_refreshes_hash()
    {
        var moduleId = "update.zip." + Guid.NewGuid().ToString("N")[..8];
        using var workspace = new TestWorkspace();
        var service = new ModuleImportService();

        var v1Folder = workspace.CreateModuleFolder(moduleId, version: "1.0", extraFile: "v1file.txt");
        service.ImportFromFolder(v1Folder);
        try
        {
            var before = service.GetInstalledModules().First(m => m.Id == moduleId);

            var v2Folder = workspace.CreateModuleFolder(moduleId, version: "2.0", extraFile: "v2file.txt");
            var zipPath = workspace.CreateZipFromFolder(v2Folder, moduleId + "-v2");
            var updated = service.UpdateFromZip(moduleId, zipPath);

            Assert.Equal(moduleId, updated.Id);
            Assert.Equal("2.0", updated.Version);
            Assert.False(updated.HasChangedSinceImport);
            Assert.NotEqual(before.CurrentHash, updated.CurrentHash);
            Assert.True(File.Exists(Path.Combine(updated.Path, "v2file.txt")));
        }
        finally
        {
            DeleteModuleIfExists(moduleId);
        }
    }

    [Fact]
    public void UpdateFromZip_preserves_existing_source_url_when_replacement_omits_it()
    {
        var moduleId = "update.source." + Guid.NewGuid().ToString("N")[..8];
        const string sourceUrl = "https://github.com/WindowsGSH/WindowsGSH.TestModule";
        using var workspace = new TestWorkspace();
        var service = new ModuleImportService();

        var v1Folder = workspace.CreateModuleFolder(moduleId, version: "1.0", sourceUrl: sourceUrl);
        service.ImportFromFolder(v1Folder);
        try
        {
            var v2Folder = workspace.CreateModuleFolder(moduleId, version: "2.0");
            var zipPath = workspace.CreateZipFromFolder(v2Folder, moduleId + "-v2");

            var updated = service.UpdateFromZip(moduleId, zipPath);

            Assert.Equal(sourceUrl, updated.SourceUrl);
            Assert.True(updated.HasSourceUrl);
        }
        finally
        {
            DeleteModuleIfExists(moduleId);
        }
    }

    [Fact]
    public void UpdateFromZip_uses_explicit_fallback_source_url_when_replacement_omits_it()
    {
        var moduleId = "update.remote." + Guid.NewGuid().ToString("N")[..8];
        const string sourceUrl = "https://github.com/WindowsGSH/WindowsGSH.RemoteTest";
        using var workspace = new TestWorkspace();
        var service = new ModuleImportService();

        var v1Folder = workspace.CreateModuleFolder(moduleId, version: "1.0");
        service.ImportFromFolder(v1Folder);
        try
        {
            var v2Folder = workspace.CreateModuleFolder(moduleId, version: "2.0");
            var zipPath = workspace.CreateZipFromFolder(v2Folder, moduleId + "-v2");

            var updated = service.UpdateFromZip(moduleId, zipPath, sourceUrl);

            Assert.Equal(sourceUrl, updated.SourceUrl);
            Assert.True(updated.HasSourceUrl);
        }
        finally
        {
            DeleteModuleIfExists(moduleId);
        }
    }

    [Fact]
    public void UpdateFromFolder_does_not_leave_update_backup_directories_under_module_roots()
    {
        var moduleId = "update.backup." + Guid.NewGuid().ToString("N")[..8];
        using var workspace = new TestWorkspace();
        var service = new ModuleImportService();

        var v1Folder = workspace.CreateModuleFolder(moduleId, version: "1.0");
        service.ImportFromFolder(v1Folder);
        try
        {
            var v2Folder = workspace.CreateModuleFolder(moduleId, version: "2.0");

            service.UpdateFromFolder(moduleId, v2Folder);

            Assert.DoesNotContain(EnumerateModuleRootDirectories(), path =>
                Path.GetFileName(path).Contains(".update-backup.", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteModuleIfExists(moduleId);
        }
    }

    [Fact]
    public void UpdateFromFolder_restores_original_module_when_metadata_write_fails()
    {
        var moduleId = "update.rollback." + Guid.NewGuid().ToString("N")[..8];
        using var workspace = new TestWorkspace();
        var service = new ModuleImportService();

        var v1Folder = workspace.CreateModuleFolder(moduleId, version: "1.0", extraFile: "v1only.txt");
        service.ImportFromFolder(v1Folder);
        try
        {
            var v2Folder = workspace.CreateModuleFolder(moduleId, version: "2.0", extraFile: "v2only.txt");
            var importJsonDirectory = Path.Combine(v2Folder, "import.json");
            Directory.CreateDirectory(importJsonDirectory);
            File.WriteAllText(Path.Combine(importJsonDirectory, "blocks-metadata-write.txt"), "content");

            Assert.ThrowsAny<Exception>(() => service.UpdateFromFolder(moduleId, v2Folder));

            var current = service.GetInstalledModules().Single(module => module.Id == moduleId);
            Assert.Equal("1.0", current.Version);
            Assert.True(File.Exists(Path.Combine(current.Path, "v1only.txt")));
            Assert.False(File.Exists(Path.Combine(current.Path, "v2only.txt")));
            Assert.DoesNotContain(EnumerateModuleRootDirectories(), path =>
                Path.GetFileName(path).Contains(".update-backup.", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteModuleIfExists(moduleId);
        }
    }

    [Fact]
    public void UpdateFromFolder_throws_when_module_not_found()
    {
        using var workspace = new TestWorkspace();
        var service = new ModuleImportService();
        var folder = workspace.CreateModuleFolder("nonexistent.module", version: "1.0");

        Assert.Throws<InvalidOperationException>(() =>
            service.UpdateFromFolder("nonexistent.module", folder));
    }

    [Fact]
    public void HasSourceUrl_returns_true_when_source_url_is_set()
    {
        var candidate = MakeCandidate(sourceUrl: "https://example.test/module.zip");
        Assert.True(candidate.HasSourceUrl);
        Assert.False(candidate.HasNoSourceUrl);
    }

    [Fact]
    public void HasSourceUrl_returns_false_when_source_url_is_null()
    {
        var candidate = MakeCandidate(sourceUrl: null);
        Assert.False(candidate.HasSourceUrl);
        Assert.True(candidate.HasNoSourceUrl);
    }

    private static void DeleteModuleIfExists(string moduleId)
    {
        foreach (var root in new[] { ModuleStoragePaths.InstalledModules, ModuleStoragePaths.DisabledModules })
        {
            var path = Path.Combine(root, moduleId);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static IEnumerable<string> EnumerateModuleRootDirectories()
    {
        foreach (var root in new[] { ModuleStoragePaths.InstalledModules, ModuleStoragePaths.DisabledModules })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                yield return directory;
            }
        }
    }

    private static ModuleImportCandidate MakeCandidate(string? sourceUrl)
    {
        return new ModuleImportCandidate(
            "test.module", "Test", "1.0", null, null, null, null, null,
            "/modules/test.module", Enabled: true, "JSON module",
            "abc123", DateTimeOffset.UtcNow, "folder", null,
            null, null, sourceUrl,
            false, "warning", ModuleCompatibilityStatus.Compatible,
            "Compatible", "1.0", "1.0", "1.0");
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root;

        public TestWorkspace()
        {
            _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public string CreateModuleFolder(string id, string version, string? extraFile = null, string? sourceUrl = null)
        {
            var folder = Path.Combine(_root, id + "-" + version);
            Directory.CreateDirectory(folder);
            var manifest = sourceUrl is null
                ? $$"""
                    {
                      "id": "{{id}}",
                      "name": "{{id}}",
                      "version": "{{version}}",
                      "entryPoints": { "start": "server.exe" },
                      "runtime": { "processNames": [ "server" ] }
                    }
                    """
                : $$"""
                    {
                      "id": "{{id}}",
                      "name": "{{id}}",
                      "version": "{{version}}",
                      "sourceUrl": "{{sourceUrl}}",
                      "entryPoints": { "start": "server.exe" },
                      "runtime": { "processNames": [ "server" ] }
                    }
                    """;
            File.WriteAllText(Path.Combine(folder, "module.json"), manifest);
            if (extraFile is not null)
            {
                File.WriteAllText(Path.Combine(folder, extraFile), "content");
            }

            return folder;
        }

        public string CreateZipFromFolder(string folderPath, string zipName)
        {
            var zipPath = Path.Combine(_root, zipName + ".zip");
            ZipFile.CreateFromDirectory(folderPath, zipPath);
            return zipPath;
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
    }
}
