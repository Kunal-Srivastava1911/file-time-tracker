using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Agent.Configuration;

/// <summary>
/// Fetches the central <c>config.json</c> on start and every <c>config_poll_sec</c>, and
/// hot-reloads it without restarting the agent (design.md §4.4).
/// </summary>
/// <remarks>
/// Resolution order is deliberate: live Dropbox copy, then the on-disk cache of the last good
/// fetch, then built-in defaults. A laptop that boots on a train therefore keeps the policy it was
/// last given rather than reverting to defaults — which matters most for the privacy toggles,
/// where silently reverting to "capture everything" would be the wrong failure.
/// </remarks>
public sealed class ConfigProvider : IDisposable
{
    private readonly IRemoteStorage? _remote;
    private readonly string _remotePath;
    private readonly string _cachePath;
    private readonly TrackerLog _log;
    private readonly CancellationTokenSource _cts = new();

    private volatile TrackerConfig _current;
    private Task? _pollTask;
    private string? _lastRawJson;

    public ConfigProvider(IRemoteStorage? remote, string remotePath, string cachePath, TrackerLog log)
    {
        _remote = remote;
        _remotePath = remotePath;
        _cachePath = cachePath;
        _log = log;
        _current = LoadCached() ?? new TrackerConfig().Sanitized();
    }

    public TrackerConfig Current => _current;

    /// <summary>Raised only when the effective configuration actually changed.</summary>
    public event Action<TrackerConfig>? Changed;

    /// <summary>Fetches once, then starts the background poll loop.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await RefreshAsync(ct).ConfigureAwait(false);
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_current.ConfigPoll, ct).ConfigureAwait(false);
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("Config poll failed; keeping the current configuration.", ex);
            }
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (_remote is null)
        {
            return;
        }

        string? json;
        try
        {
            json = await _remote.DownloadTextAsync(_remotePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not fetch '{_remotePath}'; keeping the current configuration.", ex);
            return;
        }

        if (json is null)
        {
            _log.Warn($"Central config '{_remotePath}' does not exist; keeping the current configuration.");
            return;
        }

        if (string.Equals(json, _lastRawJson, StringComparison.Ordinal))
        {
            return;
        }

        TrackerConfig sanitized;
        try
        {
            var parsed = TrackerJson.Deserialize<TrackerConfig>(json)
                         ?? throw new InvalidOperationException("config.json deserialized to null.");
            sanitized = parsed.Sanitized();
        }
        catch (Exception ex)
        {
            // A malformed central file would otherwise take the whole fleet down at once.
            _log.Error($"Central config '{_remotePath}' is invalid; keeping the current configuration.", ex);
            return;
        }

        _lastRawJson = json;
        _current = sanitized;
        SaveCache(json);

        _log.Info(
            $"Configuration applied: sample={sanitized.SampleIntervalSec}s upload={sanitized.UploadIntervalSec}s " +
            $"idle={sanitized.IdleThresholdSec}s title={sanitized.CaptureWindowTitle} path={sanitized.CaptureFilePath} " +
            $"hash={sanitized.HashSensitiveFields}");

        try
        {
            Changed?.Invoke(sanitized);
        }
        catch (Exception ex)
        {
            _log.Error("A configuration-changed handler threw.", ex);
        }
    }

    private TrackerConfig? LoadCached()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            var json = File.ReadAllText(_cachePath);
            _lastRawJson = json;
            return TrackerJson.Deserialize<TrackerConfig>(json)?.Sanitized();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read the cached configuration at '{_cachePath}'.", ex);
            return null;
        }
    }

    private void SaveCache(string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Write-then-replace: a power cut mid-write must not leave a truncated cache that
            // would then be rejected on next boot.
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not cache the configuration to '{_cachePath}'.", ex);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _pollTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }
}
