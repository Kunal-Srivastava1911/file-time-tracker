using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Aggregator.Reporting;

public sealed record ArchiveOutcome(Quarter Quarter, string WorkbookPath, int Intervals, int LogFilesMoved, bool Finalised);

/// <summary>
/// Quarterly rollover (design.md §4.5): archive each completed quarter to
/// <c>archive/TimeLog_&lt;YYYY&gt;-Q&lt;n&gt;.xlsx</c>, move that quarter's logs across, and leave
/// the current workbook covering only the current quarter.
/// </summary>
/// <remarks>
/// Runs in two phases per quarter. While the grace window is open the archived workbook is rebuilt
/// from source on every run, so a laptop that was offline over the boundary still lands in the
/// right quarter. Once the window closes the logs move under <c>archive/logs/</c> and the workbook
/// stops changing. Archives are kept, never deleted (§4.5).
/// </remarks>
public sealed class QuarterArchiver
{
    private readonly IRemoteStorage _remote;
    private readonly TrackerLog _log;

    public QuarterArchiver(IRemoteStorage remote, TrackerLog log)
    {
        _remote = remote;
        _log = log;
    }

    public async Task<IReadOnlyList<ArchiveOutcome>> ArchiveCompletedQuartersAsync(
        LogReadResult logs,
        TrackerConfig config,
        DateOnly today,
        CancellationToken ct = default)
    {
        var currentQuarter = Quarter.FromDate(today);
        var outcomes = new List<ArchiveOutcome>();

        var completedQuarters = logs.Entries
            .Select(e => Quarter.FromDate(e.Record.LocalDate))
            .Where(q => q < currentQuarter)
            .Distinct()
            .OrderBy(q => q)
            .ToList();

        foreach (var quarter in completedQuarters)
        {
            ct.ThrowIfCancellationRequested();
            outcomes.Add(await ArchiveQuarterAsync(logs, config, quarter, today, ct).ConfigureAwait(false));
        }

        return outcomes;
    }

    private async Task<ArchiveOutcome> ArchiveQuarterAsync(
        LogReadResult logs,
        TrackerConfig config,
        Quarter quarter,
        DateOnly today,
        CancellationToken ct)
    {
        var entries = logs.Entries.Where(e => quarter.Contains(e.Record.LocalDate)).ToList();

        var totals = DailyTotalsBuilder.Build(entries.Select(e => e.Record), config.ComputerEmployeeMap);
        var workbookPath = LogLayout.ArchiveWorkbookPath(config.ArchivePath, quarter);

        var bytes = WorkbookWriter.Build(totals, new WorkbookMetadata
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            Scope = $"Archive for {quarter.Label} ({quarter.Start:yyyy-MM-dd} to {quarter.EndExclusive.AddDays(-1):yyyy-MM-dd})",
            SourceFiles = entries.Select(e => e.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Intervals = entries.Count,
            DuplicatesDropped = 0,
            MalformedLines = 0,
        });

        await _remote.UploadAsync(workbookPath, bytes, ct).ConfigureAwait(false);
        _log.Info($"Archived {quarter.Label}: {totals.Count} daily total(s) written to '{workbookPath}'.");

        var graceEnds = quarter.EndExclusive.AddDays(config.ArchiveGraceDays);
        if (today < graceEnds)
        {
            _log.Info(
                $"Logs for {quarter.Label} stay in place until {graceEnds:yyyy-MM-dd} so late uploads " +
                "from machines that were offline are still counted.");

            return new ArchiveOutcome(quarter, workbookPath, entries.Count, LogFilesMoved: 0, Finalised: false);
        }

        var moved = await MoveLogsAsync(logs, config, quarter, ct).ConfigureAwait(false);
        return new ArchiveOutcome(quarter, workbookPath, entries.Count, moved, Finalised: true);
    }

    /// <summary>
    /// Moves only the log files whose every interval falls inside the quarter. A chunk that
    /// straddles the boundary is left alone: moving it would hide the current quarter's rows from
    /// the live workbook, and splitting it would break the id-based de-duplication.
    /// </summary>
    private async Task<int> MoveLogsAsync(LogReadResult logs, TrackerConfig config, Quarter quarter, CancellationToken ct)
    {
        var candidates = logs.Entries
            .GroupBy(e => e.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.All(e => quarter.Contains(e.Record.LocalDate)))
            .Select(g => g.Key)
            .ToList();

        var destinationRoot = LogLayout.ArchiveFolder(config.ArchivePath, quarter);
        var moved = 0;

        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Preserve the per-machine folder so the archive keeps the same shape as logs/.
            var machine = RemotePath.FileName(RemotePath.Parent(path));
            var destination = RemotePath.Combine(destinationRoot, machine, RemotePath.FileName(path));

            try
            {
                await _remote.MoveAsync(path, destination, ct).ConfigureAwait(false);
                moved++;
            }
            catch (Exception ex)
            {
                // A failed move is safe to retry next run; the source file is still readable.
                _log.Warn($"Could not archive log '{path}' to '{destination}'.", ex);
            }
        }

        _log.Info($"Moved {moved} of {candidates.Count} log file(s) for {quarter.Label} into '{destinationRoot}'.");
        return moved;
    }
}
