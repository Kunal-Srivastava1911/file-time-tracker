using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace FileTimeTracker.Agent.Deployment;

/// <summary>
/// Registers the two scheduled tasks from design.md §6: the agent "At log on" and the watchdog
/// every few minutes.
/// </summary>
/// <remarks>
/// <para>Both run as the <b>Users</b> group with an interactive token rather than as SYSTEM. That
/// is a hard requirement, not a preference: a Session 0 service cannot read the interactive
/// desktop's foreground window (§6), so a SYSTEM-scheduled agent would return nothing all day.
/// Registering against the group means the task fires for whichever employee signs in, in their
/// own session.</para>
/// <para>The task definitions are generated here rather than shipped as static XML so the
/// executable path always matches the chosen install directory.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ScheduledTasks
{
    public const string Folder = "FileTimeTracker";
    public const string AgentTaskName = Folder + @"\Agent";
    public const string WatchdogTaskName = Folder + @"\Watchdog";

    /// <summary>Well-known SID of the local Users group; language-independent.</summary>
    private const string UsersGroupSid = "S-1-5-32-545";

    public static int Install(string installFolder, int watchdogMinutes, TextWriter output)
    {
        var agentExe = Path.Combine(installFolder, "FileTimeTracker.Agent.exe");
        var watchdogExe = Path.Combine(installFolder, "FileTimeTracker.Watchdog.exe");

        if (!File.Exists(agentExe))
        {
            output.WriteLine($"install-tasks: '{agentExe}' does not exist.");
            return 3;
        }

        var result = Register(AgentTaskName, BuildAgentTaskXml(agentExe), output);
        if (result != 0)
        {
            return result;
        }

        if (!File.Exists(watchdogExe))
        {
            output.WriteLine($"install-tasks: '{watchdogExe}' does not exist; skipped the watchdog task.");
            return 0;
        }

        return Register(WatchdogTaskName, BuildWatchdogTaskXml(watchdogExe, watchdogMinutes), output);
    }

    public static int Uninstall(TextWriter output)
    {
        // Deleting a task that is already gone is not a failure during uninstall.
        Delete(WatchdogTaskName, output);
        Delete(AgentTaskName, output);
        return 0;
    }

    private static int Register(string taskName, string xml, TextWriter output)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ftt-task-{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks /XML requires UTF-16 with a BOM; UTF-8 is rejected with a parse error.
            File.WriteAllText(path, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            var exit = RunSchTasks($"/Create /TN \"{taskName}\" /XML \"{path}\" /F", output);
            if (exit == 0)
            {
                output.WriteLine($"Registered scheduled task '{taskName}'.");
            }
            else
            {
                output.WriteLine($"Could not register scheduled task '{taskName}' (schtasks exit {exit}).");
            }

            return exit;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Delete(string taskName, TextWriter output)
    {
        var exit = RunSchTasks($"/Delete /TN \"{taskName}\" /F", output);
        output.WriteLine(exit == 0
            ? $"Removed scheduled task '{taskName}'."
            : $"Scheduled task '{taskName}' was not present.");
    }

    private static int RunSchTasks(string arguments, TextWriter output)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            output.WriteLine("Could not start schtasks.exe.");
            return 1;
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        if (process.ExitCode != 0)
        {
            output.WriteLine(string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
        }

        return process.ExitCode;
    }

    /// <summary>Public so the generated definition can be asserted on without registering it.</summary>
    public static string BuildAgentTaskXml(string agentExe) => TaskXml(
        description:
            "Records which document is in the foreground during the working day. Installed by the " +
            "employer under a documented monitoring policy.",
        command: agentExe,
        triggers: """
                <LogonTrigger>
                  <Enabled>true</Enabled>
                </LogonTrigger>
            """,
        // PT0S: never time-limited. This is a long-running agent, not a job.
        executionTimeLimit: "PT0S",
        restartOnFailure: """
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
            """);

    public static string BuildWatchdogTaskXml(string watchdogExe, int minutes) => TaskXml(
        description: "Verifies the File-Time Tracker agent is alive and restarts it if not.",
        command: watchdogExe,
        triggers: $"""
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <Repetition>
                    <Interval>PT{minutes}M</Interval>
                    <StopAtDurationEnd>false</StopAtDurationEnd>
                  </Repetition>
                </LogonTrigger>
            """,
        // Each check is a few milliseconds; cap it so a wedged check cannot pile up.
        executionTimeLimit: "PT5M",
        restartOnFailure: string.Empty);

    private static string TaskXml(
        string description,
        string command,
        string triggers,
        string executionTimeLimit,
        string restartOnFailure) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>{SecurityElement.Escape(description)}</Description>
            <Author>{SecurityElement.Escape(Environment.UserDomainName)}</Author>
          </RegistrationInfo>
          <Triggers>
        {triggers}
          </Triggers>
          <Principals>
            <Principal id="Author">
              <GroupId>{UsersGroupSid}</GroupId>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <!-- §2, no stealth: the task stays visible in Task Scheduler. The agent shows no
                 window because it is a WinExe, not because it is concealed. -->
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>{executionTimeLimit}</ExecutionTimeLimit>
            <Priority>7</Priority>
        {restartOnFailure}
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(command)}</Command>
            </Exec>
          </Actions>
        </Task>
        """;
}
