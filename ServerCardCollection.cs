using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WindowsGSH.Core.Servers;

namespace WindowsGSH;

internal sealed class ServerCardCollection
{
    // Keep the collection and each item stable across the three-second metrics refresh. Replacing
    // ItemsSource (or every item) makes WPF rebuild the full card template and its automation peers;
    // those detached trees can be retained by WPF long enough to cause unbounded Gen 2 growth.
    public ObservableCollection<ServerCardItem> Items { get; } = [];

    public void Update(IReadOnlyList<InstalledServer> servers)
    {
        for (var targetIndex = 0; targetIndex < servers.Count; targetIndex++)
        {
            var server = servers[targetIndex];
            var existingIndex = FindIndex(server.Id, targetIndex);
            ServerCardItem item;

            if (existingIndex < 0)
            {
                item = new ServerCardItem(server);
                Items.Insert(targetIndex, item);
            }
            else
            {
                if (existingIndex != targetIndex)
                {
                    Items.Move(existingIndex, targetIndex);
                }

                item = Items[targetIndex];
                item.Update(server);
            }
        }

        while (Items.Count > servers.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }

    private int FindIndex(string serverId, int startIndex)
    {
        for (var index = startIndex; index < Items.Count; index++)
        {
            if (string.Equals(Items[index].Server.Id, serverId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}

internal sealed class ServerCardItem : INotifyPropertyChanged
{
    public ServerCardItem(InstalledServer server)
    {
        Server = server;
    }

    public InstalledServer Server { get; private set; }

    public string Id => Server.Id;
    public string Name => Server.Name;
    public string ModuleId => Server.ModuleId;
    public string IpAddress => Server.IpAddress;
    public string Port => Server.Port;
    public string ProcessId => Server.ProcessId;
    public string CpuUsage => Server.CpuUsage;
    public string MemoryUsage => Server.MemoryUsage;
    public string PlayerCount => Server.PlayerCount;
    public string CurrentStatusText => Server.CurrentStatusText;
    public string Uptime => Server.Uptime;
    public bool IsOperationRunning => Server.IsOperationRunning;
    public string OperationText => Server.OperationText;
    public string? LastOperationError => Server.LastOperationError;
    public ServerRuntimeStatus Status => Server.Status;
    public string StatusText => Server.StatusText;
    public bool HasUpdateAvailable => Server.HasUpdateAvailable;
    public bool CanShowInfo => Server.CanShowInfo;
    public bool CanEditConfig => Server.CanEditConfig;
    public bool CanStart => Server.CanStart;
    public bool CanStop => Server.CanStop;
    public bool SupportsVerify => Server.SupportsVerify;
    public bool AllowsOnlineVerification => Server.AllowsOnlineVerification;
    public string CpuTrend => Server.CpuTrend;
    public string MemoryTrend => Server.MemoryTrend;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(InstalledServer server)
    {
        if (ReferenceEquals(Server, server))
        {
            return;
        }

        Server = server;
        OnPropertyChanged(string.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
