using System.Text.Json.Serialization;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Aggregator.Configuration;

/// <summary>
/// Local paths for the aggregator, which runs on one always-on admin machine (design.md §4.5).
/// </summary>
public static class AggregatorPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FileTimeTracker",
        "Aggregator");

    public static string SettingsFile => Path.Combine(Root, "aggregator.json");

    /// <summary>
    /// DPAPI-encrypted credentials for the <b>admin</b> Dropbox app, which has broader scope than
    /// the agents' app folder (§4.5). Keeping it on one machine is what limits the blast radius of
    /// the §4.3 credential caveat.
    /// </summary>
    public static string CredentialFile => Path.Combine(Root, "dropbox.dat");

    public static string LogFile => Path.Combine(Root, "aggregator.log");
}

public sealed class AggregatorSettings
{
    [JsonPropertyName("config_path")]
    public string ConfigPath { get; set; } = "/config.json";

    /// <summary><c>dropbox</c> (default, §4.5) or <c>filesystem</c> for a folder or UNC share.</summary>
    [JsonPropertyName("storage_backend")]
    public string StorageBackend { get; set; } = RemoteStorageFactory.Dropbox;

    /// <summary>
    /// Root folder or UNC path when <see cref="StorageBackend"/> is <c>filesystem</c>. Must resolve
    /// to the same location the agents write to, or the aggregator will find no logs.
    /// </summary>
    [JsonPropertyName("storage_root")]
    public string StorageRoot { get; set; } = string.Empty;

    /// <summary>
    /// Time zone that defines the reporting day and the quarter boundary. Set this to the
    /// company's zone so a VM running in UTC does not roll the day over at 05:30 local.
    /// </summary>
    [JsonPropertyName("time_zone_id")]
    public string TimeZoneId { get; set; } = string.Empty;

    [JsonPropertyName("log_level")]
    public string LogLevel { get; set; } = "Info";

    public TimeZoneInfo ResolveTimeZone(TrackerLog? log = null)
    {
        if (string.IsNullOrWhiteSpace(TimeZoneId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            log?.Warn($"Unknown time_zone_id '{TimeZoneId}'; falling back to the machine time zone.", ex);
            return TimeZoneInfo.Local;
        }
    }

    public LogLevel ResolveLogLevel() =>
        Enum.TryParse<LogLevel>(LogLevel, ignoreCase: true, out var level) ? level : Core.Diagnostics.LogLevel.Info;

    public static AggregatorSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AggregatorSettings();
        }

        return TrackerJson.Deserialize<AggregatorSettings>(File.ReadAllText(path)) ?? new AggregatorSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, TrackerJson.SerializePretty(this));
    }
}
