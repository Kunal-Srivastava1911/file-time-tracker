using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Core.Tracking;

/// <summary>
/// Interactive-session state. Everything except <see cref="Unlocked"/> means "not counted" under
/// design.md §3.
/// </summary>
public enum SessionState
{
    /// <summary>Employee is signed in at an unlocked desktop. The only countable state.</summary>
    Unlocked,

    /// <summary>Workstation locked (Win+L, timeout, screensaver-with-password).</summary>
    Locked,

    /// <summary>Machine suspending, resuming, hibernating, or shutting down.</summary>
    Suspended,

    /// <summary>Session disconnected, signed out, or sitting on the logon screen.</summary>
    LoggedOff,
}

/// <summary>What the foreground window resolved to at one sampling tick.</summary>
public sealed record FocusSnapshot
{
    public required string ProcessName { get; init; }

    public required string App { get; init; }

    public required string File { get; init; }

    public string? FilePath { get; init; }

    public string? WindowTitle { get; init; }

    public required FileSource Source { get; init; }

    /// <summary>
    /// Identity used to decide "same file still focused". Deliberately excludes the window title:
    /// a dirty marker appearing or a page number changing must not split an interval.
    /// </summary>
    public (string App, string File, string? FilePath, FileSource Source) Key =>
        (App, File, FilePath, Source);
}

/// <param name="TimestampUtc">When the sample was taken.</param>
/// <param name="Focus">Resolved foreground window, or null when there is none or it is excluded.</param>
/// <param name="IdleFor">Time since the last keyboard/mouse input (<c>GetLastInputInfo</c>).</param>
/// <param name="Session">Session state at sample time.</param>
public readonly record struct Sample(
    DateTimeOffset TimestampUtc,
    FocusSnapshot? Focus,
    TimeSpan IdleFor,
    SessionState Session)
{
    public bool IsCountable => Session == SessionState.Unlocked && Focus is not null;
}
