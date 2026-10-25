using System.Globalization;

namespace FileTimeTracker.Core.Model;

/// <summary>Calendar quarter, the rollover unit from design.md §4.5.</summary>
public readonly record struct Quarter(int Year, int Number) : IComparable<Quarter>
{
    public static Quarter FromDate(DateOnly date) => new(date.Year, ((date.Month - 1) / 3) + 1);

    public static Quarter Current(TimeZoneInfo? tz = null) =>
        FromDate(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz ?? TimeZoneInfo.Local).DateTime));

    public DateOnly Start => new(Year, ((Number - 1) * 3) + 1, 1);

    /// <summary>First day of the following quarter.</summary>
    public DateOnly EndExclusive => Next.Start;

    public Quarter Next => Number == 4 ? new Quarter(Year + 1, 1) : new Quarter(Year, Number + 1);

    public Quarter Previous => Number == 1 ? new Quarter(Year - 1, 4) : new Quarter(Year, Number - 1);

    public bool Contains(DateOnly date) => date >= Start && date < EndExclusive;

    /// <summary>Stable label used in archive file names, e.g. <c>2026-Q3</c>.</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-Q{Number}");

    public static bool TryParseLabel(string label, out Quarter quarter)
    {
        quarter = default;

        var parts = (label ?? string.Empty).Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[1].Length != 2
            || (parts[1][0] != 'Q' && parts[1][0] != 'q')
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[1][1..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 4)
        {
            return false;
        }

        quarter = new Quarter(year, number);
        return true;
    }

    public int CompareTo(Quarter other)
    {
        var byYear = Year.CompareTo(other.Year);
        return byYear != 0 ? byYear : Number.CompareTo(other.Number);
    }

    public static bool operator <(Quarter left, Quarter right) => left.CompareTo(right) < 0;

    public static bool operator >(Quarter left, Quarter right) => left.CompareTo(right) > 0;

    public static bool operator <=(Quarter left, Quarter right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Quarter left, Quarter right) => left.CompareTo(right) >= 0;

    public override string ToString() => Label;
}
