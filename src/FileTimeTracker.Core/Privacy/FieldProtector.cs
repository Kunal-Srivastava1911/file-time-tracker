using System.Security.Cryptography;
using System.Text;
using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Core.Privacy;

/// <summary>
/// Applies the §2 data-minimization gates to an interval before it is persisted.
/// </summary>
/// <remarks>
/// Gating happens at write time, not upload time: if <c>capture_window_title</c> is off the title
/// never reaches the local SQLite buffer either, so turning a toggle off cannot be undone by
/// reading the agent's disk. When <c>hash_sensitive_fields</c> is on the value is replaced by a
/// keyed hash so two visits to the same document still group together in the aggregator without
/// the name being recoverable.
/// </remarks>
public sealed class FieldProtector
{
    private const string HashPrefix = "sha256:";

    private readonly byte[] _salt;

    /// <param name="salt">
    /// Per-fleet salt. A single shared salt keeps hashes comparable across machines, which is the
    /// point — the aggregator must be able to group the same document seen on two laptops.
    /// </param>
    public FieldProtector(string salt)
    {
        _salt = Encoding.UTF8.GetBytes(salt ?? string.Empty);
    }

    public IntervalRecord Apply(IntervalRecord record, TrackerConfig config)
    {
        var title = config.CaptureWindowTitle ? Protect(record.WindowTitle, config) : null;
        var path = config.CaptureFilePath ? Protect(record.FilePath, config) : null;

        // The file name is the report's whole point, so it is never dropped — but it is hashed
        // alongside the other free-text fields when the fleet opts into hashing.
        var file = config.HashSensitiveFields
            ? Protect(record.File, config) ?? record.File
            : record.File;

        return record with { WindowTitle = title, FilePath = path, File = file };
    }

    private string? Protect(string? value, TrackerConfig config)
    {
        if (string.IsNullOrEmpty(value) || !config.HashSensitiveFields)
        {
            return value;
        }

        return Hash(value);
    }

    public string Hash(string value)
    {
        var payload = Encoding.UTF8.GetBytes(value);
        var buffer = new byte[_salt.Length + payload.Length];
        _salt.CopyTo(buffer, 0);
        payload.CopyTo(buffer, _salt.Length);

        // Truncated to 16 bytes: still collision-safe at this scale and keeps rows readable.
        var digest = SHA256.HashData(buffer);
        return HashPrefix + Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
    }

    public static bool IsHashed(string? value) =>
        value is not null && value.StartsWith(HashPrefix, StringComparison.Ordinal);
}
