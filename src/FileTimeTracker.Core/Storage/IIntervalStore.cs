using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Core.Storage;

/// <summary>
/// The crash-safe local buffer (design.md §4.2). Survives reboots and offline periods; rows are
/// only cleared once the uploader has confirmed them.
/// </summary>
public interface IIntervalStore : IDisposable
{
    /// <summary>Inserts a batch. Re-inserting an existing <c>id</c> is a no-op, never an error.</summary>
    int Add(IEnumerable<IntervalRecord> records);

    IReadOnlyList<IntervalRecord> GetPendingUploads(int limit);

    void MarkUploaded(IEnumerable<string> ids);

    int PendingCount();

    /// <summary>Drops already-uploaded rows older than the cutoff so the buffer cannot grow forever.</summary>
    int PurgeUploaded(DateTimeOffset olderThanUtc);

    string? GetState(string key);

    void SetState(string key, string value);
}
