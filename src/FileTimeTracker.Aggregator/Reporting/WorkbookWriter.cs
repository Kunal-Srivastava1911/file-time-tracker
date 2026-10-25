using ClosedXML.Excel;
using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Aggregator.Reporting;

/// <summary>Everything shown on the workbook's Run Info sheet.</summary>
public sealed record WorkbookMetadata
{
    public required DateTimeOffset GeneratedUtc { get; init; }

    public required string Scope { get; init; }

    public required int SourceFiles { get; init; }

    public required int Intervals { get; init; }

    public required int DuplicatesDropped { get; init; }

    public required int MalformedLines { get; init; }

    public IReadOnlyList<MachineStatus> Machines { get; init; } = Array.Empty<MachineStatus>();
}

/// <param name="ComputerNo">Machine identifier.</param>
/// <param name="Employee">Mapped employee, blank when unmapped.</param>
/// <param name="LastHeartbeatUtc">Null when the machine has never reported.</param>
public sealed record MachineStatus(
    string ComputerNo,
    string Employee,
    DateTimeOffset? LastHeartbeatUtc,
    string SessionState,
    int PendingIntervals);

/// <summary>
/// Writes the master workbook of design.md §5: a <c>DailyTotals</c> sheet, a <c>Summary</c>
/// PivotTable over it that refreshes on open, and a Run Info sheet.
/// </summary>
public static class WorkbookWriter
{
    private const string DataSheet = "DailyTotals";
    private const string SummarySheet = "Summary";
    private const string InfoSheet = "Run Info";
    private const string TableName = "DailyTotalsData";

    private static readonly string[] Headers =
    {
        "Computer No.", "Employee", "Date", "Month", "File / Document",
        "Application", "Active (min)", "Idle (min)", "Source",
    };

    public static byte[] Build(IReadOnlyList<DailyTotal> totals, WorkbookMetadata metadata)
    {
        using var workbook = new XLWorkbook();

        var data = WriteDataSheet(workbook, totals);
        WriteSummarySheet(workbook, data, totals.Count);
        WriteInfoSheet(workbook, metadata);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static IXLRange WriteDataSheet(XLWorkbook workbook, IReadOnlyList<DailyTotal> totals)
    {
        var sheet = workbook.Worksheets.Add(DataSheet);

        for (var i = 0; i < Headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = Headers[i];
        }

        var row = 2;
        foreach (var total in totals)
        {
            sheet.Cell(row, 1).Value = total.ComputerNo;
            sheet.Cell(row, 2).Value = total.Employee;
            sheet.Cell(row, 3).Value = total.Date.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 4).Value = total.Month;
            sheet.Cell(row, 5).Value = total.File;
            sheet.Cell(row, 6).Value = total.Application;
            sheet.Cell(row, 7).Value = total.ActiveMinutes;
            sheet.Cell(row, 8).Value = total.IdleMinutes;
            sheet.Cell(row, 9).Value = Describe(total.Source);
            row++;
        }

        // An empty fleet still needs a valid table and pivot source, so the range always spans at
        // least one body row.
        var lastRow = Math.Max(row - 1, 2);
        var range = sheet.Range(1, 1, lastRow, Headers.Length);

        var table = range.CreateTable(TableName);
        table.Theme = XLTableTheme.TableStyleMedium2;

        sheet.Column(3).Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Column(7).Style.NumberFormat.Format = "0.0";
        sheet.Column(8).Style.NumberFormat.Format = "0.0";

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, Headers.Length).AdjustToContents(1, 200);

        return range;
    }

    private static void WriteSummarySheet(XLWorkbook workbook, IXLRange source, int rowCount)
    {
        var sheet = workbook.Worksheets.Add(SummarySheet);

        sheet.Cell(1, 1).Value = "Active and idle minutes by machine, employee and document.";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(2, 1).Value =
            "Swap the column field between Date and Month, or drag fields, to re-cut. Data refreshes when the file is opened.";
        sheet.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        if (rowCount == 0)
        {
            sheet.Cell(4, 1).Value = "No intervals have been reported yet.";
            return;
        }

        var pivot = sheet.PivotTables.Add("SummaryPivot", sheet.Cell(4, 1), source);

        pivot.RowLabels.Add("Computer No.");
        pivot.RowLabels.Add("Employee");
        pivot.RowLabels.Add("File / Document");
        pivot.ColumnLabels.Add("Date");

        pivot.Values.Add("Active (min)", "Active (min)").SetSummaryFormula(XLPivotSummary.Sum);
        pivot.Values.Add("Idle (min)", "Idle (min)").SetSummaryFormula(XLPivotSummary.Sum);

        // §5: refreshes on open, so the admin never has to remember to hit Refresh. These live on
        // the shared pivot cache in ClosedXML 0.105, not on the pivot table itself.
        pivot.PivotCache.SetRefreshDataOnOpen().SetSaveSourceData();

        pivot.ShowGrandTotalsRows = true;
        pivot.ShowGrandTotalsColumns = true;
    }

    private static void WriteInfoSheet(XLWorkbook workbook, WorkbookMetadata metadata)
    {
        var sheet = workbook.Worksheets.Add(InfoSheet);

        var facts = new (string Label, string Value)[]
        {
            ("Generated (UTC)", metadata.GeneratedUtc.ToString("u")),
            ("Scope", metadata.Scope),
            ("Source log files", metadata.SourceFiles.ToString()),
            ("Intervals aggregated", metadata.Intervals.ToString()),
            ("Duplicate intervals dropped", metadata.DuplicatesDropped.ToString()),
            ("Unparseable log lines", metadata.MalformedLines.ToString()),
        };

        var row = 1;
        foreach (var (label, value) in facts)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 2).Value = value;
            row++;
        }

        row++;
        sheet.Cell(row, 1).Value = "Machine status";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        row++;

        var statusHeaders = new[] { "Computer No.", "Employee", "Last heartbeat (UTC)", "Age (hours)", "Session", "Pending" };
        for (var i = 0; i < statusHeaders.Length; i++)
        {
            sheet.Cell(row, i + 1).Value = statusHeaders[i];
            sheet.Cell(row, i + 1).Style.Font.Bold = true;
        }

        row++;

        foreach (var machine in metadata.Machines.OrderBy(m => m.ComputerNo, StringComparer.OrdinalIgnoreCase))
        {
            sheet.Cell(row, 1).Value = machine.ComputerNo;
            sheet.Cell(row, 2).Value = machine.Employee;
            sheet.Cell(row, 3).Value = machine.LastHeartbeatUtc?.ToString("u") ?? "never";
            sheet.Cell(row, 4).Value = machine.LastHeartbeatUtc is { } beat
                ? Math.Round((DateTimeOffset.UtcNow - beat).TotalHours, 1)
                : Blank.Value;
            sheet.Cell(row, 5).Value = machine.SessionState;
            sheet.Cell(row, 6).Value = machine.PendingIntervals;

            // A silent machine looks identical to an idle one in the totals; flag it here so the
            // §6 "data gaps are visible" promise is actually kept.
            if (machine.LastHeartbeatUtc is null || DateTimeOffset.UtcNow - machine.LastHeartbeatUtc > TimeSpan.FromHours(24))
            {
                sheet.Range(row, 1, row, statusHeaders.Length).Style.Fill.BackgroundColor = XLColor.LightPink;
            }

            row++;
        }

        sheet.Columns(1, statusHeaders.Length).AdjustToContents(1, 120);
    }

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
