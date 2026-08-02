using System.Collections;
using System.Globalization;
using System.Text.Json;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Query;

internal static class QueryResponseReader
{
    public static string? ReadString(object? source, params string[] keys)
    {
        var value = ReadValue(source, keys);
        return value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } json => json.ToString(),
            _ => value.ToString()
        };
    }

    public static int? ReadInt(object? source, params string[] keys)
    {
        var value = ReadValue(source, keys);
        return value switch
        {
            byte number => number,
            short number => number,
            int number => number,
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            float number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            double number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetInt32(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } json when TryParseInt(json.GetString(), out var number) => number,
            string text when TryParseInt(text, out var number) => number,
            _ => null
        };
    }

    public static double? ReadDouble(object? source, params string[] keys)
    {
        var value = ReadValue(source, keys);
        return value switch
        {
            float number => number,
            double number => number,
            decimal number => (double)number,
            JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetDouble(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } json when TryParseDouble(json.GetString(), out var number) => number,
            string text when TryParseDouble(text, out var number) => number,
            _ => null
        };
    }

    public static IReadOnlyList<QueryPlayer> ReadPlayers(object? source)
    {
        if (source is JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return json.EnumerateArray()
                .Select(item => ReadPlayer(item))
                .Where(player => player != null)
                .Cast<QueryPlayer>()
                .ToArray();
        }

        if (source is not IEnumerable items || source is string)
        {
            return [];
        }

        var players = new List<QueryPlayer>();
        foreach (var item in items)
        {
            var player = ReadPlayer(item);
            if (player == null)
            {
                continue;
            }

            players.Add(player);
        }

        return players;
    }

    private static QueryPlayer? ReadPlayer(object? item)
    {
        var name = ReadString(item, "Name", "name", "playerName", "PlayerName");
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new QueryPlayer(
                name,
                ReadInt(item, "Score", "score", "Frags", "frags"),
                ReadDouble(item, "Duration", "duration", "Time", "time"),
                ReadInt(item, "Ping", "ping", "Latency", "latency"),
                ReadString(item, "Id", "id", "Identifier", "identifier"));
    }

    private static bool TryParseInt(string? value, out int number)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private static bool TryParseDouble(string? value, out double number)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    public static string Describe(object? source)
    {
        if (source is JsonElement { ValueKind: JsonValueKind.Object } json)
        {
            return "Fields: " + string.Join(", ", json.EnumerateObject().Select(property => property.Name).Take(20));
        }

        if (source is IEnumerable<KeyValuePair<string, JsonElement>> jsonValues)
        {
            return "Fields: " + string.Join(", ", jsonValues.Select(pair => pair.Key).Take(20));
        }

        if (source is IEnumerable<KeyValuePair<string, object?>> values)
        {
            return "Fields: " + string.Join(", ", values.Select(pair => pair.Key).Take(20));
        }

        return source == null ? "No detail payload." : $"Response type: {source.GetType().Name}.";
    }

    private static object? ReadValue(object? source, params string[] keys)
    {
        if (source is null)
        {
            return null;
        }

        if (source is JsonElement json)
        {
            foreach (var key in keys)
            {
                if (json.ValueKind == JsonValueKind.Object && TryGetJsonProperty(json, key, out var property))
                {
                    return property;
                }
            }

            return null;
        }

        if (source is IEnumerable<KeyValuePair<string, JsonElement>> jsonValues)
        {
            foreach (var key in keys)
            {
                var pair = jsonValues.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        if (source is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var key in keys)
            {
                var pair = values.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        var type = source.GetType();
        foreach (var key in keys)
        {
            var property = type.GetProperties()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, key, StringComparison.OrdinalIgnoreCase));
            if (property != null)
            {
                return property.GetValue(source);
            }
        }

        return null;
    }

    private static bool TryGetJsonProperty(JsonElement source, string key, out JsonElement value)
    {
        if (source.TryGetProperty(key, out value))
        {
            return true;
        }

        foreach (var property in source.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
