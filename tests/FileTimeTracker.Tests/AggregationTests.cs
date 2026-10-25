using System.Text;
using FileTimeTracker.Aggregator.Reporting;
using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;
using FileTimeTracker.Tests.Fakes;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>
/// design.md §8, integration: "aggregation correctness against known intervals" and
/// "quarter rollover".
/// </summary>
public class AggregationTests
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

    private static void WriteChunk(InMemoryRemoteStorage remote, string computerNo, params IntervalRecord[] records)
    {
        var body = new StringBuilder();
        foreach (var record in records)
        {
            body.Append(TrackerJson.Serialize(record)).Append('\n');
        }

        remote.PutText(LogLayout.NewChunkPath("/logs", computerNo, DateTimeOffset.UtcNow), body.ToString());
    }

    // ---- Daily totals ----------------------------------------------------------------------

    [Fact]
    public void Intervals_are_summed_per_computer_file_and_local_date()
    {
        var day1 = new DateOnly(2026, 7, 20);
        var day2 = new DateOnly(2026, 7, 21);

        var totals = DailyTotalsBuilder.Build(new[]
        {
            TestData.Interval("1", file: "Plan.docx", date: day1, activeSec: 600),
            TestData.Interval("2", file: "Plan.docx", date: day1, activeSec: 300, idleSec: 120),
            TestData.Interval("3", file: "Plan.docx", date: day2, activeSec: 900),
            TestData.Interval("4", file: "Ledger.xlsx", app: "Excel", date: day1, activeSec: 450),
            TestData.Interval("5", computerNo: "PC-020", file: "Plan.docx", date: day1, activeSec: 60),
        });

        Assert.Equal(4, totals.Count);

        var planDay1 = totals.Single(t => t.ComputerNo == "PC-014" && t.File == "Plan.docx" && t.Date == day1);
        Assert.Equal(900, planDay1.ActiveSec);
        Assert.Equal(120, planDay1.IdleSec);
        Assert.Equal(15.0, planDay1.ActiveMinutes);
        Assert.Equal(2.0, planDay1.IdleMinutes);

        // A different day and a different machine stay separate.
        Assert.Equal(900, totals.Single(t => t.File == "Plan.docx" && t.Date == day2).ActiveSec);
        Assert.Equal(60, totals.Single(t => t.ComputerNo == "PC-020").ActiveSec);
    }

    [Fact]
    public void The_same_file_in_two_applications_stays_on_separate_rows()
    {
        var totals = DailyTotalsBuilder.Build(new[]
        {
            TestData.Interval("1", file: "notes.txt", app: "Notepad", activeSec: 120),
            TestData.Interval("2", file: "notes.txt", app: "Visual Studio Code", activeSec: 300),
        });

        Assert.Equal(2, totals.Count);
        Assert.Equal(420, totals.Sum(t => t.ActiveSec));
    }

    [Fact]
    public void Employee_names_come_from_the_map_and_are_blank_when_unmapped()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PC-014"] = "Asha Nair",
            ["PC-020"] = "   ",
        };

        var totals = DailyTotalsBuilder.Build(new[]
        {
            TestData.Interval("1", computerNo: "PC-014"),
            TestData.Interval("2", computerNo: "PC-020"),
            TestData.Interval("3", computerNo: "PC-099"),
        }, map);

        Assert.Equal("Asha Nair", totals.Single(t => t.ComputerNo == "PC-014").Employee);
        Assert.Equal(string.Empty, totals.Single(t => t.ComputerNo == "PC-020").Employee);
        Assert.Equal(string.Empty, totals.Single(t => t.ComputerNo == "PC-099").Employee);
    }

    [Fact]
    public void The_most_specific_source_wins_when_a_file_was_seen_more_than_one_way()
    {
        var totals = DailyTotalsBuilder.Build(new[]
        {
            TestData.Interval("1", source: FileSource.AppOnly),
            TestData.Interval("2", source: FileSource.UiaFilePath),
            TestData.Interval("3", source: FileSource.DesktopTitle),
        });

        Assert.Equal(FileSource.UiaFilePath, Assert.Single(totals).Source);
    }

    // ---- Reading logs ----------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_ids_across_chunks_are_counted_once()
    {
        var remote = new InMemoryRemoteStorage();
        var shared = TestData.Interval("shared", activeSec: 600);

        WriteChunk(remote, "PC-014", shared, TestData.Interval("a", activeSec: 300));
        WriteChunk(remote, "PC-014", shared, TestData.Interval("b", activeSec: 120));

        var result = await new LogReader(remote, TrackerLog.Null).ReadAllAsync("/logs");

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(1, result.DuplicatesDropped);
        Assert.Equal(1020, DailyTotalsBuilder.Build(result.Records).Sum(t => t.ActiveSec));
    }

    [Fact]
    public async Task A_truncated_final_line_is_skipped_without_losing_the_rest()
    {
        var remote = new InMemoryRemoteStorage();

        var good = TrackerJson.Serialize(TestData.Interval("a", activeSec: 300));
        remote.PutText("/logs/PC-014/20260720-090000-aaaaaaaa.jsonl", good + "\n{\"id\":\"trunc\",\"comp");

        var result = await new LogReader(remote, TrackerLog.Null).ReadAllAsync("/logs");

        Assert.Single(result.Entries);
        Assert.Equal(1, result.MalformedLines);
    }

    [Fact]
    public async Task Heartbeat_files_are_not_mistaken_for_interval_logs()
    {
        var remote = new InMemoryRemoteStorage();
        WriteChunk(remote, "PC-014", TestData.Interval("a"));
        remote.PutText("/logs/_heartbeat/PC-014.json", """{"computer_no":"PC-014","updated_utc":"2026-07-20T09:00:00+00:00"}""");

        var result = await new LogReader(remote, TrackerLog.Null).ReadAllAsync("/logs");

        Assert.Single(result.Entries);
        Assert.Equal(1, result.FilesRead);
    }

    // ---- Full run --------------------------------------------------------------------------

    [Fact]
    public async Task A_full_run_writes_the_master_workbook_and_reports_machine_status()
    {
        var remote = new InMemoryRemoteStorage();
        var today = new DateOnly(2026, 7, 20);

        WriteChunk(remote, "PC-014", TestData.Interval("a", date: today, activeSec: 3600));
        WriteChunk(remote, "PC-020", TestData.Interval("b", computerNo: "PC-020", date: today, activeSec: 1800));

        remote.PutText("/logs/_heartbeat/PC-014.json", TrackerJson.Serialize(new
        {
            computer_no = "PC-014",
            updated_utc = DateTimeOffset.UtcNow,
            session_state = "Unlocked",
            pending_intervals = 0,
        }));

        var config = new TrackerConfig();
        config.ComputerEmployeeMap["PC-014"] = "Asha Nair";
        config.ComputerEmployeeMap["PC-020"] = "Ravi Kumar";

        var runner = new AggregationRunner(remote, TrackerLog.Null, India, () => new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var report = await runner.RunAsync(config.Sanitized(), upload: true);

        Assert.Equal(new Quarter(2026, 3), report.CurrentQuarter);
        Assert.Equal(2, report.Intervals);
        Assert.Equal(2, report.DailyTotalRows);
        Assert.Equal("/TimeLog_Current.xlsx", report.UploadedTo);
        Assert.Contains("/TimeLog_Current.xlsx", remote.Paths);

        // A real .xlsx is a zip: check the magic bytes rather than just a non-empty buffer.
        Assert.True(report.Workbook.Length > 1000);
        Assert.Equal(0x50, report.Workbook[0]);
        Assert.Equal(0x4B, report.Workbook[1]);

        // PC-020 never sent a heartbeat, so it must be surfaced as silent rather than omitted.
        var silent = report.Machines.Single(m => m.ComputerNo == "PC-020");
        Assert.Null(silent.LastHeartbeatUtc);
        Assert.Equal("Ravi Kumar", silent.Employee);

        Assert.NotNull(report.Machines.Single(m => m.ComputerNo == "PC-014").LastHeartbeatUtc);
    }

    [Fact]
    public async Task An_empty_fleet_still_produces_a_valid_workbook()
    {
        var remote = new InMemoryRemoteStorage();

        var runner = new AggregationRunner(remote, TrackerLog.Null, India, () => new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var report = await runner.RunAsync(new TrackerConfig().Sanitized(), upload: true);

        Assert.Equal(0, report.DailyTotalRows);
        Assert.Contains("/TimeLog_Current.xlsx", remote.Paths);
        Assert.True(report.Workbook.Length > 0);
    }

    [Fact]
    public async Task Two_runs_over_identical_input_produce_the_same_totals()
    {
        var remote = new InMemoryRemoteStorage();
        WriteChunk(remote, "PC-014", TestData.Interval("a", activeSec: 600), TestData.Interval("b", file: "X.docx", activeSec: 300));

        var runner = new AggregationRunner(remote, TrackerLog.Null, India, () => new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

        var first = await runner.RunAsync(new TrackerConfig().Sanitized(), upload: true);
        var second = await runner.RunAsync(new TrackerConfig().Sanitized(), upload: true);

        Assert.Equal(first.DailyTotalRows, second.DailyTotalRows);
        Assert.Equal(first.Intervals, second.Intervals);
    }

    // ---- Quarterly rollover ----------------------------------------------------------------

    [Fact]
    public async Task A_completed_quarter_is_archived_and_left_out_of_the_current_workbook()
    {
        var remote = new InMemoryRemoteStorage();

        WriteChunk(remote, "PC-014", TestData.Interval("q1", date: new DateOnly(2026, 2, 10), activeSec: 1200));
        WriteChunk(remote, "PC-014", TestData.Interval("q3", date: new DateOnly(2026, 7, 10), activeSec: 600));

        var config = new TrackerConfig { ArchiveGraceDays = 30 }.Sanitized();
        var runner = new AggregationRunner(remote, TrackerLog.Null, India, () => new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

        var report = await runner.RunAsync(config, upload: true);

        var archive = Assert.Single(report.Archives);
        Assert.Equal(new Quarter(2026, 1), archive.Quarter);
        Assert.Equal("/archive/TimeLog_2026-Q1.xlsx", archive.WorkbookPath);
        Assert.Contains("/archive/TimeLog_2026-Q1.xlsx", remote.Paths);

        // Grace window is long past for Q1 (ended 31 Mar, +30 days = 30 Apr), so its logs move.
        Assert.True(archive.Finalised);
        Assert.Equal(1, archive.LogFilesMoved);
        Assert.Contains(remote.Paths, p => p.StartsWith("/archive/logs/2026-Q1/", StringComparison.OrdinalIgnoreCase));

        // The master covers the current quarter only (§4.5).
        Assert.Equal(1, report.Intervals);
        Assert.Equal(1, report.DailyTotalRows);
    }

    [Fact]
    public async Task Inside_the_grace_window_the_logs_stay_put_so_late_uploads_still_land()
    {
        var remote = new InMemoryRemoteStorage();
        WriteChunk(remote, "PC-014", TestData.Interval("q2", date: new DateOnly(2026, 6, 20), activeSec: 1200));

        var config = new TrackerConfig { ArchiveGraceDays = 30 }.Sanitized();

        // 5 July: Q2 has ended but the window is still open.
        var runner = new AggregationRunner(remote, TrackerLog.Null, India, () => new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
        var report = await runner.RunAsync(config, upload: true);

        var archive = Assert.Single(report.Archives);
        Assert.False(archive.Finalised);
        Assert.Equal(0, archive.LogFilesMoved);

        // The archive workbook exists and the source log is still readable, so a laptop that
        // reconnects tomorrow with more Q2 data is still counted on the next run.
        Assert.Contains("/archive/TimeLog_2026-Q2.xlsx", remote.Paths);
        Assert.Contains(remote.Paths, p => p.StartsWith("/logs/PC-014/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_chunk_straddling_the_quarter_boundary_is_not_moved()
    {
        // Moving it would hide the current quarter's rows from the live workbook.
        var remote = new InMemoryRemoteStorage();

        WriteChunk(remote, "PC-014",
            TestData.Interval("late-q1", date: new DateOnly(2026, 3, 31), activeSec: 600),
            TestData.Interval("early-q2", date: new DateOnly(2026, 4, 1), activeSec: 600));

        var config = new TrackerConfig { ArchiveGraceDays = 0 }.Sanitized();
        var runner = new AggregationRunner(remote, TrackerLog.Null, India, () => new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));

        var report = await runner.RunAsync(config, upload: true);

        var archive = Assert.Single(report.Archives);
        Assert.True(archive.Finalised);
        Assert.Equal(0, archive.LogFilesMoved);

        Assert.Contains(remote.Paths, p => p.StartsWith("/logs/PC-014/", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, report.Intervals);
    }

    [Fact]
    public async Task Several_completed_quarters_are_each_archived_separately()
    {
        var remote = new InMemoryRemoteStorage();

        WriteChunk(remote, "PC-014", TestData.Interval("a", date: new DateOnly(2025, 11, 5), activeSec: 600));
        WriteChunk(remote, "PC-014", TestData.Interval("b", date: new DateOnly(2026, 2, 5), activeSec: 600));
        WriteChunk(remote, "PC-014", TestData.Interval("c", date: new DateOnly(2026, 5, 5), activeSec: 600));
        WriteChunk(remote, "PC-014", TestData.Interval("d", date: new DateOnly(2026, 7, 5), activeSec: 600));

        var runner = new AggregationRunner(remote, TrackerLog.Null, India, () => new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var report = await runner.RunAsync(new TrackerConfig().Sanitized(), upload: true);

        Assert.Equal(3, report.Archives.Count);
        Assert.Equal(
            new[] { new Quarter(2025, 4), new Quarter(2026, 1), new Quarter(2026, 2) },
            report.Archives.Select(a => a.Quarter));

        Assert.Contains("/archive/TimeLog_2025-Q4.xlsx", remote.Paths);
        Assert.Contains("/archive/TimeLog_2026-Q1.xlsx", remote.Paths);
        Assert.Contains("/archive/TimeLog_2026-Q2.xlsx", remote.Paths);

        Assert.Equal(1, report.Intervals);
    }

    [Fact]
    public async Task Archives_are_kept_not_deleted()
    {
        // §4.5: "Archives kept by default."
        var remote = new InMemoryRemoteStorage();
        WriteChunk(remote, "PC-014", TestData.Interval("old", date: new DateOnly(2025, 1, 15), activeSec: 600));

        var runner = new AggregationRunner(remote, TrackerLog.Null, India, () => new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

        await runner.RunAsync(new TrackerConfig().Sanitized(), upload: true);
        await runner.RunAsync(new TrackerConfig().Sanitized(), upload: true);

        Assert.Contains("/archive/TimeLog_2025-Q1.xlsx", remote.Paths);
        Assert.Contains(remote.Paths, p => p.StartsWith("/archive/logs/2025-Q1/", StringComparison.OrdinalIgnoreCase));
    }
}
