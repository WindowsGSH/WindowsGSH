using System.IO;

namespace WindowsGSH.Core.Modules;

public sealed class ModuleDevelopmentDiagnosticsService
{
    public ModuleDevelopmentDiagnosticReport ValidateFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return ModuleDevelopmentDiagnosticReport.Failed(
                folderPath,
                "Unknown module",
                [ModuleDevelopmentDiagnostic.Error("folder.missing", folderPath, "Module folder does not exist.")]);
        }

        var moduleJsonPath = FindModuleJson(folderPath);
        if (moduleJsonPath == null)
        {
            return ModuleDevelopmentDiagnosticReport.Failed(
                folderPath,
                "Unknown module",
                [ModuleDevelopmentDiagnostic.Error("manifest.missing", folderPath, "Folder does not contain a module.json file.")]);
        }

        var sourceFolder = Path.GetDirectoryName(moduleJsonPath)!;
        var diagnostics = new List<ModuleDevelopmentDiagnostic>();
        try
        {
            IGameServerModule module;
            if (Directory.EnumerateFiles(sourceFolder, "*.cs", SearchOption.AllDirectories).Any(IsNotGeneratedPath))
            {
                module = new CSharpModuleLoader().Load(sourceFolder, warning => diagnostics.Add(ToDiagnostic(warning)));
            }
            else
            {
                module = JsonGameServerModule.Load(moduleJsonPath, warning => diagnostics.Add(ToDiagnostic(warning)));
            }

            return new ModuleDevelopmentDiagnosticReport(
                sourceFolder,
                module.Name,
                module.Id,
                module.Version,
                module is JsonGameServerModule ? "JSON module" : "C# module",
                module.Capabilities,
                Success: true,
                diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(ModuleDevelopmentDiagnostic.Error("module.validation.failed", moduleJsonPath, FormatException(ex)));
            return ModuleDevelopmentDiagnosticReport.Failed(sourceFolder, Path.GetFileName(sourceFolder), diagnostics);
        }
    }

    private static string? FindModuleJson(string root)
    {
        var rootModuleJson = Path.Combine(root, "module.json");
        return File.Exists(rootModuleJson)
            ? rootModuleJson
            : Directory.EnumerateFiles(root, "module.json", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static bool IsNotGeneratedPath(string path)
    {
        var parts = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return !parts.Any(part =>
            string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static ModuleDevelopmentDiagnostic ToDiagnostic(ModuleValidationMessage message)
    {
        return new ModuleDevelopmentDiagnostic(message.Severity, message.Code, message.Path, message.Message);
    }

    private static string FormatException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(Environment.NewLine, messages);
    }
}

public sealed record ModuleDevelopmentDiagnosticReport(
    string ModulePath,
    string Name,
    string Id,
    string Version,
    string ModuleType,
    ModuleCapabilities? Capabilities,
    bool Success,
    IReadOnlyList<ModuleDevelopmentDiagnostic> Diagnostics)
{
    public static ModuleDevelopmentDiagnosticReport Failed(
        string modulePath,
        string name,
        IReadOnlyList<ModuleDevelopmentDiagnostic> diagnostics)
    {
        return new ModuleDevelopmentDiagnosticReport(
            modulePath,
            name,
            string.Empty,
            string.Empty,
            "Unknown module",
            null,
            Success: false,
            diagnostics);
    }
}

public sealed record ModuleDevelopmentDiagnostic(
    ModuleValidationSeverity Severity,
    string Code,
    string Path,
    string Message)
{
    public string DisplayText => $"{DisplayPrefix}: {Path} [{Code}] {Message}";

    public string DisplayPrefix => Code == "module.loaded"
        ? "Loaded"
        : Severity.ToString();

    public static ModuleDevelopmentDiagnostic Error(string code, string path, string message)
    {
        return new ModuleDevelopmentDiagnostic(ModuleValidationSeverity.Error, code, path, message);
    }

    public static ModuleDevelopmentDiagnostic Info(string code, string path, string message)
    {
        return new ModuleDevelopmentDiagnostic(ModuleValidationSeverity.Info, code, path, message);
    }
}
