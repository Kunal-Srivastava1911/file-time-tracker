using System.Text.Json.Serialization;

namespace FileTimeTracker.Core.Model;

/// <summary>
/// How the <c>file</c> value on an interval was determined. Reported honestly on every row so
/// downstream reports can tell a real document apart from a browser tab (design.md §3, §9).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileSource>))]
public enum FileSource
{
    /// <summary>Nothing usable could be resolved; only the application is known.</summary>
    Unknown = 0,

    /// <summary>Filename parsed out of the window title by an app-specific rule. The common case.</summary>
    DesktopTitle,

    /// <summary>Full path resolved via UI Automation. Most precise.</summary>
    UiaFilePath,

    /// <summary>Web/cloud app: the value is a browser tab or site name, not a file (§9).</summary>
    BrowserTab,

    /// <summary>App works on a dataset rather than files (TallyPrime company/voucher context, §9).</summary>
    AppContext,

    /// <summary>Foreground app has no document concept (shell, settings, launcher).</summary>
    AppOnly,
}

public static class FileSourceExtensions
{
    /// <summary>
    /// How much the value can be trusted to name a real document, highest first. Declared
    /// separately from the enum's numeric order so that reordering or extending
    /// <see cref="FileSource"/> cannot silently change reporting behaviour.
    /// </summary>
    public static int Specificity(this FileSource source) => source switch
    {
        FileSource.UiaFilePath => 5,
        FileSource.DesktopTitle => 4,
        FileSource.AppContext => 3,
        FileSource.BrowserTab => 2,
        FileSource.AppOnly => 1,
        _ => 0,
    };

    /// <summary>True when the value names an actual file rather than a tab or dataset (§3, §9).</summary>
    public static bool IsRealFile(this FileSource source) =>
        source is FileSource.DesktopTitle or FileSource.UiaFilePath;
}
