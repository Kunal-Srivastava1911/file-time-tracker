using Dropbox.Api;
using Dropbox.Api.Files;
using FileTimeTracker.Core.Diagnostics;

namespace FileTimeTracker.Core.Storage;

/// <summary>
/// <see cref="IRemoteStorage"/> over the official Dropbox.NET SDK (design.md §4.3). The SDK holds
/// the refresh token and mints short-lived access tokens itself, so nothing here caches a bearer
/// token on disk.
/// </summary>
public sealed class DropboxRemoteStorage : IRemoteStorage, IDisposable
{
    /// <summary>Dropbox rejects a single-shot upload above 150 MB; switch to sessions well before that.</summary>
    private const int UploadSessionThreshold = 96 * 1024 * 1024;

    private const int UploadChunkSize = 8 * 1024 * 1024;

    private readonly DropboxClient _client;
    private readonly TrackerLog _log;
    private readonly int _maxAttempts;

    public DropboxRemoteStorage(DropboxCredentials credentials, string userAgent, TrackerLog? log = null, int maxAttempts = 5)
    {
        credentials.Validate();
        _log = log ?? TrackerLog.Null;
        _maxAttempts = Math.Max(1, maxAttempts);

        var config = new DropboxClientConfig(userAgent) { HttpClient = SharedHttp.Client };

        _client = credentials.IsPkce
            ? new DropboxClient(credentials.RefreshToken, credentials.AppKey, config)
            : new DropboxClient(credentials.RefreshToken, credentials.AppKey, credentials.AppSecret, config);
    }

    public async Task<byte[]?> DownloadAsync(string path, CancellationToken ct = default)
    {
        var target = RemotePath.Normalize(path);

        return await RetryAsync<byte[]?>(async () =>
        {
            try
            {
                using var response = await _client.Files.DownloadAsync(target).ConfigureAwait(false);
                return await response.GetContentAsByteArrayAsync().ConfigureAwait(false);
            }
            catch (ApiException<DownloadError> ex) when (IsNotFound(ex.ErrorResponse))
            {
                return null;
            }
        }, $"download {target}", ct).ConfigureAwait(false);
    }

    public async Task<string?> DownloadTextAsync(string path, CancellationToken ct = default)
    {
        var bytes = await DownloadAsync(path, ct).ConfigureAwait(false);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    public Task UploadTextAsync(string path, string content, CancellationToken ct = default) =>
        UploadAsync(path, System.Text.Encoding.UTF8.GetBytes(content), ct);

    public async Task UploadAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var target = RemotePath.Normalize(path);

        await RetryAsync(async () =>
        {
            if (content.Length < UploadSessionThreshold)
            {
                using var stream = new MemoryStream(content, writable: false);
                await _client.Files.UploadAsync(target, WriteMode.Overwrite.Instance, body: stream)
                    .ConfigureAwait(false);
            }
            else
            {
                await UploadViaSessionAsync(target, content, ct).ConfigureAwait(false);
            }

            return true;
        }, $"upload {target}", ct).ConfigureAwait(false);
    }

    private async Task UploadViaSessionAsync(string target, byte[] content, CancellationToken ct)
    {
        string sessionId;
        using (var first = new MemoryStream(content, 0, Math.Min(UploadChunkSize, content.Length), writable: false))
        {
            var start = await _client.Files.UploadSessionStartAsync(body: first).ConfigureAwait(false);
            sessionId = start.SessionId;
        }

        var offset = (ulong)Math.Min(UploadChunkSize, content.Length);

        while ((int)offset < content.Length)
        {
            ct.ThrowIfCancellationRequested();

            var length = Math.Min(UploadChunkSize, content.Length - (int)offset);
            using var chunk = new MemoryStream(content, (int)offset, length, writable: false);
            await _client.Files
                .UploadSessionAppendV2Async(new UploadSessionCursor(sessionId, offset), body: chunk)
                .ConfigureAwait(false);
            offset += (ulong)length;
        }

        using var empty = new MemoryStream(Array.Empty<byte>(), writable: false);
        await _client.Files.UploadSessionFinishAsync(
                new UploadSessionCursor(sessionId, offset),
                new CommitInfo(target, WriteMode.Overwrite.Instance),
                body: empty)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteFile>> ListFilesAsync(string folder, CancellationToken ct = default)
    {
        var target = RemotePath.Normalize(folder);
        var files = new List<RemoteFile>();

        return await RetryAsync<IReadOnlyList<RemoteFile>>(async () =>
        {
            files.Clear();

            ListFolderResult page;
            try
            {
                page = await _client.Files.ListFolderAsync(target, recursive: true).ConfigureAwait(false);
            }
            catch (ApiException<ListFolderError> ex) when (IsNotFound(ex.ErrorResponse))
            {
                return files;
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var entry in page.Entries)
                {
                    if (entry.IsFile)
                    {
                        var file = entry.AsFile;
                        files.Add(new RemoteFile(file.PathDisplay ?? file.PathLower, (long)file.Size, file.ServerModified));
                    }
                }

                if (!page.HasMore)
                {
                    break;
                }

                page = await _client.Files.ListFolderContinueAsync(page.Cursor).ConfigureAwait(false);
            }

            return files;
        }, $"list {target}", ct).ConfigureAwait(false);
    }

    public Task CopyAsync(string fromPath, string toPath, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            await _client.Files.CopyV2Async(RemotePath.Normalize(fromPath), RemotePath.Normalize(toPath))
                .ConfigureAwait(false);
            return true;
        }, $"copy {fromPath} -> {toPath}", ct);

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            await _client.Files.MoveV2Async(RemotePath.Normalize(fromPath), RemotePath.Normalize(toPath))
                .ConfigureAwait(false);
            return true;
        }, $"move {fromPath} -> {toPath}", ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            try
            {
                await _client.Files.DeleteV2Async(RemotePath.Normalize(path)).ConfigureAwait(false);
            }
            catch (ApiException<DeleteError> ex) when (ex.ErrorResponse.IsPathLookup)
            {
                // Already gone; deleting is idempotent by intent.
            }

            return true;
        }, $"delete {path}", ct);

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var target = RemotePath.Normalize(path);

        return await RetryAsync(async () =>
        {
            try
            {
                await _client.Files.GetMetadataAsync(target).ConfigureAwait(false);
                return true;
            }
            catch (ApiException<GetMetadataError> ex) when (IsNotFound(ex.ErrorResponse))
            {
                return false;
            }
        }, $"stat {target}", ct).ConfigureAwait(false);
    }

    private static bool IsNotFound(DownloadError error) => error.IsPath && error.AsPath.Value.IsNotFound;

    private static bool IsNotFound(ListFolderError error) => error.IsPath && error.AsPath.Value.IsNotFound;

    private static bool IsNotFound(GetMetadataError error) => error.IsPath && error.AsPath.Value.IsNotFound;

    /// <summary>
    /// Exponential backoff over the transient failures that actually happen in the field: Dropbox
    /// 429/5xx and a laptop that lost Wi-Fi mid-call. Permanent errors are rethrown immediately so
    /// a bad token surfaces instead of being retried into a stall.
    /// </summary>
    private async Task<T> RetryAsync<T>(Func<Task<T>> action, string what, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _maxAttempts && IsTransient(ex))
            {
                _log.Warn($"Dropbox {what} failed (attempt {attempt}/{_maxAttempts}); retrying in {delay.TotalSeconds:0}s.", ex);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        RateLimitException => true,
        RetryException => true,
        HttpException => true,
        HttpRequestException => true,
        TaskCanceledException => true,
        IOException => true,
        _ => false,
    };

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// One <see cref="HttpClient"/> for the process. Dropbox.NET disposes the client it is given, so
/// this is deliberately not disposed — socket exhaustion from per-call clients is the failure this
/// avoids in a process that runs for months.
/// </summary>
internal static class SharedHttp
{
    public static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };
}
