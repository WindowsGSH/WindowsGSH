using System.IO;
using System.Threading;

namespace WindowsGSH.Core.Modules;

public sealed class ModuleRegistry
{
    private static ModuleCacheState SharedState = new(0, null, null);

    public IReadOnlyList<IGameServerModule> GetModules()
    {
        return GetModuleDescriptors()
            .Select(descriptor => descriptor.Module)
            .ToArray();
    }

    public IReadOnlyList<ModuleDescriptor> GetModuleDescriptors()
    {
        while (true)
        {
            var observed = Volatile.Read(ref SharedState);
            if (observed.Descriptors != null)
            {
                return observed.Descriptors;
            }

            if (observed.LoadTask != null)
            {
                return CompleteLoad(observed, observed.LoadTask);
            }

            var loadTask = Task.Factory.StartNew(
                LoadModules,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            var loading = observed with { LoadTask = loadTask };
            if (ReferenceEquals(Interlocked.CompareExchange(ref SharedState, loading, observed), observed))
            {
                return CompleteLoad(loading, loadTask);
            }
        }
    }

    public static void InvalidateCache()
    {
        while (true)
        {
            var observed = Volatile.Read(ref SharedState);
            var updated = new ModuleCacheState(observed.Generation + 1, null, null);
            if (ReferenceEquals(Interlocked.CompareExchange(ref SharedState, updated, observed), observed))
            {
                return;
            }
        }
    }

    private static IReadOnlyList<ModuleDescriptor> CompleteLoad(ModuleCacheState loadingState, Task<IReadOnlyList<ModuleDescriptor>> loadTask)
    {
        var loaded = loadTask.GetAwaiter().GetResult();
        while (true)
        {
            var observed = Volatile.Read(ref SharedState);
            if (observed.Generation != loadingState.Generation)
            {
                return loaded;
            }

            if (observed.Descriptors != null)
            {
                return observed.Descriptors;
            }

            if (!ReferenceEquals(observed.LoadTask, loadTask))
            {
                return observed.LoadTask == null
                    ? loaded
                    : CompleteLoad(observed, observed.LoadTask);
            }

            var completed = new ModuleCacheState(observed.Generation, loaded, null);
            if (ReferenceEquals(Interlocked.CompareExchange(ref SharedState, completed, observed), observed))
            {
                return loaded;
            }
        }
    }

    private static IReadOnlyList<ModuleDescriptor> LoadModules()
    {
        ModuleStoragePaths.EnsureCreated();

        // Built-ins are added first so that group.First() always returns the built-in
        // when an installed module uses a colliding ID. Built-in IDs are reserved.
        var descriptors = new List<ModuleDescriptor>(GetBuiltInModuleDescriptors());
        foreach (var moduleDirectory in Directory.EnumerateDirectories(ModuleStoragePaths.InstalledModules))
        {
            try
            {
                var provenance = GetProvenanceSnapshot(moduleDirectory);
                LogProvenanceWarning(moduleDirectory, provenance);
                var module = LoadExternalModule(moduleDirectory, out var loadKind, out var warnings);
                descriptors.Add(ModuleDescriptor.Create(module, moduleDirectory, loadKind, provenance, warnings));
                ModuleLog.Add($"Loaded {loadKind} module {module.Name} ({module.Id}) from {moduleDirectory}.");
            }
            catch (Exception ex)
            {
                ModuleLog.Add($"Failed to load module {moduleDirectory}: {ex.Message}");
            }
        }

        return descriptors
            .GroupBy(descriptor => descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(descriptor => descriptor.Name)
            .ToArray();
    }

    private static IReadOnlyList<ModuleDescriptor> GetBuiltInModuleDescriptors()
    {
        IGameServerModule[] modules =
        [
            new GenericWrapperModule()
        ];

        return modules
            .Select(module => ModuleDescriptor.Create(
                module,
                modulePath: "built-in",
                moduleType: "Built-in module",
                provenance: new ModuleProvenanceSnapshot(
                    Metadata: null,
                    CurrentHash: string.Empty,
                    HasChangedSinceImport: false,
                    Warning: null)))
            .ToArray();
    }

    private static IGameServerModule LoadExternalModule(
        string moduleDirectory,
        out string loadKind,
        out IReadOnlyList<ModuleValidationMessage> warnings)
    {
        var collectedWarnings = new List<ModuleValidationMessage>();

        // Tamper detection applies to all load kinds (C# and JSON).
        // HasChangedSinceImport is only true when import.json exists and the hash differs;
        // dev/local modules with no import.json are unaffected.
        var snapshot = new ModuleProvenanceService().GetSnapshot(moduleDirectory);
        if (snapshot.HasChangedSinceImport)
        {
            throw new InvalidOperationException(
                $"Module '{Path.GetFileName(moduleDirectory)}' cannot be loaded: " +
                "its files have changed since it was imported. " +
                "Use 'Re-trust module' in the Modules panel to confirm trust before loading.");
        }

        if (Directory.EnumerateFiles(moduleDirectory, "*.cs", SearchOption.AllDirectories).Any())
        {
            try
            {
                var module = new CSharpModuleLoader().Load(moduleDirectory, warning =>
                {
                    collectedWarnings.Add(warning);
                    LogValidationWarning(moduleDirectory, warning);
                });
                loadKind = "C#";
                warnings = collectedWarnings;
                return module;
            }
            catch (Exception ex)
            {
                ModuleLog.Add($"C# module load failed for {moduleDirectory}. Falling back to module.json. {ex}");
            }
        }

        var moduleJsonPath = Path.Combine(moduleDirectory, "module.json");
        if (!File.Exists(moduleJsonPath))
        {
            throw new InvalidOperationException("Module folder does not contain module.json.");
        }

        loadKind = "JSON";
        var jsonModule = JsonGameServerModule.Load(moduleJsonPath, warning =>
        {
            collectedWarnings.Add(warning);
            LogValidationWarning(moduleDirectory, warning);
        });
        warnings = collectedWarnings;
        return jsonModule;
    }

    private static void LogValidationWarning(string moduleDirectory, ModuleValidationMessage warning)
    {
        ModuleValidationWarningLog.AddOnce(moduleDirectory, warning);
    }

    private static ModuleProvenanceSnapshot GetProvenanceSnapshot(string moduleDirectory)
    {
        return new ModuleProvenanceService().GetSnapshot(moduleDirectory);
    }

    private static void LogProvenanceWarning(string moduleDirectory, ModuleProvenanceSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Warning))
        {
            ModuleLog.Add($"Module provenance warning for {Path.GetFileName(moduleDirectory)}: {snapshot.Warning} Hash={snapshot.CurrentHash}.");
        }
    }

    private sealed record ModuleCacheState(
        int Generation,
        IReadOnlyList<ModuleDescriptor>? Descriptors,
        Task<IReadOnlyList<ModuleDescriptor>>? LoadTask);
}
