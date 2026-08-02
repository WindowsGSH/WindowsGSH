using System.Text.RegularExpressions;

namespace WindowsGSH.Core.Java;

public static partial class JavaVersionParser
{
    public static int? ParseMajorVersion(string versionText)
    {
        var version = ParseVersionToken(versionText);
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Split(['.', '-', '+', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var first))
        {
            return null;
        }

        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacyMajor))
        {
            return legacyMajor;
        }

        return first;
    }

    public static string ParseVendor(string versionText)
    {
        var firstLine = versionText
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim() ?? string.Empty;

        if (firstLine.StartsWith("openjdk", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenJDK";
        }

        if (firstLine.StartsWith("java version", StringComparison.OrdinalIgnoreCase) ||
            firstLine.StartsWith("Java(TM)", StringComparison.OrdinalIgnoreCase))
        {
            return "Java";
        }

        var match = LeadingVendorRegex().Match(firstLine);
        return match.Success ? match.Groups["vendor"].Value : string.Empty;
    }

    private static string? ParseVersionToken(string versionText)
    {
        var quoted = QuotedVersionRegex().Match(versionText);
        if (quoted.Success)
        {
            return quoted.Groups["version"].Value;
        }

        var unquoted = UnquotedVersionRegex().Match(versionText);
        return unquoted.Success ? unquoted.Groups["version"].Value : null;
    }

    [GeneratedRegex("version\\s+\"(?<version>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedVersionRegex();

    [GeneratedRegex("version\\s+(?<version>\\d+(?:[._+-][\\w]+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex UnquotedVersionRegex();

    [GeneratedRegex("^(?<vendor>[A-Za-z][A-Za-z0-9 ._-]*?)\\s+version\\b", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingVendorRegex();
}
