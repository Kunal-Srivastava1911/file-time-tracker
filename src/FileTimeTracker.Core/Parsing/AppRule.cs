using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Core.Parsing;

/// <summary>Which segment of a split title actually names the document.</summary>
public enum SegmentPick
{
    /// <summary>Rejoin everything left after trimming the app and noise tokens. Office, browsers, editors with plain titles.</summary>
    JoinAll,

    /// <summary>Take the first segment. Editors that append workspace/solution context after the file.</summary>
    First,

    /// <summary>Take the last segment. Apps that prefix context before the document.</summary>
    Last,
}

/// <summary>
/// One entry in the per-app title→filename rule set called for in design.md §9. Adding support for
/// a new application is meant to be a new entry here plus a unit test, not a code change.
/// </summary>
public sealed record AppRule
{
    /// <summary>Process names without extension, matched case-insensitively.</summary>
    public required string[] ProcessNames { get; init; }

    /// <summary>Friendly name written to the <c>app</c> column.</summary>
    public required string App { get; init; }

    public FileSource Source { get; init; } = FileSource.DesktopTitle;

    public SegmentPick Pick { get; init; } = SegmentPick.JoinAll;

    /// <summary>Trailing segments stripped from the end, e.g. <c>Word</c>, <c>Google Chrome</c>.</summary>
    public string[] AppSuffixes { get; init; } = Array.Empty<string>();

    /// <summary>Segments dropped wherever they appear, e.g. <c>Compatibility Mode</c>.</summary>
    public string[] NoiseSegments { get; init; } = Array.Empty<string>();

    /// <summary>Used when the title is empty or reduces to nothing — a launcher or start screen.</summary>
    public string EmptyTitleFile { get; init; } = string.Empty;

    /// <summary>Source reported when the title reduces to nothing.</summary>
    public FileSource EmptyTitleSource { get; init; } = FileSource.AppOnly;

    /// <summary>Escape hatch for titles a segment split cannot express. Returning null falls through.</summary>
    public Func<string, TitleParseResult?>? Custom { get; init; }

    public bool Matches(string processName) =>
        ProcessNames.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
}
