namespace WindowsGSH.Core.Modules;

public enum PortProtocol
{
    Tcp,
    Udp,
    Both,
    Either
}

public enum PortSource
{
    ConfigField,
    Fixed,
    Offset
}

public sealed record ServerPortDefinition(
    string Id,
    string Name,
    PortProtocol Protocol,
    string? ConfigField = null,
    int? FixedValue = null,
    string? OffsetFrom = null,
    int Offset = 0,
    int RangeSize = 1,
    bool Required = false,
    bool OpenExternally = true,
    bool CheckLocalListener = true)
{
    // Exactly one of ConfigField/FixedValue/OffsetFrom is expected to be set - ModuleValidator
    // enforces that at manifest-load time (ports.source.exclusive), so this is just a read of
    // whichever one is actually present, not a re-validation of the constraint.
    public PortSource Source => FixedValue.HasValue
        ? PortSource.Fixed
        : !string.IsNullOrWhiteSpace(OffsetFrom)
            ? PortSource.Offset
            : PortSource.ConfigField;
}
