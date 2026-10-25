using System.Runtime.Versioning;
using FileTimeTracker.Agent.Capture;
using FileTimeTracker.Agent.Configuration;
using FileTimeTracker.Agent.Interop;
using FileTimeTracker.Agent.Upload;
using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Parsing;
using FileTimeTracker.Core.Privacy;
using FileTimeTracker.Core.Security;
using FileTimeTracker.Core.Storage;
using FileTimeTracker.Core.Tracking;

namespace FileTimeTracker.Agent.Runtime;

/// <summary>
/// Wires the agent together and runs it: sample, accumulate, buffer, upload, heartbeat
/// (design.md §4.1–§4.4).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentHost : IDisposable
{
    /// <summary>Uploaded rows are kept this long for auditing before the buffer reclaims them.</summary>
    private static readonly TimeSpan UploadedRetention = TimeSpan.FromDays(7);

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>Publish the remote heartbeat every Nth local write.</summary>
    private const int RemoteHeartbeatEvery = 5;

    private readonly MachineSettings _settings;
    private readonly TrackerLog _log;
    private readonly bool _console;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    private readonly object _accumulatorGate = new();

    private SqliteIntervalStore _store = null!;
    private IRemoteStorage? _remote;
    private ConfigProvider _config = null!;
    private FieldProtector _protector = null!;
    private IntervalAccumulator _accumulator = null!;
    private SessionStateMonitor _session = null!;
    private ForegroundResolver _foreground = null!;
    private IFilePathResolver _paths = NullFilePathResolver.Instance;
    private LogUploader? _uploader;
    private HeartbeatWriter _heartbeat = null!;

    private IReadOnlySet<string> _excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private volatile bool _uploadRequested;

    public AgentHost(MachineSettings settings, TrackerLog log, bool console)
    {
        _settings = settings;
        _log = log;
        _console = console;
    }

    /// <summary>
    /// Raised on the sampling thread as each interval closes, so a UI can show live progress.
    /// Handlers must marshal to their own thread and must not throw.
    /// </summary>
    public event Action<IntervalRecord>? IntervalClosed;

    /// <summary>
    /// What the foreground window is showing as of the last sample, for display only. Null when
    /// nothing is counted right now — locked, no foreground window, or an excluded process (§3).
    /// </summary>
    public string? CurrentDocument { get; private set; }

    /// <summary>Intervals closed since this host started. Display only.</summary>
    public int ClosedIntervals => _closedIntervals;

    private int _closedIntervals;

    public async Task RunAsync(CancellationToken ct)
    {
        Initialize();

        await _config.StartAsync(ct).ConfigureAwait(false);
        ApplyConfig(_config.Current);
        _config.Changed += ApplyConfig;

        _log.Info(
            $"Agent started. Computer No. '{_settings.ComputerNo}', user '{Environment.UserName}', " +
            $"time zone '{_accumulator.Options.TimeZone.Id}', buffer '{AgentPaths.DatabaseFile}'.");

        if (_remote is null)
        {
            _log.Warn(
                RemoteStorageFactory.IsFileSystem(_settings.StorageBackend)
                    ? "Filesystem storage is unavailable. Tracking locally only; nothing will be uploaded."
                    : "No Dropbox credentials are provisioned. Tracking locally only; nothing will be uploaded.");
        }

        if (_config.Current.ShowFirstRunNotice)
        {
            FirstRunNotice.ShowOnce(_log);
        }

        var tasks = new List<Task>
        {
            SampleLoopAsync(ct),
            UploadLoopAsync(ct),
            HeartbeatLoopAsync(ct),
        };

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
    }

    private void Initialize()
    {
        AgentPaths.EnsureDataDirectory();

        _store = new SqliteIntervalStore(AgentPaths.DatabaseFile);
        _remote = CreateRemote();
        _config = new ConfigProvider(_remote, _settings.ConfigPath, AgentPaths.ConfigCacheFile, _log);
        _protector = new FieldProtector(_settings.HashSalt);

        _accumulator = new IntervalAccumulator(new IntervalAccumulatorOptions
        {
            ComputerNo = _settings.ComputerNo,
            WinUser = Environment.UserName,
            TimeZone = _settings.ResolveTimeZone(_log),
        });

        _session = new SessionStateMonitor(_log);
        _session.Interrupting += OnInterrupting;

        _foreground = new ForegroundResolver(new TitleParser(), _log);
        _paths = new UiaFilePathResolver(_log);
        _heartbeat = new HeartbeatWriter(AgentPaths.HeartbeatFile, _remote, _log);

        if (_remote is not null)
        {
            _uploader = new LogUploader(_remote, _store, _settings.ComputerNo, _log);
        }
    }

    private IRemoteStorage? CreateRemote()
    {
        try
        {
            if (RemoteStorageFactory.IsFileSystem(_settings.StorageBackend))
            {
                return RemoteStorageFactory.CreateFileSystem(_settings.StorageRoot, _log);
            }

            var credentials = new DpapiSecretStore(AgentPaths.CredentialFile).Load();
            if (credentials is null)
            {
                return null;
            }

            return new DropboxRemoteStorage(credentials, "FileTimeTracker.Agent/1.0", _log);
        }
        catch (Exception ex)
        {
            // Keep sampling: local buffering still preserves the data until storage is repaired.
            _log.Error("Could not initialise remote storage; continuing with local buffering only.", ex);
            return null;
        }
    }

    private void ApplyConfig(TrackerConfig config)
    {
        lock (_accumulatorGate)
        {
            _accumulator.Options.IdleThreshold = config.IdleThreshold;

            // Tolerate a couple of missed ticks before declaring a gap; below ~30s the guard would
            // fire on ordinary scheduler jitter and shred intervals for no reason.
            var gap = TimeSpan.FromSeconds(Math.Max(30, config.SampleIntervalSec * 3));
            _accumulator.Options.MaxSampleGap = gap;
        }

        _excluded = new HashSet<string>(
            config.ExcludedProcesses.Select(p =>
                p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var config = _config.Current;

            try
            {
                Tick(config);
            }
            catch (Exception ex)
            {
                // One bad tick must never end the day's tracking.
                _log.Error("Sampling tick failed.", ex);
            }

            await Task.Delay(config.SampleInterval, ct).ConfigureAwait(false);
        }
    }

    private void Tick(TrackerConfig config)
    {
        var session = _session.Current;

        FocusSnapshot? focus = null;
        if (session == SessionState.Unlocked)
        {
            var resolver = config.CaptureFilePath ? _paths : NullFilePathResolver.Instance;
            focus = _foreground.Resolve(resolver, _excluded, config.CaptureWindowTitle);
        }

        CurrentDocument = focus?.File;

        var sample = new Sample(DateTimeOffset.UtcNow, focus, NativeMethods.GetIdleTime(), session);

        IReadOnlyList<IntervalRecord> completed;
        lock (_accumulatorGate)
        {
            completed = _accumulator.Observe(sample);
        }

        Persist(completed, config);
    }

    private void Persist(IReadOnlyList<IntervalRecord> completed, TrackerConfig config)
    {
        if (completed.Count == 0)
        {
            return;
        }

        // Privacy gating happens before the row ever touches disk (§2).
        var protectedRows = completed.Select(r => _protector.Apply(r, config)).ToList();
        _store.Add(protectedRows);

        Interlocked.Add(ref _closedIntervals, protectedRows.Count);

        if (IntervalClosed is { } handler)
        {
            foreach (var row in protectedRows)
            {
                try
                {
                    handler(row);
                }
                catch (Exception ex)
                {
                    // A broken UI handler must never stop the day's tracking.
                    _log.Warn("An IntervalClosed handler threw; continuing.", ex);
                }
            }
        }

        if (!_console)
        {
            return;
        }

        foreach (var row in protectedRows)
        {
            Console.WriteLine(
                $"{row.LocalDate:yyyy-MM-dd}  {row.StartUtc.ToLocalTime():HH:mm:ss}-{row.EndUtc.ToLocalTime():HH:mm:ss}  " +
                $"{row.App,-18} {Truncate(row.File, 48),-48} active={row.ActiveSec,5}s idle={row.IdleSec,5}s  [{row.Source}]");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private async Task UploadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var config = _config.Current;

            try
            {
                await Task.Delay(config.UploadInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await DrainAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        if (_uploader is null)
        {
            return;
        }

        _uploadRequested = false;
        var config = _config.Current;

        try
        {
            await _uploader.UploadPendingAsync(config, ct).ConfigureAwait(false);

            var purged = _store.PurgeUploaded(DateTimeOffset.UtcNow - UploadedRetention);
            if (purged > 0)
            {
                _log.Debug($"Purged {purged} uploaded interval(s) from the local buffer.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Upload cycle failed; intervals remain buffered.", ex);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var tick = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var document = BuildHeartbeat();
                _heartbeat.WriteLocal(document);

                if (tick % RemoteHeartbeatEvery == 0)
                {
                    await _heartbeat.PublishAsync(document, _config.Current.LogsPath, ct).ConfigureAwait(false);
                }

                // An interrupt (lock, suspend) asks for an out-of-band drain so the last interval
                // is not stranded on a laptop that is about to close.
                if (_uploadRequested)
                {
                    await DrainAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("Heartbeat tick failed.", ex);
            }

            tick++;

            try
            {
                await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private HeartbeatDocument BuildHeartbeat()
    {
        bool tracking;
        lock (_accumulatorGate)
        {
            tracking = _accumulator.HasOpenInterval;
        }

        return HeartbeatWriter.Build(
            _settings.ComputerNo,
            Environment.UserName,
            _startedUtc,
            _session.Current,
            tracking,
            _store.PendingCount(),
            _uploader?.LastSuccessfulUploadUtc);
    }

    /// <summary>Lock, suspend or sign-out: close the open interval now, while we still can.</summary>
    private void OnInterrupting(string reason)
    {
        try
        {
            FlushAccumulator();
            _uploadRequested = true;
        }
        catch (Exception ex)
        {
            _log.Error($"Flush on '{reason}' failed.", ex);
        }
    }

    private void FlushAccumulator()
    {
        IReadOnlyList<IntervalRecord> completed;
        lock (_accumulatorGate)
        {
            completed = _accumulator.Flush(DateTimeOffset.UtcNow);
        }

        Persist(completed, _config.Current);
    }

    private async Task ShutdownAsync()
    {
        _log.Info("Agent stopping; flushing the open interval and draining the buffer.");

        try
        {
            FlushAccumulator();
        }
        catch (Exception ex)
        {
            _log.Error("Final flush failed.", ex);
        }

        // A fresh token: the run token is already cancelled, but the final drain is exactly when
        // uploading matters most. Bounded so shutdown cannot hang a sign-out.
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            await DrainAsync(shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Final drain timed out; intervals stay buffered for the next start.");
        }

        try
        {
            _heartbeat.WriteLocal(BuildHeartbeat());
        }
        catch (Exception ex)
        {
            _log.Debug($"Final heartbeat failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _config.Changed -= ApplyConfig;

        if (_session is not null)
        {
            _session.Interrupting -= OnInterrupting;
            _session.Dispose();
        }

        _paths.Dispose();
        _config?.Dispose();
        (_remote as IDisposable)?.Dispose();
        _store?.Dispose();
    }
}
