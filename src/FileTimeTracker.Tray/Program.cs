using System.Runtime.Versioning;
using System.Windows.Forms;
using FileTimeTracker.Agent.Configuration;
using FileTimeTracker.Core.Diagnostics;

namespace FileTimeTracker.Tray;

[SupportedOSPlatform("windows")]
internal static class Program
{
    /// <summary>
    /// Session-local: one tracker per signed-in user. Two running at once would double-count every
    /// interval, so a second launch surfaces the first window instead.
    /// </summary>
    private const string InstanceMutexName = @"Local\FileTimeTracker.Tray.Instance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "File-Time Tracker is already running. Look for it in the notification area, " +
                "near the clock.",
                "File-Time Tracker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        ApplicationConfiguration.Initialize();

        Directory.CreateDirectory(SetupService.Root);
        using var log = new TrackerLog(SetupService.LogFile, console: false, LogLevel.Info);

        try
        {
            var settings = Resolve(log);
            if (settings is null)
            {
                return;
            }

            using var session = new TrackerSession(settings, log);
            session.Start();

            Application.Run(new MainForm(session, log));
        }
        catch (Exception ex)
        {
            log.Error("The tracker could not start.", ex);

            MessageBox.Show(
                $"File-Time Tracker could not start.\r\n\r\n{ex.Message}\r\n\r\nDetails are in:\r\n{SetupService.LogFile}",
                "File-Time Tracker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>Loads settings, running first-time setup when there are none. Null means the user cancelled.</summary>
    private static MachineSettings? Resolve(TrackerLog log)
    {
        if (SetupService.IsConfigured)
        {
            // An install predating the publish folder still has everything in one synced folder.
            return StorageMigration.Apply(SetupService.Load(), log);
        }

        // A fleet laptop was given its destination by the installer. Asking the employee where
        // their reports should go would be both pointless and the wrong question.
        if (SetupService.IsFleetProvisioned)
        {
            return SetupService.ConfigureForFleet(log);
        }

        using var setup = new SetupForm(SetupService.SuggestDisplayName(), SetupService.SuggestPublishRoot());

        if (setup.ShowDialog() != DialogResult.OK)
        {
            return null;
        }

        return SetupService.Configure(setup.DisplayName, setup.PublishRoot, log);
    }
}
