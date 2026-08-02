namespace WindowsGSH.Core.Modules;

public sealed record InstallPlan(
    string Tool,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyList<string>? Notes = null);
