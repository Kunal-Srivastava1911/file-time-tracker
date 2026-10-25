using System.Globalization;
using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Core.Storage;

/// <summary>
/// Naming and discovery rules for the per-machine JSONL logs.
/// </summary>
/// <remarks>
/// <para><b>Deviation from design.md §4.3, deliberate.</b> The spec says the agent "appends" to a
/// single <c>logs/&lt;computer_no&gt;.jsonl</c>. The Dropbox API has no append operation, so that
/// wording can only be implemented as download-modify-reupload of the whole file every
/// <c>upload_interval</c>. By late in a quarter that file is tens of megabytes, giving each laptop
/// gigabytes of daily upload for kilobytes of new data — and a connection dropped mid-write would
/// truncate that machine's entire history.</para>
/// <para>Instead each upload writes a new immutable chunk under
/// <c>logs/&lt;computer_no&gt;/&lt;yyyyMMdd-HHmmss&gt;-&lt;id&gt;.jsonl</c>. Only new bytes are
/// ever sent, no write can damage existing data, and two agents can never race. Semantics the spec
/// actually depends on are unchanged: one logical log per machine, idempotent by interval id.</para>
/// <para><see cref="IsLogFile"/> also accepts the flat <c>&lt;computer_no&gt;.jsonl</c> form, so an
/// aggregator run reads both layouts and a hand-placed file still works.</para>
/// </remarks>
public static class LogLayout
{
    public const string Extension = ".jsonl";

    public static string MachineFolder(string logsRoot, string computerNo) =>
        RemotePath.Combine(logsRoot, RemotePath.SafeSegment(computerNo));

    public static string NewChunkPath(string logsRoot, string computerNo, DateTimeOffset stampUtc)
    {
        // Timestamp first so a folder listing sorts chronologically; a short random suffix keeps
        // two uploads inside the same second from colliding.
        var stamp = stampUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var unique = Guid.NewGuid().ToString("N")[..8];

        return RemotePath.Combine(MachineFolder(logsRoot, computerNo), $"{stamp}-{unique}{Extension}");
    }

    public static bool IsLogFile(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Archive destination for a machine's logs at quarter rollover (§4.5).</summary>
    public static string ArchiveFolder(string archiveRoot, Quarter quarter) =>
        RemotePath.Combine(archiveRoot, "logs", quarter.Label);

    /// <summary>Archive destination for the master workbook at quarter rollover (§4.5).</summary>
    public static string ArchiveWorkbookPath(string archiveRoot, Quarter quarter) =>
        RemotePath.Combine(archiveRoot, $"TimeLog_{quarter.Label}.xlsx");
}
