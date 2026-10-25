using System.Text.Json.Serialization;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Aggregator.Reporting;

/// <summary>Aggregator-side view of the agent heartbeat (design.md §6).</summary>
internal sealed class RemoteHeartbeat
{
    [JsonPropertyName("computer_no")]
    public string ComputerNo { get; set; } = string.Empty;

    [JsonPropertyName("updated_utc")]
    public DateTimeOffset UpdatedUtc { get; set; }

    [JsonPropertyName("session_state")]
    public string SessionState { get; set; } = string.Empty;

    [JsonPropertyName("pending_intervals")]
    public int PendingIntervals { get; set; }
}

/// <summary>
/// Collects agent heartbeats so the workbook can distinguish "no work recorded" from "agent not
/// running", which is the whole point of the §6 heartbeat.
/// </summary>
public sealed class HeartbeatReader
{
    private const string HeartbeatFolder = "_heartbeat";

    private readonly IRemoteStorage _remote;
    private readonly TrackerLog _log;

    public HeartbeatReader(IRemoteStorage remote, TrackerLog log)
    {
        _remote = remote;
        _log = log;
    }

    public async Task<IReadOnlyList<MachineStatus>> ReadAsync(
        string logsRoot,
        IReadOnlyDictionary<string, string> employeeMap,
        CancellationToken ct = default)
    {
        var folder = RemotePath.Combine(logsRoot, HeartbeatFolder);
        var statuses = new List<MachineStatus>();

        IReadOnlyList<RemoteFile> files;
        try
        {
            files = await _remote.ListFilesAsync(folder, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not list heartbeats under '{folder}'.", ex);
            return statuses;
        }

        foreach (var file in files.Where(f => f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var json = await _remote.DownloadTextAsync(file.Path, ct).ConfigureAwait(false);
                if (json is null)
                {
                    continue;
                }

                var beat = TrackerJson.Deserialize<RemoteHeartbeat>(json);
                if (beat is null || string.IsNullOrWhiteSpace(beat.ComputerNo))
                {
                    continue;
                }

                statuses.Add(new MachineStatus(
                    beat.ComputerNo,
                    employeeMap.GetValueOrDefault(beat.ComputerNo, string.Empty),
                    beat.UpdatedUtc,
                    beat.SessionState,
                    beat.PendingIntervals));
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not read heartbeat '{file.Path}'.", ex);
            }
        }

        // Machines listed in the employee map that have never checked in are the interesting case:
        // they are silently missing from every report unless surfaced explicitly.
        foreach (var (computerNo, employee) in employeeMap)
        {
            if (!statuses.Any(s => string.Equals(s.ComputerNo, computerNo, StringComparison.OrdinalIgnoreCase)))
            {
                statuses.Add(new MachineStatus(computerNo, employee, null, "never reported", 0));
            }
        }

        return statuses;
    }
}
