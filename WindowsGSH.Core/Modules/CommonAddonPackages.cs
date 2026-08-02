namespace WindowsGSH.Core.Modules;

public static class CommonAddonPackages
{
    public static AddonPackageDefinition SourceMod(Uri source, string gameFolder) =>
        new(
            AddonPackageKind.TarGz,
            source.ToString(),
            gameFolder,
            RequiredMarkers: ["addons/sourcemod"]);

    public static AddonPackageDefinition MetaModSource(Uri source, string gameFolder) =>
        new(
            AddonPackageKind.TarGz,
            source.ToString(),
            gameFolder,
            RequiredMarkers: ["addons/metamod"]);

    public static AddonPackageDefinition JavaPlugin(Uri source, string? fileName = null, string pluginFolder = "plugins") =>
        new(
            AddonPackageKind.File,
            source.ToString(),
            pluginFolder,
            FileName: fileName);
}
