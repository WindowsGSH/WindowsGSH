using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace WindowsGSH.Core.Modules;

public sealed class CSharpModuleLoader
{
    internal const int  MaxSourceFiles = 200;
    internal const long MaxSourceBytes = 50L * 1024 * 1024;

    private const string GeneratedGlobalUsings = """
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net;
global using System.Net.NetworkInformation;
global using System.Net.Sockets;
global using System.Runtime.InteropServices;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Nodes;
global using System.Text.RegularExpressions;
global using System.Threading;
global using System.Threading.Tasks;
""";

    public IGameServerModule Load(string moduleDirectory, Action<ModuleValidationMessage>? warningSink = null)
    {
        var manifestPath = Path.Combine(moduleDirectory, "module.json");
        var manifest = ModuleManifest.Load(manifestPath, warningSink);
        var sourceFiles = Directory.EnumerateFiles(moduleDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(moduleDirectory, path))
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            throw new InvalidOperationException("Module does not contain C# source files.");
        }

        // Prevent pathological modules from exhausting CPU/memory during compilation.
        if (sourceFiles.Length > MaxSourceFiles)
            throw new InvalidOperationException(
                $"C# module has too many source files ({sourceFiles.Length}; limit is {MaxSourceFiles}).");

        var totalBytes = sourceFiles.Sum(path => new FileInfo(path).Length);
        if (totalBytes > MaxSourceBytes)
            throw new InvalidOperationException(
                $"C# module source files are too large ({totalBytes / (1024 * 1024)} MB; limit is {MaxSourceBytes / (1024 * 1024)} MB).");

        var syntaxTrees = sourceFiles
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path))
            .Append(CSharpSyntaxTree.ParseText(GeneratedGlobalUsings, path: "WindowsGSH.GeneratedGlobalUsings.cs"));
        var assemblyName = "WindowsGSH.ExternalModule." + Path.GetFileName(moduleDirectory) + "." + Guid.NewGuid().ToString("N");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var assemblyStream = new MemoryStream();
        var result = compilation.Emit(assemblyStream);
        if (!result.Success)
        {
            var diagnostics = result.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Take(8)
                .Select(diagnostic => diagnostic.ToString());
            throw new InvalidOperationException("C# module compile failed: " + string.Join(Environment.NewLine, diagnostics));
        }

        assemblyStream.Position = 0;
        var loadContext = new AssemblyLoadContext(assemblyName, isCollectible: false);
        var assembly = loadContext.LoadFromStream(assemblyStream);
        var moduleType = ResolveModuleType(assembly, manifest)
            ?? throw new InvalidOperationException("C# module did not expose a public parameterless IGameServerModule implementation.");

        var module = (IGameServerModule)Activator.CreateInstance(moduleType)!;
        if (module is IManifestBackedModule manifestBackedModule)
        {
            manifestBackedModule.Configure(manifest, moduleDirectory);
        }

        ModulePortSnapshotStore.Register(module, manifest.ToPorts());
        return module;
    }

    private static Type? ResolveModuleType(Assembly assembly, ModuleManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Entry))
        {
            var type = assembly.GetType(manifest.Entry, throwOnError: false, ignoreCase: false);
            if (type == null)
            {
                throw new InvalidOperationException($"C# module entry type was not found: {manifest.Entry}");
            }

            if (!typeof(IGameServerModule).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new InvalidOperationException($"C# module entry type must be a public parameterless {nameof(IGameServerModule)}: {manifest.Entry}");
            }

            return type;
        }

        return assembly.GetTypes()
            .FirstOrDefault(type => typeof(IGameServerModule).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null);
    }

    private static IReadOnlyList<MetadataReference> GetReferences()
    {
        var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? [];

        var references = new List<MetadataReference>();
        var referencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in trustedPlatformAssemblies)
        {
            AddReference(references, referencePaths, path);
        }

        var coreAssemblyReferenced = EnsureCurrentAssemblyReference(references, referencePaths, typeof(IGameServerModule).Assembly);
        var appAssemblyReferenced = EnsureCurrentAssemblyReference(references, referencePaths, typeof(CSharpModuleLoader).Assembly);

        if (!coreAssemblyReferenced || !appAssemblyReferenced)
        {
            throw new InvalidOperationException(
                "WindowsGSH assemblies are not available as files for C# module compilation. " +
                "If this is a single-file publish, publish with -p:IncludeAllContentForSelfExtract=true.");
        }

        return references;
    }

    internal static bool EnsureCurrentAssemblyReference(
        List<MetadataReference> references,
        HashSet<string> referencePaths,
        Assembly assembly)
    {
        var assemblyFileName = $"{assembly.GetName().Name}.dll";
        RemoveReferencesByFileName(references, referencePaths, assemblyFileName);

        var assemblyLocation = assembly.Location;
        if (AddReference(references, referencePaths, assemblyLocation))
        {
            return true;
        }

        return AddReference(references, referencePaths, Path.Combine(AppContext.BaseDirectory, assemblyFileName));
    }

    internal static void RemoveReferencesByFileName(
        List<MetadataReference> references,
        HashSet<string> referencePaths,
        string assemblyFileName)
    {
        var stalePaths = referencePaths
            .Where(path => string.Equals(Path.GetFileName(path), assemblyFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (stalePaths.Length == 0)
        {
            return;
        }

        references.RemoveAll(reference =>
            reference is PortableExecutableReference portable &&
            !string.IsNullOrWhiteSpace(portable.FilePath) &&
            stalePaths.Contains(portable.FilePath, StringComparer.OrdinalIgnoreCase));
        foreach (var path in stalePaths)
        {
            referencePaths.Remove(path);
        }
    }

    private static bool AddReference(
        List<MetadataReference> references,
        HashSet<string> referencePaths,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !referencePaths.Add(path))
        {
            return false;
        }

        references.Add(MetadataReference.CreateFromFile(path));
        return true;
    }

    private static bool IsGeneratedPath(string moduleDirectory, string path)
    {
        var relativePath = Path.GetRelativePath(moduleDirectory, path);
        var parts = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return parts.Any(part =>
            string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
    }
}
