using System.Text.Json.Serialization;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;
using FileTimeTracker.Core.Tracking;

namespace FileTimeTracker.Agent.Runtime;

/// <summary>
/// Liveness record required by design.md §6, written so that "the agent stopped" and "the employee
/// had a quiet afternoon" cannot be confused in the reports.
/// </summary>
public sealed class HeartbeatDocument
{
    [JsonPropertyName("computer_no")]
    public required string ComputerNo { get; init; }

    [JsonPropertyName("win_user")]
    public required string WinUser { get; init; }

    [JsonPropertyName("machine_name")]
    public required string MachineName { get; init; }

    [JsonPropertyName("agent_version")]
    public required string AgentVersion { get; init; }

    [JsonPropertyName("started_utc")]
    public required DateTimeOffset StartedUtc { get; init; }

    [JsonPropertyName("updated_utc")]
    public required DateTimeOffset UpdatedUtc { get; init; }

    [JsonPropertyName("session_state")]
    public required string SessionState { get; init; }

    [JsonPropertyName("tracking")]
    public required bool Tracking { get; init; }

    [JsonPropertyName("pending_intervals")]
    public required int PendingIntervals { get; init; }

    [JsonPropertyName("last_upload_utc")]
    public DateTimeOffset? LastUploadUtc { get; init; }
}

/// <summary>
/// Publishes the heartbeat locally for the watchdog and remotely for the admin.
/// </summary>
/// <remarks>
/// The local file is the watchdog's restart trigger, so it is written on every tick even when the
/// network is down. The remote copy rides along with the upload cycle and is what makes a laptop
/// that has been off for three days visible as "off" rather than "idle".
/// </remarks>
public sealed class HeartbeatWriter
{
    private readonly string _localPath;
    private readonly IRemoteStorage? _remote;
    private readonly TrackerLog _log;

    public HeartbeatWriter(string localPath, IRemoteStorage? remote, TrackerLog log)
    {
        _localPath = localPath;
        _remote = remote;
        _log = log;
    }

    public static string RemotePathFor(string logsRoot, string computerNo) =>
        RemotePath.Combine(logsRoot, "_heartbeat", RemotePath.SafeSegment(computerNo) + ".json");

    public void WriteLocal(HeartbeatDocument document)
    {
        try
        {
            var dir = Path.GetDirectoryName(_localPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = _localPath + ".tmp";
            File.WriteAllText(temp, TrackerJson.SerializePretty(document));
            File.Move(temp, _localPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not write the heartbeat to '{_localPath}'.", ex);
        }
    }

    public async Task PublishAsync(HeartbeatDocument document, string logsRoot, CancellationToken ct)
    {
        if (_remote is null)
        {
            return;
        }

        try
        {
            await _remote
                .UploadTextAsync(RemotePathFor(logsRoot, document.ComputerNo), TrackerJson.SerializePretty(document), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"Heartbeat publish failed (offline?): {ex.Message}");
        }
    }

    public static HeartbeatDocument Build(
        string computerNo,
        string winUser,
        DateTimeOffset startedUtc,
        SessionState session,
        bool tracking,
        int pending,
        DateTimeOffset? lastUpload) => new()
    {
        ComputerNo = computerNo,
        WinUser = winUser,
        MachineName = Environment.MachineName,
        AgentVersion = typeof(HeartbeatWriter).Assembly.GetName().Version?.ToString() ?? "unknown",
        StartedUtc = startedUtc,
        UpdatedUtc = DateTimeOffset.UtcNow,
        SessionState = session.ToString(),
        Tracking = tracking,
        PendingIntervals = pending,
        LastUploadUtc = lastUpload,
    };
}
