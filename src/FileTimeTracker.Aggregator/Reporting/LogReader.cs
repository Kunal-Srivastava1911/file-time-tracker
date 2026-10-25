using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Aggregator.Reporting;

/// <summary>An interval plus the remote file it came from, so logs can later be archived by quarter.</summary>
public sealed record LogEntry(string SourcePath, IntervalRecord Record);

public sealed record LogReadResult
{
    public required IReadOnlyList<LogEntry> Entries { get; init; }

    public required int FilesRead { get; init; }

    public required int DuplicatesDropped { get; init; }

    public required int MalformedLines { get; init; }

    public IEnumerable<IntervalRecord> Records => Entries.Select(e => e.Record);
}

/// <summary>
/// Downloads every per-machine log and materialises the intervals (design.md §4.5).
/// </summary>
/// <remarks>
/// <b>De-duplication by interval id is the correctness keystone.</b> The uploader deliberately
/// re-sends a batch it could not confirm (§4.3), so the same interval legitimately appears in more
/// than one chunk. Dropping repeats here is what makes at-least-once delivery add up to
/// exactly-once totals.
/// </remarks>
public sealed class LogReader
{
    private readonly IRemoteStorage _remote;
    private readonly TrackerLog _log;
    private readonly int _parallelism;

    public LogReader(IRemoteStorage remote, TrackerLog log, int parallelism = 4)
    {
        _remote = remote;
        _log = log;
        _parallelism = Math.Max(1, parallelism);
    }

    public async Task<LogReadResult> ReadAllAsync(string logsRoot, CancellationToken ct = default)
    {
        var files = (await _remote.ListFilesAsync(logsRoot, ct).ConfigureAwait(false))
            .Where(f => LogLayout.IsLogFile(f.Path))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _log.Info($"Found {files.Count} log file(s) under '{logsRoot}'.");

        var byFile = new Dictionary<string, List<LogEntry>>(StringComparer.OrdinalIgnoreCase);
        var malformed = 0;

        using var throttle = new SemaphoreSlim(_parallelism);
        var gate = new object();

        var tasks = files.Select(async file =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var text = await _remote.DownloadTextAsync(file.Path, ct).ConfigureAwait(false);
                if (text is null)
                {
                    // Deleted between listing and download — a rollover running concurrently.
                    return;
                }

                var (entries, bad) = Parse(file.Path, text);

                lock (gate)
                {
                    byFile[file.Path] = entries;
                    malformed += bad;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable machine must not block the whole fleet's report.
                _log.Error($"Could not read log '{file.Path}'; skipping it.", ex);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<LogEntry>();
        var duplicates = 0;

        // Deterministic order so two runs over identical input produce an identical workbook.
        foreach (var path in byFile.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var entry in byFile[path])
            {
                if (seen.Add(entry.Record.Id))
                {
                    deduped.Add(entry);
                }
                else
                {
                    duplicates++;
                }
            }
        }

        _log.Info(
            $"Read {deduped.Count} interval(s) from {byFile.Count} file(s); " +
            $"dropped {duplicates} duplicate(s) and {malformed} malformed line(s).");

        return new LogReadResult
        {
            Entries = deduped,
            FilesRead = byFile.Count,
            DuplicatesDropped = duplicates,
            MalformedLines = malformed,
        };
    }

    private (List<LogEntry> Entries, int Malformed) Parse(string path, string text)
    {
        var entries = new List<LogEntry>();
        var malformed = 0;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                var record = TrackerJson.Deserialize<IntervalRecord>(trimmed);
                if (record is null || string.IsNullOrEmpty(record.Id))
                {
                    malformed++;
                    continue;
                }

                entries.Add(new LogEntry(path, record));
            }
            catch (System.Text.Json.JsonException)
            {
                // A partially-written final line is possible if a chunk upload was interrupted.
                // Skip it: the interval will be re-sent under the same id.
                malformed++;
            }
        }

        if (malformed > 0)
        {
            _log.Warn($"'{path}' contained {malformed} unparseable line(s).");
        }

        return (entries, malformed);
    }
}
