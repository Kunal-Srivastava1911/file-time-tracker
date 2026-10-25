using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Privacy;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;
using Xunit;

namespace FileTimeTracker.Tests;

public class QuarterTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(12, 4)]
    public void Months_map_to_the_right_quarter(int month, int expected)
    {
        Assert.Equal(expected, Quarter.FromDate(new DateOnly(2026, month, 15)).Number);
    }

    [Fact]
    public void Boundaries_are_half_open()
    {
        var q3 = new Quarter(2026, 3);

        Assert.Equal(new DateOnly(2026, 7, 1), q3.Start);
        Assert.Equal(new DateOnly(2026, 10, 1), q3.EndExclusive);

        Assert.True(q3.Contains(new DateOnly(2026, 7, 1)));
        Assert.True(q3.Contains(new DateOnly(2026, 9, 30)));
        Assert.False(q3.Contains(new DateOnly(2026, 10, 1)));
        Assert.False(q3.Contains(new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public void Adjacent_quarters_cross_the_year_boundary()
    {
        Assert.Equal(new Quarter(2027, 1), new Quarter(2026, 4).Next);
        Assert.Equal(new Quarter(2025, 4), new Quarter(2026, 1).Previous);
    }

    [Fact]
    public void Quarters_order_chronologically()
    {
        Assert.True(new Quarter(2025, 4) < new Quarter(2026, 1));
        Assert.True(new Quarter(2026, 3) > new Quarter(2026, 2));
        Assert.True(new Quarter(2026, 2) <= new Quarter(2026, 2));
    }

    [Theory]
    [InlineData("2026-Q3", 2026, 3)]
    [InlineData("2025-q1", 2025, 1)]
    public void Labels_round_trip(string label, int year, int number)
    {
        Assert.True(Quarter.TryParseLabel(label, out var quarter));
        Assert.Equal(new Quarter(year, number), quarter);
        Assert.Equal($"{year:D4}-Q{number}", quarter.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("2026-Q5")]
    [InlineData("2026-X3")]
    [InlineData("abcd-Q1")]
    public void Malformed_labels_are_rejected(string label)
    {
        Assert.False(Quarter.TryParseLabel(label, out _));
    }
}

public class TrackerConfigTests
{
    [Fact]
    public void Defaults_match_the_specification()
    {
        var config = new TrackerConfig();

        Assert.Equal(10, config.SampleIntervalSec);
        Assert.Equal(300, config.UploadIntervalSec);
        Assert.Equal(300, config.IdleThresholdSec);
        Assert.Equal(900, config.ConfigPollSec);
        Assert.True(config.CaptureWindowTitle);
        Assert.True(config.CaptureFilePath);
        Assert.False(config.HashSensitiveFields);
        Assert.Equal("/logs", config.LogsPath);
        Assert.Equal("/TimeLog_Current.xlsx", config.MasterWorkbookPath);
        Assert.Equal("/archive", config.ArchivePath);
    }

    [Fact]
    public void The_specification_example_deserializes_exactly()
    {
        // The §4.4 sample verbatim, so a drift in the JSON key names fails here.
        var config = TrackerJson.Deserialize<TrackerConfig>("""
            {
              "sample_interval_sec": 10,
              "upload_interval_sec": 300,
              "idle_threshold_sec": 300,
              "config_poll_sec": 900,
              "capture_window_title": true,
              "capture_file_path": true,
              "hash_sensitive_fields": false,
              "logs_path": "/logs",
              "master_workbook_path": "/TimeLog_Current.xlsx",
              "archive_path": "/archive",
              "computer_employee_map": { "PC-014": "" }
            }
            """);

        Assert.NotNull(config);
        Assert.Equal(10, config.SampleIntervalSec);
        Assert.Equal("/logs", config.LogsPath);
        Assert.True(config.ComputerEmployeeMap.ContainsKey("PC-014"));
    }

    [Theory]
    [InlineData("logs", "/logs")]
    [InlineData("/logs/", "/logs")]
    [InlineData(@"\logs", "/logs")]
    [InlineData("", "/logs")]
    [InlineData("   ", "/logs")]
    public void Log_paths_are_normalised(string input, string expected)
    {
        Assert.Equal(expected, new TrackerConfig { LogsPath = input }.Sanitized().LogsPath);
    }

    [Fact]
    public void The_employee_map_is_case_insensitive_after_sanitising()
    {
        var config = new TrackerConfig();
        config.ComputerEmployeeMap["PC-014"] = "Asha";

        Assert.Equal("Asha", config.Sanitized().ComputerEmployeeMap["pc-014"]);
    }
}

public class FieldProtectorTests
{
    private static readonly IntervalRecord Row = TestData.Interval("a") with
    {
        FilePath = @"C:\work\Plan.docx",
        WindowTitle = "Plan.docx - Word",
    };

    [Fact]
    public void With_capture_on_and_hashing_off_the_values_pass_through()
    {
        var config = new TrackerConfig { CaptureWindowTitle = true, CaptureFilePath = true, HashSensitiveFields = false };
        var result = new FieldProtector("salt").Apply(Row, config);

        Assert.Equal("Plan.docx - Word", result.WindowTitle);
        Assert.Equal(@"C:\work\Plan.docx", result.FilePath);
        Assert.Equal("Plan.docx", result.File);
    }

    [Fact]
    public void Disabled_capture_drops_the_values_entirely()
    {
        // §2: gating happens before the row reaches disk, so it cannot be undone locally.
        var config = new TrackerConfig { CaptureWindowTitle = false, CaptureFilePath = false };
        var result = new FieldProtector("salt").Apply(Row, config);

        Assert.Null(result.WindowTitle);
        Assert.Null(result.FilePath);
        Assert.Equal("Plan.docx", result.File);
    }

    [Fact]
    public void Hashing_replaces_the_values_but_keeps_them_groupable()
    {
        var config = new TrackerConfig { HashSensitiveFields = true };
        var protector = new FieldProtector("fleet-salt");

        var first = protector.Apply(Row, config);
        var second = protector.Apply(Row with { Id = "b" }, config);

        Assert.True(FieldProtector.IsHashed(first.File));
        Assert.True(FieldProtector.IsHashed(first.WindowTitle));
        Assert.True(FieldProtector.IsHashed(first.FilePath));

        // Same document, same hash: the aggregator can still group it (§4.5).
        Assert.Equal(first.File, second.File);
        Assert.NotEqual(first.File, protector.Apply(Row with { File = "Other.docx" }, config).File);
    }

    [Fact]
    public void The_same_salt_on_two_machines_produces_the_same_hash()
    {
        // A per-machine salt would make one document hash differently per laptop and break the
        // fleet-wide grouping, which is why the salt is fleet-wide.
        var config = new TrackerConfig { HashSensitiveFields = true };

        var pc014 = new FieldProtector("fleet-salt").Apply(Row, config);
        var pc020 = new FieldProtector("fleet-salt").Apply(Row with { Id = "b", ComputerNo = "PC-020" }, config);

        Assert.Equal(pc014.File, pc020.File);
        Assert.NotEqual(pc014.File, new FieldProtector("other-salt").Apply(Row, config).File);
    }

    [Fact]
    public void Disabled_capture_wins_over_hashing()
    {
        var config = new TrackerConfig { CaptureWindowTitle = false, CaptureFilePath = false, HashSensitiveFields = true };
        var result = new FieldProtector("salt").Apply(Row, config);

        Assert.Null(result.WindowTitle);
        Assert.Null(result.FilePath);
        Assert.True(FieldProtector.IsHashed(result.File));
    }
}

public class RemotePathTests
{
    [Theory]
    [InlineData("logs", "/logs")]
    [InlineData("/logs", "/logs")]
    [InlineData("/logs/", "/logs")]
    [InlineData(@"logs\PC-014", "/logs/PC-014")]
    [InlineData("/logs//PC-014", "/logs/PC-014")]
    [InlineData("/", "")]
    [InlineData("", "")]
    public void Paths_are_normalised_for_the_api(string input, string expected)
    {
        Assert.Equal(expected, RemotePath.Normalize(input));
    }

    [Fact]
    public void Combine_joins_and_normalises()
    {
        Assert.Equal("/logs/PC-014/a.jsonl", RemotePath.Combine("/logs", "PC-014", "a.jsonl"));
        Assert.Equal("/logs/PC-014", RemotePath.Combine("logs/", "/PC-014/"));
    }

    [Theory]
    [InlineData("PC-014", "PC-014")]
    [InlineData("PC 014", "PC-014")]
    [InlineData("PC/014", "PC-014")]
    // Leading dots and dashes are trimmed, so a traversal attempt collapses to a plain name.
    [InlineData("../etc", "etc")]
    [InlineData("", "unknown")]
    public void Computer_numbers_are_made_safe_as_folder_names(string input, string expected)
    {
        Assert.Equal(expected, RemotePath.SafeSegment(input));
    }

    [Theory]
    // A person's name survives intact, spaces included: this is a folder someone browses by name.
    [InlineData("Kunal Srivastava", "Kunal Srivastava")]
    [InlineData("Asha Nair", "Asha Nair")]
    [InlineData("O'Brien", "O'Brien")]
    [InlineData("PC-001", "PC-001")]
    // Separators cannot become extra path segments, and must not leave hyphen debris behind.
    [InlineData(@"Priya R/S\T:Kumar", "Priya R S T Kumar")]
    // Windows silently rejects a folder name ending in a space or a dot.
    [InlineData("  Ravi Kumar  ", "Ravi Kumar")]
    [InlineData("Ravi Kumar.", "Ravi Kumar")]
    // Runs of whitespace read as a typo rather than a name.
    [InlineData("Ravi    Kumar", "Ravi Kumar")]
    [InlineData("../etc", "etc")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData("///", "unknown")]
    public void Employee_names_keep_their_spaces_as_folder_names(string input, string expected)
    {
        Assert.Equal(expected, RemotePath.SafeDisplaySegment(input));
    }

    [Fact]
    public void File_name_and_parent_split_correctly()
    {
        Assert.Equal("a.jsonl", RemotePath.FileName("/logs/PC-014/a.jsonl"));
        Assert.Equal("/logs/PC-014", RemotePath.Parent("/logs/PC-014/a.jsonl"));
    }

    [Fact]
    public void Chunk_paths_land_under_the_machine_folder_with_a_jsonl_extension()
    {
        var path = LogLayout.NewChunkPath("/logs", "PC-014", new DateTimeOffset(2026, 7, 20, 9, 30, 0, TimeSpan.Zero));

        Assert.StartsWith("/logs/PC-014/20260720-093000-", path, StringComparison.Ordinal);
        Assert.True(LogLayout.IsLogFile(path));
    }

    [Fact]
    public void Two_chunks_in_the_same_second_do_not_collide()
    {
        var stamp = new DateTimeOffset(2026, 7, 20, 9, 30, 0, TimeSpan.Zero);

        Assert.NotEqual(
            LogLayout.NewChunkPath("/logs", "PC-014", stamp),
            LogLayout.NewChunkPath("/logs", "PC-014", stamp));
    }

    [Fact]
    public void The_flat_single_file_layout_is_still_recognised_as_a_log()
    {
        // §4.3 describes logs/<computer_no>.jsonl; a hand-placed file in that shape must still be read.
        Assert.True(LogLayout.IsLogFile("/logs/PC-014.jsonl"));
        Assert.False(LogLayout.IsLogFile("/logs/_heartbeat/PC-014.json"));
    }
}

public class IntervalRecordSerializationTests
{
    [Fact]
    public void The_jsonl_line_uses_the_column_names_from_the_specification()
    {
        // Populated optional fields, since null ones are omitted by design (asserted below).
        var json = TrackerJson.Serialize(TestData.Interval("abc") with { FilePath = @"C:\work\Plan.docx" });

        foreach (var key in new[]
                 {
                     "\"id\"", "\"computer_no\"", "\"win_user\"", "\"app\"", "\"file\"",
                     "\"file_path\"", "\"window_title\"", "\"start_utc\"", "\"end_utc\"",
                     "\"active_sec\"", "\"idle_sec\"", "\"source\"",
                 })
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }

        // JSONL: one record must never contain a raw newline.
        Assert.DoesNotContain('\n', json);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var original = TestData.Interval("abc", idleSec: 45, source: FileSource.BrowserTab);
        var restored = TrackerJson.Deserialize<IntervalRecord>(TrackerJson.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.LocalDate, restored.LocalDate);
        Assert.Equal(original.Source, restored.Source);
        Assert.Equal(original.IdleSec, restored.IdleSec);
    }

    [Fact]
    public void Null_optional_fields_are_omitted_rather_than_written_as_null()
    {
        var json = TrackerJson.Serialize(TestData.Interval("abc") with { FilePath = null, WindowTitle = null });

        Assert.DoesNotContain("file_path", json, StringComparison.Ordinal);
        Assert.DoesNotContain("window_title", json, StringComparison.Ordinal);
    }
}
