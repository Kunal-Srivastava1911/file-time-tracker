using System.Globalization;
using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Aggregator.Reporting;

/// <summary>One row of the <c>DailyTotals</c> sheet (design.md §5).</summary>
public sealed record DailyTotal
{
    public required string ComputerNo { get; init; }

    /// <summary>From <c>computer_employee_map</c>; empty when unmapped (§5).</summary>
    public required string Employee { get; init; }

    public required DateOnly Date { get; init; }

    public required string File { get; init; }

    public required string Application { get; init; }

    public required int ActiveSec { get; init; }

    public required int IdleSec { get; init; }

    /// <summary>How the file name was derived, carried through so a browser tab is never mistaken for a document.</summary>
    public required FileSource Source { get; init; }

    /// <summary>
    /// <c>yyyy-MM</c>. Present so the §5 pivot can pivot columns by month without depending on
    /// Excel's date grouping, which ClosedXML cannot author.
    /// </summary>
    public string Month => Date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public double ActiveMinutes => Math.Round(ActiveSec / 60.0, 1, MidpointRounding.AwayFromZero);

    public double IdleMinutes => Math.Round(IdleSec / 60.0, 1, MidpointRounding.AwayFromZero);
}
