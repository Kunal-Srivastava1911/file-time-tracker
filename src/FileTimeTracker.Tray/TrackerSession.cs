using System.Runtime.Versioning;
using FileTimeTracker.Agent.Configuration;
using FileTimeTracker.Agent.Runtime;
using FileTimeTracker.Aggregator.Reporting;
using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Security;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Tray;

/// <summary>What the Stop button produced, for the UI to report.</summary>
public sealed record StopResult
{
    /// <summary>Where the workbook was filed, for the log. Always set.</summary>
    /// <remarks>
    /// There is no companion "local path" property any more. Stopping files the spreadsheet and
    /// says nothing, so nothing needs to know whether the path is openable on this machine.
    /// </remarks>
    public required string SessionLocation { get; init; }

    /// <summary>Intervals captured in this session.</summary>
    public required int Intervals { get; init; }

    /// <summary>The span this session covered, in local time, for the confirmation dialog.</summary>
    public string RangeText { get; init; } = string.Empty;

    public required TimeSpan Elapsed { get; init; }

    public required TimeSpan Active { get; init; }

    public string? Error { get; init; }

    public bool Succeeded => Error is null;
}

/// <summary>
/// One tracking session: start sampling, stop on demand, then build the workbook into the user's
/// Dropbox folder.
/// </summary>
/// <remarks>
/// Deliberately a thin shell over <see cref="AgentHost"/> and <see cref="AggregationRunner"/>. The
/// timing, buffering and reporting are the same code the fleet agent runs, so this front end cannot
/// drift from it — the only difference is that a person decides when it starts and stops.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrackerSession : IDisposable
{
    private readonly MachineSettings _settings;
    private readonly TrackerLog _log;

    /// <summary>
    /// This session's intervals, captured as they close. Held here rather than re-read from the
    /// uploaded logs so the session report covers exactly this run — the logs also contain earlier
    /// sessions and anything that was still buffered from a previous one.
    /// </summary>
    private readonly List<IntervalRecord> _captured = new();

    private readonly object _capturedGate = new();

    private AgentHost? _host;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public TrackerSession(MachineSettings settings, TrackerLog log)
    {
        _settings = settings;
        _log = log;
    }

    /// <summary>Raised on the sampling thread each time an interval closes.</summary>
    public event Action<IntervalRecord>? IntervalClosed;

    public DateTimeOffset StartedUtc { get; private set; }

    public TimeSpan Elapsed => StartedUtc == default ? TimeSpan.Zero : DateTimeOffset.UtcNow - StartedUtc;

    public string? CurrentDocument => _host?.CurrentDocument;

    public int ClosedIntervals => _host?.ClosedIntervals ?? 0;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        StartedUtc = DateTimeOffset.UtcNow;

        _cts = new CancellationTokenSource();
        _host = new AgentHost(_settings, _log, console: false);
        _host.IntervalClosed += OnIntervalClosed;

        _runTask = Task.Run(() => _host.RunAsync(_cts.Token));
    }

    private void OnIntervalClosed(IntervalRecord record)
    {
        lock (_capturedGate)
        {
            _captured.Add(record);
        }

        IntervalClosed?.Invoke(record);
    }

    /// <summary>
    /// Stops sampling and produces the workbook. Cancelling the host is what triggers its flush of
    /// the open interval and the final drain of the buffer, so the last few minutes of work are in
    /// the report rather than stranded locally.
    /// </summary>
    public async Task<StopResult> StopAndReportAsync()
    {
        var elapsed = Elapsed;

        try
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }

            if (_runTask is not null)
            {
                await _runTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: this is how the host is asked to shut down.
        }
        catch (Exception ex)
        {
            _log.Error("The tracking host did not stop cleanly; the report may be missing the last interval.", ex);
        }

        if (_host is not null)
        {
            _host.IntervalClosed -= OnIntervalClosed;
        }

        IntervalRecord[] captured;
        lock (_capturedGate)
        {
            captured = _captured.ToArray();
        }

        try
        {
            return await BuildReportAsync(captured, elapsed).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Could not build the session workbook.", ex);

            return new StopResult
            {
                SessionLocation = string.Empty,
                Intervals = captured.Length,
                Elapsed = elapsed,
                Active = TimeSpan.FromSeconds(captured.Sum(i => i.ActiveSec)),
                Error = ex.Message,
            };
        }
    }

    private async Task<StopResult> BuildReportAsync(IReadOnlyList<IntervalRecord> captured, TimeSpan elapsed)
    {
        using var owned = CreateStorage();
        var storage = owned.Storage;

        var config = await LoadConfigAsync(storage).ConfigureAwait(false);
        var zone = _settings.ResolveTimeZone(_log);

        config.ComputerEmployeeMap.TryGetValue(_settings.ComputerNo, out var employee);

        var metadata = new SessionMetadata
        {
            ComputerNo = _settings.ComputerNo,
            Employee = employee ?? string.Empty,
            StartedUtc = StartedUtc,
            EndedUtc = DateTimeOffset.UtcNow,
            TimeZone = zone,
        };

        var sessionBytes = SessionWorkbookWriter.Build(captured, metadata);

        // The report goes to the publish folder when one is configured, and the working store keeps
        // the logs. With no publish folder this is the fleet arrangement: both in the app folder.
        using var published = CreatePublishStorage();
        var destination = published?.Storage ?? storage;
        var sessionPath = SessionWorkbookWriter.PathFor(metadata, published is null ? "/sessions" : "/");

        await destination.UploadAsync(sessionPath, sessionBytes).ConfigureAwait(false);
        _log.Info($"Wrote session workbook to '{sessionPath}' ({captured.Count} capture(s)).");

        var startedLocal = TimeZoneInfo.ConvertTime(metadata.StartedUtc, zone);
        var endedLocal = TimeZoneInfo.ConvertTime(metadata.EndedUtc, zone);

        // Only the filesystem backend produces something this machine can open; a Dropbox upload
        // lives in the admin's account and was never a local file.
        var local = destination is FileSystemRemoteStorage files ? ToLocal(files, sessionPath) : string.Empty;

        return new StopResult
        {
            SessionLocation = local.Length > 0 ? local : $"Dropbox (admin){sessionPath}",
            Intervals = captured.Count,
            Elapsed = elapsed,
            Active = TimeSpan.FromSeconds(captured.Sum(i => i.ActiveSec)),
            RangeText = $"{startedLocal:yyyy-MM-dd HH:mm:ss}  to  {endedLocal:HH:mm:ss}",
        };
    }

    /// <summary>Remote paths are storage-relative; the UI needs a real path it can open.</summary>
    private static string ToLocal(FileSystemRemoteStorage storage, string remotePath) =>
        Path.Combine(storage.Root, remotePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Builds the configured backend. Dropbox when the installer provisioned a credential — the
    /// fleet case, where every laptop writes into the one admin app folder — and otherwise a local
    /// folder, which is the single-user case.
    /// </summary>
    private OwnedStorage CreateStorage()
    {
        if (RemoteStorageFactory.IsFileSystem(_settings.StorageBackend))
        {
            return new OwnedStorage(RemoteStorageFactory.CreateFileSystem(_settings.StorageRoot, _log));
        }

        var credentials = new DpapiSecretStore(AgentPaths.CredentialFile).Load()
            ?? throw new InvalidOperationException(
                "This machine is set to use Dropbox but no credential is provisioned. " +
                "Re-run the installer, or switch to a local folder.");

        return new OwnedStorage(new DropboxRemoteStorage(credentials, "FileTimeTracker.Tray/1.0", _log));
    }

    /// <summary>
    /// The folder finished spreadsheets are filed into, or null when they belong in the working
    /// store alongside the logs — the fleet case, where the admin's app folder is the destination.
    /// </summary>
    private OwnedStorage? CreatePublishStorage()
    {
        if (!_settings.PublishesSeparately)
        {
            return null;
        }

        return new OwnedStorage(RemoteStorageFactory.CreateFileSystem(_settings.PublishRoot, _log));
    }

    /// <summary>Disposes the backend only when it owns something, keeping the call site uncluttered.</summary>
    private sealed class OwnedStorage : IDisposable
    {
        public OwnedStorage(IRemoteStorage storage) => Storage = storage;

        public IRemoteStorage Storage { get; }

        public void Dispose() => (Storage as IDisposable)?.Dispose();
    }

    private async Task<TrackerConfig> LoadConfigAsync(IRemoteStorage storage)
    {
        var json = await storage.DownloadTextAsync(_settings.ConfigPath).ConfigureAwait(false);

        if (json is null)
        {
            return new TrackerConfig().Sanitized();
        }

        try
        {
            // A BOM left by a text editor would otherwise make this throw and silently cost the
            // user their employee-name mapping.
            var config = TrackerJson.Deserialize<TrackerConfig>(json.TrimStart('﻿'));
            return (config ?? new TrackerConfig()).Sanitized();
        }
        catch (System.Text.Json.JsonException ex)
        {
            _log.Warn($"'{_settings.ConfigPath}' is not valid JSON; using defaults for this report.", ex);
            return new TrackerConfig().Sanitized();
        }
    }

    public void Dispose()
    {
        if (_host is not null)
        {
            _host.IntervalClosed -= OnIntervalClosed;
            _host.Dispose();
        }

        _cts?.Dispose();
    }
}
