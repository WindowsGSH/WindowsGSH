using System.IO;
using System.Text;
using WindowsGSH.Core.IO;

namespace WindowsGSH.Core.Config;

public sealed class PropertiesFile
{
    private readonly List<PropertyLine> _lines = [];

    private PropertiesFile()
    {
    }

    public static PropertiesFile Load(string path)
    {
        var file = new PropertiesFile();
        if (!File.Exists(path))
        {
            return file;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            file._lines.Add(PropertyLine.Parse(line));
        }

        return file;
    }

    public string GetString(string key, string fallback = "")
    {
        var line = _lines.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        return line?.Value ?? fallback;
    }

    public bool GetBoolean(string key, bool fallback = false)
    {
        var value = GetString(key);
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    public int GetInt32(string key, int fallback = 0)
    {
        var value = GetString(key);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    public void Set(string key, string value)
    {
        var index = _lines.FindLastIndex(line => string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _lines[index] = PropertyLine.Property(key, value, _lines[index].Separator);
            return;
        }

        _lines.Add(PropertyLine.Property(key, value, "="));
    }

    public void Set(string key, bool value)
    {
        Set(key, value ? "true" : "false");
    }

    public void Set(string key, int value)
    {
        Set(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(
            path,
            string.Join(Environment.NewLine, _lines.Select(line => line.ToText())) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed record PropertyLine(string? Key, string Value, string Separator, string OriginalText)
    {
        public static PropertyLine Parse(string text)
        {
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("#", StringComparison.Ordinal) ||
                trimmed.StartsWith("!", StringComparison.Ordinal))
            {
                return new PropertyLine(null, string.Empty, string.Empty, text);
            }

            var separatorIndex = FindSeparator(text);
            if (separatorIndex < 0)
            {
                return new PropertyLine(text.Trim(), string.Empty, "=", text);
            }

            var key = text[..separatorIndex].Trim();
            var separator = text[separatorIndex].ToString();
            var value = text[(separatorIndex + 1)..].Trim();
            return new PropertyLine(key, value, separator, text);
        }

        public static PropertyLine Property(string key, string value, string separator)
        {
            return new PropertyLine(key.Trim(), value, string.IsNullOrWhiteSpace(separator) ? "=" : separator, string.Empty);
        }

        public string ToText()
        {
            return Key == null ? OriginalText : $"{Key}{Separator}{Value}";
        }

        private static int FindSeparator(string text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] is '=' or ':')
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
