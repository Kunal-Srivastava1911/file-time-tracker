using System.Text.Json.Serialization;

namespace FileTimeTracker.Core.Configuration;

/// <summary>
/// Central configuration (design.md §4.4). Lives as <c>config.json</c> in the admin Dropbox app
/// folder, is fetched on start and every <see cref="ConfigPollSec"/>, and hot-reloads without a
/// restart. Property names are the spec's snake_case keys and must not drift.
/// </summary>
public sealed class TrackerConfig
{
    [JsonPropertyName("sample_interval_sec")]
    public int SampleIntervalSec { get; set; } = 10;

    [JsonPropertyName("upload_interval_sec")]
    public int UploadIntervalSec { get; set; } = 300;

    [JsonPropertyName("idle_threshold_sec")]
    public int IdleThresholdSec { get; set; } = 300;

    [JsonPropertyName("config_poll_sec")]
    public int ConfigPollSec { get; set; } = 900;

    /// <summary>§2: window titles are capturable but gated. Off means the column is written null.</summary>
    [JsonPropertyName("capture_window_title")]
    public bool CaptureWindowTitle { get; set; } = true;

    /// <summary>§2: full paths are capturable but gated. Off means the column is written null.</summary>
    [JsonPropertyName("capture_file_path")]
    public bool CaptureFilePath { get; set; } = true;

    /// <summary>
    /// §2: when true, title and path are replaced by a salted hash instead of being dropped, so
    /// distinct documents stay distinguishable without exposing their names.
    /// </summary>
    [JsonPropertyName("hash_sensitive_fields")]
    public bool HashSensitiveFields { get; set; }

    [JsonPropertyName("logs_path")]
    public string LogsPath { get; set; } = "/logs";

    [JsonPropertyName("master_workbook_path")]
    public string MasterWorkbookPath { get; set; } = "/TimeLog_Current.xlsx";

    [JsonPropertyName("archive_path")]
    public string ArchivePath { get; set; } = "/archive";

    /// <summary>Computer No. → employee name. Blank/missing leaves the Employee column empty (§5).</summary>
    [JsonPropertyName("computer_employee_map")]
    public Dictionary<string, string> ComputerEmployeeMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Days after a quarter ends before its per-machine logs are moved into <c>archive_path</c>.
    /// </summary>
    /// <remarks>
    /// Not in §4.4; added because §4.5 archives logs at the quarter boundary while §4.3 allows a
    /// laptop to be offline indefinitely. A laptop that reconnects in week one of the new quarter
    /// uploads intervals dated to the old one, and moving the logs on day zero would leave that
    /// data outside the workbook that covers it. During the grace window the archived workbook is
    /// rebuilt each run; afterwards the logs move and it is final. Set to 0 for the literal §4.5
    /// behaviour.
    /// </remarks>
    [JsonPropertyName("archive_grace_days")]
    public int ArchiveGraceDays { get; set; } = 30;

    /// <summary>Show the optional first-run notice banner (§2). Off by default; the written policy is primary.</summary>
    [JsonPropertyName("show_first_run_notice")]
    public bool ShowFirstRunNotice { get; set; }

    /// <summary>Process names (no extension) that are never recorded, e.g. a password manager.</summary>
    [JsonPropertyName("excluded_processes")]
    public List<string> ExcludedProcesses { get; set; } = new();

    public TimeSpan SampleInterval => TimeSpan.FromSeconds(SampleIntervalSec);

    public TimeSpan UploadInterval => TimeSpan.FromSeconds(UploadIntervalSec);

    public TimeSpan IdleThreshold => TimeSpan.FromSeconds(IdleThresholdSec);

    public TimeSpan ConfigPoll => TimeSpan.FromSeconds(ConfigPollSec);

    /// <summary>
    /// Clamps every tunable into a sane band and repairs missing paths. A typo in the central file
    /// reaches all ~50 agents at once, so a bad value must degrade rather than brick the fleet.
    /// </summary>
    public TrackerConfig Sanitized()
    {
        return new TrackerConfig
        {
            SampleIntervalSec = Clamp(SampleIntervalSec, 1, 300, 10),
            UploadIntervalSec = Clamp(UploadIntervalSec, 30, 86_400, 300),
            IdleThresholdSec = Clamp(IdleThresholdSec, 30, 7_200, 300),
            ConfigPollSec = Clamp(ConfigPollSec, 60, 86_400, 900),
            CaptureWindowTitle = CaptureWindowTitle,
            CaptureFilePath = CaptureFilePath,
            HashSensitiveFields = HashSensitiveFields,
            LogsPath = NormalizeFolder(LogsPath, "/logs"),
            MasterWorkbookPath = NormalizeFile(MasterWorkbookPath, "/TimeLog_Current.xlsx"),
            ArchivePath = NormalizeFolder(ArchivePath, "/archive"),
            ComputerEmployeeMap = new Dictionary<string, string>(
                ComputerEmployeeMap ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase),
            ShowFirstRunNotice = ShowFirstRunNotice,
            ExcludedProcesses = ExcludedProcesses ?? new List<string>(),
            ArchiveGraceDays = Math.Clamp(ArchiveGraceDays, 0, 365),
        };
    }

    private static int Clamp(int value, int min, int max, int fallback)
    {
        if (value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private static string NormalizeFolder(string? value, string fallback)
    {
        var path = NormalizeFile(value, fallback);
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string NormalizeFile(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var path = value.Trim().Replace('\\', '/');
        return path.StartsWith('/') ? path : "/" + path;
    }
}
