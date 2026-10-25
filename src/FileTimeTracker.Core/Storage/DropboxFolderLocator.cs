using System.Text.Json;

namespace FileTimeTracker.Core.Storage;

/// <summary>
/// Finds the local folder the Dropbox desktop client syncs, so a single-user install can write
/// there with no app key, no refresh token and no OAuth.
/// </summary>
/// <remarks>
/// This is for the click-to-start desktop app, not the fleet (§4.3). It relies on the Dropbox
/// client being installed and signed in; every lookup is best-effort and returns null rather than
/// throwing, because "no Dropbox here" is an ordinary answer the caller has to handle anyway.
/// </remarks>
public static class DropboxFolderLocator
{
    /// <summary>Subfolder created inside the Dropbox root so the app never litters its top level.</summary>
    public const string DefaultSubfolder = "FileTimeTracker";

    /// <summary>
    /// The only folder this app puts in a user's Dropbox: finished spreadsheets, one subfolder per
    /// employee, and nothing else.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DefaultSubfolder"/> because the two hold different kinds of thing.
    /// Dropbox is a place a person opens and reads, so what syncs there is the report and only the
    /// report; the JSONL interval logs, the heartbeat and <c>config.json</c> are the app's own
    /// working files and stay on the machine, where nobody has to look past them.
    /// </remarks>
    public const string PublishSubfolder = "Employee Timeslots";

    /// <summary>
    /// The synced Dropbox root, or null if it cannot be determined. Prefers the client's own
    /// <c>info.json</c> — the only authoritative source, since the folder can be moved anywhere —
    /// and falls back to the conventional locations.
    /// </summary>
    public static string? FindRoot()
    {
        foreach (var candidate in InfoJsonCandidates())
        {
            var root = TryReadInfoJson(candidate);
            if (root is not null)
            {
                return root;
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var name in new[] { "Dropbox", "Dropbox (Personal)" })
        {
            var path = Path.Combine(profile, name);
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>The folder this app should write to, or null when Dropbox cannot be located.</summary>
    public static string? FindStorageRoot()
    {
        var root = FindRoot();
        return root is null ? null : Path.Combine(root, DefaultSubfolder);
    }

    /// <summary>
    /// Where finished spreadsheets are filed, or null when Dropbox cannot be located.
    /// </summary>
    public static string? FindPublishRoot()
    {
        var root = FindRoot();
        return root is null ? null : Path.Combine(root, PublishSubfolder);
    }

    private static IEnumerable<string> InfoJsonCandidates()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            yield return Path.Combine(Environment.GetFolderPath(folder), "Dropbox", "info.json");
        }
    }

    /// <summary>
    /// <c>info.json</c> maps each linked account to its local path, e.g.
    /// <c>{"personal":{"path":"C:\\Users\\me\\Dropbox", ...}}</c>. Business comes first when both
    /// are linked, on the assumption that a work machine's work account is the intended target.
    /// </summary>
    private static string? TryReadInfoJson(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));

            foreach (var account in new[] { "business", "personal" })
            {
                if (document.RootElement.TryGetProperty(account, out var element)
                    && element.TryGetProperty("path", out var folder)
                    && folder.ValueKind == JsonValueKind.String)
                {
                    var value = folder.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                    {
                        return value;
                    }
                }
            }

            // Some builds use an account id as the key rather than business/personal.
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("path", out var folder)
                    && folder.ValueKind == JsonValueKind.String)
                {
                    var value = folder.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
