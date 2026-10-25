using System.Text;
using FileTimeTracker.Core.Storage;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>
/// Contract tests for the folder/UNC backend. The pipeline was written against Dropbox semantics,
/// so these pin the behaviours it actually relies on: a missing file reads as null rather than
/// throwing, a missing folder lists empty, and <see cref="IRemoteStorage.ListFilesAsync"/> hands
/// back <em>remote</em> paths that can be fed straight back in.
/// </summary>
public sealed class FileSystemRemoteStorageTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemRemoteStorage _storage;

    public FileSystemRemoteStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ftt-fs-" + Guid.NewGuid().ToString("N")[..8]);
        _storage = new FileSystemRemoteStorage(_root);
        _storage.EnsureReachable();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp folder left behind must not fail the suite.
        }
    }

    [Fact]
    public async Task Upload_then_download_round_trips_text()
    {
        await _storage.UploadTextAsync("/logs/PC-001/a.jsonl", "hello");

        Assert.Equal("hello", await _storage.DownloadTextAsync("/logs/PC-001/a.jsonl"));
    }

    [Fact]
    public async Task Upload_creates_missing_parent_folders()
    {
        await _storage.UploadTextAsync("/logs/PC-001/deep/nested/a.jsonl", "x");

        Assert.True(File.Exists(Path.Combine(_root, "logs", "PC-001", "deep", "nested", "a.jsonl")));
    }

    [Fact]
    public async Task Missing_file_downloads_as_null_rather_than_throwing()
    {
        Assert.Null(await _storage.DownloadAsync("/logs/nope.jsonl"));
        Assert.Null(await _storage.DownloadTextAsync("/logs/nope.jsonl"));
    }

    [Fact]
    public async Task Missing_folder_lists_empty()
    {
        Assert.Empty(await _storage.ListFilesAsync("/logs"));
    }

    /// <summary>
    /// The important one. LogReader feeds each listed path back into DownloadTextAsync, and
    /// QuarterArchiver pulls the machine folder out with RemotePath.Parent — both of which need the
    /// forward-slash remote form, not a Windows path.
    /// </summary>
    [Fact]
    public async Task Listing_returns_remote_paths_that_round_trip()
    {
        await _storage.UploadTextAsync("/logs/PC-001/a.jsonl", "one");
        await _storage.UploadTextAsync("/logs/PC-002/b.jsonl", "two");

        var listed = await _storage.ListFilesAsync("/logs");

        Assert.Equal(2, listed.Count);
        Assert.All(listed, f => Assert.StartsWith("/logs/", f.Path, StringComparison.Ordinal));
        Assert.DoesNotContain(listed, f => f.Path.Contains('\\', StringComparison.Ordinal));

        foreach (var file in listed)
        {
            Assert.NotNull(await _storage.DownloadTextAsync(file.Path));
        }

        var machines = listed.Select(f => RemotePath.FileName(RemotePath.Parent(f.Path))).OrderBy(m => m).ToList();
        Assert.Equal(new[] { "PC-001", "PC-002" }, machines);
    }

    [Fact]
    public async Task Listing_recurses_and_reports_size()
    {
        await _storage.UploadTextAsync("/logs/PC-001/a.jsonl", "12345");

        var file = Assert.Single(await _storage.ListFilesAsync("/logs"));

        Assert.Equal(5, file.Size);
    }

    [Fact]
    public async Task Listing_the_root_sees_everything()
    {
        await _storage.UploadTextAsync("/config.json", "{}");
        await _storage.UploadTextAsync("/logs/PC-001/a.jsonl", "x");

        Assert.Equal(2, (await _storage.ListFilesAsync("/")).Count);
    }

    [Fact]
    public async Task Upload_overwrites_in_place()
    {
        await _storage.UploadTextAsync("/TimeLog_Current.xlsx", "first");
        await _storage.UploadTextAsync("/TimeLog_Current.xlsx", "second");

        Assert.Equal("second", await _storage.DownloadTextAsync("/TimeLog_Current.xlsx"));
        Assert.Single(await _storage.ListFilesAsync("/"));
    }

    /// <summary>A half-written chunk must never be offered to the aggregator as a log file.</summary>
    [Fact]
    public async Task Partial_upload_temp_files_are_hidden_from_listings()
    {
        await _storage.UploadTextAsync("/logs/PC-001/a.jsonl", "done");

        Directory.CreateDirectory(Path.Combine(_root, "logs", "PC-001"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "logs", "PC-001", "b.jsonl.deadbeef.uploadtmp"), "half written");

        var file = Assert.Single(await _storage.ListFilesAsync("/logs"));
        Assert.Equal("/logs/PC-001/a.jsonl", file.Path);
    }

    [Fact]
    public async Task Move_relocates_and_creates_the_destination_folder()
    {
        await _storage.UploadTextAsync("/logs/PC-001/a.jsonl", "payload");

        await _storage.MoveAsync("/logs/PC-001/a.jsonl", "/archive/logs/2026-Q4/PC-001/a.jsonl");

        Assert.Null(await _storage.DownloadTextAsync("/logs/PC-001/a.jsonl"));
        Assert.Equal("payload", await _storage.DownloadTextAsync("/archive/logs/2026-Q4/PC-001/a.jsonl"));
    }

    [Fact]
    public async Task Copy_leaves_the_source_in_place()
    {
        await _storage.UploadTextAsync("/TimeLog_Current.xlsx", "book");

        await _storage.CopyAsync("/TimeLog_Current.xlsx", "/archive/TimeLog_2026-Q4.xlsx");

        Assert.Equal("book", await _storage.DownloadTextAsync("/TimeLog_Current.xlsx"));
        Assert.Equal("book", await _storage.DownloadTextAsync("/archive/TimeLog_2026-Q4.xlsx"));
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        await _storage.UploadTextAsync("/logs/PC-001/a.jsonl", "x");

        await _storage.DeleteAsync("/logs/PC-001/a.jsonl");
        await _storage.DeleteAsync("/logs/PC-001/a.jsonl");

        Assert.False(await _storage.ExistsAsync("/logs/PC-001/a.jsonl"));
    }

    [Fact]
    public async Task Exists_answers_for_files_and_folders()
    {
        await _storage.UploadTextAsync("/logs/PC-001/a.jsonl", "x");

        Assert.True(await _storage.ExistsAsync("/logs/PC-001/a.jsonl"));
        Assert.True(await _storage.ExistsAsync("/logs/PC-001"));
        Assert.False(await _storage.ExistsAsync("/logs/PC-999"));
    }

    [Fact]
    public async Task Paths_that_climb_out_of_the_root_are_refused()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _storage.UploadTextAsync("/../escaped.txt", "nope"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _storage.DownloadTextAsync("/logs/../../escaped.txt"));
    }

    [Fact]
    public void A_blank_root_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new FileSystemRemoteStorage("  "));
    }

    [Fact]
    public async Task Unicode_survives_the_round_trip()
    {
        const string content = "{\"file\":\"Q3 Rapport — ünïcode.docx\"}";

        await _storage.UploadTextAsync("/logs/PC-001/u.jsonl", content);

        Assert.Equal(content, await _storage.DownloadTextAsync("/logs/PC-001/u.jsonl"));
        Assert.Equal(
            Encoding.UTF8.GetByteCount(content),
            (await _storage.ListFilesAsync("/logs")).Single().Size);
    }
}

public sealed class RemoteStorageFactoryTests
{
    [Theory]
    [InlineData(null, RemoteStorageFactory.Dropbox)]
    [InlineData("", RemoteStorageFactory.Dropbox)]
    [InlineData("dropbox", RemoteStorageFactory.Dropbox)]
    [InlineData("DropBox", RemoteStorageFactory.Dropbox)]
    [InlineData("filesystem", RemoteStorageFactory.FileSystem)]
    [InlineData("  FileSystem  ", RemoteStorageFactory.FileSystem)]
    public void Canonicalize_accepts_the_known_backends(string? input, string expected)
    {
        Assert.Equal(expected, RemoteStorageFactory.Canonicalize(input));
    }

    [Fact]
    public void Canonicalize_rejects_an_unknown_backend()
    {
        // A typo must not silently fall back to Dropbox and then fail with a credential error.
        Assert.Throws<ArgumentException>(() => RemoteStorageFactory.Canonicalize("s3"));
    }

    [Fact]
    public void Filesystem_without_a_root_fails_with_an_actionable_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RemoteStorageFactory.CreateFileSystem(null, Core.Diagnostics.TrackerLog.Null));

        Assert.Contains("--storage-root", ex.Message, StringComparison.Ordinal);
    }
}
