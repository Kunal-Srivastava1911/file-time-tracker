using System.Text.RegularExpressions;
using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Core.Parsing;

/// <summary>
/// The per-application rule set (design.md §9). Ordered only for readability — lookup is by
/// process name, so entries do not compete.
/// </summary>
public static partial class AppRules
{
    /// <summary>Office decorations that are not part of the document name.</summary>
    private static readonly string[] OfficeNoise =
    {
        "Compatibility Mode",
        "Read-Only",
        "ReadOnly",
        "Protected View",
        "Saved to OneDrive",
        "Saved to this PC",
        "AutoRecovered",
        "Recovered",
        "Shared",
        "Restricted Access",
        "Last Modified by Another User",
    };

    public static IReadOnlyList<AppRule> Default { get; } = new List<AppRule>
    {
        // ---- Microsoft Office ------------------------------------------------------------
        new()
        {
            ProcessNames = new[] { "winword" },
            App = "Word",
            AppSuffixes = new[] { "Word", "Microsoft Word" },
            NoiseSegments = OfficeNoise,
            EmptyTitleFile = "(no document)",
        },
        new()
        {
            ProcessNames = new[] { "excel" },
            App = "Excel",
            AppSuffixes = new[] { "Excel", "Microsoft Excel" },
            NoiseSegments = OfficeNoise,
            EmptyTitleFile = "(no workbook)",
        },
        new()
        {
            ProcessNames = new[] { "powerpnt" },
            App = "PowerPoint",
            AppSuffixes = new[] { "PowerPoint", "Microsoft PowerPoint" },
            NoiseSegments = OfficeNoise,
            EmptyTitleFile = "(no presentation)",
        },
        new()
        {
            ProcessNames = new[] { "msaccess" },
            App = "Access",
            AppSuffixes = new[] { "Access", "Microsoft Access" },
            NoiseSegments = OfficeNoise,
        },
        new()
        {
            // Mail is not a document: report the folder/message context and flag it honestly.
            ProcessNames = new[] { "outlook", "olk" },
            App = "Outlook",
            Source = FileSource.AppContext,
            Pick = SegmentPick.First,
            AppSuffixes = new[] { "Outlook", "Microsoft Outlook" },
            EmptyTitleFile = "Outlook",
            EmptyTitleSource = FileSource.AppContext,
        },

        // ---- PDF -------------------------------------------------------------------------
        new()
        {
            ProcessNames = new[] { "acrobat", "acrord32", "acrodist" },
            App = "Adobe Acrobat",
            AppSuffixes = new[]
            {
                "Adobe Acrobat Reader DC", "Adobe Acrobat Reader", "Adobe Acrobat Pro DC",
                "Adobe Acrobat Pro", "Adobe Acrobat DC", "Adobe Acrobat",
            },
            Custom = BracketedDocument,
            EmptyTitleFile = "(no document)",
        },
        new()
        {
            ProcessNames = new[] { "foxitreader", "foxitphantompdf", "sumatrapdf", "pdfxedit" },
            App = "PDF Reader",
            AppSuffixes = new[] { "Foxit Reader", "Foxit PhantomPDF", "SumatraPDF", "PDF-XChange Editor" },
            Custom = BracketedDocument,
        },

        // ---- Text and code editors -------------------------------------------------------
        new()
        {
            ProcessNames = new[] { "notepad" },
            App = "Notepad",
            AppSuffixes = new[] { "Notepad" },
            EmptyTitleFile = "Untitled",
        },
        new()
        {
            // Notepad++ puts the full path first, so the path comes free.
            ProcessNames = new[] { "notepad++" },
            App = "Notepad++",
            Pick = SegmentPick.First,
            AppSuffixes = new[] { "Notepad++" },
        },
        new()
        {
            ProcessNames = new[] { "code", "code - insiders", "cursor", "windsurf" },
            App = "Visual Studio Code",
            Pick = SegmentPick.First,
            AppSuffixes = new[] { "Visual Studio Code", "Visual Studio Code - Insiders", "Cursor", "Windsurf" },
            EmptyTitleFile = "(no file)",
        },
        new()
        {
            ProcessNames = new[] { "devenv" },
            App = "Visual Studio",
            Pick = SegmentPick.First,
            AppSuffixes = new[] { "Microsoft Visual Studio" },
        },
        new()
        {
            ProcessNames = new[] { "sublime_text" },
            App = "Sublime Text",
            Pick = SegmentPick.First,
            AppSuffixes = new[] { "Sublime Text" },
        },
        new()
        {
            ProcessNames = new[] { "idea64", "pycharm64", "rider64", "webstorm64", "clion64" },
            App = "JetBrains IDE",
            Pick = SegmentPick.Last,
            AppSuffixes = Array.Empty<string>(),
        },

        // ---- Browsers: web/cloud apps degrade to the tab name (§9) ------------------------
        new()
        {
            ProcessNames = new[] { "chrome" },
            App = "Chrome",
            Source = FileSource.BrowserTab,
            AppSuffixes = new[] { "Google Chrome" },
            NoiseSegments = new[] { "Incognito" },
            EmptyTitleFile = "(browser)",
            EmptyTitleSource = FileSource.BrowserTab,
        },
        new()
        {
            ProcessNames = new[] { "msedge" },
            App = "Edge",
            Source = FileSource.BrowserTab,
            // The parser strips zero-width characters first, so the literal U+200B that Edge
            // embeds in its own title ("Microsoft<ZWSP> Edge") needs no special entry here.
            AppSuffixes = new[] { "Microsoft Edge", "Work", "Personal" },
            EmptyTitleFile = "(browser)",
            EmptyTitleSource = FileSource.BrowserTab,
        },
        new()
        {
            ProcessNames = new[] { "firefox" },
            App = "Firefox",
            Source = FileSource.BrowserTab,
            AppSuffixes = new[] { "Mozilla Firefox", "Mozilla Firefox Private Browsing" },
            EmptyTitleFile = "(browser)",
            EmptyTitleSource = FileSource.BrowserTab,
        },
        new()
        {
            ProcessNames = new[] { "brave", "opera", "vivaldi" },
            App = "Browser",
            Source = FileSource.BrowserTab,
            AppSuffixes = new[] { "Brave", "Opera", "Vivaldi" },
            EmptyTitleFile = "(browser)",
            EmptyTitleSource = FileSource.BrowserTab,
        },

        // ---- Accounting ------------------------------------------------------------------
        new()
        {
            // §9 open item: TallyPrime works on a company dataset, not files. The company name is
            // the closest honest analogue to a document, so it is reported as AppContext and
            // prefixed so it can never be mistaken for a real file in the workbook. *pilot*
            ProcessNames = new[] { "tally", "tallyprime", "tallyerp9" },
            App = "TallyPrime",
            Source = FileSource.AppContext,
            AppSuffixes = new[] { "TallyPrime", "Tally.ERP 9", "Tally" },
            Custom = TallyContext,
            EmptyTitleFile = "Tally: (no company)",
            EmptyTitleSource = FileSource.AppContext,
        },

        // ---- Shell and chat --------------------------------------------------------------
        new()
        {
            ProcessNames = new[] { "explorer" },
            App = "File Explorer",
            Source = FileSource.AppOnly,
            AppSuffixes = new[] { "File Explorer" },
            EmptyTitleFile = "File Explorer",
        },
        new()
        {
            ProcessNames = new[] { "ms-teams", "teams" },
            App = "Teams",
            Source = FileSource.AppContext,
            Pick = SegmentPick.First,
            AppSuffixes = new[] { "Microsoft Teams" },
            EmptyTitleFile = "Teams",
            EmptyTitleSource = FileSource.AppContext,
        },
    };

    private static readonly Dictionary<string, AppRule> ByProcess = BuildIndex(Default);

    private static Dictionary<string, AppRule> BuildIndex(IReadOnlyList<AppRule> rules)
    {
        var index = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            foreach (var process in rule.ProcessNames)
            {
                index[process] = rule;
            }
        }

        return index;
    }

    public static AppRule? Find(string processName) =>
        ByProcess.TryGetValue(processName, out var rule) ? rule : null;

    /// <summary>Handles the legacy MDI form <c>Adobe Acrobat Reader DC - [report.pdf]</c>.</summary>
    private static TitleParseResult? BracketedDocument(string title)
    {
        var match = BracketedRegex().Match(title);
        return match.Success
            ? new TitleParseResult(string.Empty, match.Groups["doc"].Value.Trim(), null, FileSource.DesktopTitle)
            : null;
    }

    /// <summary>
    /// Pulls the company/dataset out of a TallyPrime title. Observed forms include
    /// <c>TallyPrime</c>, <c>Acme Traders - TallyPrime</c> and <c>TallyPrime (Educational) - [Acme Traders]</c>.
    /// </summary>
    private static TitleParseResult? TallyContext(string title)
    {
        var bracketed = BracketedRegex().Match(title);
        if (bracketed.Success)
        {
            return new TitleParseResult(
                string.Empty, "Tally: " + bracketed.Groups["doc"].Value.Trim(), null, FileSource.AppContext);
        }

        // Strip a parenthesised licence/mode marker such as "(Educational Mode)".
        var cleaned = ParentheticalRegex().Replace(title, string.Empty).Trim();
        var segments = TitleParser.SplitSegments(cleaned)
            .Where(s => !s.StartsWith("Tally", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // No company in the title means none is open. Still return the prefixed placeholder rather
        // than falling through: every Tally row must carry the "Tally:" prefix so the workbook
        // never presents a dataset as though it were a file.
        var company = segments.Count == 0 ? "(no company)" : string.Join(" - ", segments);

        return new TitleParseResult(string.Empty, "Tally: " + company, null, FileSource.AppContext);
    }

    [GeneratedRegex(@"\[(?<doc>[^\]]+)\]", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex ParentheticalRegex();
}
