using System.IO;
using System.Runtime.InteropServices;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Windows;

public sealed class WindowsFirewallService
{
    private const int NetFwIpProtocolTcp = 6;
    private const int NetFwIpProtocolUdp = 17;
    private const int NetFwRuleDirectionIn = 1;
    private const int NetFwActionAllow = 1;
    private const int NetFwProfileAll = int.MaxValue;
    private const int FileNotFoundHResult = unchecked((int)0x80070002);
    private readonly Func<IWindowsFirewallRuleStore> _storeFactory;

    public WindowsFirewallService(Func<IWindowsFirewallRuleStore>? storeFactory = null)
    {
        _storeFactory = storeFactory ?? (() => new ComWindowsFirewallRuleStore());
    }

    public FirewallAvailabilityResult CheckAvailability()
    {
        try
        {
            _ = _storeFactory();
            return new FirewallAvailabilityResult(true, "Windows Firewall COM API is available.");
        }
        catch (Exception ex)
        {
            return new FirewallAvailabilityResult(false, MapFirewallError(ex));
        }
    }

    public IReadOnlyList<FirewallRuleStatus> GetRuleStatuses(InstalledServer server, IGameServerModule module)
    {
        var rules = GetRequiredRules(server, module).ToArray();
        if (rules.Length == 0)
        {
            return [];
        }

        var store = _storeFactory();
        return rules
            .Select(rule => rule with { Exists = store.RuleExists(rule.Name) })
            .ToArray();
    }

    public IReadOnlyList<FirewallRuleStatus> CreateMissingRules(InstalledServer server, IGameServerModule module)
    {
        return CreateOrUpdateRules(new FirewallRuleApplyRequest(ServerInstanceFactory.Load(server), module))
            .CreatedRules;
    }

    public IReadOnlyList<FirewallRuleStatus> CreateMissingRules(ServerInstance instance, IGameServerModule module)
    {
        return CreateOrUpdateRules(new FirewallRuleApplyRequest(instance, module))
            .CreatedRules;
    }

    public int RemoveManagedRules(InstalledServer server, IGameServerModule module)
    {
        return RemoveRules(new FirewallRuleRemoveRequest(server, module)).RemovedRules.Count;
    }

    public FirewallRuleUpdateResult CreateOrUpdateRules(FirewallRuleApplyRequest request)
    {
        return ApplyRules(request.Instance.Name, GetRequiredRules(request.Instance, request.Module), FirewallRuleAction.CreateOrUpdate);
    }

    public FirewallRuleUpdateResult RemoveRules(FirewallRuleRemoveRequest request)
    {
        return ApplyRules(request.Server.Name, GetRequiredRules(request.Server, request.Module), FirewallRuleAction.Remove);
    }

    public IReadOnlyList<FirewallRuleStatus> GetRequiredRules(InstalledServer server, IGameServerModule module)
    {
        if (!server.CanEditConfig || !File.Exists(server.ConfigPath))
        {
            return [];
        }

        var instance = ServerInstanceFactory.Load(server);
        return GetRequiredRules(server.Id, server.Name, instance.Settings, module);
    }

    public IReadOnlyList<FirewallRuleStatus> GetRequiredRules(ServerInstance instance, IGameServerModule module)
    {
        return GetRequiredRules(instance.Id, instance.Name, instance.Settings, module);
    }

    private static IReadOnlyList<FirewallRuleStatus> GetRequiredRules(
        string serverId,
        string serverName,
        IReadOnlyDictionary<string, object?> settings,
        IGameServerModule module)
    {
        var ports = module.GetConfigFields()
            .Where(field => field.Type == ConfigFieldType.Port)
            .Select(field => TryCreatePortRule(field, settings))
            .OfType<FirewallPortRule>()
            .GroupBy(rule => rule.Port)
            .Select(group => group.First())
            .OrderBy(rule => rule.Port)
            .ToArray();

        return ports
            .SelectMany(rule => new[]
            {
                CreateStatus(serverId, serverName, rule, FirewallProtocol.Tcp),
                CreateStatus(serverId, serverName, rule, FirewallProtocol.Udp)
            })
            .ToArray();
    }

    private FirewallRuleUpdateResult ApplyRules(
        string serverName,
        IReadOnlyList<FirewallRuleStatus> rules,
        FirewallRuleAction action)
    {
        IWindowsFirewallRuleStore store;
        try
        {
            store = _storeFactory();
        }
        catch (Exception ex)
        {
            var failure = FirewallRuleActionResult.Failed(
                null,
                action,
                "Windows Firewall could not be opened.",
                MapFirewallError(ex));
            return new FirewallRuleUpdateResult(false, [failure]);
        }

        var steps = new List<FirewallRuleActionResult>();
        foreach (var rule in rules)
        {
            try
            {
                var exists = store.RuleExists(rule.Name);
                if (action == FirewallRuleAction.CreateOrUpdate)
                {
                    if (exists)
                    {
                        steps.Add(FirewallRuleActionResult.Skipped(rule with { Exists = true }, action, "Firewall rule already exists."));
                        continue;
                    }

                    store.AddRule(new FirewallRuleDefinition(rule, serverName));
                    steps.Add(FirewallRuleActionResult.Succeeded(rule with { Exists = true }, action, "Firewall rule created."));
                    continue;
                }

                if (!exists)
                {
                    steps.Add(FirewallRuleActionResult.Skipped(rule, action, "Managed firewall rule was not present."));
                    continue;
                }

                store.RemoveRule(rule.Name);
                steps.Add(FirewallRuleActionResult.Succeeded(rule, action, "Firewall rule removed."));
            }
            catch (Exception ex)
            {
                steps.Add(FirewallRuleActionResult.Failed(rule, action, "Windows Firewall rule action failed.", MapFirewallError(ex)));
            }
        }

        return new FirewallRuleUpdateResult(steps.All(step => step.Success), steps);
    }

    private static void AddRule(dynamic policy, FirewallRuleDefinition definition)
    {
        var rule = definition.Rule;
        dynamic firewallRule = TryFirewallStep($"create COM rule for '{rule.Name}'", CreateRule);
        TryFirewallStep($"set Name for '{rule.Name}'", () => { firewallRule.Name = rule.Name; });
        TryFirewallStep($"set Description for '{rule.Name}'", () => { firewallRule.Description = definition.Description; });
        TryFirewallStep($"set Protocol for '{rule.Name}'", () => { firewallRule.Protocol = ToFirewallProtocol(rule.Protocol); });
        TryFirewallStep($"set LocalPorts for '{rule.Name}'", () => { firewallRule.LocalPorts = rule.Port.ToString(); });
        TryFirewallStep($"set Direction for '{rule.Name}'", () => { firewallRule.Direction = NetFwRuleDirectionIn; });
        TryFirewallStep($"set Enabled for '{rule.Name}'", () => { firewallRule.Enabled = true; });
        TryFirewallStep($"set Profiles for '{rule.Name}'", () => { firewallRule.Profiles = NetFwProfileAll; });
        TryFirewallStep($"set InterfaceTypes for '{rule.Name}'", () => { firewallRule.InterfaceTypes = "All"; });
        TryFirewallStep($"set Action for '{rule.Name}'", () => { firewallRule.Action = NetFwActionAllow; });
        TryFirewallStep($"add '{rule.Name}'", () => { policy.Rules.Add(firewallRule); });
    }

    private static string MapFirewallError(Exception ex)
    {
        var message = ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005)
            ? "Administrator rights are required to manage Windows Firewall rules."
            : ex.Message;
        return $"HRESULT 0x{ex.HResult:X8}: {message}";
    }

    private static void TryFirewallStep(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            throw CreateFirewallStepException(step, ex);
        }
    }

    private static T TryFirewallStep<T>(string step, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            throw CreateFirewallStepException(step, ex);
        }
    }

    private static InvalidOperationException CreateFirewallStepException(string step, Exception ex)
    {
        return new InvalidOperationException(
            $"Windows Firewall failed to {step}. HRESULT 0x{ex.HResult:X8}: {ex.Message}",
            ex);
    }

    private static FirewallPortRule? TryCreatePortRule(ConfigFieldDefinition field, IReadOnlyDictionary<string, object?> settings)
    {
        if (!IsFieldVisible(field, settings))
        {
            return null;
        }

        if (!settings.TryGetValue(field.Key, out var value) ||
            !int.TryParse(value?.ToString(), out var port) ||
            port is <= 0 or > 65535)
        {
            return null;
        }

        return new FirewallPortRule(field.Key, field.Label, port);
    }

    private static bool IsFieldVisible(ConfigFieldDefinition field, IReadOnlyDictionary<string, object?> settings)
    {
        if (field.VisibleWhen == null)
        {
            return true;
        }

        settings.TryGetValue(field.VisibleWhen.Key, out var value);
        if (field.VisibleWhen.IsTruthy.HasValue)
        {
            return IsTruthy(value) == field.VisibleWhen.IsTruthy.Value;
        }

        return string.Equals(value?.ToString() ?? string.Empty, field.VisibleWhen.EqualsValue ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool boolean => boolean,
            string text => bool.TryParse(text, out var parsed) ? parsed : !string.IsNullOrWhiteSpace(text),
            null => false,
            _ => true
        };
    }

    private static FirewallRuleStatus CreateStatus(string serverId, string serverName, FirewallPortRule rule, FirewallProtocol protocol)
    {
        return new FirewallRuleStatus(
            BuildRuleName(serverId, serverName, rule.Port, protocol),
            rule.Key,
            rule.Label,
            rule.Port,
            protocol,
            Exists: false);
    }

    public static string BuildRuleName(string serverId, string serverName, int port, FirewallProtocol protocol)
    {
        return $"WindowsGSH {serverId} {SanitizeRuleName(serverName)} {protocol} {port}";
    }

    private static string SanitizeRuleName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Server" : sanitized;
    }

    private static int ToFirewallProtocol(FirewallProtocol protocol)
    {
        return protocol == FirewallProtocol.Tcp ? NetFwIpProtocolTcp : NetFwIpProtocolUdp;
    }

    private static dynamic CreatePolicy()
    {
        return CreateComObject("HNetCfg.FwPolicy2");
    }

    private static dynamic CreateRule()
    {
        return CreateComObject("HNetCfg.FWRule");
    }

    private static dynamic CreateComObject(string progId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Firewall rule management is only available on Windows.");
        }

        var type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException($"Windows Firewall COM API is not available ({progId}).");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not create Windows Firewall COM object ({progId}).");
    }

    private static dynamic? TryFindRule(dynamic policy, string name)
    {
        try
        {
            return policy.Rules.Item(name);
        }
        catch (COMException)
        {
            return null;
        }
        catch (Exception ex) when (ex.HResult == FileNotFoundHResult)
        {
            return null;
        }
    }

    private sealed record FirewallPortRule(string Key, string Label, int Port);

    private sealed class ComWindowsFirewallRuleStore : IWindowsFirewallRuleStore
    {
        private readonly dynamic _policy = TryFirewallStep("create firewall policy", CreatePolicy);

        public bool RuleExists(string name)
        {
            return TryFindRule(_policy, name) != null;
        }

        public void AddRule(FirewallRuleDefinition rule)
        {
            WindowsFirewallService.AddRule(_policy, rule);
        }

        public void RemoveRule(string name)
        {
            TryFirewallStep($"remove rule '{name}'", () => { _policy.Rules.Remove(name); });
        }
    }
}

public interface IWindowsFirewallRuleStore
{
    bool RuleExists(string name);

    void AddRule(FirewallRuleDefinition rule);

    void RemoveRule(string name);
}

public sealed record FirewallRuleApplyRequest(ServerInstance Instance, IGameServerModule Module);

public sealed record FirewallRuleRemoveRequest(InstalledServer Server, IGameServerModule Module);

public sealed record FirewallRuleDefinition(FirewallRuleStatus Rule, string ServerName)
{
    public string Description => $"WindowsGSH managed inbound rule for {ServerName} ({Rule.Label}).";
}

public sealed record FirewallRuleUpdateResult(
    bool Success,
    IReadOnlyList<FirewallRuleActionResult> Steps)
{
    public IReadOnlyList<FirewallRuleStatus> CreatedRules => Steps
        .Where(step => step.Action == FirewallRuleAction.CreateOrUpdate && step.Success && !step.WasSkipped && step.Rule != null)
        .Select(step => step.Rule!)
        .ToArray();

    public IReadOnlyList<FirewallRuleStatus> RemovedRules => Steps
        .Where(step => step.Action == FirewallRuleAction.Remove && step.Success && !step.WasSkipped && step.Rule != null)
        .Select(step => step.Rule!)
        .ToArray();

    public string Message => string.Join("; ", Steps.Select(step => step.Message));
}

public sealed record FirewallRuleActionResult(
    FirewallRuleStatus? Rule,
    FirewallRuleAction Action,
    bool Success,
    bool WasSkipped,
    string Message,
    string? Error = null)
{
    public static FirewallRuleActionResult Succeeded(FirewallRuleStatus rule, FirewallRuleAction action, string message)
    {
        return new FirewallRuleActionResult(rule, action, true, false, message);
    }

    public static FirewallRuleActionResult Skipped(FirewallRuleStatus rule, FirewallRuleAction action, string message)
    {
        return new FirewallRuleActionResult(rule, action, true, true, message);
    }

    public static FirewallRuleActionResult Failed(FirewallRuleStatus? rule, FirewallRuleAction action, string message, string error)
    {
        return new FirewallRuleActionResult(rule, action, false, false, message, error);
    }
}

public enum FirewallRuleAction
{
    CreateOrUpdate,
    Remove
}

public sealed record FirewallRuleStatus(
    string Name,
    string Key,
    string Label,
    int Port,
    FirewallProtocol Protocol,
    bool Exists);

public enum FirewallProtocol
{
    Tcp,
    Udp
}

public sealed record FirewallAvailabilityResult(bool IsAvailable, string Message);
