using ClosedXML.Excel;
using FileTimeTracker.Aggregator.Reporting;
using FileTimeTracker.Core.Model;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>
/// The session workbook keeps every interval with its exact clock times, which is precisely what
/// the daily-totals master workbook discards. These pin that difference.
/// </summary>
public sealed class SessionWorkbookWriterTests
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

    private static IntervalRecord Interval(
        string file, string app, DateTimeOffset startUtc, int activeSec, int idleSec = 0)
    {
        var end = startUtc.AddSeconds(activeSec + idleSec);

        return new IntervalRecord
        {
            Id = Guid.NewGuid().ToString(),
            ComputerNo = "PC-001",
            WinUser = "tester",
            App = app,
            File = file,
            StartUtc = startUtc,
            EndUtc = end,
            ActiveSec = activeSec,
            IdleSec = idleSec,
            Source = FileSource.DesktopTitle,
            LocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startUtc, India).DateTime),
        };
    }

    private static SessionMetadata Metadata(DateTimeOffset startUtc, DateTimeOffset endUtc) => new()
    {
        ComputerNo = "PC-001",
        Employee = "Asha Nair",
        StartedUtc = startUtc,
        EndedUtc = endUtc,
        TimeZone = India,
    };

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    /// <summary>Row 1 of every sheet is the session-range banner, so headers sit on row 2.</summary>
    private const int HeaderRow = 2;

    private const int FirstDataRow = 3;

    /// <summary>Reads a Session sheet's label/value pairs without depending on their row numbers.</summary>
    private static Dictionary<string, string> Facts(IXLWorksheet sheet) =>
        Enumerable.Range(1, 20)
            .Select(r => (Label: sheet.Cell(r, 1).GetString(), Value: sheet.Cell(r, 2).GetString()))
            .Where(p => !string.IsNullOrWhiteSpace(p.Label) && !string.IsNullOrWhiteSpace(p.Value))
            .GroupBy(p => p.Label)
            .ToDictionary(g => g.Key, g => g.First().Value);

    [Fact]
    public void Every_interval_appears_with_its_exact_start_and_end_time()
    {
        var start = new DateTimeOffset(2026, 10, 9, 4, 14, 30, TimeSpan.Zero);
        var intervals = new[] { Interval("Q3 Report.docx", "Word", start, activeSec: 460) };

        using var book = Open(SessionWorkbookWriter.Build(intervals, Metadata(start, start.AddMinutes(10))));
        var sheet = book.Worksheet("Activity");

        // 04:14:30 UTC is 09:44:30 in India; the sheet reports local time, to the second.
        // Asserted as rendered, because a time-formatted cell round-trips as a time rather than a
        // DateTime — what matters is the value the user reads in Excel.
        Assert.Equal(new DateTime(2026, 10, 9), sheet.Cell(FirstDataRow, 1).GetDateTime());
        Assert.Equal("09:44:30", sheet.Cell(FirstDataRow, 2).GetFormattedString());
        Assert.Equal("09:52:10", sheet.Cell(FirstDataRow, 3).GetFormattedString());
        Assert.Equal("Word", sheet.Cell(FirstDataRow, 7).GetString());
        Assert.Equal("Q3 Report.docx", sheet.Cell(FirstDataRow, 8).GetString());
    }

    [Fact]
    public void Start_and_end_columns_are_formatted_to_the_second()
    {
        var start = new DateTimeOffset(2026, 10, 9, 4, 0, 0, TimeSpan.Zero);

        using var book = Open(SessionWorkbookWriter.Build(
            new[] { Interval("a.docx", "Word", start, 60) }, Metadata(start, start.AddMinutes(1))));

        var sheet = book.Worksheet("Activity");

        // Minute precision would defeat the point of the sheet.
        Assert.Equal("hh:mm:ss", sheet.Column(2).Style.DateFormat.Format);
        Assert.Equal("hh:mm:ss", sheet.Column(3).Style.DateFormat.Format);
    }

    [Fact]
    public void Rows_are_in_chronological_order_regardless_of_input_order()
    {
        var baseUtc = new DateTimeOffset(2026, 10, 9, 4, 0, 0, TimeSpan.Zero);

        var intervals = new[]
        {
            Interval("third.docx", "Word", baseUtc.AddMinutes(20), 60),
            Interval("first.docx", "Word", baseUtc, 60),
            Interval("second.docx", "Word", baseUtc.AddMinutes(10), 60),
        };

        using var book = Open(SessionWorkbookWriter.Build(intervals, Metadata(baseUtc, baseUtc.AddHours(1))));
        var sheet = book.Worksheet("Activity");

        Assert.Equal("first.docx", sheet.Cell(FirstDataRow, 8).GetString());
        Assert.Equal("second.docx", sheet.Cell(FirstDataRow + 1, 8).GetString());
        Assert.Equal("third.docx", sheet.Cell(FirstDataRow + 2, 8).GetString());
    }

    [Fact]
    public void Totals_group_by_document_and_sum_the_captures()
    {
        var baseUtc = new DateTimeOffset(2026, 10, 9, 4, 0, 0, TimeSpan.Zero);

        var intervals = new[]
        {
            Interval("Q3.docx", "Word", baseUtc, 120),
            Interval("Q3.docx", "Word", baseUtc.AddMinutes(10), 180),
            Interval("notes.txt", "Notepad", baseUtc.AddMinutes(20), 60),
        };

        using var book = Open(SessionWorkbookWriter.Build(intervals, Metadata(baseUtc, baseUtc.AddHours(1))));
        var sheet = book.Worksheet("Totals");

        // Ordered by time spent, so the biggest consumer is the first thing read.
        Assert.Equal("Q3.docx", sheet.Cell(FirstDataRow, 2).GetString());
        Assert.Equal(5.0, sheet.Cell(FirstDataRow, 3).GetDouble());
        Assert.Equal(2, sheet.Cell(FirstDataRow, 6).GetValue<int>());

        Assert.Equal("notes.txt", sheet.Cell(FirstDataRow + 1, 2).GetString());
        Assert.Equal("Total", sheet.Cell(FirstDataRow + 2, 2).GetString());
        Assert.Equal(6.0, sheet.Cell(FirstDataRow + 2, 3).GetDouble());
    }

    [Fact]
    public void The_session_sheet_separates_active_idle_and_uncounted_time()
    {
        var start = new DateTimeOffset(2026, 10, 9, 4, 0, 0, TimeSpan.Zero);

        // One hour of wall time holding 10 min active and 5 min idle: the other 45 were locked.
        var intervals = new[]
        {
            Interval("a.docx", "Word", start, activeSec: 600),
            Interval("a.docx", "Word", start.AddMinutes(10), activeSec: 0, idleSec: 300),
        };

        using var book = Open(SessionWorkbookWriter.Build(intervals, Metadata(start, start.AddHours(1))));

        var values = Facts(book.Worksheet("Session"));

        Assert.Equal("Asha Nair", values["Employee"]);
        Assert.Equal("01:00:00", values["Session length"]);
        Assert.Equal("00:10:00", values["Active time"]);
        Assert.Equal("00:05:00", values["Idle time"]);
        Assert.Equal("00:45:00", values["Not counted"]);
        Assert.Equal("2", values["Captures"]);
    }

    [Fact]
    public void An_empty_session_still_produces_a_readable_workbook()
    {
        var start = DateTimeOffset.UtcNow;

        using var book = Open(SessionWorkbookWriter.Build(Array.Empty<IntervalRecord>(), Metadata(start, start)));

        Assert.Contains("Nothing was captured", book.Worksheet("Activity").Cell(FirstDataRow, 1).GetString());
        Assert.Equal("0", Facts(book.Worksheet("Session"))["Captures"]);
    }

    [Fact]
    public void Uncounted_time_never_reads_as_negative()
    {
        // Rounding and a flush at shutdown can make the summed intervals marginally exceed the
        // wall clock; a negative "Not counted" would look like a bug to the user.
        var start = new DateTimeOffset(2026, 10, 9, 4, 0, 0, TimeSpan.Zero);
        var intervals = new[] { Interval("a.docx", "Word", start, activeSec: 120) };

        using var book = Open(SessionWorkbookWriter.Build(intervals, Metadata(start, start.AddSeconds(60))));

        Assert.Equal("00:00:00", Facts(book.Worksheet("Session"))["Not counted"]);
    }

    [Fact]
    public void Every_sheet_states_the_span_the_session_covered()
    {
        var start = new DateTimeOffset(2026, 10, 9, 4, 14, 30, TimeSpan.Zero);
        var end = start.AddHours(2).AddMinutes(5);

        using var book = Open(SessionWorkbookWriter.Build(
            new[] { Interval("a.docx", "Word", start, 600) }, Metadata(start, end)));

        // Whichever sheet the reader lands on first must answer "which session is this?".
        foreach (var name in new[] { "Activity", "Totals", "Session" })
        {
            var banner = book.Worksheet(name).Cell(1, 1).GetString();

            Assert.Contains("2026-10-09 09:44:30", banner, StringComparison.Ordinal);
            Assert.Contains("11:49:30", banner, StringComparison.Ordinal);
            Assert.Contains("02:05:00", banner, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_session_spanning_midnight_shows_the_date_on_both_ends()
    {
        // 23:30 to 00:30 local: an end of "00:30:00" alone would read as a session that ran
        // backwards, so the date has to come back.
        var start = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);

        var banner = SessionWorkbookWriter.DescribeRange(Metadata(start, end));

        Assert.Contains("2026-10-09 23:30:00", banner, StringComparison.Ordinal);
        Assert.Contains("2026-10-10 00:30:00", banner, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_employee_gets_their_own_folder()
    {
        var start = new DateTimeOffset(2026, 10, 9, 3, 32, 11, TimeSpan.Zero);
        var path = SessionWorkbookWriter.PathFor(Metadata(start, start.AddHours(4)));

        // The person's actual name, spaces and all: this folder is browsed by whoever is named on it.
        Assert.Equal("/sessions/Asha Nair/TimeLog_2026-10-09_09-02-11_to_13-02-11.xlsx", path);
    }

    [Fact]
    public void A_dedicated_publish_root_puts_employee_folders_at_its_top_level()
    {
        // The folder that syncs to Dropbox is itself the reports folder, so there is no wrapper
        // segment above the names — opening it shows people, not a "sessions" folder to click past.
        var start = new DateTimeOffset(2026, 10, 9, 3, 32, 11, TimeSpan.Zero);
        var path = SessionWorkbookWriter.PathFor(Metadata(start, start.AddHours(4)), "/");

        Assert.Equal("/Asha Nair/TimeLog_2026-10-09_09-02-11_to_13-02-11.xlsx", path);
    }

    [Fact]
    public void An_unmapped_machine_files_under_its_computer_number()
    {
        // Not a shared "unknown" folder: two unmapped machines must not pile into one place.
        var start = new DateTimeOffset(2026, 10, 9, 3, 32, 11, TimeSpan.Zero);
        var metadata = Metadata(start, start.AddHours(1)) with { Employee = "   " };

        Assert.StartsWith("/sessions/PC-001/", SessionWorkbookWriter.PathFor(metadata), StringComparison.Ordinal);
    }

    [Fact]
    public void Names_that_would_break_a_path_are_made_safe()
    {
        var start = new DateTimeOffset(2026, 10, 9, 3, 32, 11, TimeSpan.Zero);
        var metadata = Metadata(start, start.AddHours(1)) with { Employee = @"Priya R/S\T:Kumar" };

        var path = SessionWorkbookWriter.PathFor(metadata);

        // One folder, not four: separators in a name must not become extra path segments.
        Assert.Equal(4, path.Split('/').Length);
        Assert.Contains("/sessions/Priya R S T Kumar/", path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_crossing_midnight_keeps_the_date_in_the_file_name()
    {
        var start = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
        var path = SessionWorkbookWriter.PathFor(Metadata(start, start.AddHours(1)));

        Assert.EndsWith("TimeLog_2026-10-09_23-30-00_to_2026-10-10_00-30-00.xlsx", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Headers_sit_below_the_banner_so_the_data_still_starts_where_expected()
    {
        var start = new DateTimeOffset(2026, 10, 9, 4, 0, 0, TimeSpan.Zero);

        using var book = Open(SessionWorkbookWriter.Build(
            new[] { Interval("a.docx", "Word", start, 60) }, Metadata(start, start.AddMinutes(1))));

        Assert.Equal("Date", book.Worksheet("Activity").Cell(HeaderRow, 1).GetString());
        Assert.Equal("Start", book.Worksheet("Activity").Cell(HeaderRow, 2).GetString());
        Assert.Equal("Application", book.Worksheet("Totals").Cell(HeaderRow, 1).GetString());
    }
}
