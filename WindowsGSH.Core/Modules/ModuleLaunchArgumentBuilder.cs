using System.Text.RegularExpressions;

namespace WindowsGSH.Core.Modules;

/// <remarks>
/// P3-05: <c>{key}</c> substitutes a value with no escaping at all (contrast with
/// <c>{quote:key}</c>, which goes through <see cref="WindowsCommandLineEscaper.Quote"/>).
/// <c>server.additionalArguments</c> in particular is always spliced in raw, by design - it's
/// documented as privileged, module-author-facing raw command-line text, the same trust level as
/// SteamCMD's <c>CustomArguments</c> (see <c>SteamInstallDefinition.CustomArguments</c>). It is
/// deliberately not escaped or tokenized, since users are expected to type real, already-composed
/// command-line syntax into it (e.g. <c>-nolog +exec autoexec.cfg</c>) - escaping it would break
/// that. Any other <c>ConfigFieldType.CommandLine</c> field (e.g. <c>GenericWrapperModule</c>'s
/// <c>launch.arguments</c>) carries the same "pre-composed, splice in as-is" meaning, so
/// <see cref="ModuleValidator"/> excludes the whole type from its raw-placeholder warning, not
/// just this one key. It warns only on free-form Text/Password/Path fields referenced via the raw
/// <c>{key}</c> form instead of <c>{quote:key}</c>, since those carry a single opaque value, not
/// composed command-line syntax.
/// </remarks>
public static class ModuleLaunchArgumentBuilder
{
    private static readonly Regex QuotedPlaceholder = new("\\{quote:(?<key>[^}]+)\\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RawPlaceholder = new("\\{(?<key>[^}]+)\\}", RegexOptions.Compiled);

    public static string Build(string template, IReadOnlyDictionary<string, object?> settings)
    {
        template ??= string.Empty;
        var expandedConditionals = ExpandConditionals(template, settings);

        var arguments = ReplacePlaceholders(expandedConditionals, settings);
        if (template.Contains("{server.additionalArguments}", StringComparison.OrdinalIgnoreCase))
        {
            return arguments;
        }

        var additional = GetSetting(settings, "server.additionalArguments", "");
        if (string.IsNullOrWhiteSpace(additional))
        {
            return arguments;
        }

        return string.IsNullOrWhiteSpace(arguments)
            ? additional.Trim()
            : $"{arguments} {additional.Trim()}";
    }

    private static string ExpandConditionals(string template, IReadOnlyDictionary<string, object?> settings)
    {
        var result = new System.Text.StringBuilder();
        for (var index = 0; index < template.Length;)
        {
            if (index + 1 >= template.Length || template[index] != '{' || template[index + 1] != '?')
            {
                result.Append(template[index]);
                index++;
                continue;
            }

            var keyStart = index + 2;
            var colon = template.IndexOf(':', keyStart);
            if (colon < 0)
            {
                result.Append(template[index]);
                index++;
                continue;
            }

            var key = template[keyStart..colon].Trim();
            var valueStart = colon + 1;
            var close = FindConditionalClose(template, valueStart);
            if (close < 0)
            {
                result.Append(template[index]);
                index++;
                continue;
            }

            if (settings.TryGetValue(key, out var value) && IsTruthy(value))
            {
                result.Append(ReplacePlaceholders(ExpandConditionals(template[valueStart..close], settings), settings));
            }

            index = close + 1;
        }

        return result.ToString();
    }

    internal static int FindConditionalClose(string template, int valueStart)
    {
        var nestedDepth = 0;
        for (var index = valueStart; index < template.Length; index++)
        {
            var character = template[index];
            if (character == '{')
            {
                nestedDepth++;
                continue;
            }

            if (character != '}')
            {
                continue;
            }

            if (nestedDepth == 0)
            {
                return index;
            }

            nestedDepth--;
        }

        return -1;
    }

    private static string ReplacePlaceholders(string value, IReadOnlyDictionary<string, object?> settings)
    {
        var withQuoted = QuotedPlaceholder.Replace(value, match =>
        {
            var key = match.Groups["key"].Value;
            var replacement = settings.TryGetValue(key, out var settingValue)
                ? settingValue?.ToString() ?? string.Empty
                : string.Empty;
            return WindowsCommandLineEscaper.Quote(replacement);
        });

        return RawPlaceholder.Replace(withQuoted, match =>
        {
            var key = match.Groups["key"].Value;
            return settings.TryGetValue(key, out var replacement) ? replacement?.ToString() ?? string.Empty : string.Empty;
        });
    }

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool boolean => boolean,
            string text => IsTruthyString(text),
            int number => number != 0,
            long number => number != 0,
            double number => Math.Abs(number) > double.Epsilon,
            null => false,
            _ => true
        };
    }

    private static bool IsTruthyString(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (bool.TryParse(text, out var boolean))
        {
            return boolean;
        }

        if (long.TryParse(text, out var integer))
        {
            return integer != 0;
        }

        if (double.TryParse(text, out var number))
        {
            return Math.Abs(number) > double.Epsilon;
        }

        return true;
    }

    private static string GetSetting(IReadOnlyDictionary<string, object?> settings, string key, string fallback)
    {
        return settings.TryGetValue(key, out var value) ? value?.ToString() ?? fallback : fallback;
    }
}
