using System.Text.Json.Serialization;
using FileTimeTracker.Core.Serialization;

namespace FileTimeTracker.Watchdog;

/// <summary>
/// Restart history, persisted because the watchdog is a scheduled task that exits between checks
/// and therefore has no memory of its own.
/// </summary>
public sealed class WatchdogState
{
    [JsonPropertyName("recent_restarts_utc")]
    public List<DateTimeOffset> RecentRestartsUtc { get; set; } = new();

    /// <summary>
    /// Restarts allowed in <see cref="BackoffWindow"/> before the watchdog stops trying. A crash
    /// loop restarted every few minutes forever would fill the log and hide the real fault; after
    /// the limit it records the problem and waits for the window to clear.
    /// </summary>
    public const int MaxRestartsPerWindow = 5;

    public static readonly TimeSpan BackoffWindow = TimeSpan.FromHours(1);

    public void Prune(DateTimeOffset now) =>
        RecentRestartsUtc.RemoveAll(t => now - t > BackoffWindow);

    public bool IsInBackoff(DateTimeOffset now)
    {
        Prune(now);
        return RecentRestartsUtc.Count >= MaxRestartsPerWindow;
    }

    public static WatchdogState Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? TrackerJson.Deserialize<WatchdogState>(File.ReadAllText(path)) ?? new WatchdogState()
                : new WatchdogState();
        }
        catch
        {
            // A corrupt state file must not stop the watchdog doing its job.
            return new WatchdogState();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, TrackerJson.SerializePretty(this));
        }
        catch
        {
            // Losing history only costs a slightly more eager backoff.
        }
    }
}
