namespace WindowsGSH.Core.Modules;

public sealed record ServerDisplayInfo(
    string IpAddress,
    string Port,
    string MaxPlayers)
{
    public int? MaxPlayersNumber => int.TryParse(MaxPlayers, out var value) ? value : null;
}
