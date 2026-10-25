using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Aggregator.Reporting;

/// <summary>
/// Collapses raw intervals into the daily totals of design.md §4.5.
/// </summary>
public static class DailyTotalsBuilder
{
    /// <summary>
    /// Groups by <c>computer_no × file × local date</c> and sums active/idle time.
    /// </summary>
    /// <remarks>
    /// The application is part of the grouping key as well as the spec's three fields. §5 gives
    /// every row a single Application column, so a file opened in two applications on one day has
    /// to either split into two rows or have one application chosen arbitrarily — splitting is the
    /// only option that keeps the column truthful. Totals per computer/file/date are unaffected.
    /// </remarks>
    public static IReadOnlyList<DailyTotal> Build(
        IEnumerable<IntervalRecord> records,
        IReadOnlyDictionary<string, string>? employeeMap = null)
    {
        var map = employeeMap ?? new Dictionary<string, string>();

        return records
            .GroupBy(r => new
            {
                r.ComputerNo,
                r.LocalDate,
                r.App,
                r.File,
            })
            .Select(group => new DailyTotal
            {
                ComputerNo = group.Key.ComputerNo,
                Employee = LookupEmployee(map, group.Key.ComputerNo),
                Date = group.Key.LocalDate,
                File = group.Key.File,
                Application = group.Key.App,
                ActiveSec = group.Sum(r => r.ActiveSec),
                IdleSec = group.Sum(r => r.IdleSec),
                // Where a file was seen both as a real document and as a degraded tab name, report
                // the more specific of the two rather than whichever happened to come first.
                Source = group.MaxBy(r => r.Source.Specificity())!.Source,
            })
            .OrderBy(t => t.ComputerNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Date)
            .ThenBy(t => t.Application, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.File, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string LookupEmployee(IReadOnlyDictionary<string, string> map, string computerNo)
    {
        if (map.TryGetValue(computerNo, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        // §5: blank when unmapped. Blank is honest; a placeholder would look like real data.
        return string.Empty;
    }
}
