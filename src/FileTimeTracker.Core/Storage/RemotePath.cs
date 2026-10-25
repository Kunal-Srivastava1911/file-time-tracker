namespace FileTimeTracker.Core.Storage;

/// <summary>
/// Dropbox path rules in one place. The API wants forward slashes, a leading slash on every real
/// path, and — the easy one to get wrong — an <em>empty</em> string rather than <c>"/"</c> when
/// listing the root of an app folder.
/// </summary>
public static class RemotePath
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized == "/" ? string.Empty : normalized;
    }

    public static string Combine(string folder, params string[] segments)
    {
        var parts = new List<string> { Normalize(folder).TrimEnd('/') };
        parts.AddRange(segments
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().Replace('\\', '/').Trim('/')));

        return Normalize(string.Join('/', parts));
    }

    public static string FileName(string path)
    {
        var normalized = Normalize(path);
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    public static string Parent(string path)
    {
        var normalized = Normalize(path);
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? string.Empty : normalized[..index];
    }

    /// <summary>Strips characters Dropbox rejects so a Computer No. can be used as a folder name.</summary>
    public static string SafeSegment(string value)
    {
        var cleaned = new string(value
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray())
            .Trim('-', '.');

        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }

    /// <summary>
    /// Folder name for a segment a person reads rather than a machine, such as an employee's name.
    /// </summary>
    /// <remarks>
    /// Keeps spaces, where <see cref="SafeSegment"/> replaces them. A Computer No. is an identifier
    /// and survives being punched down to <c>PC-014</c>, but a person's folder is browsed by someone
    /// looking for their own name: "Kunal Srivastava" is the name and "Kunal-Srivastava" is not.
    /// Only characters a path genuinely cannot carry are substituted, and the result is trimmed of
    /// trailing spaces and dots because Windows silently rejects a folder ending in either.
    /// </remarks>
    public static string SafeDisplaySegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        // Unsupported characters become spaces rather than hyphens, so the collapse below folds
        // them into the surrounding word break instead of leaving "Priya-R" style debris.
        var cleaned = new string(value
            .Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '\'' ? c : ' ')
            .ToArray());

        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.');

        return cleaned.Length == 0 ? "unknown" : cleaned;
    }
}
