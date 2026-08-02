using System.IO;
using WindowsGSH.Core;

namespace WindowsGSH.Core.Modules;

public static class ModuleStoragePaths
{
    public static string ImportRoot => AppPaths.GetPath("module-imports");

    public static string ImportInbox => Path.Combine(ImportRoot, "inbox");

    public static string ImportRejected => Path.Combine(ImportRoot, "rejected");

    public static string ImportArchive => Path.Combine(ImportRoot, "archive");

    public static string ModuleRoot => AppPaths.GetPath("modules");

    public static string InstalledModules => Path.Combine(ModuleRoot, "installed");

    public static string DisabledModules => Path.Combine(ModuleRoot, "disabled");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ImportInbox);
        Directory.CreateDirectory(ImportRejected);
        Directory.CreateDirectory(ImportArchive);
        Directory.CreateDirectory(InstalledModules);
        Directory.CreateDirectory(DisabledModules);
    }
}
