using Microsoft.CodeAnalysis;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class CSharpModuleLoaderReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.ModuleReferences", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RemoveReferencesByFileName_removes_stale_same_named_reference()
    {
        var staleDirectory = Path.Combine(_root, "stale");
        Directory.CreateDirectory(staleDirectory);
        var stalePath = Path.Combine(staleDirectory, "WindowsGSH.Core.dll");
        File.Copy(typeof(IGameServerModule).Assembly.Location, stalePath);
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(stalePath)
        };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stalePath };

        CSharpModuleLoader.RemoveReferencesByFileName(references, paths, "WindowsGSH.Core.dll");

        Assert.Empty(references);
        Assert.Empty(paths);
    }

    [Fact]
    public void EnsureCurrentAssemblyReference_replaces_stale_same_named_reference()
    {
        var staleDirectory = Path.Combine(_root, "stale");
        Directory.CreateDirectory(staleDirectory);
        var stalePath = Path.Combine(staleDirectory, "WindowsGSH.Core.dll");
        File.Copy(typeof(IGameServerModule).Assembly.Location, stalePath);
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(stalePath)
        };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stalePath };

        var added = CSharpModuleLoader.EnsureCurrentAssemblyReference(
            references,
            paths,
            typeof(IGameServerModule).Assembly);

        Assert.True(added);
        var reference = Assert.Single(references);
        var fileReference = Assert.IsAssignableFrom<PortableExecutableReference>(reference);
        Assert.Equal(typeof(IGameServerModule).Assembly.Location, fileReference.FilePath);
        Assert.DoesNotContain(stalePath, paths);
    }

    // ── P3-08: source-file count and size limits ─────────────────────────────

    [Fact]
    public void MaxSourceFiles_and_MaxSourceBytes_constants_have_expected_values()
    {
        Assert.Equal(200, CSharpModuleLoader.MaxSourceFiles);
        Assert.Equal(50L * 1024 * 1024, CSharpModuleLoader.MaxSourceBytes);
    }

    [Fact]
    public void Load_rejects_module_with_too_many_source_files()
    {
        var moduleDir = Path.Combine(_root, "too-many-files");
        Directory.CreateDirectory(moduleDir);
        File.WriteAllText(Path.Combine(moduleDir, "module.json"), """
            {
              "id": "too-many",
              "name": "Too Many Files",
              "entryPoints": { "start": "server.exe" }
            }
            """);

        // Write MaxSourceFiles + 1 minimal .cs files.
        for (var i = 0; i <= CSharpModuleLoader.MaxSourceFiles; i++)
            File.WriteAllText(Path.Combine(moduleDir, $"File{i}.cs"), "// placeholder");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new CSharpModuleLoader().Load(moduleDir));
        Assert.Contains("too many source files", ex.Message, StringComparison.OrdinalIgnoreCase);
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
