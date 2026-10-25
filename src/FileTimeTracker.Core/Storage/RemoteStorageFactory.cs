using FileTimeTracker.Core.Diagnostics;

namespace FileTimeTracker.Core.Storage;

/// <summary>
/// Chooses the <see cref="IRemoteStorage"/> backend named in per-machine settings, so the agent and
/// the aggregator resolve it the same way.
/// </summary>
/// <remarks>
/// Dropbox (§4.3) stays the default; <c>filesystem</c> targets a folder or UNC share for
/// deployments that would rather not hold a cloud credential at all. The choice is per-machine
/// rather than central because an agent must be able to reach storage before it can read the
/// central config (§4.4) — bootstrapping it from that config would be circular.
/// </remarks>
public static class RemoteStorageFactory
{
    public const string Dropbox = "dropbox";

    public const string FileSystem = "filesystem";

    public static bool IsFileSystem(string? backend) =>
        string.Equals(backend?.Trim(), FileSystem, StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates <paramref name="backend"/>, returning the canonical name.</summary>
    public static string Canonicalize(string? backend)
    {
        var value = backend?.Trim();

        if (string.IsNullOrEmpty(value) || string.Equals(value, Dropbox, StringComparison.OrdinalIgnoreCase))
        {
            return Dropbox;
        }

        if (IsFileSystem(value))
        {
            return FileSystem;
        }

        throw new ArgumentException($"Unknown storage backend '{backend}'. Use '{Dropbox}' or '{FileSystem}'.", nameof(backend));
    }

    /// <summary>
    /// Builds the filesystem backend and proves the root is reachable, so a mistyped path or an
    /// unavailable share fails at startup rather than looking like a machine that never reports.
    /// </summary>
    public static FileSystemRemoteStorage CreateFileSystem(string? root, TrackerLog log)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                $"storage_backend is '{FileSystem}' but storage_root is empty. " +
                "Re-run provision with --storage-root <folder or UNC path>.");
        }

        var storage = new FileSystemRemoteStorage(root, log);
        storage.EnsureReachable();

        log.Info($"Storage backend '{FileSystem}' at '{storage.Root}'. No cloud credential is used.");

        return storage;
    }
}
