using System.IO.Compression;
using ClosedXML.Excel;
using FileTimeTracker.Aggregator.Reporting;
using FileTimeTracker.Core.Model;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>
/// The §5 master workbook. These re-open what was written with ClosedXML rather than only checking
/// the byte count: a malformed pivot definition saves without complaint and only fails when
/// something actually reads the file.
/// </summary>
public class WorkbookWriterTests
{
    private static WorkbookMetadata Metadata(params MachineStatus[] machines) => new()
    {
        GeneratedUtc = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
        Scope = "Current quarter 2026-Q3",
        SourceFiles = 2,
        Intervals = 3,
        DuplicatesDropped = 1,
        MalformedLines = 0,
        Machines = machines,
    };

    private static IReadOnlyList<DailyTotal> Totals() => DailyTotalsBuilder.Build(
        new[]
        {
            TestData.Interval("1", file: "Q3 Report.docx", app: "Word", activeSec: 3600, idleSec: 600),
            TestData.Interval("2", file: "Ledger.xlsx", app: "Excel", activeSec: 1800),
            TestData.Interval("3", computerNo: "PC-020", file: "Intranet", app: "Chrome",
                activeSec: 900, source: FileSource.BrowserTab),
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PC-014"] = "Asha Nair" });

    private static XLWorkbook Reopen(byte[] bytes) => new(new MemoryStream(bytes));

    [Fact]
    public void Writes_the_three_sheets_from_the_specification()
    {
        using var workbook = Reopen(WorkbookWriter.Build(Totals(), Metadata()));

        Assert.Contains("DailyTotals", workbook.Worksheets.Select(w => w.Name));
        Assert.Contains("Summary", workbook.Worksheets.Select(w => w.Name));
        Assert.Contains("Run Info", workbook.Worksheets.Select(w => w.Name));
    }

    [Fact]
    public void DailyTotals_carries_the_specified_columns_and_values()
    {
        using var workbook = Reopen(WorkbookWriter.Build(Totals(), Metadata()));
        var sheet = workbook.Worksheet("DailyTotals");

        var headers = Enumerable.Range(1, 9).Select(c => sheet.Cell(1, c).GetString()).ToList();
        Assert.Equal(
            new[]
            {
                "Computer No.", "Employee", "Date", "Month", "File / Document",
                "Application", "Active (min)", "Idle (min)", "Source",
            },
            headers);

        // Row order is Computer, Date, Application, File - so PC-014/Excel/Ledger.xlsx is first.
        Assert.Equal("PC-014", sheet.Cell(2, 1).GetString());
        Assert.Equal("Asha Nair", sheet.Cell(2, 2).GetString());
        Assert.Equal(new DateTime(2026, 7, 20), sheet.Cell(2, 3).GetDateTime());
        Assert.Equal("2026-07", sheet.Cell(2, 4).GetString());
        Assert.Equal("Ledger.xlsx", sheet.Cell(2, 5).GetString());
        Assert.Equal("Excel", sheet.Cell(2, 6).GetString());
        Assert.Equal(30.0, sheet.Cell(2, 7).GetDouble());
    }

    [Fact]
    public void Minutes_are_rounded_to_one_decimal()
    {
        var totals = DailyTotalsBuilder.Build(new[] { TestData.Interval("1", activeSec: 3665, idleSec: 95) });

        using var workbook = Reopen(WorkbookWriter.Build(totals, Metadata()));
        var sheet = workbook.Worksheet("DailyTotals");

        Assert.Equal(61.1, sheet.Cell(2, 7).GetDouble(), 3);
        Assert.Equal(1.6, sheet.Cell(2, 8).GetDouble(), 3);
    }

    [Fact]
    public void The_summary_sheet_carries_a_pivot_over_the_daily_totals()
    {
        using var workbook = Reopen(WorkbookWriter.Build(Totals(), Metadata()));
        var pivot = Assert.Single(workbook.Worksheet("Summary").PivotTables);

        Assert.Equal("SummaryPivot", pivot.Name);
        Assert.Equal(
            new[] { "Computer No.", "Employee", "File / Document" },
            pivot.RowLabels.Select(f => f.SourceName));
        // Excel appends its own "values" placeholder field to the column axis whenever a pivot has
        // more than one value field, so Date is asserted positionally rather than as the only entry.
        Assert.Equal("Date", pivot.ColumnLabels.First().SourceName);
        Assert.Equal(new[] { "Active (min)", "Idle (min)" }, pivot.Values.Select(v => v.SourceName));
    }

    [Fact]
    public void The_pivot_cache_is_marked_to_refresh_when_the_file_is_opened()
    {
        // §5 requires the Summary to refresh on open. Asserted against the package XML rather than
        // ClosedXML's object model: ClosedXML writes refreshOnLoad correctly but does not populate
        // IXLPivotCache.RefreshDataOnOpen when re-reading, and the XML is what Excel honours.
        using var package = new ZipArchive(new MemoryStream(WorkbookWriter.Build(Totals(), Metadata())));

        var definition = Assert.Single(
            package.Entries,
            e => e.Name.StartsWith("pivotCacheDefinition", StringComparison.OrdinalIgnoreCase)
                 && e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

        using var reader = new StreamReader(definition.Open());
        var xml = reader.ReadToEnd();

        Assert.Contains("refreshOnLoad=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("saveData=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("DailyTotalsData", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_web_tab_is_labelled_so_it_cannot_be_read_as_a_filename()
    {
        using var workbook = Reopen(WorkbookWriter.Build(Totals(), Metadata()));
        var sheet = workbook.Worksheet("DailyTotals");

        var browserRow = Enumerable.Range(2, 3).First(r => sheet.Cell(r, 1).GetString() == "PC-020");

        Assert.Equal("Intranet", sheet.Cell(browserRow, 5).GetString());
        Assert.Equal("Web/cloud tab", sheet.Cell(browserRow, 9).GetString());

        // Unmapped machines leave Employee blank rather than inventing a placeholder (§5).
        Assert.Equal(string.Empty, sheet.Cell(browserRow, 2).GetString());
    }

    [Fact]
    public void Run_info_reports_provenance_and_flags_silent_machines()
    {
        var metadata = Metadata(
            new MachineStatus("PC-014", "Asha Nair", DateTimeOffset.UtcNow, "Unlocked", 3),
            new MachineStatus("PC-099", "Ravi Kumar", null, "never reported", 0));

        using var workbook = Reopen(WorkbookWriter.Build(Totals(), metadata));
        var sheet = workbook.Worksheet("Run Info");

        var text = string.Join("\n", sheet.CellsUsed().Select(c => c.GetString()));

        Assert.Contains("Duplicate intervals dropped", text, StringComparison.Ordinal);
        Assert.Contains("PC-099", text, StringComparison.Ordinal);
        Assert.Contains("never", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_fleet_still_writes_a_readable_workbook()
    {
        // The pivot source range needs a body row even with no data, or the file will not open.
        using var workbook = Reopen(WorkbookWriter.Build(Array.Empty<DailyTotal>(), Metadata()));

        Assert.Equal("Computer No.", workbook.Worksheet("DailyTotals").Cell(1, 1).GetString());
        Assert.Empty(workbook.Worksheet("Summary").PivotTables);
    }
}
