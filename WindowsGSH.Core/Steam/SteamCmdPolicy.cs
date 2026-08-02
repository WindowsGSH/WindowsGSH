using System.Globalization;
using System.Text.RegularExpressions;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Steam;

public static class SteamCmdPolicy
{
    public static IReadOnlyList<string> BuildInstallArgumentList(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        SteamCredentials? credentials)
    {
        ValidateAppId(definition.AppId);
        ValidateValue(installPath, "Steam install path");
        ValidateValue(branch, "Steam branch");
        ValidateValue(branchPassword, "Steam branch password");

        var arguments = new List<string> { "+force_install_dir", installPath };
        if (definition.LoginAnonymous)
        {
            arguments.AddRange(["+login", "anonymous"]);
        }
        else if (credentials != null)
        {
            ValidateValue(credentials.Username, "Steam username");
            arguments.AddRange(["+login", credentials.Username]);
        }

        if (!string.IsNullOrWhiteSpace(definition.ModName))
        {
            ValidateValue(definition.ModName, "Steam mod name");
            arguments.AddRange(["+app_set_config", definition.AppId, "mod", definition.ModName]);
        }

        var customArguments = ParseCustomArguments(definition.CustomArguments);
        var updateCount = string.Equals(definition.AppId, "90", StringComparison.Ordinal) ? 4 : 1;
        for (var index = 0; index < updateCount; index++)
        {
            arguments.AddRange(["+app_update", definition.AppId]);
            if (!string.IsNullOrWhiteSpace(branch) &&
                !string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange(["-beta", branch]);
            }

            if (!string.IsNullOrWhiteSpace(branchPassword))
            {
                arguments.AddRange(["-betapassword", branchPassword]);
            }

            arguments.AddRange(customArguments);
            if (definition.ValidateByDefault)
            {
                arguments.Add("validate");
            }
        }

        arguments.Add("+quit");
        return arguments;
    }

    public static string BuildInstallArguments(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        SteamCredentials? credentials)
    {
        return FormatArguments(BuildInstallArgumentList(
            installPath, definition, branch, branchPassword, credentials));
    }

    public static string FormatArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(WindowsCommandLineEscaper.Quote));

    public static string FormatArgumentsForDiagnostics(
        IReadOnlyList<string> arguments,
        params string?[] secrets)
    {
        var secretSet = secrets
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .ToHashSet(StringComparer.Ordinal);
        var formatted = new string[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            var redact = secretSet.Contains(arguments[index]) ||
                (index > 0 && (arguments[index - 1].Equals("+login", StringComparison.OrdinalIgnoreCase) ||
                               arguments[index - 1].Equals("-betapassword", StringComparison.OrdinalIgnoreCase)));
            formatted[index] = WindowsCommandLineEscaper.Quote(redact ? "***" : arguments[index]);
        }

        return string.Join(" ", formatted);
    }

    /// <summary>
    /// Checks a single real-time output line for an interactive Steam Guard prompt.
    /// Returns the challenge kind if the line is a Steam Guard code request, otherwise null.
    /// </summary>
    public static SteamGuardChallengeKind? ClassifyChallengeLine(string line)
    {
        if (!Regex.IsMatch(line, @"\bcode\b", RegexOptions.IgnoreCase))
        {
            return null;
        }

        // Exclude completion/status lines — the code was accepted or is being used, not requested.
        if (Regex.IsMatch(line, @"\baccepted\b|\bverified\b|\bconfirmed\b|\bprovided\b", RegexOptions.IgnoreCase))
        {
            return null;
        }

        if (Regex.IsMatch(line, @"email", RegexOptions.IgnoreCase))
        {
            return SteamGuardChallengeKind.Email;
        }

        if (Regex.IsMatch(line, @"mobile|authenticator|two[\s\-]factor|2[\s\-]factor", RegexOptions.IgnoreCase))
        {
            return SteamGuardChallengeKind.MobileAuthenticator;
        }

        if (Regex.IsMatch(line, @"Steam\s*Guard|enter.*code|code.*enter", RegexOptions.IgnoreCase))
        {
            return SteamGuardChallengeKind.Unknown;
        }

        return null;
    }

    public static bool IsPasswordPrompt(string line)
    {
        if (Regex.IsMatch(
                line,
                @"invalid\s+password|incorrect\s+password|password\s+(?:was\s+)?(?:rejected|failed)",
                RegexOptions.IgnoreCase))
        {
            return false;
        }

        return Regex.IsMatch(
            line,
            @"^\s*(?:please\s+)?(?:enter\s+)?(?:the\s+)?password(?:\s+for\b[^:]*)?\s*:\s*$",
            RegexOptions.IgnoreCase);
    }

    public static SteamCmdFailureClassification ClassifyFailure(string output, int exitCode)
    {
        // Check access/subscription failures first — these are post-login failures.
        // A successful push-auth login may leave "two-factor" phrases in the output
        // that would otherwise be mistaken for a Steam Guard challenge.
        if (Regex.IsMatch(
                output,
                "No subscription|not.*subscribed|Access Denied|access denied|License expired|Missing license|does not own|Invalid platform",
                RegexOptions.IgnoreCase))
        {
            return new SteamCmdFailureClassification(
                SteamCmdFailureKind.AccessDenied,
                $"SteamCMD failed with exit code {exitCode}: the Steam account does not have access or a required subscription for this app.",
                ShouldRetryAfterManifestDelete: false);
        }

        if (Regex.IsMatch(
                output,
                "Steam Guard|steamguard|two-factor|2-factor|auth(?:entication)? code|mobile authenticator|Account Logon Denied",
                RegexOptions.IgnoreCase))
        {
            return new SteamCmdFailureClassification(
                SteamCmdFailureKind.SteamGuard,
                $"SteamCMD failed with exit code {exitCode}: Steam Guard or two-factor authentication is required.",
                ShouldRetryAfterManifestDelete: false);
        }

        if (Regex.IsMatch(
                output,
                "Invalid Password|Login Failure|Failed to log on|Login failed|password.*incorrect|not logged on",
                RegexOptions.IgnoreCase))
        {
            return new SteamCmdFailureClassification(
                SteamCmdFailureKind.LoginRequired,
                $"SteamCMD failed with exit code {exitCode}: Steam login failed or valid account credentials are required.",
                ShouldRetryAfterManifestDelete: false);
        }

        if (Regex.IsMatch(
                output,
                "appmanifest|state is 0x6|corrupt|content.*state|missing.*manifest|Update state \\(0x[0-9a-f]+\\)",
                RegexOptions.IgnoreCase))
        {
            return new SteamCmdFailureClassification(
                SteamCmdFailureKind.StaleManifest,
                $"SteamCMD failed with exit code {exitCode}: possible stale or corrupt appmanifest/content state.",
                ShouldRetryAfterManifestDelete: true);
        }

        if (Regex.IsMatch(output, "timed out|timeout|connection|network|No Connection", RegexOptions.IgnoreCase))
        {
            return new SteamCmdFailureClassification(
                SteamCmdFailureKind.Network,
                $"SteamCMD failed with exit code {exitCode}: network or Steam connection problem.",
                ShouldRetryAfterManifestDelete: false);
        }

        return new SteamCmdFailureClassification(
            SteamCmdFailureKind.Unknown,
            $"SteamCMD failed with exit code {exitCode}. Review the SteamCMD log above for details.",
            ShouldRetryAfterManifestDelete: false);
    }

    public static string MaskSensitiveText(string text, params string?[] secrets)
    {
        var masked = Regex.Replace(
            text ?? string.Empty,
            @"\+login\s+""[^""]*""\s+""[^""]*""",
            "+login \"***\" \"***\"",
            RegexOptions.IgnoreCase);
        masked = Regex.Replace(
            masked,
            @"\+login\s+(?:""[^""]*""|\S+)",
            "+login \"***\"",
            RegexOptions.IgnoreCase);
        masked = Regex.Replace(
            masked,
            @"-betapassword\s+(?:""[^""]*""|\S+)",
            "-betapassword \"***\"",
            RegexOptions.IgnoreCase);

        foreach (var secret in secrets.Where(secret => !string.IsNullOrWhiteSpace(secret)))
        {
            masked = masked.Replace(secret!, "***", StringComparison.Ordinal);
        }

        return masked;
    }

    public static void ValidateAppId(string? appId)
    {
        if (!ulong.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
        {
            throw new ArgumentException("Steam App ID must be a positive numeric value.", nameof(appId));
        }
    }

    private static IReadOnlyList<string> ParseCustomArguments(string? customArguments)
    {
        var tokens = WindowsCommandLineParser.Split(customArguments);
        foreach (var token in tokens)
        {
            ValidateValue(token, "Steam custom argument");
            if (token.StartsWith('+'))
            {
                throw new ArgumentException(
                    "Steam custom arguments cannot add SteamCMD +commands. Use dedicated module fields instead.",
                    nameof(customArguments));
            }
        }

        return tokens;
    }

    private static void ValidateValue(string? value, string name)
    {
        if (value?.IndexOfAny(['\0', '\r', '\n']) >= 0 || value?.TrimStart().StartsWith('+') == true)
        {
            throw new ArgumentException($"{name} contains an unsafe SteamCMD command token.", name);
        }
    }
}

public sealed record SteamCmdFailureClassification(
    SteamCmdFailureKind Kind,
    string Message,
    bool ShouldRetryAfterManifestDelete)
{
    public bool RequiresSteamGuard => Kind == SteamCmdFailureKind.SteamGuard;
}

public enum SteamCmdFailureKind
{
    Unknown,
    SteamGuard,
    LoginRequired,
    StaleManifest,
    AccessDenied,
    Network
}
