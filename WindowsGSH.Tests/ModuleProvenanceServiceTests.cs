using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleProvenanceServiceTests
{
    [Fact]
    public void ComputeModuleHash_is_deterministic_and_ignores_metadata()
    {
        using var workspace = new TestWorkspace();
        var modulePath = workspace.CreateModule("test.module");

        var first = ModuleProvenanceService.ComputeModuleHash(modulePath);
        File.WriteAllText(Path.Combine(modulePath, ModuleProvenanceService.MetadataFileName), "{}");
        var second = ModuleProvenanceService.ComputeModuleHash(modulePath);

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetSnapshot_detects_file_changes_since_import()
    {
        using var workspace = new TestWorkspace();
        var modulePath = workspace.CreateModule("changed.module");
        var service = new ModuleProvenanceService();
        service.WriteImportMetadata(modulePath, "folder", "module", ModuleManifestProvenance.Empty);

        File.AppendAllText(Path.Combine(modulePath, "module.json"), Environment.NewLine);
        var snapshot = service.GetSnapshot(modulePath);

        Assert.True(snapshot.HasChangedSinceImport);
        Assert.Contains("changed", snapshot.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteImportMetadata_persists_manifest_provenance()
    {
        using var workspace = new TestWorkspace();
        var modulePath = workspace.CreateModule(
            "provenance.module",
            """
            {
              "id": "provenance.module",
              "name": "Provenance",
              "version": "1.0",
              "author": "Someone",
              "description": "A useful game module.",
              "homepage": "https://example.test",
              "repository": "https://example.test/repo",
              "sourceUrl": "https://example.test/module.zip",
              "url": "https://example.test/project",
              "color": "#1E8449",
              "moduleApiVersion": "1.0",
              "minimumWindowsGshVersion": "1.0",
              "supportedWindowsGshVersions": [ "1.*" ],
              "entryPoints": { "start": "server.exe" },
              "runtime": { "processNames": [ "server" ] }
            }
            """);
        var service = new ModuleProvenanceService();
        var provenance = ModuleProvenanceService.ReadManifestProvenance(Path.Combine(modulePath, "module.json"));

        var metadata = service.WriteImportMetadata(modulePath, "zip", "module.zip", provenance);

        Assert.Equal("Someone", metadata.Author);
        Assert.Equal("A useful game module.", metadata.Description);
        Assert.Equal("https://example.test", metadata.Homepage);
        Assert.Equal("https://example.test/repo", metadata.Repository);
        Assert.Equal("https://example.test/module.zip", metadata.SourceUrl);
        Assert.Equal("https://example.test/project", metadata.Url);
        Assert.Equal("#1E8449", metadata.Color);
        Assert.Equal("1.0", metadata.ModuleApiVersion);
        Assert.Equal("1.0", metadata.MinimumWindowsGshVersion);
        Assert.Equal(["1.*"], metadata.SupportedWindowsGshVersions);
        Assert.Contains(metadata.Warnings, warning => warning.Contains("not created", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Snapshot_without_metadata_warns_but_does_not_mark_changed()
    {
        using var workspace = new TestWorkspace();
        var modulePath = workspace.CreateModule("json.module");

        var snapshot = new ModuleProvenanceService().GetSnapshot(modulePath);

        Assert.False(snapshot.HasChangedSinceImport);
        Assert.Contains("metadata", snapshot.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CSharp_candidate_warns_that_code_runs_without_blocking()
    {
        var warning = ModuleImportService.GetDefaultWarning(hasCSharp: true);

        Assert.Contains("run code", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not review", warning, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root;

        public TestWorkspace()
        {
            _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public string CreateModule(string id, string? manifest = null)
        {
            var modulePath = Path.Combine(_root, id);
            Directory.CreateDirectory(modulePath);
            File.WriteAllText(Path.Combine(modulePath, "module.json"), manifest ?? $$"""
                {
                  "id": "{{id}}",
                  "name": "{{id}}",
                  "version": "1.0",
                  "entryPoints": { "start": "server.exe" },
                  "runtime": { "processNames": [ "server" ] }
                }
                """);
            File.WriteAllText(Path.Combine(modulePath, "README.md"), "hello");
            return modulePath;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
