namespace WindowsGSH.Core.Steam;

public interface ISteamCredentialProvider
{
    SteamCredentials? Load();
}
