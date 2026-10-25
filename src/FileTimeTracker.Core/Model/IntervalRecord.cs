using System.Text.Json.Serialization;

namespace FileTimeTracker.Core.Model;

/// <summary>
/// One contiguous stretch of foreground focus on a single file, closed on file/app switch, idle
/// transition, lock, sleep or local midnight (design.md §4.1).
/// </summary>
/// <remarks>
/// This is both the SQLite row shape (§4.2) and the per-machine JSONL line shape (§5), so the JSON
/// names are the snake_case column names from the spec and must not drift.
/// </remarks>
public sealed record IntervalRecord
{
    /// <summary>Stable UUID. The idempotency key for uploads and aggregation (§4.3).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("computer_no")]
    public required string ComputerNo { get; init; }

    [JsonPropertyName("win_user")]
    public required string WinUser { get; init; }

    /// <summary>Friendly application name, e.g. <c>Word</c>, <c>Chrome</c>, <c>TallyPrime</c>.</summary>
    [JsonPropertyName("app")]
    public required string App { get; init; }

    /// <summary>Document/tab/dataset the window was showing. Never null; falls back to the app name.</summary>
    [JsonPropertyName("file")]
    public required string File { get; init; }

    /// <summary>Full path when UI Automation could resolve it; null when suppressed or unavailable.</summary>
    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }

    /// <summary>Raw title; null when <c>capture_window_title</c> is off (§2 data minimization).</summary>
    [JsonPropertyName("window_title")]
    public string? WindowTitle { get; init; }

    [JsonPropertyName("start_utc")]
    public required DateTimeOffset StartUtc { get; init; }

    [JsonPropertyName("end_utc")]
    public required DateTimeOffset EndUtc { get; init; }

    [JsonPropertyName("active_sec")]
    public required int ActiveSec { get; init; }

    [JsonPropertyName("idle_sec")]
    public required int IdleSec { get; init; }

    [JsonPropertyName("source")]
    public required FileSource Source { get; init; }

    /// <summary>
    /// Local date the interval belongs to, used for the <c>computer × file × date</c> grouping in
    /// §4.5. The accumulator splits intervals at local midnight so this is unambiguous.
    /// </summary>
    [JsonPropertyName("local_date")]
    public required DateOnly LocalDate { get; init; }

    public int TotalSec => ActiveSec + IdleSec;
}
