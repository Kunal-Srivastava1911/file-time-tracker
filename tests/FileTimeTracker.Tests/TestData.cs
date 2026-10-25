using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Tests;

internal static class TestData
{
    public static IntervalRecord Interval(
        string id,
        string computerNo = "PC-014",
        string file = "Plan.docx",
        string app = "Word",
        DateOnly? date = null,
        int activeSec = 600,
        int idleSec = 0,
        FileSource source = FileSource.DesktopTitle)
    {
        var localDate = date ?? new DateOnly(2026, 7, 20);
        var start = new DateTimeOffset(localDate.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(5.5));

        return new IntervalRecord
        {
            Id = id,
            ComputerNo = computerNo,
            WinUser = "asha",
            App = app,
            File = file,
            FilePath = null,
            WindowTitle = $"{file} - {app}",
            StartUtc = start.ToUniversalTime(),
            EndUtc = start.AddSeconds(activeSec + idleSec).ToUniversalTime(),
            ActiveSec = activeSec,
            IdleSec = idleSec,
            Source = source,
            LocalDate = localDate,
        };
    }
}
