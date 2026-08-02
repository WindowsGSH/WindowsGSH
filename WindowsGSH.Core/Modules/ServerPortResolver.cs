namespace WindowsGSH.Core.Modules;

public enum ResolvedPortStatus
{
    Resolved,
    Unresolved,
    Invalid
}

public enum PortResolutionFailureReason
{
    None,
    Invalid,
    Overlap
}

public sealed record ResolvedPort(
    string Id,
    string Name,
    PortProtocol Protocol,
    ResolvedPortStatus Status,
    int? Port,
    int RangeSize,
    bool Required,
    bool OpenExternally,
    string? Error = null,
    bool CheckLocalListener = true,
    PortResolutionFailureReason FailureReason = PortResolutionFailureReason.None)
{
    public IEnumerable<int> PortRange => Port.HasValue
        ? Enumerable.Range(Port.Value, RangeSize)
        : [];
}

public interface IServerPortResolver
{
    IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings);

    IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance);
}

/// <summary>
/// Resolves a module's declared <see cref="ServerPortDefinition"/>s against one configured
/// server's actual settings, producing the effective port number(s) for each declaration, and
/// flags any of that <em>same server's own</em> ports that land on overlapping numbers on a
/// protocol they share. Deliberately does not detect conflicts against a <em>different</em>
/// configured server - that stays <c>ServerHealthService</c>'s job (see its own
/// <c>GetConfiguredPorts</c>/port-conflict check), which can migrate onto this resolver later
/// without changing its own behaviour, since this class only adds structure (protocol, ranges,
/// offsets, required/openExternally, same-server overlap) on top of what it already does today.
/// </summary>
public sealed class ServerPortResolver : IServerPortResolver
{
    public IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance)
    {
        return Resolve(module.GetPorts(), instance.Settings);
    }

    public IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings)
    {
        // Manifest-sourced ports already passed ModuleValidator.ValidatePorts before ever reaching
        // here, but a compiled C# module's IGameServerModule.GetPorts() override (see
        // GenericWrapperModule for a real example) returns ServerPortDefinition objects directly -
        // there is no equivalent validation step on that path at all. Tier 5.1's "never silently
        // accept invalid ports" rule has to hold for both, or a malformed C# module can crash this
        // method outright (a null/duplicate Id throws straight out of Dictionary/ToDictionary)
        // instead of degrading to a normal Invalid result the way every other bad-input case here
        // does. This upfront pass is index-based, not Id-based, specifically because it has to
        // stay safe even when the Id itself is the thing that's wrong.
        var invariantErrors = new string?[ports.Count];
        for (var i = 0; i < ports.Count; i++)
        {
            invariantErrors[i] = ValidateOwnInvariants(ports[i]);
        }

        // A blank/duplicate Id is already caught above (blank) or below (duplicate) as its own
        // per-port error; ports that fail there are excluded from byId entirely - not only would a
        // duplicate key crash a naive ToDictionary, but even without a crash, an offsetFrom
        // pointing at an ambiguous (duplicated) id has no single answer to resolve against.
        var idCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ports.Count; i++)
        {
            if (invariantErrors[i] == null)
            {
                idCounts[ports[i].Id] = idCounts.GetValueOrDefault(ports[i].Id) + 1;
            }
        }

        for (var i = 0; i < ports.Count; i++)
        {
            if (invariantErrors[i] == null && idCounts[ports[i].Id] > 1)
            {
                invariantErrors[i] = $"Port id '{ports[i].Id}' is declared more than once.";
            }
        }

        var safePorts = ports.Where((_, i) => invariantErrors[i] == null).ToArray();
        var byId = safePorts.ToDictionary(port => port.Id, StringComparer.OrdinalIgnoreCase);

        // long, not int, for the resolved base value: an offset port's value is a base (already
        // known to be within 1-65535) plus an arbitrary int Offset, and int+int can overflow
        // silently (e.g. a base near int.MaxValue-ish - impossible for a real port, but Offset
        // itself has no such bound). Doing the arithmetic in long and only narrowing to int once
        // BuildResult has confirmed the result is actually a valid port keeps a large or negative
        // offset from ever wrapping around into a coincidentally-in-range int.
        var resolvedBase = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: resolve every port whose value doesn't depend on another port (ConfigField and
        // Fixed sources). Offset ports are left for the fixed-point pass below, since their base
        // may be another offset port declared earlier or later in the list.
        foreach (var port in safePorts)
        {
            switch (port.Source)
            {
                case PortSource.Fixed:
                    resolvedBase[port.Id] = port.FixedValue;
                    break;
                case PortSource.ConfigField:
                    ResolveFromConfigField(port, settings, resolvedBase, errors);
                    break;
            }
        }

        // Pass 2: fixed-point resolution of Offset ports - repeatedly resolve (or fail) any offset
        // port whose base is now known, until a full pass makes no further change. Recording an
        // error counts as progress here just as much as a successful resolution does - otherwise a
        // port declared *before* the base it (indirectly) depends on can get stuck: it sees its
        // base as neither resolved nor errored yet in the very pass that base gets marked invalid,
        // "continue"s past it once with no error of its own recorded, and if that happened to be
        // the pass where nothing else changed either, the loop would stop before this port ever
        // gets a chance to notice its base is now broken. Treating "newly errored" as progress too
        // guarantees at least one more pass runs, so every dependent - regardless of declaration
        // order - eventually sees a base's final state, not just its state part-way through.
        bool progressed;
        do
        {
            progressed = false;
            foreach (var port in safePorts.Where(port => port.Source == PortSource.Offset))
            {
                if (resolvedBase.ContainsKey(port.Id) || errors.ContainsKey(port.Id))
                {
                    continue;
                }

                if (!byId.TryGetValue(port.OffsetFrom!, out var basePort))
                {
                    errors[port.Id] = $"Port {port.Id} references unknown offsetFrom port id: {port.OffsetFrom}.";
                    progressed = true;
                    continue;
                }

                if (errors.ContainsKey(basePort.Id))
                {
                    errors[port.Id] = $"Port {port.Id} depends on {basePort.Id}, which failed to resolve.";
                    progressed = true;
                    continue;
                }

                if (!resolvedBase.TryGetValue(basePort.Id, out var baseValue) || baseValue == null)
                {
                    continue;
                }

                resolvedBase[port.Id] = baseValue.Value + port.Offset;
                progressed = true;
            }
        } while (progressed);

        // Anything still unresolved at this point either bottoms out at a legitimately-unresolved
        // non-offset port (e.g. an optional config field with no value - stays Unresolved, not an
        // error) or is genuinely circular (a chain of Offset ports that only ever reference each
        // other, so the fixed-point loop above could never make progress on any of them - that
        // case becomes Invalid). Walking the chain, not just checking "did it settle," is what
        // tells the two apart.
        foreach (var port in safePorts.Where(port => port.Source == PortSource.Offset))
        {
            if (resolvedBase.ContainsKey(port.Id) || errors.ContainsKey(port.Id))
            {
                continue;
            }

            if (IsPartOfOffsetCycle(port, byId))
            {
                errors[port.Id] = $"Port {port.Id} could not be resolved - its offsetFrom chain is circular.";
            }
        }

        var results = new List<ResolvedPort>(ports.Count);
        for (var i = 0; i < ports.Count; i++)
        {
            results.Add(invariantErrors[i] != null
                ? BuildInvariantFailure(ports[i], invariantErrors[i]!)
                : BuildResult(ports[i], resolvedBase, errors));
        }

        DetectOverlaps(results);
        return results;
    }

    // Everything ServerPortResolver itself needs to hold for the rest of this method to be safe:
    // a non-blank Id (used as a dictionary key throughout), a non-blank Name (so Server Doctor and
    // forwarding instructions never render a blank label), a defined Protocol, a sane RangeSize,
    // and exactly one source. ModuleValidator checks the equivalent JSON-manifest shape
    // (ports.id.required, ports.name.required, ports.protocol.unsupported, ports.rangeSize.invalid,
    // ports.source.exclusive) - this is the same set of checks, applied to the domain type
    // directly, for the path that never goes through a manifest at all. Duplicate-Id detection
    // isn't here because it's inherently cross-port, not a single port's own property - handled by
    // the caller instead.
    private static string? ValidateOwnInvariants(ServerPortDefinition port)
    {
        if (string.IsNullOrWhiteSpace(port.Id))
        {
            return "Port has no id.";
        }

        if (string.IsNullOrWhiteSpace(port.Name))
        {
            return $"Port {port.Id} has no name.";
        }

        if (!Enum.IsDefined(port.Protocol))
        {
            return $"Port {port.Id} has an undefined protocol value.";
        }

        if (port.RangeSize < 1)
        {
            return $"Port {port.Id} has an invalid rangeSize ({port.RangeSize}); it must be 1 or greater.";
        }

        var sourceCount = (string.IsNullOrWhiteSpace(port.ConfigField) ? 0 : 1) +
            (port.FixedValue.HasValue ? 1 : 0) +
            (string.IsNullOrWhiteSpace(port.OffsetFrom) ? 0 : 1);
        if (sourceCount != 1)
        {
            return $"Port {port.Id} must set exactly one of ConfigField, FixedValue, or OffsetFrom (found {sourceCount}).";
        }

        if (!string.IsNullOrWhiteSpace(port.OffsetFrom) &&
            string.Equals(port.OffsetFrom.Trim(), port.Id.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return $"Port {port.Id} cannot be offset from itself.";
        }

        return null;
    }

    private static ResolvedPort BuildInvariantFailure(ServerPortDefinition port, string error) =>
        new(
            string.IsNullOrWhiteSpace(port.Id) ? "(no id)" : port.Id,
            port.Name,
            port.Protocol,
            ResolvedPortStatus.Invalid,
            null,
            // RangeSize is passed through unclamped even when it's the thing that's invalid (e.g.
            // negative) - Port is always null here, so PortRange's own Port.HasValue check short-
            // circuits it to an empty sequence without ever calling Enumerable.Range, meaning the
            // raw (bad) value is safe to surface as-is rather than silently normalized to something
            // that looks valid.
            port.RangeSize,
            port.Required,
            port.OpenExternally,
            error,
            port.CheckLocalListener);

    private static bool IsPartOfOffsetCycle(ServerPortDefinition start, IReadOnlyDictionary<string, ServerPortDefinition> byId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = start;
        while (current.Source == PortSource.Offset)
        {
            if (!visited.Add(current.Id))
            {
                return true;
            }

            if (!byId.TryGetValue(current.OffsetFrom!, out var next))
            {
                // Unknown reference - already captured as its own error for that specific port in
                // the fixed-point loop above, not a cycle.
                return false;
            }

            current = next;
        }

        return false;
    }

    private static void ResolveFromConfigField(
        ServerPortDefinition port,
        IReadOnlyDictionary<string, object?> settings,
        Dictionary<string, long?> resolvedBase,
        Dictionary<string, string> errors)
    {
        var value = FindSettingValue(settings, port.ConfigField!);
        if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
        {
            if (port.Required)
            {
                errors[port.Id] = $"Port {port.Id} requires a value for config field {port.ConfigField}, but none is set.";
            }

            return;
        }

        if (!int.TryParse(value.ToString(), out var parsed) || parsed is < 1 or > 65535)
        {
            errors[port.Id] = $"Port {port.Id}'s config field {port.ConfigField} has an invalid value: {value}.";
            return;
        }

        resolvedBase[port.Id] = parsed;
    }

    // ModuleValidator matches ports.configField against declared config field keys case-
    // insensitively (an OrdinalIgnoreCase HashSet), so a manifest referencing "NETWORK.PORT" for a
    // field actually keyed "network.port" passes validation cleanly - but settings dictionaries
    // built by real callers (e.g. InstallServerWindow.ReadSettings) are plain case-sensitive
    // Dictionary<string, object?> instances, keyed by the field's own exact declared casing. A
    // naive settings.TryGetValue(port.ConfigField, ...) would silently miss that same reference at
    // resolve time, reporting a perfectly valid port as missing. The exact-match attempt first
    // covers the overwhelmingly common case at normal dictionary speed; the fallback scan only
    // runs on an exact miss, and settings collections are small (one server's config fields), so
    // the O(n) cost there is not a practical concern.
    private static object? FindSettingValue(IReadOnlyDictionary<string, object?> settings, string key)
    {
        if (settings.TryGetValue(key, out var exact))
        {
            return exact;
        }

        foreach (var pair in settings)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static ResolvedPort BuildResult(
        ServerPortDefinition port,
        Dictionary<string, long?> resolvedBase,
        Dictionary<string, string> errors)
    {
        if (errors.TryGetValue(port.Id, out var error))
        {
            return new ResolvedPort(port.Id, port.Name, port.Protocol, ResolvedPortStatus.Invalid, null, port.RangeSize, port.Required, port.OpenExternally, error, port.CheckLocalListener);
        }

        if (!resolvedBase.TryGetValue(port.Id, out var value) || value == null)
        {
            // A required port with nothing to resolve is a real problem regardless of which
            // source kind it uses - ResolveFromConfigField already reports this itself for
            // ConfigField ports (it knows the specific missing key), so this only fires for
            // Offset ports whose (optional) base never got a value.
            return port.Required
                ? new ResolvedPort(port.Id, port.Name, port.Protocol, ResolvedPortStatus.Invalid, null, port.RangeSize, port.Required, port.OpenExternally, $"Port {port.Id} is required but could not be resolved.", port.CheckLocalListener)
                : new ResolvedPort(port.Id, port.Name, port.Protocol, ResolvedPortStatus.Unresolved, null, port.RangeSize, port.Required, port.OpenExternally, CheckLocalListener: port.CheckLocalListener);
        }

        // Fixed and ConfigField sources are already individually bounds-checked before they ever
        // reach resolvedBase (see the < 1 or > 65535 guards above and ports.fixedValue.range in the
        // validator), but an Offset port's value is a base plus an arbitrary signed Offset - a
        // negative offset can drive it below 1, and this is the one place that's actually checked
        // (BuildResult is source-agnostic on purpose, so this bound applies uniformly regardless of
        // how the value was derived, rather than trusting each source kind to have already proven
        // it independently).
        if (value.Value < 1 || value.Value + port.RangeSize - 1 > 65535)
        {
            return new ResolvedPort(
                port.Id,
                port.Name,
                port.Protocol,
                ResolvedPortStatus.Invalid,
                null,
                port.RangeSize,
                port.Required,
                port.OpenExternally,
                $"Port {port.Id}'s effective range ({value.Value}-{value.Value + port.RangeSize - 1}) is outside the valid 1-65535 port range.",
                port.CheckLocalListener);
        }

        return new ResolvedPort(port.Id, port.Name, port.Protocol, ResolvedPortStatus.Resolved, (int)value.Value, port.RangeSize, port.Required, port.OpenExternally, CheckLocalListener: port.CheckLocalListener);
    }

    // Same-server overlap only - two of this module's own declared ports landing on the same
    // number(s) on a protocol they share. Cross-server conflicts (this server vs. a different
    // configured server) stay ServerHealthService's job, per this class's own doc comment above.
    // Runs after every port has an initial Resolved/Unresolved/Invalid status, since overlap can
    // only be judged once actual effective port numbers are known - a config-derived or offset
    // port's value isn't known any earlier than this.
    //
    // Two passes, deliberately not one: the first only READS results (every pairwise comparison
    // sees the same original Resolved/Port values, regardless of what any other pair found), and
    // the second is the only place anything gets downgraded to Invalid. Downgrading in place
    // during the comparison pass - the first version of this method did - breaks for 3+ ports all
    // sharing one number: once the first pair flips to Invalid, either a null Port gets
    // dereferenced by a later comparison still expecting a Resolved value, or (if that's guarded
    // against by skipping) later pairs involving the now-Invalid port never get compared at all,
    // silently leaving a genuine 3-way conflict's third member marked Resolved.
    private static void DetectOverlaps(IList<ResolvedPort> results)
    {
        var conflicts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Status != ResolvedPortStatus.Resolved)
            {
                continue;
            }

            for (var j = i + 1; j < results.Count; j++)
            {
                if (results[j].Status != ResolvedPortStatus.Resolved || !ProtocolsOverlap(results[i].Protocol, results[j].Protocol))
                {
                    continue;
                }

                var aStart = results[i].Port!.Value;
                var aEnd = aStart + results[i].RangeSize - 1;
                var bStart = results[j].Port!.Value;
                var bEnd = bStart + results[j].RangeSize - 1;
                if (aStart > bEnd || bStart > aEnd)
                {
                    continue;
                }

                AddConflict(conflicts, results[i].Id, results[j].Id);
                AddConflict(conflicts, results[j].Id, results[i].Id);
            }
        }

        if (conflicts.Count == 0)
        {
            return;
        }

        for (var i = 0; i < results.Count; i++)
        {
            if (!conflicts.TryGetValue(results[i].Id, out var conflictingIds))
            {
                continue;
            }

            results[i] = results[i] with
            {
                Status = ResolvedPortStatus.Invalid,
                Error = $"Port {results[i].Id} overlaps port{(conflictingIds.Count > 1 ? "s" : "")} {string.Join(", ", conflictingIds)} on a protocol they share.",
                FailureReason = PortResolutionFailureReason.Overlap
            };
        }
    }

    private static void AddConflict(Dictionary<string, List<string>> conflicts, string id, string conflictsWithId)
    {
        if (!conflicts.TryGetValue(id, out var list))
        {
            list = [];
            conflicts[id] = list;
        }

        list.Add(conflictsWithId);
    }

    private static bool ProtocolsOverlap(PortProtocol a, PortProtocol b) =>
        a is PortProtocol.Both or PortProtocol.Either ||
        b is PortProtocol.Both or PortProtocol.Either ||
        a == b;
}
