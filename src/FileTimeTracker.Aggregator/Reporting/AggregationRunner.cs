using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Aggregator.Reporting;

public sealed record AggregationReport
{
    public required Quarter CurrentQuarter { get; init; }

    public required int LogFiles { get; init; }

    public required int Intervals { get; init; }

    public required int DuplicatesDropped { get; init; }

    public required int MalformedLines { get; init; }

    public required int DailyTotalRows { get; init; }

    public required IReadOnlyList<ArchiveOutcome> Archives { get; init; }

    public required IReadOnlyList<MachineStatus> Machines { get; init; }

    public required byte[] Workbook { get; init; }

    public string? UploadedTo { get; init; }
}

/// <summary>
/// One aggregator pass (design.md §4.5): pull every machine's logs, roll over completed quarters,
/// and rewrite the master workbook.
/// </summary>
/// <remarks>
/// The workbook is recomputed from source on every run rather than updated in place. That makes a
/// run idempotent and self-healing: a laptop that uploads a week late, a chunk that arrived twice,
/// or a workbook someone edited by hand all resolve on the next pass. At fleet scale (&lt;50
/// machines, a quarter of data) the full rebuild is a few seconds' work.
/// </remarks>
public sealed class AggregationRunner
{
    private readonly IRemoteStorage _remote;
    private readonly TrackerLog _log;
    private readonly TimeZoneInfo _timeZone;
    private readonly Func<DateTimeOffset> _nowUtc;

    /// <param name="nowUtc">Overridable so quarter-rollover behaviour can be tested (§8).</param>
    public AggregationRunner(IRemoteStorage remote, TrackerLog log, TimeZoneInfo timeZone, Func<DateTimeOffset>? nowUtc = null)
    {
        _remote = remote;
        _log = log;
        _timeZone = timeZone;
        _nowUtc = nowUtc ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<AggregationReport> RunAsync(TrackerConfig config, bool upload, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_nowUtc(), _timeZone).DateTime);
        var currentQuarter = Quarter.FromDate(today);

        _log.Info($"Aggregating for {today:yyyy-MM-dd} ({currentQuarter.Label}, time zone '{_timeZone.Id}').");

        var logs = await new LogReader(_remote, _log).ReadAllAsync(config.LogsPath, ct).ConfigureAwait(false);

        var archives = await new QuarterArchiver(_remote, _log)
            .ArchiveCompletedQuartersAsync(logs, config, today, ct)
            .ConfigureAwait(false);

        // Anything belonging to a completed quarter now lives in that quarter's archive workbook,
        // so the master covers the current quarter only (§4.5, "start a fresh current workbook").
        var currentRecords = logs.Entries
            .Where(e => Quarter.FromDate(e.Record.LocalDate) >= currentQuarter)
            .ToList();

        var totals = DailyTotalsBuilder.Build(currentRecords.Select(e => e.Record), config.ComputerEmployeeMap);

        var machines = await new HeartbeatReader(_remote, _log)
            .ReadAsync(config.LogsPath, config.ComputerEmployeeMap, ct)
            .ConfigureAwait(false);

        var workbook = WorkbookWriter.Build(totals, new WorkbookMetadata
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            Scope = $"Current quarter {currentQuarter.Label} (from {currentQuarter.Start:yyyy-MM-dd})",
            SourceFiles = logs.FilesRead,
            Intervals = currentRecords.Count,
            DuplicatesDropped = logs.DuplicatesDropped,
            MalformedLines = logs.MalformedLines,
            Machines = machines,
        });

        string? uploadedTo = null;
        if (upload)
        {
            await _remote.UploadAsync(config.MasterWorkbookPath, workbook, ct).ConfigureAwait(false);
            uploadedTo = config.MasterWorkbookPath;
            _log.Info($"Wrote master workbook to '{config.MasterWorkbookPath}' ({totals.Count} row(s), {workbook.Length / 1024} KB).");
        }

        return new AggregationReport
        {
            CurrentQuarter = currentQuarter,
            LogFiles = logs.FilesRead,
            Intervals = currentRecords.Count,
            DuplicatesDropped = logs.DuplicatesDropped,
            MalformedLines = logs.MalformedLines,
            DailyTotalRows = totals.Count,
            Archives = archives,
            Machines = machines,
            Workbook = workbook,
            UploadedTo = uploadedTo,
        };
    }
}
