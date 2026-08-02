namespace WindowsGSH.Core.Servers;

public sealed class ServerStateCache
{
    private readonly object _sync = new();
    private readonly Dictionary<string, InstalledServer> _servers = [];

    public static ServerStateCache Shared { get; } = new();

    public void Update(IEnumerable<InstalledServer> servers)
    {
        lock (_sync)
        {
            foreach (var server in servers)
            {
                _servers[server.ServerFolder] = server;
            }
        }
    }

    public InstalledServer? GetByFolder(string serverFolder)
    {
        lock (_sync)
        {
            return _servers.TryGetValue(serverFolder, out var server) ? server : null;
        }
    }
}
