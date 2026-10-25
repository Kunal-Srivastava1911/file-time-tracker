using System.Text;
using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Agent.Upload;

/// <summary>
/// Drains not-yet-uploaded intervals from the local buffer to the machine's Dropbox log
/// (design.md §4.3).
/// </summary>
/// <remarks>
/// <para><b>Idempotent by interval id.</b> Rows are marked uploaded only after Dropbox has
/// acknowledged the write. If the process dies in between, the same rows are re-sent next cycle
/// and appear twice on the wire — the aggregator de-duplicates by id, so a duplicate costs
/// bandwidth and nothing else. The reverse ordering (mark first, upload second) could lose data
/// permanently, which is why it is not used.</para>
/// <para><b>Offline is not an error.</b> A failed upload leaves every row pending and returns; the
/// buffer simply grows until the laptop reconnects.</para>
/// </remarks>
public sealed class LogUploader
{
    private readonly IRemoteStorage _remote;
    private readonly IIntervalStore _store;
    private readonly string _computerNo;
    private readonly TrackerLog _log;
    private readonly int _batchSize;

    public LogUploader(
        IRemoteStorage remote,
        IIntervalStore store,
        string computerNo,
        TrackerLog log,
        int batchSize = 2000)
    {
        _remote = remote;
        _store = store;
        _computerNo = computerNo;
        _log = log;
        _batchSize = batchSize;
    }

    public DateTimeOffset? LastSuccessfulUploadUtc { get; private set; }

    /// <returns>Number of intervals successfully uploaded.</returns>
    public async Task<int> UploadPendingAsync(TrackerConfig config, CancellationToken ct)
    {
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = _store.GetPendingUploads(_batchSize);
            if (batch.Count == 0)
            {
                break;
            }

            var builder = new StringBuilder();
            foreach (var record in batch)
            {
                builder.Append(TrackerJson.Serialize(record)).Append('\n');
            }

            var path = LogLayout.NewChunkPath(config.LogsPath, _computerNo, DateTimeOffset.UtcNow);

            try
            {
                await _remote.UploadAsync(path, Encoding.UTF8.GetBytes(builder.ToString()), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"Upload of {batch.Count} interval(s) failed; they stay buffered for the next cycle.", ex);
                break;
            }

            _store.MarkUploaded(batch.Select(r => r.Id));
            LastSuccessfulUploadUtc = DateTimeOffset.UtcNow;
            total += batch.Count;

            _log.Info($"Uploaded {batch.Count} interval(s) to '{path}'.");

            if (batch.Count < _batchSize)
            {
                break;
            }
        }

        return total;
    }
}
