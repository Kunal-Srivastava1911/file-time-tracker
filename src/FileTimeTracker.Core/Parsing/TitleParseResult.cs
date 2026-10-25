using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Core.Parsing;

/// <param name="App">Friendly application name for the <c>app</c> column.</param>
/// <param name="File">Document, tab or dataset name. Never empty.</param>
/// <param name="FilePath">Full path when the title itself carried one; otherwise null.</param>
/// <param name="Source">How <paramref name="File"/> was derived (design.md §3).</param>
public sealed record TitleParseResult(string App, string File, string? FilePath, FileSource Source);
