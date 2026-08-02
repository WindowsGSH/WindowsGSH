namespace WindowsGSH.Core.Readiness;

public sealed class FirstRunChecklistComposer
{
    public IReadOnlyList<FirstRunChecklistItem> Compose(
        IReadOnlyList<ReadinessCheckResult> readiness,
        bool discordEnabled,
        bool discordTokenStored)
    {
        var items = new List<FirstRunChecklistItem>
        {
            FromReadiness(
                "Modules",
                readiness.FirstOrDefault(item => item.Name == "Modules"),
                "At least one game module must be available."),
            FromReadiness(
                "SteamCMD",
                readiness.FirstOrDefault(item => item.Name == "SteamCMD"),
                "SteamCMD will be installed when a Steam server needs it.")
        };

        var java = readiness
            .Where(item => item.Name.Contains("Java", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (java.Length > 0)
        {
            var worst = java.OrderByDescending(item => Severity(item.Status)).First();
            items.Add(FromReadiness("Java", worst, "Java is needed only by Java-based modules."));
        }

        items.Add(new FirstRunChecklistItem(
            "Discord",
            discordEnabled && discordTokenStored
                ? FirstRunChecklistState.Ready
                : FirstRunChecklistState.Optional,
            discordEnabled && discordTokenStored
                ? "Discord bot is configured."
                : "Optional. Configure Discord later from WindowsGSH Config."));
        return items;
    }

    private static FirstRunChecklistItem FromReadiness(
        string label,
        ReadinessCheckResult? result,
        string fallback)
    {
        if (result == null)
        {
            return new FirstRunChecklistItem(label, FirstRunChecklistState.Unknown, fallback);
        }

        return new FirstRunChecklistItem(
            label,
            result.Status switch
            {
                ReadinessStatus.Pass => FirstRunChecklistState.Ready,
                ReadinessStatus.Fail => FirstRunChecklistState.ActionRequired,
                ReadinessStatus.Warning => FirstRunChecklistState.Attention,
                _ => FirstRunChecklistState.Optional
            },
            result.Message);
    }

    private static int Severity(ReadinessStatus status) => status switch
    {
        ReadinessStatus.Fail => 4,
        ReadinessStatus.Warning => 3,
        ReadinessStatus.Info => 2,
        _ => 1
    };
}

public sealed record FirstRunChecklistItem(
    string Label,
    FirstRunChecklistState State,
    string Detail)
{
    public string StatusText => State switch
    {
        FirstRunChecklistState.Ready => "Ready",
        FirstRunChecklistState.Attention => "Check",
        FirstRunChecklistState.ActionRequired => "Action required",
        FirstRunChecklistState.Optional => "Optional",
        _ => "Checking..."
    };
}

public enum FirstRunChecklistState
{
    Unknown,
    Ready,
    Attention,
    ActionRequired,
    Optional
}
