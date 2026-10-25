using FileTimeTracker.Agent.Upload;
using FileTimeTracker.Aggregator.Reporting;
using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Storage;
using FileTimeTracker.Tests.Fakes;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>
/// design.md §8: "idempotent upload" (unit) and "offline buffering + reconnect; concurrent uploads
/// from multiple machines" (integration).
/// </summary>
public class LogUploaderTests
{
    private static readonly TrackerConfig Config = new TrackerConfig().Sanitized();

    [Fact]
    public async Task Uploads_pending_intervals_and_clears_them_from_the_buffer()
    {
        var remote = new InMemoryRemoteStorage();
        using var store = SqliteIntervalStore.InMemory();
        store.Add(Enumerable.Range(0, 5).Select(i => TestData.Interval($"id-{i}")));

        var uploader = new LogUploader(remote, store, "PC-014", TrackerLog.Null);
        var uploaded = await uploader.UploadPendingAsync(Config, CancellationToken.None);

        Assert.Equal(5, uploaded);
        Assert.Equal(0, store.PendingCount());
        Assert.NotNull(uploader.LastSuccessfulUploadUtc);

        var chunk = Assert.Single(remote.Paths, LogLayout.IsLogFile);
        Assert.StartsWith("/logs/PC-014/", chunk, StringComparison.OrdinalIgnoreCase);

        var lines = remote.GetText(chunk)!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public async Task Uploading_again_with_nothing_pending_writes_no_new_chunk()
    {
        var remote = new InMemoryRemoteStorage();
        using var store = SqliteIntervalStore.InMemory();
        store.Add(new[] { TestData.Interval("a") });

        var uploader = new LogUploader(remote, store, "PC-014", TrackerLog.Null);

        Assert.Equal(1, await uploader.UploadPendingAsync(Config, CancellationToken.None));
        Assert.Equal(0, await uploader.UploadPendingAsync(Config, CancellationToken.None));

        Assert.Single(remote.Paths, LogLayout.IsLogFile);
    }

    [Fact]
    public async Task An_interrupted_cycle_re_sends_and_the_aggregator_still_counts_it_once()
    {
        // The §4.3 crash window: Dropbox accepted the chunk but the process died before the rows
        // were marked uploaded. At-least-once delivery plus id de-duplication must equal
        // exactly-once totals.
        var remote = new InMemoryRemoteStorage();
        var store = new ReplayingIntervalStore(new[] { TestData.Interval("a"), TestData.Interval("b") });

        var uploader = new LogUploader(remote, store, "PC-014", TrackerLog.Null);

        store.SuppressMarking = true;
        await uploader.UploadPendingAsync(Config, CancellationToken.None);

        store.SuppressMarking = false;
        await uploader.UploadPendingAsync(Config, CancellationToken.None);

        // Two chunks on the wire, carrying the same two ids.
        Assert.Equal(2, remote.Paths.Count(LogLayout.IsLogFile));

        var result = await new LogReader(remote, TrackerLog.Null).ReadAllAsync(Config.LogsPath);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(2, result.DuplicatesDropped);

        var totals = DailyTotalsBuilder.Build(result.Records);
        Assert.Equal(1200, Assert.Single(totals).ActiveSec);
    }

    [Fact]
    public async Task Offline_keeps_intervals_buffered_and_reconnecting_drains_them()
    {
        var remote = new InMemoryRemoteStorage { Offline = true };
        using var store = SqliteIntervalStore.InMemory();
        store.Add(Enumerable.Range(0, 3).Select(i => TestData.Interval($"id-{i}")));

        var uploader = new LogUploader(remote, store, "PC-014", TrackerLog.Null);

        Assert.Equal(0, await uploader.UploadPendingAsync(Config, CancellationToken.None));
        Assert.Equal(3, store.PendingCount());
        Assert.Null(uploader.LastSuccessfulUploadUtc);

        // More work happens while the laptop is still off the network.
        store.Add(Enumerable.Range(3, 2).Select(i => TestData.Interval($"id-{i}")));
        Assert.Equal(0, await uploader.UploadPendingAsync(Config, CancellationToken.None));
        Assert.Equal(5, store.PendingCount());

        remote.Offline = false;

        Assert.Equal(5, await uploader.UploadPendingAsync(Config, CancellationToken.None));
        Assert.Equal(0, store.PendingCount());
        Assert.NotNull(uploader.LastSuccessfulUploadUtc);
    }

    [Fact]
    public async Task Batches_larger_than_the_limit_are_uploaded_in_several_chunks()
    {
        var remote = new InMemoryRemoteStorage();
        using var store = SqliteIntervalStore.InMemory();
        store.Add(Enumerable.Range(0, 25).Select(i => TestData.Interval($"id-{i:D2}")));

        var uploader = new LogUploader(remote, store, "PC-014", TrackerLog.Null, batchSize: 10);

        Assert.Equal(25, await uploader.UploadPendingAsync(Config, CancellationToken.None));
        Assert.Equal(3, remote.Paths.Count(LogLayout.IsLogFile));
        Assert.Equal(0, store.PendingCount());
    }

    [Fact]
    public async Task Concurrent_uploads_from_several_machines_do_not_collide()
    {
        // Per-machine chunk paths are what make this safe: no shared file, so no lost update and
        // no Dropbox "conflicted copy" (§4, "why not write one shared Excel directly").
        var remote = new InMemoryRemoteStorage();
        var machines = new[] { "PC-001", "PC-002", "PC-003" };
        var stores = new List<SqliteIntervalStore>();

        try
        {
            var uploads = machines.Select(machine =>
            {
                var store = SqliteIntervalStore.InMemory();
                lock (stores)
                {
                    stores.Add(store);
                }

                store.Add(Enumerable.Range(0, 20).Select(i =>
                    TestData.Interval($"{machine}-{i:D2}", computerNo: machine, activeSec: 60)));

                var uploader = new LogUploader(remote, store, machine, TrackerLog.Null, batchSize: 7);
                return uploader.UploadPendingAsync(Config, CancellationToken.None);
            }).ToList();

            var counts = await Task.WhenAll(uploads);
            Assert.All(counts, count => Assert.Equal(20, count));

            var result = await new LogReader(remote, TrackerLog.Null).ReadAllAsync(Config.LogsPath);
            Assert.Equal(60, result.Entries.Count);
            Assert.Equal(0, result.DuplicatesDropped);

            var totals = DailyTotalsBuilder.Build(result.Records);
            Assert.Equal(3, totals.Count);
            Assert.All(totals, total => Assert.Equal(20 * 60, total.ActiveSec));
            Assert.Equal(machines.OrderBy(m => m), totals.Select(t => t.ComputerNo).OrderBy(m => m));
        }
        finally
        {
            foreach (var store in stores)
            {
                store.Dispose();
            }
        }
    }

    /// <summary>
    /// Buffer whose <see cref="MarkUploaded"/> can be suppressed, reproducing a crash between a
    /// successful upload and the local commit.
    /// </summary>
    private sealed class ReplayingIntervalStore : IIntervalStore
    {
        private readonly List<IntervalRecord> _records;
        private readonly HashSet<string> _uploaded = new(StringComparer.Ordinal);

        public ReplayingIntervalStore(IEnumerable<IntervalRecord> records) => _records = records.ToList();

        public bool SuppressMarking { get; set; }

        public int Add(IEnumerable<IntervalRecord> records)
        {
            var added = 0;
            foreach (var record in records.Where(r => _records.All(existing => existing.Id != r.Id)))
            {
                _records.Add(record);
                added++;
            }

            return added;
        }

        public IReadOnlyList<IntervalRecord> GetPendingUploads(int limit) =>
            _records.Where(r => !_uploaded.Contains(r.Id)).Take(limit).ToList();

        public void MarkUploaded(IEnumerable<string> ids)
        {
            if (SuppressMarking)
            {
                return;
            }

            foreach (var id in ids)
            {
                _uploaded.Add(id);
            }
        }

        public int PendingCount() => _records.Count(r => !_uploaded.Contains(r.Id));

        public int PurgeUploaded(DateTimeOffset olderThanUtc) => 0;

        public string? GetState(string key) => null;

        public void SetState(string key, string value)
        {
        }

        public void Dispose()
        {
        }
    }
}
