namespace WindowsGSH.Core.Web;

// BindAddress defaults to loopback only. Change to "0.0.0.0" or a specific LAN IP
// to allow access from other devices on the network — note that the web API is
// HTTP-only, so LAN exposure means credentials travel in cleartext.
//
// AllowLegacyWebSocketQueryStringAuth defaults to false (secure) - the compatibility exception
// for existing installs lives solely in AppSettings.LoadFrom's migration logic, not here, so any
// other caller constructing WebHostOptions directly gets the secure default automatically.
public sealed record WebHostOptions(
    int Port = 5000,
    string BindAddress = "127.0.0.1",
    bool TrustForwardedHeaders = false,
    bool AllowLegacyWebSocketQueryStringAuth = false);
