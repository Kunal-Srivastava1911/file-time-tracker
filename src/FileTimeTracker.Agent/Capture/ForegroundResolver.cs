using System.Diagnostics;
using System.Runtime.Versioning;
using FileTimeTracker.Agent.Interop;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Parsing;
using FileTimeTracker.Core.Tracking;

namespace FileTimeTracker.Agent.Capture;

/// <summary>
/// Turns the current foreground window into a <see cref="FocusSnapshot"/>: process, title,
/// parsed document, and a path when one is available (design.md §4.1).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ForegroundResolver
{
    /// <summary>UWP/packaged apps are hosted by this process; the real app is a child window.</summary>
    private const string UwpHost = "ApplicationFrameHost";

    private readonly TitleParser _parser;
    private readonly TrackerLog _log;
    private readonly Dictionary<string, string?> _productNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _ownProcessId = Environment.ProcessId;

    public ForegroundResolver(TitleParser parser, TrackerLog log)
    {
        _parser = parser;
        _log = log;
    }

    /// <param name="pathResolver">Supplies best-effort paths; pass the null resolver to suppress them.</param>
    /// <param name="excluded">Process names never recorded (<c>excluded_processes</c>).</param>
    /// <param name="captureTitle">False when <c>capture_window_title</c> is off (§2).</param>
    /// <returns>Null when there is no foreground window or it is excluded — "not counted" under §3.</returns>
    public FocusSnapshot? Resolve(IFilePathResolver pathResolver, IReadOnlySet<string> excluded, bool captureTitle)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            // No foreground window: the logon screen, a desktop transition, or a UAC prompt on the
            // secure desktop. §3 says this is not counted.
            return null;
        }

        if (NativeMethods.GetWindowThreadProcessId(hwnd, out var rawPid) == 0 || rawPid == 0)
        {
            return null;
        }

        var pid = (int)rawPid;
        var processName = TryGetProcessName(pid);
        if (processName is null)
        {
            return null;
        }

        if (string.Equals(processName, UwpHost, StringComparison.OrdinalIgnoreCase))
        {
            var realPid = ResolveHostedPid(hwnd, rawPid);
            if (realPid != rawPid)
            {
                var hostedName = TryGetProcessName((int)realPid);
                if (hostedName is not null)
                {
                    pid = (int)realPid;
                    processName = hostedName;
                }
            }
        }

        if (pid == _ownProcessId || excluded.Contains(processName))
        {
            return null;
        }

        var title = NativeMethods.GetWindowTitle(hwnd);
        var parsed = _parser.Parse(processName, title, GetProductName(pid));

        // A path from the title (Notepad++) beats a UIA walk and is already on the parse result.
        var path = parsed.FilePath ?? pathResolver.TryGet(hwnd, processName, title);
        var source = parsed.Source;

        if (path is not null && source == Core.Model.FileSource.DesktopTitle)
        {
            source = Core.Model.FileSource.UiaFilePath;
        }

        return new FocusSnapshot
        {
            ProcessName = processName,
            App = parsed.App,
            File = parsed.File,
            FilePath = path,
            WindowTitle = captureTitle && title.Length > 0 ? title : null,
            Source = source,
        };
    }

    private string? TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Exited between GetForegroundWindow and here. Normal; skip this tick.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A packaged app's frame window belongs to ApplicationFrameHost while its content belongs to
    /// the real process. Without this, every Store app on the fleet would be reported as
    /// "ApplicationFrameHost".
    /// </summary>
    private static uint ResolveHostedPid(IntPtr hwnd, uint hostPid)
    {
        var found = hostPid;

        NativeMethods.EnumChildWindows(hwnd, (child, _) =>
        {
            if (NativeMethods.GetWindowThreadProcessId(child, out var childPid) != 0
                && childPid != 0 && childPid != hostPid)
            {
                found = childPid;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// <c>FileVersionInfo.ProductName</c> for apps with no explicit rule, so an unknown editor is
    /// still labelled meaningfully. Cached by executable path — reading version resources on every
    /// sample would be pure waste.
    /// </summary>
    private string? GetProductName(int pid)
    {
        string? path;

        try
        {
            using var process = Process.GetProcessById(pid);
            path = process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // Protected or higher-integrity process: the name is simply unavailable to us.
            return null;
        }

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (_productNames.TryGetValue(path, out var cached))
        {
            return cached;
        }

        string? product = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            product = string.IsNullOrWhiteSpace(info.ProductName) ? null : info.ProductName.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug($"Could not read version info for '{path}': {ex.Message}");
        }

        if (_productNames.Count > 256)
        {
            _productNames.Clear();
        }

        _productNames[path] = product;
        return product;
    }
}
