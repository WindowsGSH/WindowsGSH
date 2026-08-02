using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using WindowsGSH.Core.Windows;

namespace WindowsGSH.Core.Health;

public sealed class PortDiagnosticsService
{
    private readonly Func<IPortFirewallChecker?> _firewallCheckerFactory;
    private readonly Func<IReadOnlyList<IPEndPoint>> _getTcpListeners;

    public PortDiagnosticsService(
        Func<IPortFirewallChecker?>? firewallCheckerFactory = null,
        Func<IReadOnlyList<IPEndPoint>>? getTcpListeners = null)
    {
        _firewallCheckerFactory = firewallCheckerFactory ?? TryCreateComChecker;
        _getTcpListeners = getTcpListeners ?? GetRealTcpListeners;
    }

    /// <summary>
    /// Checks firewall rules and TCP listening state for each spec.
    /// Each spec maps to one TCP row and one UDP row in the result.
    /// The caller is responsible for providing all relevant port specs — including
    /// auxiliary ports (RCON, query, etc.) if module config is available.
    /// </summary>
    public IReadOnlyList<PortDiagnosticsRow> CheckPorts(IReadOnlyList<PortCheckSpec> specs)
    {
        IReadOnlyList<IPEndPoint> listeners;
        try
        {
            listeners = _getTcpListeners();
        }
        catch
        {
            listeners = [];
        }

        var firewall = _firewallCheckerFactory();
        var rows = new List<PortDiagnosticsRow>(specs.Count * 2);

        foreach (var spec in specs)
        {
            var tcpRuleName = WindowsFirewallService.BuildRuleName(spec.ServerId, spec.ServerName, spec.Port, FirewallProtocol.Tcp);
            var udpRuleName = WindowsFirewallService.BuildRuleName(spec.ServerId, spec.ServerName, spec.Port, FirewallProtocol.Udp);

            var tcpFirewall = firewall == null ? FirewallQueryResult.Unavailable : firewall.CheckRule(tcpRuleName);
            var udpFirewall = firewall == null ? FirewallQueryResult.Unavailable : firewall.CheckRule(udpRuleName);
            var tcpListening = IsListeningOnConfiguredAddress(listeners, spec.Port, spec.BindAddress);

            // Only warn about TCP not-listening when the caller explicitly marks this port as TCP.
            // Many game ports are UDP-only and have no TCP listener even when healthy.
            var tcpListeningWarning = spec.ExpectsTcpListener && !tcpListening;

            rows.Add(new PortDiagnosticsRow(
                spec.ServerName,
                spec.Port.ToString(),
                "TCP",
                MapFirewallStatus(tcpFirewall),
                tcpListening ? "Listening" : "Not listening",
                IsWarning: tcpFirewall == FirewallQueryResult.Missing || tcpListeningWarning));

            rows.Add(new PortDiagnosticsRow(
                spec.ServerName,
                spec.Port.ToString(),
                "UDP",
                MapFirewallStatus(udpFirewall),
                "Cannot verify",
                IsWarning: udpFirewall == FirewallQueryResult.Missing));
        }

        return rows;
    }

    private static bool IsListeningOnConfiguredAddress(IReadOnlyList<IPEndPoint> listeners, int port, string bindAddress)
    {
        // Wildcard or unspecified bind address — any listener on the port counts.
        if (string.IsNullOrWhiteSpace(bindAddress) ||
            bindAddress is "0.0.0.0" or "::")
        {
            return listeners.Any(ep => ep.Port == port);
        }

        if (!IPAddress.TryParse(bindAddress, out var configuredIp))
        {
            return listeners.Any(ep => ep.Port == port);
        }

        // Wildcard listeners (0.0.0.0 / ::) cover any configured address.
        return listeners.Any(ep =>
            ep.Port == port &&
            (ep.Address.Equals(configuredIp) ||
             ep.Address.Equals(IPAddress.Any) ||
             ep.Address.Equals(IPAddress.IPv6Any)));
    }

    private static string MapFirewallStatus(FirewallQueryResult result) => result switch
    {
        FirewallQueryResult.Exists => "Rule exists",
        FirewallQueryResult.Missing => "No rule",
        FirewallQueryResult.RequiresAdmin => "Requires admin",
        _ => "Unavailable"
    };

    private static IPortFirewallChecker? TryCreateComChecker()
    {
        try
        {
            return new ComPortFirewallChecker();
        }
        catch (UnauthorizedAccessException)
        {
            return new RequiresAdminFirewallChecker();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<IPEndPoint> GetRealTcpListeners()
    {
        return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
    }

    private sealed class ComPortFirewallChecker : IPortFirewallChecker
    {
        private readonly dynamic _policy;

        public ComPortFirewallChecker()
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows Firewall is only available on Windows.");
            }

            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                ?? throw new InvalidOperationException("Windows Firewall COM API not available.");
            _policy = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Could not create Windows Firewall COM object.");
        }

        public FirewallQueryResult CheckRule(string ruleName)
        {
            try
            {
                return _policy.Rules.Item(ruleName) != null
                    ? FirewallQueryResult.Exists
                    : FirewallQueryResult.Missing;
            }
            catch (COMException)
            {
                return FirewallQueryResult.Missing;
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002))
            {
                return FirewallQueryResult.Missing;
            }
            catch (UnauthorizedAccessException)
            {
                return FirewallQueryResult.RequiresAdmin;
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005))
            {
                return FirewallQueryResult.RequiresAdmin;
            }
            catch
            {
                return FirewallQueryResult.Unavailable;
            }
        }
    }

    private sealed class RequiresAdminFirewallChecker : IPortFirewallChecker
    {
        public FirewallQueryResult CheckRule(string ruleName) => FirewallQueryResult.RequiresAdmin;
    }
}

public interface IPortFirewallChecker
{
    FirewallQueryResult CheckRule(string ruleName);
}

public enum FirewallQueryResult
{
    Exists,
    Missing,
    RequiresAdmin,
    Unavailable
}

/// <summary>
/// Describes one port that should be checked for firewall rules and TCP listening state.
/// The caller provides all specs — one per managed port per server. Auxiliary ports
/// (RCON, query, etc.) derived from module config can be included when module context
/// is available at the call site.
/// </summary>
public sealed record PortCheckSpec(
    string ServerName,
    string ServerId,
    int Port,
    string BindAddress,
    bool ExpectsTcpListener = false);

public sealed record PortDiagnosticsRow(
    string ServerName,
    string Port,
    string Protocol,
    string FirewallStatus,
    string ListeningStatus,
    bool IsWarning);
