using System.Collections.Concurrent;
using System.Text;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IRemoteStorage"/> standing in for Dropbox, so the upload and aggregation
/// pipelines can be tested end to end without network or credentials (design.md §8).
/// </summary>
public sealed class InMemoryRemoteStorage : IRemoteStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true every operation throws, simulating an offline laptop.</summary>
    public bool Offline { get; set; }

    public int UploadCount => _uploadCount;

    private int _uploadCount;

    public IReadOnlyCollection<string> Paths => _files.Keys.ToList();

    public string? GetText(string path) =>
        _files.TryGetValue(RemotePath.Normalize(path), out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    public void PutText(string path, string content) =>
        _files[RemotePath.Normalize(path)] = Encoding.UTF8.GetBytes(content);

    private void ThrowIfOffline()
    {
        if (Offline)
        {
            throw new HttpRequestException("Simulated network failure.");
        }
    }

    public Task<byte[]?> DownloadAsync(string path, CancellationToken ct = default)
    {
        ThrowIfOffline();
        return Task.FromResult(_files.TryGetValue(RemotePath.Normalize(path), out var bytes) ? bytes : null);
    }

    public async Task<string?> DownloadTextAsync(string path, CancellationToken ct = default)
    {
        var bytes = await DownloadAsync(path, ct).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public Task UploadAsync(string path, byte[] content, CancellationToken ct = default)
    {
        ThrowIfOffline();
        _files[RemotePath.Normalize(path)] = content;
        Interlocked.Increment(ref _uploadCount);
        return Task.CompletedTask;
    }

    public Task UploadTextAsync(string path, string content, CancellationToken ct = default) =>
        UploadAsync(path, Encoding.UTF8.GetBytes(content), ct);

    public Task<IReadOnlyList<RemoteFile>> ListFilesAsync(string folder, CancellationToken ct = default)
    {
        ThrowIfOffline();

        var prefix = RemotePath.Normalize(folder);
        var matches = _files
            .Where(kvp => prefix.Length == 0 || kvp.Key.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            .Select(kvp => new RemoteFile(kvp.Key, kvp.Value.Length, DateTime.UtcNow))
            .ToList();

        return Task.FromResult<IReadOnlyList<RemoteFile>>(matches);
    }

    public Task CopyAsync(string fromPath, string toPath, CancellationToken ct = default)
    {
        ThrowIfOffline();

        if (_files.TryGetValue(RemotePath.Normalize(fromPath), out var bytes))
        {
            _files[RemotePath.Normalize(toPath)] = bytes;
        }

        return Task.CompletedTask;
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken ct = default)
    {
        await CopyAsync(fromPath, toPath, ct).ConfigureAwait(false);
        _files.TryRemove(RemotePath.Normalize(fromPath), out _);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        ThrowIfOffline();
        _files.TryRemove(RemotePath.Normalize(path), out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        ThrowIfOffline();
        return Task.FromResult(_files.ContainsKey(RemotePath.Normalize(path)));
    }
}
