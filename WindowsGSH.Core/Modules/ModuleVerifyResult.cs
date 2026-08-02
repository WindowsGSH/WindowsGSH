namespace WindowsGSH.Core.Modules;

public enum ModuleVerifyOutcome
{
    Verified,
    Repaired,
    Failed,
    Unsupported,
    Cancelled
}

public sealed record ModuleVerifyResult(
    ModuleVerifyOutcome Outcome,
    string Message);
