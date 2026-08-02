namespace WindowsGSH.Core.Scheduling;

public sealed class CronSchedule
{
    private readonly IReadOnlySet<int> _minutes;
    private readonly IReadOnlySet<int> _hours;
    private readonly IReadOnlySet<int> _days;
    private readonly IReadOnlySet<int> _months;
    private readonly IReadOnlySet<int> _weekdays;

    private CronSchedule(
        IReadOnlySet<int> minutes,
        IReadOnlySet<int> hours,
        IReadOnlySet<int> days,
        IReadOnlySet<int> months,
        IReadOnlySet<int> weekdays)
    {
        _minutes = minutes;
        _hours = hours;
        _days = days;
        _months = months;
        _weekdays = weekdays;
    }

    public static CronSchedule Parse(string expression)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 5)
        {
            throw new FormatException("Cron expression must use five fields: minute hour day month weekday.");
        }

        return new CronSchedule(
            ParseField(parts[0], 0, 59, allowSevenAsZero: false),
            ParseField(parts[1], 0, 23, allowSevenAsZero: false),
            ParseField(parts[2], 1, 31, allowSevenAsZero: false),
            ParseField(parts[3], 1, 12, allowSevenAsZero: false),
            ParseField(parts[4], 0, 6, allowSevenAsZero: true));
    }

    public bool IsMatch(DateTimeOffset time)
    {
        var local = time.LocalDateTime;
        var weekday = (int)local.DayOfWeek;
        return _minutes.Contains(local.Minute) &&
            _hours.Contains(local.Hour) &&
            _days.Contains(local.Day) &&
            _months.Contains(local.Month) &&
            _weekdays.Contains(weekday);
    }

    private static IReadOnlySet<int> ParseField(string field, int min, int max, bool allowSevenAsZero)
    {
        var values = new SortedSet<int>();
        foreach (var segment in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rangePart = segment;
            var step = 1;
            var slashIndex = segment.IndexOf('/', StringComparison.Ordinal);
            if (slashIndex >= 0)
            {
                rangePart = segment[..slashIndex];
                if (!int.TryParse(segment[(slashIndex + 1)..], out step) || step <= 0)
                {
                    throw new FormatException($"Invalid cron step: {segment}");
                }
            }

            var (start, end) = ParseRange(rangePart, min, max, allowSevenAsZero);
            for (var value = start; value <= end; value += step)
            {
                values.Add(Normalize(value, allowSevenAsZero));
            }
        }

        if (values.Count == 0)
        {
            throw new FormatException($"Invalid cron field: {field}");
        }

        return values;
    }

    private static (int Start, int End) ParseRange(string value, int min, int max, bool allowSevenAsZero)
    {
        if (value == "*")
        {
            return (min, max);
        }

        var dashIndex = value.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            var start = ParseNumber(value[..dashIndex], min, max, allowSevenAsZero);
            var end = ParseNumber(value[(dashIndex + 1)..], min, max, allowSevenAsZero);
            if (start > end)
            {
                throw new FormatException($"Invalid cron range: {value}");
            }

            return (start, end);
        }

        var single = ParseNumber(value, min, max, allowSevenAsZero);
        return (single, single);
    }

    private static int ParseNumber(string value, int min, int max, bool allowSevenAsZero)
    {
        if (!int.TryParse(value, out var number))
        {
            throw new FormatException($"Invalid cron value: {value}");
        }

        number = Normalize(number, allowSevenAsZero);
        if (number < min || number > max)
        {
            throw new FormatException($"Cron value {value} is outside {min}-{max}.");
        }

        return number;
    }

    private static int Normalize(int value, bool allowSevenAsZero)
    {
        return allowSevenAsZero && value == 7 ? 0 : value;
    }
}
