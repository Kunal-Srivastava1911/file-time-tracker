using System.Text.RegularExpressions;
using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Core.Parsing;

/// <summary>
/// Turns a process name plus a raw window title into the <c>app</c> / <c>file</c> / <c>source</c>
/// columns (design.md §4.1). Pure string logic with no Win32 dependency, which is what makes the
/// §8 per-app parsing tests possible.
/// </summary>
public sealed partial class TitleParser
{
    /// <summary>Title separators in the wild: hyphen, en dash, em dash.</summary>
    private static readonly string[] Separators = { " - ", " – ", " — " };

    /// <summary>Unsaved-changes markers various editors prepend.</summary>
    private static readonly char[] DirtyMarkers = { '*', '●', '•', '◆', '▲', '‎' };

    private readonly IReadOnlyList<AppRule> _rules;
    private readonly Dictionary<string, AppRule> _byProcess;

    public TitleParser(IReadOnlyList<AppRule>? rules = null)
    {
        _rules = rules ?? AppRules.Default;
        _byProcess = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _rules)
        {
            foreach (var process in rule.ProcessNames)
            {
                _byProcess[process] = rule;
            }
        }
    }

    /// <param name="processName">Process name with or without <c>.exe</c>.</param>
    /// <param name="windowTitle">Raw <c>GetWindowText</c> output.</param>
    /// <param name="productName">
    /// Optional <c>FileVersionInfo.ProductName</c>, used to name and de-suffix applications that
    /// have no explicit rule yet.
    /// </param>
    public TitleParseResult Parse(string processName, string? windowTitle, string? productName = null)
    {
        var process = NormalizeProcessName(processName);
        var rule = _byProcess.GetValueOrDefault(process);

        var app = rule?.App
                  ?? (string.IsNullOrWhiteSpace(productName) ? PrettifyProcessName(process) : productName.Trim());

        var title = CleanTitle(windowTitle);
        if (title.Length == 0)
        {
            return EmptyResult(app, rule);
        }

        if (rule?.Custom is not null)
        {
            var custom = rule.Custom(title);
            if (custom is not null && custom.File.Length > 0)
            {
                return Finalize(app, custom.File, custom.Source);
            }
        }

        var segments = SplitSegments(title);
        segments = StripTrailingAppNames(segments, rule, app, productName, process);
        segments = StripNoise(segments, rule);

        var file = Pick(segments, rule?.Pick ?? SegmentPick.JoinAll);
        file = TrimDecorations(file);

        if (file.Length == 0)
        {
            return EmptyResult(app, rule);
        }

        return Finalize(app, file, rule?.Source ?? FileSource.DesktopTitle);
    }

    /// <summary>
    /// A title that already contains a full path (Notepad++, some editors) upgrades the row: the
    /// path is recorded and the file name is reduced to the leaf, no UI Automation needed.
    /// </summary>
    private static TitleParseResult Finalize(string app, string file, FileSource source)
    {
        if (!LooksLikePath(file))
        {
            return new TitleParseResult(app, file, null, source);
        }

        string leaf;
        try
        {
            leaf = Path.GetFileName(file);
        }
        catch (ArgumentException)
        {
            return new TitleParseResult(app, file, null, source);
        }

        return leaf.Length == 0
            ? new TitleParseResult(app, file, null, source)
            : new TitleParseResult(app, leaf, file, FileSource.UiaFilePath);
    }

    private static TitleParseResult EmptyResult(string app, AppRule? rule)
    {
        var file = rule is null || rule.EmptyTitleFile.Length == 0 ? app : rule.EmptyTitleFile;
        return new TitleParseResult(app, file, null, rule?.EmptyTitleSource ?? FileSource.AppOnly);
    }

    public static IReadOnlyList<string> SplitSegments(string title)
    {
        var working = new List<string> { title };

        foreach (var separator in Separators)
        {
            var next = new List<string>();
            foreach (var part in working)
            {
                next.AddRange(part.Split(separator, StringSplitOptions.None));
            }

            working = next;
        }

        return working.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    private static IReadOnlyList<string> StripTrailingAppNames(
        IReadOnlyList<string> segments, AppRule? rule, string app, string? productName, string process)
    {
        var candidates = new List<string>(rule?.AppSuffixes ?? Array.Empty<string>()) { app, process };

        if (!string.IsNullOrWhiteSpace(productName))
        {
            candidates.Add(productName.Trim());
        }

        var result = segments.ToList();

        // Loop: Edge stacks two trailing tokens ("… - Personal - Microsoft Edge").
        // Never strip the last remaining segment — "Word - Word" must still yield a document.
        var changed = true;
        while (changed && result.Count > 1)
        {
            changed = false;
            var last = result[^1];

            if (candidates.Any(c => c.Length > 0 && string.Equals(c, last, StringComparison.OrdinalIgnoreCase)))
            {
                result.RemoveAt(result.Count - 1);
                changed = true;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> StripNoise(IReadOnlyList<string> segments, AppRule? rule)
    {
        if (rule is null || rule.NoiseSegments.Length == 0 || segments.Count <= 1)
        {
            return segments;
        }

        var filtered = segments
            .Where(s => !rule.NoiseSegments.Any(n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return filtered.Count == 0 ? segments : filtered;
    }

    private static string Pick(IReadOnlyList<string> segments, SegmentPick pick)
    {
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        return pick switch
        {
            SegmentPick.First => segments[0],
            SegmentPick.Last => segments[^1],
            _ => string.Join(" - ", segments),
        };
    }

    private static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // Zero-width characters really do appear in shipped titles (Edge embeds U+200B).
        var cleaned = ZeroWidthRegex().Replace(title, string.Empty).Trim();
        cleaned = UnreadCountRegex().Replace(cleaned, string.Empty);
        cleaned = cleaned.Trim(DirtyMarkers).Trim();

        return cleaned;
    }

    private static string TrimDecorations(string value)
    {
        var trimmed = MorePagesRegex().Replace(value, string.Empty);
        trimmed = trimmed.Trim().Trim(DirtyMarkers).Trim().Trim('"');
        return trimmed;
    }

    private static bool LooksLikePath(string value) => PathRegex().IsMatch(value);

    private static string NormalizeProcessName(string processName)
    {
        var name = (processName ?? string.Empty).Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name;
    }

    private static string PrettifyProcessName(string process) =>
        process.Length == 0 ? "Unknown" : char.ToUpperInvariant(process[0]) + process[1..];

    [GeneratedRegex(@"[​‌‍‎‏﻿]", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex ZeroWidthRegex();

    /// <summary>Leading unread badge, e.g. <c>(3) Inbox - Gmail</c>.</summary>
    [GeneratedRegex(@"^\(\d+\)\s*", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex UnreadCountRegex();

    /// <summary>Edge/Chrome tab-group suffix, e.g. <c>Report and 4 more pages</c>.</summary>
    [GeneratedRegex(@"\s+and\s+\d+\s+more\s+pages?$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex MorePagesRegex();

    /// <summary>Drive-rooted or UNC path.</summary>
    [GeneratedRegex(@"^([a-zA-Z]:\\|\\\\[^\\]).*[^\\]$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex PathRegex();
}
