using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;

namespace WindowsGSH;

public partial class MainWindow
{
    private ModuleRegistry _moduleRegistry = new();

    private void OpenModuleManagementButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        NavigateTo(ModulesNavigationId);
    }

    private void ModuleManagementView_ModulesChanged(object sender, EventArgs e)
    {
        _moduleRegistry = new ModuleRegistry();
        _installedServerLoader.ReloadModules();
        _ = RefreshInstalledServersSafelyAsync("module refresh");
        _ = RefreshFirstRunReadinessAsync("module change");
    }

    private IGameServerModule GetModule(InstalledServer server)
    {
        return _moduleRegistry.GetModules().FirstOrDefault(module => string.Equals(module.Id, server.ModuleId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Module not found: {server.ModuleId}");
    }
}
