using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FileTimeTracker.Core.Diagnostics;

namespace FileTimeTracker.Agent.Runtime;

/// <summary>
/// The optional first-run notice banner from design.md §2. Off unless the admin enables
/// <c>show_first_run_notice</c>; the written policy remains the primary notification.
/// </summary>
/// <remarks>
/// Shown once per Windows user, tracked by a marker in that user's own profile. Displayed on a
/// dedicated thread so a dialog nobody dismisses cannot stall sampling.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class FirstRunNotice
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbSetForeground = 0x00010000;
    private const uint MbTopMost = 0x00040000;

    private const string Title = "File-Time Tracker";

    private const string Body =
        "This company-owned laptop records which document or application is in the foreground " +
        "during the working day, and how much of that time is active versus idle.\n\n" +
        "It does not record keystrokes, screen contents, or personal accounts.\n\n" +
        "The software is listed in Task Manager and in Installed apps. Refer to the employee " +
        "monitoring notice issued by your administrator for the full policy, the retention period, " +
        "and how to raise a query.";

    public static void ShowOnce(TrackerLog log)
    {
        var marker = MarkerPath();

        try
        {
            if (File.Exists(marker))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            log.Warn("Could not record the first-run notice marker; skipping the notice.", ex);
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                MessageBox(IntPtr.Zero, Body, Title, MbOk | MbIconInformation | MbSetForeground | MbTopMost);
            }
            catch (Exception ex)
            {
                log.Warn("Could not display the first-run notice.", ex);
            }
        })
        {
            IsBackground = true,
            Name = "first-run-notice",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        log.Info("Displayed the first-run monitoring notice.");
    }

    private static string MarkerPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Configuration.AgentPaths.ProductFolder,
        "notice-shown.marker");

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
