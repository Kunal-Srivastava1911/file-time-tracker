using FileTimeTracker.Core.Diagnostics;

namespace FileTimeTracker.Core.Storage;

/// <summary>
/// <see cref="IRemoteStorage"/> over an ordinary directory: a local folder, a mapped drive, or a
/// UNC share such as <c>\\fileserver\timetracker</c>.
/// </summary>
/// <remarks>
/// <para>Exists so a deployment needs no cloud account and, more importantly, no embedded
/// credential. Windows authentication to the share replaces the §4.3 refresh token, which removes
/// that caveat's entire risk surface — there is nothing on the laptop for a monitored user to
/// decrypt. NTFS rights can also be tightened past anything Dropbox app-folder scoping allows:
/// granting laptops <em>create files</em> while denying <em>list folder contents</em> and
/// <em>delete</em> stops an employee reading a colleague's log or erasing their own history.</para>
/// <para>The tradeoff is reach. A laptop off the network buffers locally until it is back, which is
/// exactly the §4.3 offline path the SQLite buffer already covers.</para>
/// <para>Remote paths are the same forward-slash, leading-slash form Dropbox uses, so the agent,
/// uploader and aggregator are unchanged. <see cref="ListFilesAsync"/> in particular returns
/// <em>remote</em> paths, not Windows paths, because callers feed them straight back into
/// <see cref="DownloadTextAsync"/> and pick them apart with <see cref="RemotePath.Parent"/>.</para>
/// </remarks>
public sealed class FileSystemRemoteStorage : IRemoteStorage
{
    /// <summary>
    /// Suffix for a partially-written upload. Filtered out of listings so a concurrent aggregator
    /// run never tries to parse a chunk that is still being written.
    /// </summary>
    private const string TempSuffix = ".uploadtmp";

    private readonly string _root;
    private readonly TrackerLog _log;

    public FileSystemRemoteStorage(string root, TrackerLog? log = null)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A storage root is required.", nameof(root));
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _log = log ?? TrackerLog.Null;
    }

    public string Root => _root;

    /// <summary>
    /// Creates the root if it is missing and fails loudly if it cannot be reached, so a wrong or
    /// unavailable share surfaces at startup instead of looking like a fleet that never reports.
    /// </summary>
    public void EnsureReachable()
    {
        try
        {
            Directory.CreateDirectory(_root);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The storage root '{_root}' is not reachable. Check the path, the share and this account's rights to it.",
                ex);
        }
    }

    public async Task<byte[]?> DownloadAsync(string path, CancellationToken ct = default)
    {
        var local = ToLocal(path);

        try
        {
            return File.Exists(local)
                ? await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between the check and the read — a rollover running concurrently.
            return null;
        }
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
        var local = ToLocal(path);

        var directory = Path.GetDirectoryName(local);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write beside the target and swap. A reader never observes a half-written chunk, and an
        // interrupted upload cannot damage what was already there — the same property that makes
        // the chunk layout safe against a dropped connection (§4.3).
        var temp = local + "." + Guid.NewGuid().ToString("N")[..8] + TempSuffix;

        try
        {
            await File.WriteAllBytesAsync(temp, content, ct).ConfigureAwait(false);
            File.Move(temp, local, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public Task<IReadOnlyList<RemoteFile>> ListFilesAsync(string folder, CancellationToken ct = default)
    {
        var local = ToLocal(folder);

        if (!Directory.Exists(local))
        {
            return Task.FromResult<IReadOnlyList<RemoteFile>>(Array.Empty<RemoteFile>());
        }

        // IgnoreInaccessible so one locked-down subfolder cannot abort the whole fleet's listing.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var files = new List<RemoteFile>();

        foreach (var entry in Directory.EnumerateFiles(local, "*", options))
        {
            ct.ThrowIfCancellationRequested();

            if (entry.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(entry);
                files.Add(new RemoteFile(ToRemote(entry), info.Length, info.LastWriteTimeUtc));
            }
            catch (FileNotFoundException)
            {
                // Vanished mid-enumeration; nothing to report.
            }
        }

        return Task.FromResult<IReadOnlyList<RemoteFile>>(files);
    }

    public Task CopyAsync(string fromPath, string toPath, CancellationToken ct = default)
    {
        var source = ToLocal(fromPath);
        var destination = ToLocal(toPath);

        EnsureParent(destination);
        File.Copy(source, destination, overwrite: true);

        return Task.CompletedTask;
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct = default)
    {
        var source = ToLocal(fromPath);
        var destination = ToLocal(toPath);

        EnsureParent(destination);
        File.Move(source, destination, overwrite: true);

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var local = ToLocal(path);

        // Deleting is idempotent by intent, matching the Dropbox implementation.
        if (File.Exists(local))
        {
            File.Delete(local);
        }
        else if (Directory.Exists(local))
        {
            Directory.Delete(local, recursive: true);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var local = ToLocal(path);

        // Dropbox's GetMetadata answers for files and folders alike, so this does too.
        return Task.FromResult(File.Exists(local) || Directory.Exists(local));
    }

    private static void EnsureParent(string localPath)
    {
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void TryDelete(string localPath)
    {
        try
        {
            if (File.Exists(localPath))
            {
                File.Delete(localPath);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not remove the temporary upload file '{localPath}': {ex.Message}");
        }
    }

    /// <summary>Maps a remote path such as <c>/logs/PC-014/x.jsonl</c> onto a path under the root.</summary>
    private string ToLocal(string remotePath)
    {
        var normalized = RemotePath.Normalize(remotePath);

        if (normalized.Length == 0)
        {
            return _root;
        }

        var relative = normalized.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_root, relative));

        // A path that climbs out of the root would let a bad config.json read or overwrite
        // arbitrary files as whatever account the agent runs under. Refuse rather than clamp.
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"The path '{remotePath}' resolves outside the storage root '{_root}'.");
        }

        return full;
    }

    /// <summary>The inverse of <see cref="ToLocal"/>: callers round-trip listed paths back in.</summary>
    private string ToRemote(string localPath)
    {
        var relative = Path.GetRelativePath(_root, Path.GetFullPath(localPath))
            .Replace(Path.DirectorySeparatorChar, '/');

        return RemotePath.Normalize(relative);
    }
}
