using FileTimeTracker.Agent.Configuration;
using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Tests.Fakes;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>design.md §8, integration: "config hot-reload".</summary>
public class ConfigProviderTests : IDisposable
{
    private const string ConfigPath = "/config.json";

    private readonly string _cacheDirectory =
        Path.Combine(Path.GetTempPath(), $"ftt-config-{Guid.NewGuid():N}");

    private string CachePath => Path.Combine(_cacheDirectory, "config.cache.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string Json(TrackerConfig config) => TrackerJson.SerializePretty(config);

    [Fact]
    public async Task Fetches_the_central_config_on_start()
    {
        var remote = new InMemoryRemoteStorage();
        remote.PutText(ConfigPath, Json(new TrackerConfig { SampleIntervalSec = 20, IdleThresholdSec = 120 }));

        using var provider = new ConfigProvider(remote, ConfigPath, CachePath, TrackerLog.Null);
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(20, provider.Current.SampleIntervalSec);
        Assert.Equal(120, provider.Current.IdleThresholdSec);
    }

    [Fact]
    public async Task A_changed_central_config_is_applied_and_announced()
    {
        var remote = new InMemoryRemoteStorage();
        remote.PutText(ConfigPath, Json(new TrackerConfig { IdleThresholdSec = 300 }));

        using var provider = new ConfigProvider(remote, ConfigPath, CachePath, TrackerLog.Null);

        var announced = new List<TrackerConfig>();
        provider.Changed += announced.Add;

        await provider.RefreshAsync(CancellationToken.None);
        Assert.Single(announced);

        // Unchanged content must not re-announce: handlers reset agent state.
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Single(announced);

        remote.PutText(ConfigPath, Json(new TrackerConfig { IdleThresholdSec = 900, CaptureWindowTitle = false }));
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, announced.Count);
        Assert.Equal(900, provider.Current.IdleThresholdSec);
        Assert.False(provider.Current.CaptureWindowTitle);
    }

    [Fact]
    public async Task An_invalid_central_config_is_ignored_rather_than_applied()
    {
        // A typo in one file reaches all ~50 agents at once; it must not brick them.
        var remote = new InMemoryRemoteStorage();
        remote.PutText(ConfigPath, Json(new TrackerConfig { IdleThresholdSec = 450 }));

        using var provider = new ConfigProvider(remote, ConfigPath, CachePath, TrackerLog.Null);
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(450, provider.Current.IdleThresholdSec);

        remote.PutText(ConfigPath, "{ this is not json ");
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(450, provider.Current.IdleThresholdSec);
    }

    [Fact]
    public async Task Out_of_range_values_are_clamped_instead_of_taking_effect()
    {
        var remote = new InMemoryRemoteStorage();
        remote.PutText(ConfigPath, """
            {
              "sample_interval_sec": 0,
              "upload_interval_sec": -5,
              "idle_threshold_sec": 999999,
              "config_poll_sec": 1
            }
            """);

        using var provider = new ConfigProvider(remote, ConfigPath, CachePath, TrackerLog.Null);
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(10, provider.Current.SampleIntervalSec);
        Assert.Equal(300, provider.Current.UploadIntervalSec);
        Assert.Equal(7200, provider.Current.IdleThresholdSec);
        Assert.Equal(60, provider.Current.ConfigPollSec);
    }

    [Fact]
    public async Task A_missing_central_config_leaves_the_current_one_in_place()
    {
        var remote = new InMemoryRemoteStorage();
        remote.PutText(ConfigPath, Json(new TrackerConfig { IdleThresholdSec = 450 }));

        using var provider = new ConfigProvider(remote, ConfigPath, CachePath, TrackerLog.Null);
        await provider.RefreshAsync(CancellationToken.None);

        await remote.DeleteAsync(ConfigPath);
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(450, provider.Current.IdleThresholdSec);
    }

    [Fact]
    public async Task An_offline_start_reuses_the_last_good_config_rather_than_reverting_to_defaults()
    {
        // Reverting would silently turn the privacy toggles back on, which is the wrong failure.
        var remote = new InMemoryRemoteStorage();
        remote.PutText(ConfigPath, Json(new TrackerConfig
        {
            IdleThresholdSec = 600,
            CaptureWindowTitle = false,
            HashSensitiveFields = true,
        }));

        using (var first = new ConfigProvider(remote, ConfigPath, CachePath, TrackerLog.Null))
        {
            await first.RefreshAsync(CancellationToken.None);
        }

        remote.Offline = true;

        using var restarted = new ConfigProvider(remote, ConfigPath, CachePath, TrackerLog.Null);
        await restarted.RefreshAsync(CancellationToken.None);

        Assert.Equal(600, restarted.Current.IdleThresholdSec);
        Assert.False(restarted.Current.CaptureWindowTitle);
        Assert.True(restarted.Current.HashSensitiveFields);
    }

    [Fact]
    public void With_no_cache_and_no_network_the_built_in_defaults_apply()
    {
        var remote = new InMemoryRemoteStorage { Offline = true };

        using var provider = new ConfigProvider(remote, ConfigPath, CachePath, TrackerLog.Null);

        Assert.Equal(10, provider.Current.SampleIntervalSec);
        Assert.Equal(300, provider.Current.IdleThresholdSec);
        Assert.Equal("/logs", provider.Current.LogsPath);
        Assert.Equal("/TimeLog_Current.xlsx", provider.Current.MasterWorkbookPath);
    }
}
