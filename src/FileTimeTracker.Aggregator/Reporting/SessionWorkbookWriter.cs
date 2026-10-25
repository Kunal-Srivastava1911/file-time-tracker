using ClosedXML.Excel;
using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Aggregator.Reporting;

/// <summary>Header facts for one tracking session.</summary>
public sealed record SessionMetadata
{
    public required string ComputerNo { get; init; }

    public required string Employee { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset EndedUtc { get; init; }

    public required TimeZoneInfo TimeZone { get; init; }
}

/// <summary>
/// Writes the per-session workbook: every interval with the exact clock time it covered, plus a
/// per-document roll-up.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="WorkbookWriter"/>, which answers a different question. The
/// master workbook reports <em>daily totals</em> across the fleet and necessarily discards the
/// individual timings to do it (§4.5 groups by computer × file × date). This one keeps every
/// interval exactly as captured, because a single person stopping a session wants to see what they
/// did and when, not a day-level aggregate of themselves.
/// </remarks>
public static class SessionWorkbookWriter
{
    private const string ActivitySheet = "Activity";
    private const string TotalsSheet = "Totals";
    private const string InfoSheet = "Session";

    private static readonly string[] ActivityHeaders =
    {
        "Date", "Start", "End", "Active (min)", "Idle (min)", "Total (min)",
        "Application", "File / Document", "Source", "Window title",
    };

    private static readonly string[] TotalsHeaders =
    {
        "Application", "File / Document", "Active (min)", "Idle (min)", "Total (min)", "Captures",
    };

    public static byte[] Build(IReadOnlyList<IntervalRecord> intervals, SessionMetadata metadata)
    {
        using var workbook = new XLWorkbook();

        WriteActivitySheet(workbook, intervals, metadata);
        WriteTotalsSheet(workbook, intervals, metadata);
        WriteInfoSheet(workbook, intervals, metadata);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Where a session's workbook is filed: one folder per person, then a file named for the span
    /// it covers.
    /// </summary>
    /// <remarks>
    /// <para>Segregating by employee is what makes a shared destination usable. Fifty people's
    /// sessions in one flat folder cannot be told apart, because the name lives inside the workbook
    /// rather than in its file name. Falls back to the Computer No. when the employee is unmapped,
    /// so a machine missing from <c>computer_employee_map</c> still files somewhere predictable
    /// rather than into a shared "unknown" bucket with everyone else's.</para>
    /// <para><paramref name="root"/> is <c>"/"</c> when the destination folder is itself the place
    /// reports are published to, which puts the employee folders at its top level with nothing
    /// above them.</para>
    /// </remarks>
    public static string PathFor(SessionMetadata metadata, string root = "/sessions")
    {
        var started = TimeZoneInfo.ConvertTime(metadata.StartedUtc, metadata.TimeZone);
        var ended = TimeZoneInfo.ConvertTime(metadata.EndedUtc, metadata.TimeZone);

        var endText = started.Date == ended.Date
            ? ended.ToString("HH-mm-ss")
            : ended.ToString("yyyy-MM-dd_HH-mm-ss");

        var owner = string.IsNullOrWhiteSpace(metadata.Employee) ? metadata.ComputerNo : metadata.Employee;

        // A display segment, not a SafeSegment: this folder is browsed by the person named on it.
        return RemotePath.Combine(
            root,
            RemotePath.SafeDisplaySegment(owner),
            $"TimeLog_{started:yyyy-MM-dd_HH-mm-ss}_to_{endText}.xlsx");
    }

    /// <summary>
    /// The span the workbook covers, stated in words. Goes at the top of every sheet: a reader who
    /// opens one of these out of a folder of them must never have to guess which session it is, and
    /// the file name alone is easy to lose track of once the file is open.
    /// </summary>
    public static string DescribeRange(SessionMetadata metadata)
    {
        var started = TimeZoneInfo.ConvertTime(metadata.StartedUtc, metadata.TimeZone);
        var ended = TimeZoneInfo.ConvertTime(metadata.EndedUtc, metadata.TimeZone);
        var length = metadata.EndedUtc - metadata.StartedUtc;

        // Same-day sessions are the norm; repeating the date on both ends just adds noise.
        var endText = started.Date == ended.Date
            ? ended.ToString("HH:mm:ss")
            : ended.ToString("yyyy-MM-dd HH:mm:ss");

        return $"Recording session:  {started:yyyy-MM-dd HH:mm:ss}  to  {endText}   " +
               $"(length {Clock(length)}, {metadata.TimeZone.Id})";
    }

    /// <summary>Writes the range banner into row 1 and returns the row the headers go on.</summary>
    private static int WriteBanner(IXLWorksheet sheet, SessionMetadata metadata, int columns)
    {
        sheet.Cell(1, 1).Value = DescribeRange(metadata);
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontColor = XLColor.FromArgb(0, 90, 45);
        sheet.Range(1, 1, 1, columns).Merge();

        return 2;
    }

    private static void WriteActivitySheet(
        XLWorkbook workbook, IReadOnlyList<IntervalRecord> intervals, SessionMetadata metadata)
    {
        var sheet = workbook.Worksheets.Add(ActivitySheet);
        var zone = metadata.TimeZone;

        var headerRow = WriteBanner(sheet, metadata, ActivityHeaders.Length);

        for (var i = 0; i < ActivityHeaders.Length; i++)
        {
            sheet.Cell(headerRow, i + 1).Value = ActivityHeaders[i];
        }

        var row = headerRow + 1;

        // Chronological: this sheet is read as a timeline of the session, not sorted by size.
        foreach (var interval in intervals.OrderBy(i => i.StartUtc))
        {
            var start = TimeZoneInfo.ConvertTime(interval.StartUtc, zone);
            var end = TimeZoneInfo.ConvertTime(interval.EndUtc, zone);

            sheet.Cell(row, 1).Value = start.Date;
            sheet.Cell(row, 2).Value = start.DateTime;
            sheet.Cell(row, 3).Value = end.DateTime;
            sheet.Cell(row, 4).Value = Minutes(interval.ActiveSec);
            sheet.Cell(row, 5).Value = Minutes(interval.IdleSec);
            sheet.Cell(row, 6).Value = Minutes(interval.TotalSec);
            sheet.Cell(row, 7).Value = interval.App;
            sheet.Cell(row, 8).Value = interval.File;
            sheet.Cell(row, 9).Value = Describe(interval.Source);
            sheet.Cell(row, 10).Value = interval.WindowTitle ?? string.Empty;

            // Idle stretches are not work; greying them stops them reading as activity at a glance.
            if (interval.ActiveSec == 0)
            {
                sheet.Range(row, 1, row, ActivityHeaders.Length).Style.Font.FontColor = XLColor.Gray;
            }

            row++;
        }

        sheet.Column(1).Style.DateFormat.Format = "yyyy-mm-dd";

        // Seconds, not minutes: "the exact time something was captured" is the point of this sheet.
        sheet.Column(2).Style.DateFormat.Format = "hh:mm:ss";
        sheet.Column(3).Style.DateFormat.Format = "hh:mm:ss";

        for (var column = 4; column <= 6; column++)
        {
            sheet.Column(column).Style.NumberFormat.Format = "0.0";
        }

        sheet.Row(headerRow).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns(1, ActivityHeaders.Length).AdjustToContents(1, 200);

        if (intervals.Count == 0)
        {
            sheet.Cell(headerRow + 1, 1).Value = "Nothing was captured during this session.";
        }
    }

    private static void WriteTotalsSheet(
        XLWorkbook workbook, IReadOnlyList<IntervalRecord> intervals, SessionMetadata metadata)
    {
        var sheet = workbook.Worksheets.Add(TotalsSheet);

        var headerRow = WriteBanner(sheet, metadata, TotalsHeaders.Length);

        for (var i = 0; i < TotalsHeaders.Length; i++)
        {
            sheet.Cell(headerRow, i + 1).Value = TotalsHeaders[i];
        }

        var groups = intervals
            .GroupBy(i => (i.App, i.File))
            .Select(g => new
            {
                g.Key.App,
                g.Key.File,
                ActiveSec = g.Sum(i => i.ActiveSec),
                IdleSec = g.Sum(i => i.IdleSec),
                Captures = g.Count(),
            })
            .OrderByDescending(g => g.ActiveSec + g.IdleSec)
            .ToList();

        var row = headerRow + 1;
        foreach (var group in groups)
        {
            sheet.Cell(row, 1).Value = group.App;
            sheet.Cell(row, 2).Value = group.File;
            sheet.Cell(row, 3).Value = Minutes(group.ActiveSec);
            sheet.Cell(row, 4).Value = Minutes(group.IdleSec);
            sheet.Cell(row, 5).Value = Minutes(group.ActiveSec + group.IdleSec);
            sheet.Cell(row, 6).Value = group.Captures;
            row++;
        }

        if (groups.Count > 0)
        {
            sheet.Cell(row, 2).Value = "Total";
            sheet.Cell(row, 3).Value = Minutes(intervals.Sum(i => i.ActiveSec));
            sheet.Cell(row, 4).Value = Minutes(intervals.Sum(i => i.IdleSec));
            sheet.Cell(row, 5).Value = Minutes(intervals.Sum(i => i.TotalSec));
            sheet.Cell(row, 6).Value = intervals.Count;
            sheet.Range(row, 1, row, TotalsHeaders.Length).Style.Font.Bold = true;
            sheet.Range(row, 1, row, TotalsHeaders.Length).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        for (var column = 3; column <= 5; column++)
        {
            sheet.Column(column).Style.NumberFormat.Format = "0.0";
        }

        sheet.Row(headerRow).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns(1, TotalsHeaders.Length).AdjustToContents(1, 200);
    }

    private static void WriteInfoSheet(
        XLWorkbook workbook, IReadOnlyList<IntervalRecord> intervals, SessionMetadata metadata)
    {
        var sheet = workbook.Worksheets.Add(InfoSheet);

        var started = TimeZoneInfo.ConvertTime(metadata.StartedUtc, metadata.TimeZone);
        var ended = TimeZoneInfo.ConvertTime(metadata.EndedUtc, metadata.TimeZone);
        var wall = metadata.EndedUtc - metadata.StartedUtc;

        var firstFactRow = WriteBanner(sheet, metadata, 2) + 1;

        var active = TimeSpan.FromSeconds(intervals.Sum(i => i.ActiveSec));
        var idle = TimeSpan.FromSeconds(intervals.Sum(i => i.IdleSec));

        var facts = new (string Label, string Value)[]
        {
            ("Employee", string.IsNullOrWhiteSpace(metadata.Employee) ? "(not set)" : metadata.Employee),
            ("Computer", metadata.ComputerNo),
            ("Session started", started.ToString("yyyy-MM-dd HH:mm:ss")),
            ("Session ended", ended.ToString("yyyy-MM-dd HH:mm:ss")),
            ("Time zone", metadata.TimeZone.Id),
            ("Session length", Clock(wall)),
            ("Active time", Clock(active)),
            ("Idle time", Clock(idle)),
            // Wall time minus active and idle: locked, asleep or no foreground window (§3).
            ("Not counted", Clock(wall - active - idle)),
            ("Captures", intervals.Count.ToString()),
        };

        var row = firstFactRow;
        foreach (var (label, value) in facts)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 2).Value = value;
            row++;
        }

        row++;
        sheet.Cell(row, 1).Value =
            "\"Not counted\" is time the screen was locked, the machine asleep, or no window was in focus.";
        sheet.Cell(row, 1).Style.Font.FontColor = XLColor.Gray;

        sheet.Columns(1, 2).AdjustToContents(1, 200);
    }

    private static double Minutes(int seconds) => Math.Round(seconds / 60.0, 1, MidpointRounding.AwayFromZero);

    private static string Clock(TimeSpan value) =>
        value < TimeSpan.Zero
            ? "00:00:00"
            : $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";

    private static string Describe(FileSource source) => source switch
    {
        FileSource.UiaFilePath => "File (path resolved)",
        FileSource.DesktopTitle => "File (from title)",
        FileSource.BrowserTab => "Web/cloud tab",
        FileSource.AppContext => "App dataset",
        FileSource.AppOnly => "Application only",
        _ => "Unknown",
    };
}
