using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Parsing;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>design.md §8, unit: "title→filename parsing per app".</summary>
public class TitleParserTests
{
    private readonly TitleParser _parser = new();

    [Theory]
    // Word
    [InlineData("winword", "Q3 Report.docx - Word", "Word", "Q3 Report.docx")]
    [InlineData("winword", "Document1 - Word", "Word", "Document1")]
    [InlineData("winword", "Budget.docx - Compatibility Mode - Word", "Word", "Budget.docx")]
    [InlineData("winword", "Policy.docx - Saved to OneDrive - Word", "Word", "Policy.docx")]
    [InlineData("winword", "Notes.docx - Read-Only - Word", "Word", "Notes.docx")]
    // A document whose own name contains " - " must survive intact.
    [InlineData("winword", "Report - Draft.docx - Word", "Word", "Report - Draft.docx")]
    // Excel
    [InlineData("excel", "Budget FY26.xlsx - Excel", "Excel", "Budget FY26.xlsx")]
    [InlineData("excel", "Book1 - Excel", "Excel", "Book1")]
    [InlineData("excel", "Ledger.xlsx - Protected View - Excel", "Excel", "Ledger.xlsx")]
    // PowerPoint
    [InlineData("powerpnt", "Kickoff.pptx - PowerPoint", "PowerPoint", "Kickoff.pptx")]
    // PDF
    [InlineData("acrord32", "Invoice-2291.pdf - Adobe Acrobat Reader DC", "Adobe Acrobat", "Invoice-2291.pdf")]
    [InlineData("acrobat", "Adobe Acrobat Reader DC - [Contract.pdf]", "Adobe Acrobat", "Contract.pdf")]
    // Code editors: file first, workspace after.
    [InlineData("code", "Program.cs - cresco - Visual Studio Code", "Visual Studio Code", "Program.cs")]
    [InlineData("code", "● Program.cs - cresco - Visual Studio Code", "Visual Studio Code", "Program.cs")]
    [InlineData("devenv", "Startup.cs - MySolution - Microsoft Visual Studio", "Visual Studio", "Startup.cs")]
    [InlineData("devenv", "MySolution - Microsoft Visual Studio", "Visual Studio", "MySolution")]
    // Notepad, including the dirty marker.
    [InlineData("notepad", "notes.txt - Notepad", "Notepad", "notes.txt")]
    [InlineData("notepad", "*notes.txt - Notepad", "Notepad", "notes.txt")]
    public void Parses_document_titles(string process, string title, string expectedApp, string expectedFile)
    {
        var result = _parser.Parse(process, title);

        Assert.Equal(expectedApp, result.App);
        Assert.Equal(expectedFile, result.File);
    }

    [Fact]
    public void Notepadpp_title_yields_a_full_path_without_ui_automation()
    {
        var result = _parser.Parse("notepad++", @"C:\work\notes\todo.txt - Notepad++");

        Assert.Equal("todo.txt", result.File);
        Assert.Equal(@"C:\work\notes\todo.txt", result.FilePath);
        Assert.Equal(FileSource.UiaFilePath, result.Source);
    }

    [Theory]
    [InlineData("chrome", "Monthly plan - Google Docs - Google Chrome", "Chrome", "Monthly plan - Google Docs")]
    [InlineData("chrome", "(3) Inbox (24) - Gmail - Google Chrome", "Chrome", "Inbox (24) - Gmail")]
    [InlineData("firefox", "Wikipedia — Mozilla Firefox", "Firefox", "Wikipedia")]
    [InlineData("msedge", "Dashboard and 4 more pages - Work - Microsoft Edge", "Edge", "Dashboard")]
    public void Web_apps_degrade_to_the_tab_name_and_are_flagged(
        string process, string title, string expectedApp, string expectedFile)
    {
        var result = _parser.Parse(process, title);

        Assert.Equal(expectedApp, result.App);
        Assert.Equal(expectedFile, result.File);

        // §9: browser rows must be distinguishable from real documents in the report.
        Assert.Equal(FileSource.BrowserTab, result.Source);
        Assert.False(result.Source.IsRealFile());
    }

    [Fact]
    public void Edge_titles_containing_a_zero_width_space_still_lose_the_app_suffix()
    {
        // Edge really does embed U+200B inside its own name.
        var result = _parser.Parse("msedge", "Timesheet - Microsoft\u200b Edge");

        Assert.Equal("Timesheet", result.File);
    }

    [Theory]
    [InlineData("Acme Traders - TallyPrime", "Tally: Acme Traders")]
    [InlineData("TallyPrime (Educational Mode) - [Acme Traders]", "Tally: Acme Traders")]
    public void TallyPrime_reports_the_company_dataset_not_a_file(string title, string expectedFile)
    {
        var result = _parser.Parse("tallyprime", title);

        Assert.Equal("TallyPrime", result.App);
        Assert.Equal(expectedFile, result.File);

        // §9 open item: Tally works on a dataset, so it must never look like a file.
        Assert.Equal(FileSource.AppContext, result.Source);
        Assert.False(result.Source.IsRealFile());
    }

    [Fact]
    public void Bare_TallyPrime_title_falls_back_to_no_company()
    {
        var result = _parser.Parse("tallyprime", "TallyPrime");

        Assert.Equal("Tally: (no company)", result.File);
        Assert.Equal(FileSource.AppContext, result.Source);
    }

    [Fact]
    public void Empty_title_falls_back_to_the_apps_placeholder()
    {
        var result = _parser.Parse("winword", "");

        Assert.Equal("Word", result.App);
        Assert.Equal("(no document)", result.File);
        Assert.Equal(FileSource.AppOnly, result.Source);
    }

    [Fact]
    public void Unknown_application_is_named_from_its_product_name_and_de_suffixed()
    {
        var result = _parser.Parse("someeditor", "budget.ods - SomeEditor Pro", productName: "SomeEditor Pro");

        Assert.Equal("SomeEditor Pro", result.App);
        Assert.Equal("budget.ods", result.File);
    }

    [Fact]
    public void Unknown_application_without_a_product_name_still_yields_something_usable()
    {
        var result = _parser.Parse("mytool.exe", "Untitled");

        Assert.Equal("Mytool", result.App);
        Assert.Equal("Untitled", result.File);
    }

    [Fact]
    public void A_title_that_is_only_the_app_name_is_not_stripped_to_nothing()
    {
        // Guards the suffix-stripping loop: it must never consume the last segment.
        var result = _parser.Parse("winword", "Word");

        Assert.Equal("Word", result.File);
    }

    [Fact]
    public void Process_name_matching_ignores_the_exe_extension_and_case()
    {
        var withExtension = _parser.Parse("WINWORD.EXE", "Plan.docx - Word");
        var without = _parser.Parse("winword", "Plan.docx - Word");

        Assert.Equal(without.App, withExtension.App);
        Assert.Equal(without.File, withExtension.File);
    }
}
