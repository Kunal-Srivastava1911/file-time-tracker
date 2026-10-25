using System.Text.Json.Serialization;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Agent.Configuration;

/// <summary>
/// Per-machine identity written once by the installer (design.md §6). Everything that varies by
/// laptop lives here; everything that varies by policy lives in the central config (§4.4).
/// </summary>
public sealed class MachineSettings
{
    /// <summary>Prompted for at install time and stamped on every row (§6).</summary>
    [JsonPropertyName("computer_no")]
    public required string ComputerNo { get; set; }

    /// <summary>
    /// Fleet-wide salt for <c>hash_sensitive_fields</c>. Identical on every laptop by design —
    /// a per-machine salt would make the same document hash differently per machine and break the
    /// aggregator's grouping.
    /// </summary>
    [JsonPropertyName("hash_salt")]
    public string HashSalt { get; set; } = string.Empty;

    /// <summary>Where the central config lives inside the Dropbox app folder (§4.4).</summary>
    [JsonPropertyName("config_path")]
    public string ConfigPath { get; set; } = "/config.json";

    /// <summary>
    /// <c>dropbox</c> (default, §4.3) or <c>filesystem</c> for a folder or UNC share. Per-machine
    /// rather than central: storage has to be reachable before the central config can be read.
    /// </summary>
    [JsonPropertyName("storage_backend")]
    public string StorageBackend { get; set; } = RemoteStorageFactory.Dropbox;

    /// <summary>Root folder or UNC path when <see cref="StorageBackend"/> is <c>filesystem</c>.</summary>
    /// <remarks>
    /// The app's own working area: the JSONL interval logs, the heartbeat and the central config.
    /// For a single-user install this is deliberately a local folder rather than a synced one — see
    /// <see cref="PublishRoot"/>.
    /// </remarks>
    [JsonPropertyName("storage_root")]
    public string StorageRoot { get; set; } = string.Empty;

    /// <summary>
    /// Folder that receives finished session spreadsheets, one subfolder per employee. Empty leaves
    /// them in <see cref="StorageRoot"/> under <c>/sessions</c>, which is the fleet arrangement.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="StorageRoot"/> so a synced folder can hold the reports without
    /// also holding the machinery. A person who opens Dropbox is looking for their spreadsheet;
    /// shipping the raw logs, the liveness marker and a config file to the same place means they
    /// have to know which of five things to ignore. Those stay in <see cref="StorageRoot"/>.
    /// </remarks>
    [JsonPropertyName("publish_root")]
    public string PublishRoot { get; set; } = string.Empty;

    /// <summary>True when finished spreadsheets go somewhere other than the working store.</summary>
    [JsonIgnore]
    public bool PublishesSeparately => !string.IsNullOrWhiteSpace(PublishRoot);

    /// <summary>
    /// Windows time-zone id defining "date" for daily totals. Left empty the agent uses the
    /// machine's own zone; setting it explicitly keeps a travelling laptop reporting on the
    /// company's calendar day.
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

    public static MachineSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Machine settings not found at '{path}'. The agent has not been provisioned — run the installer, " +
                "or 'FileTimeTracker.Agent.exe provision --help' from an elevated prompt.",
                path);
        }

        var settings = TrackerJson.Deserialize<MachineSettings>(File.ReadAllText(path))
                       ?? throw new InvalidOperationException($"'{path}' is not valid machine settings JSON.");

        if (string.IsNullOrWhiteSpace(settings.ComputerNo))
        {
            throw new InvalidOperationException($"'{path}' has no computer_no. Every row must be attributable to a machine.");
        }

        return settings;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, TrackerJson.SerializePretty(this));
    }
}
