namespace WindowsGSH.Core.Modules;

public interface IManifestBackedModule
{
    void Configure(ModuleManifest manifest, string moduleDirectory);
}
