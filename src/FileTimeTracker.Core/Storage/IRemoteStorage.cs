namespace FileTimeTracker.Core.Storage;

public sealed record RemoteFile(string Path, long Size, DateTime ServerModifiedUtc);

/// <summary>
/// The Dropbox surface the agent and aggregator actually need, kept narrow so the whole upload and
/// aggregation pipeline can be tested against an in-memory fake (design.md §8).
/// </summary>
public interface IRemoteStorage
{
    Task<byte[]?> DownloadAsync(string path, CancellationToken ct = default);

    Task<string?> DownloadTextAsync(string path, CancellationToken ct = default);

    Task UploadAsync(string path, byte[] content, CancellationToken ct = default);

    Task UploadTextAsync(string path, string content, CancellationToken ct = default);

    /// <summary>Recursively lists files under <paramref name="folder"/>. Missing folder yields empty.</summary>
    Task<IReadOnlyList<RemoteFile>> ListFilesAsync(string folder, CancellationToken ct = default);

    Task CopyAsync(string fromPath, string toPath, CancellationToken ct = default);

    Task MoveAsync(string fromPath, string toPath, CancellationToken ct = default);

    Task DeleteAsync(string path, CancellationToken ct = default);

    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
}
