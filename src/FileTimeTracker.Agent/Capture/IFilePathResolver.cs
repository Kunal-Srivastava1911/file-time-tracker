namespace FileTimeTracker.Agent.Capture;

/// <summary>
/// Best-effort full-path resolution for the foreground document (design.md §4.1). Implementations
/// must never block the sampling loop: returning null now and the real value on a later tick is
/// expected and correct.
/// </summary>
public interface IFilePathResolver : IDisposable
{
    /// <summary>
    /// Returns a cached path for this window if one is known, otherwise null — and may schedule
    /// resolution in the background so a subsequent call succeeds.
    /// </summary>
    string? TryGet(IntPtr hwnd, string processName, string windowTitle);
}

/// <summary>Used when <c>capture_file_path</c> is off (§2) or UI Automation is unavailable.</summary>
public sealed class NullFilePathResolver : IFilePathResolver
{
    public static NullFilePathResolver Instance { get; } = new();

    public string? TryGet(IntPtr hwnd, string processName, string windowTitle) => null;

    public void Dispose()
    {
    }
}
