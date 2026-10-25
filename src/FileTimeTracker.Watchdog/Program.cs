using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Serialization;

namespace FileTimeTracker.Watchdog;

/// <summary>
/// Verifies the agent is alive and restarts it if not (design.md §6). Run by a scheduled task
/// every few minutes; each invocation performs one check and exits.
/// </summary>
/// <remarks>
/// Checks liveness two ways. A missing process is the obvious case. The subtler and more damaging
/// one is a process that is still running but has stopped sampling — a wedged UI Automation call,
/// a deadlock — which looks exactly like a quiet workday in the reports. A heartbeat that has
/// stopped advancing is treated as a failure and the process is replaced.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string AgentProcessName = "FileTimeTracker.Agent";
    private const string AgentExecutable = "FileTimeTracker.Agent.exe";

    /// <summary>Agent writes every 60s; allow generous slack before calling it dead.</summary>
    private static readonly TimeSpan HeartbeatGrace = TimeSpan.FromMinutes(5);

    /// <summary>Longer bar for killing a live process — restarting costs the open interval.</summary>
    private static readonly TimeSpan WedgedThreshold = TimeSpan.FromMinutes(12);

    private static int Main()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "FileTimeTracker",
            "data");

        using var log = new TrackerLog(Path.Combine(dataDirectory, "watchdog.log"));
        var statePath = Path.Combine(dataDirectory, "watchdog.state.json");

        try
        {
            return Check(dataDirectory, statePath, log);
        }
        catch (Exception ex)
        {
            log.Error("Watchdog check failed.", ex);
            return 1;
        }
    }

    private static int Check(string dataDirectory, string statePath, TrackerLog log)
    {
        var now = DateTimeOffset.UtcNow;
        var agent = FindAgentInThisSession();
        var heartbeatAge = ReadHeartbeatAge(Path.Combine(dataDirectory, "heartbeat.json"), now);

        if (agent is null)
        {
            log.Warn("Agent is not running in this session.");
            return Restart(statePath, log, now, "process missing");
        }

        using (agent)
        {
            if (heartbeatAge is null)
            {
                // Running but has never written a heartbeat: it may still be starting up.
                log.Info($"Agent (pid {agent.Id}) is running; no heartbeat yet.");
                return 0;
            }

            if (heartbeatAge <= HeartbeatGrace)
            {
                log.Debug($"Agent (pid {agent.Id}) is healthy; heartbeat {heartbeatAge.Value.TotalSeconds:0}s old.");
                return 0;
            }

            if (heartbeatAge <= WedgedThreshold)
            {
                log.Warn($"Agent (pid {agent.Id}) heartbeat is {heartbeatAge.Value.TotalMinutes:0.0} min old; watching.");
                return 0;
            }

            log.Error(
                $"Agent (pid {agent.Id}) is running but its heartbeat is {heartbeatAge.Value.TotalMinutes:0.0} min old. " +
                "Treating it as wedged and replacing it.");

            TryKill(agent, log);
        }

        return Restart(statePath, log, now, "heartbeat stalled");
    }

    private static Process? FindAgentInThisSession()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;

        foreach (var process in Process.GetProcessesByName(AgentProcessName))
        {
            try
            {
                if (process.SessionId == sessionId && !process.HasExited)
                {
                    return process;
                }
            }
            catch (InvalidOperationException)
            {
                // Exited while we looked at it.
            }

            process.Dispose();
        }

        return null;
    }

    private static TimeSpan? ReadHeartbeatAge(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var beat = TrackerJson.Deserialize<HeartbeatSnapshot>(File.ReadAllText(path));
            return beat is null || beat.UpdatedUtc == default ? null : now - beat.UpdatedUtc;
        }
        catch
        {
            // Mid-write or malformed: treat as "unknown" rather than as a failure.
            return null;
        }
    }

    private static void TryKill(Process process, TrackerLog log)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
            log.Info($"Terminated wedged agent (pid {process.Id}).");
        }
        catch (Exception ex)
        {
            log.Error($"Could not terminate the wedged agent (pid {process.Id}).", ex);
        }
    }

    private static int Restart(string statePath, TrackerLog log, DateTimeOffset now, string reason)
    {
        var state = WatchdogState.Load(statePath);

        if (state.IsInBackoff(now))
        {
            log.Error(
                $"Agent has been restarted {state.RecentRestartsUtc.Count} time(s) in the last hour and is still failing. " +
                "Backing off; check agent.log for the underlying fault.");
            state.Save(statePath);
            return 2;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, AgentExecutable);
        if (!File.Exists(exe))
        {
            log.Error($"Cannot restart: '{exe}' does not exist.");
            return 3;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            state.RecentRestartsUtc.Add(now);
            state.Save(statePath);

            log.Info($"Restarted the agent ({reason}).");
            return 0;
        }
        catch (Exception ex)
        {
            log.Error($"Could not start '{exe}'.", ex);
            return 4;
        }
    }

    /// <summary>Only the field the watchdog needs; the agent owns the full document.</summary>
    private sealed class HeartbeatSnapshot
    {
        [JsonPropertyName("updated_utc")]
        public DateTimeOffset UpdatedUtc { get; set; }
    }
}
