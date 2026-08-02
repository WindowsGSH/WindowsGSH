using System.IO.Compression;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(LocalStateRecoveryTestCollection.Name)]
public sealed class ModuleZipImportValidatorTests
{
    [Fact]
    public void Validate_allows_safe_nested_entries()
    {
        using var workspace = TestWorkspace.Create();
        var zipPath = workspace.CreateZip(("module/module.json", "{}"));

        ModuleZipImportValidator.Validate(zipPath, workspace.ExtractRoot);
    }

    [Fact]
    public void Validate_rejects_path_traversal()
    {
        using var workspace = TestWorkspace.Create();
        var zipPath = workspace.CreateZip(("../evil.txt", "bad"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModuleZipImportValidator.Validate(zipPath, workspace.ExtractRoot));
        Assert.Contains("path traversal", ex.Message);
    }

    [Fact]
    public void Validate_rejects_absolute_paths()
    {
        using var workspace = TestWorkspace.Create();
        var zipPath = workspace.CreateZip(("/evil.txt", "bad"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModuleZipImportValidator.Validate(zipPath, workspace.ExtractRoot));
        Assert.Contains("absolute path", ex.Message);
    }

    [Theory]
    [InlineData("C:evil.txt")]
    [InlineData("C:\\evil.txt")]
    [InlineData("\\\\server\\share\\evil.txt")]
    [InlineData("folder\\..\\evil.txt")]
    [InlineData("folder/../evil.txt")]
    public void Validate_rejects_windows_rooted_and_traversal_paths(string entryName)
    {
        using var workspace = TestWorkspace.Create();
        var zipPath = workspace.CreateZip((entryName, "bad"));

        Assert.Throws<InvalidOperationException>(() =>
            ModuleZipImportValidator.Validate(zipPath, workspace.ExtractRoot));
    }

    [Fact]
    public void Validate_rejects_too_many_entries()
    {
        using var workspace = TestWorkspace.Create();
        var zipPath = workspace.CreateZip(
            ("one.txt", "1"),
            ("two.txt", "2"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModuleZipImportValidator.Validate(
                zipPath,
                workspace.ExtractRoot,
                new ModuleZipImportLimits(MaxZipFileBytes: 1024, MaxExtractedBytes: 1024, MaxEntries: 1)));
        Assert.Contains("too many entries", ex.Message);
    }

    [Fact]
    public void Validate_rejects_too_large_uncompressed_size()
    {
        using var workspace = TestWorkspace.Create();
        var zipPath = workspace.CreateZip(("large.txt", "123456"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModuleZipImportValidator.Validate(
                zipPath,
                workspace.ExtractRoot,
                new ModuleZipImportLimits(MaxZipFileBytes: 1024, MaxExtractedBytes: 5, MaxEntries: 10)));
        Assert.Contains("too much data", ex.Message);
    }

    [Fact]
    public void Validate_rejects_too_large_zip_file()
    {
        using var workspace = TestWorkspace.Create();
        var zipPath = workspace.CreateZip(("tiny.txt", "1"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModuleZipImportValidator.Validate(
                zipPath,
                workspace.ExtractRoot,
                new ModuleZipImportLimits(MaxZipFileBytes: 1, MaxExtractedBytes: 1024, MaxEntries: 10)));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void ImportFromZip_accepts_module_json_nested_inside_one_top_level_folder()
    {
        using var workspace = TestWorkspace.Create();
        var moduleId = "test-module-" + Guid.NewGuid().ToString("N");
        var zipPath = workspace.CreateZip(
            ("Package/module.json", $$"""
            {
              "id": "{{moduleId}}",
              "name": "Test Module",
              "version": "1.0.0",
              "entryPoints": {
                "start": "server.exe"
              }
            }
            """));

        try
        {
            var imported = new ModuleImportService().ImportFromZip(zipPath);

            Assert.Equal(moduleId, imported.Id);
            Assert.True(imported.Enabled);
            Assert.True(File.Exists(Path.Combine(imported.Path, "module.json")));
        }
        finally
        {
            DeleteModuleDirectory(ModuleStoragePaths.InstalledModules, moduleId);
            DeleteModuleDirectory(ModuleStoragePaths.DisabledModules, moduleId);
        }
    }

    [Fact]
    public void ImportFromFolder_accepts_repository_root_and_keeps_display_metadata()
    {
        using var workspace = TestWorkspace.Create();
        var moduleId = "test-module-" + Guid.NewGuid().ToString("N");
        var repoRoot = Path.Combine(workspace.ExtractRoot, "RepoRoot");
        var moduleRoot = Path.Combine(repoRoot, "TestModule.mod");
        Directory.CreateDirectory(moduleRoot);
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        File.WriteAllText(Path.Combine(repoRoot, ".git", "config"), "private repo metadata");
        File.WriteAllBytes(Path.Combine(moduleRoot, "author.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllText(Path.Combine(moduleRoot, "module.json"), $$"""
        {
          "id": "{{moduleId}}",
          "name": "Test Module",
          "version": "1.0.0",
          "author": "Module Author",
          "description": "Module description",
          "url": "https://example.test/module",
          "color": "#1E8449",
          "entryPoints": {
            "start": "server.exe"
          }
        }
        """);

        try
        {
            var imported = new ModuleImportService().ImportFromFolder(repoRoot);

            Assert.Equal(moduleId, imported.Id);
            Assert.Equal("Module Author", imported.DisplayAuthor);
            Assert.Equal("Module description", imported.DisplayDescription);
            Assert.Equal("https://example.test/module", imported.DisplayUrl);
            Assert.Equal("#1E8449", imported.DisplayColor);
            Assert.Equal(Path.Combine(imported.Path, "author.png"), imported.AuthorImagePath);
            Assert.False(Directory.Exists(Path.Combine(imported.Path, ".git")));
        }
        finally
        {
            DeleteModuleDirectory(ModuleStoragePaths.InstalledModules, moduleId);
            DeleteModuleDirectory(ModuleStoragePaths.DisabledModules, moduleId);
        }
    }

    private static void DeleteModuleDirectory(string root, string moduleId)
    {
        var path = Path.Combine(root, moduleId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root;

        private TestWorkspace(string root)
        {
            _root = root;
            ExtractRoot = Path.Combine(root, "extract");
            Directory.CreateDirectory(ExtractRoot);
        }

        public string ExtractRoot { get; }

        public static TestWorkspace Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestWorkspace(root);
        }

        public string CreateZip(params (string Name, string Content)[] entries)
        {
            var zipPath = Path.Combine(_root, "module.zip");
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }

            return zipPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
