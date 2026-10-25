using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using FileTimeTracker.Core.Diagnostics;

namespace FileTimeTracker.Agent.Capture;

/// <summary>
/// Resolves a document's full path through UI Automation (design.md §4.1).
/// </summary>
/// <remarks>
/// <para><b>Never blocks the sampler.</b> UIA calls cross into the target process and can stall for
/// seconds when that process is busy — a saving Excel, a printing PDF. So <see cref="TryGet"/> only
/// reads a cache and queues unknown windows; a single background thread does the walking with a
/// per-window timeout. Since an interval spans many samples, a path resolved on tick 2 still lands
/// on the interval that opened on tick 1.</para>
/// <para><b>Browsers are deliberately excluded.</b> The address bar would yield a full URL, which
/// is a significant privacy escalation over the tab name that §9 asks for and that §2's
/// minimization posture does not sanction. Web apps stay at tab-name granularity.</para>
/// <para>Results are genuinely best-effort. Office does not expose a document path through UIA in
/// any dependable way, so most rows resolve a path only for editors that put one in the title.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class UiaFilePathResolver : IFilePathResolver
{
    private const int MaxCacheEntries = 512;
    private const int MaxNodesVisited = 160;
    private const int MaxDepth = 4;

    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Applications where a UIA walk is worth the cost. Browsers are absent on purpose.</summary>
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "notepad", "notepad++", "wordpad", "code", "code - insiders",
        "devenv", "sublime_text", "acrobat", "acrord32", "sumatrapdf", "foxitreader",
    };

    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly BlockingCollection<Request> _queue = new(boundedCapacity: 64);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _worker;
    private readonly TrackerLog _log;

    public UiaFilePathResolver(TrackerLog log)
    {
        _log = log;

        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "uia-path-resolver",
            Priority = ThreadPriority.BelowNormal,
        };

        // MTA: UIA client calls are made off any UI thread and must not marshal through an STA
        // pump that this process does not run.
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public string? TryGet(IntPtr hwnd, string processName, string windowTitle)
    {
        if (!Supported.Contains(processName))
        {
            return null;
        }

        var key = Key(hwnd, windowTitle);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // Reserve the key immediately so the same window is queued once, not once per tick.
        if (!_cache.TryAdd(key, null))
        {
            return null;
        }

        TrimCache();
        _queue.TryAdd(new Request(hwnd, processName, key));

        return null;
    }

    private static string Key(IntPtr hwnd, string windowTitle) =>
        hwnd.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + windowTitle;

    private void TrimCache()
    {
        if (_cache.Count <= MaxCacheEntries)
        {
            return;
        }

        // Coarse but adequate: a long-running agent must not accumulate an entry per window title
        // seen in a month. Losing warm entries only costs one more background walk.
        foreach (var key in _cache.Keys.Take(_cache.Count / 2))
        {
            _cache.TryRemove(key, out _);
        }
    }

    private void Run()
    {
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    var path = ResolveWithTimeout(request);
                    if (!string.IsNullOrEmpty(path))
                    {
                        _cache[request.Key] = path;
                    }
                }
                catch (Exception ex)
                {
                    _log.Debug($"UIA path resolution failed for {request.ProcessName}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string? ResolveWithTimeout(Request request)
    {
        // A hung UIA call cannot be cancelled, so the worker abandons the wait and moves on. The
        // orphaned call completes into a dead task rather than wedging the queue forever.
        var task = Task.Run(() => Resolve(request), _cts.Token);
        return task.Wait(ResolveTimeout) ? task.Result : null;
    }

    private string? Resolve(Request request)
    {
        if (!Interop.NativeMethods.IsWindow(request.Hwnd))
        {
            return null;
        }

        var root = AutomationElement.FromHandle(request.Hwnd);
        if (root is null)
        {
            return null;
        }

        var visited = 0;
        return Search(root, depth: 0, ref visited);
    }

    /// <summary>
    /// Bounded breadth-limited walk for the first element whose <see cref="ValuePattern"/> holds a
    /// rooted path. Both budgets exist because a UIA tree can be enormous and this runs on every
    /// new window on someone's laptop.
    /// </summary>
    private string? Search(AutomationElement element, int depth, ref int visited)
    {
        if (depth > MaxDepth || visited >= MaxNodesVisited || _cts.IsCancellationRequested)
        {
            return null;
        }

        visited++;

        if (TryReadPath(element, out var path))
        {
            return path;
        }

        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? child;

        try
        {
            child = walker.GetFirstChild(element);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }

        while (child is not null && visited < MaxNodesVisited)
        {
            var found = Search(child, depth + 1, ref visited);
            if (found is not null)
            {
                return found;
            }

            try
            {
                child = walker.GetNextSibling(child);
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryReadPath(AutomationElement element, out string? path)
    {
        path = null;

        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                || pattern is not ValuePattern value)
            {
                return false;
            }

            var text = value.Current.Value;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4096 || !RootedPathRegex().IsMatch(text))
            {
                return false;
            }

            path = text.Trim();
            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^([a-zA-Z]:\\|\\\\[^\\])[^<>|?*""]*$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex RootedPathRegex();

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();

        // Bounded join: the worker may be inside an unresponsive UIA call, and shutdown must not
        // wait on a third-party process.
        _worker.Join(TimeSpan.FromSeconds(2));

        _queue.Dispose();
        _cts.Dispose();
    }

    private readonly record struct Request(IntPtr Hwnd, string ProcessName, string Key);
}
