using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Storage;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>The §4.2 crash-safe local buffer.</summary>
public class SqliteIntervalStoreTests
{
    [Fact]
    public void Added_intervals_are_pending_until_marked_uploaded()
    {
        using var store = SqliteIntervalStore.InMemory();

        store.Add(new[] { TestData.Interval("a"), TestData.Interval("b") });
        Assert.Equal(2, store.PendingCount());

        var pending = store.GetPendingUploads(10);
        Assert.Equal(2, pending.Count);

        store.MarkUploaded(pending.Select(p => p.Id));
        Assert.Equal(0, store.PendingCount());
        Assert.Empty(store.GetPendingUploads(10));
    }

    [Fact]
    public void Re_adding_the_same_id_is_a_no_op()
    {
        // The interval id is the idempotency key end to end (§4.3); a replayed batch must not
        // duplicate rows or resurrect an already-uploaded one.
        using var store = SqliteIntervalStore.InMemory();

        Assert.Equal(1, store.Add(new[] { TestData.Interval("a") }));

        store.MarkUploaded(new[] { "a" });

        Assert.Equal(0, store.Add(new[] { TestData.Interval("a") }));
        Assert.Equal(0, store.PendingCount());
    }

    [Fact]
    public void Round_trips_every_field_including_nulls_and_the_local_date()
    {
        using var store = SqliteIntervalStore.InMemory();

        var original = TestData.Interval("a", file: "Ledger.xlsx", app: "Excel", activeSec: 300, idleSec: 120)
            with
            {
                FilePath = @"C:\work\Ledger.xlsx",
                WindowTitle = null,
                Source = FileSource.UiaFilePath,
            };

        store.Add(new[] { original });
        var loaded = Assert.Single(store.GetPendingUploads(10));

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.ComputerNo, loaded.ComputerNo);
        Assert.Equal(original.WinUser, loaded.WinUser);
        Assert.Equal(original.App, loaded.App);
        Assert.Equal(original.File, loaded.File);
        Assert.Equal(original.FilePath, loaded.FilePath);
        Assert.Null(loaded.WindowTitle);
        Assert.Equal(original.ActiveSec, loaded.ActiveSec);
        Assert.Equal(original.IdleSec, loaded.IdleSec);
        Assert.Equal(original.Source, loaded.Source);
        Assert.Equal(original.LocalDate, loaded.LocalDate);
        Assert.Equal(original.StartUtc.ToUnixTimeSeconds(), loaded.StartUtc.ToUnixTimeSeconds());
        Assert.Equal(original.EndUtc.ToUnixTimeSeconds(), loaded.EndUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public void Pending_uploads_respect_the_batch_limit()
    {
        using var store = SqliteIntervalStore.InMemory();

        store.Add(Enumerable.Range(0, 25).Select(i => TestData.Interval($"id-{i:D2}")));

        Assert.Equal(10, store.GetPendingUploads(10).Count);
        Assert.Equal(25, store.PendingCount());
    }

    [Fact]
    public void Purge_removes_only_uploaded_rows_past_the_retention_window()
    {
        using var store = SqliteIntervalStore.InMemory();

        store.Add(new[] { TestData.Interval("uploaded"), TestData.Interval("pending") });
        store.MarkUploaded(new[] { "uploaded" });

        // Nothing is old enough yet.
        Assert.Equal(0, store.PurgeUploaded(DateTimeOffset.UtcNow.AddDays(-7)));

        Assert.Equal(1, store.PurgeUploaded(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(1, store.PendingCount());
    }

    [Fact]
    public void Agent_state_round_trips_and_overwrites()
    {
        using var store = SqliteIntervalStore.InMemory();

        Assert.Null(store.GetState("last-run"));

        store.SetState("last-run", "2026-07-20");
        Assert.Equal("2026-07-20", store.GetState("last-run"));

        store.SetState("last-run", "2026-07-21");
        Assert.Equal("2026-07-21", store.GetState("last-run"));
    }

    [Fact]
    public void Survives_being_closed_and_reopened()
    {
        // The buffer's whole purpose is surviving a reboot mid-day (§4.2).
        var path = Path.Combine(Path.GetTempPath(), $"ftt-{Guid.NewGuid():N}.db");

        try
        {
            using (var store = new SqliteIntervalStore(path))
            {
                store.Add(new[] { TestData.Interval("a"), TestData.Interval("b") });
            }

            using (var reopened = new SqliteIntervalStore(path))
            {
                Assert.Equal(2, reopened.PendingCount());
            }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
