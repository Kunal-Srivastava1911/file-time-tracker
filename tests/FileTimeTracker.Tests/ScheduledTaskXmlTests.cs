using System.Xml.Linq;
using FileTimeTracker.Agent.Deployment;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>
/// The §6 scheduled tasks. These assert the generated definition rather than registering it, so
/// they run without elevation while still catching the mistakes that matter: a task that runs as
/// the wrong principal records nothing at all, and Task Scheduler rejects malformed XML at install
/// time on someone's laptop rather than here.
/// </summary>
public class ScheduledTaskXmlTests
{
    private const string TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private const string AgentExe = @"C:\Program Files\FileTimeTracker\FileTimeTracker.Agent.exe";

    private static XElement Parse(string xml) => XDocument.Parse(xml).Root!;

    private static string? Value(XElement root, params string[] path)
    {
        XElement? current = root;
        foreach (var name in path)
        {
            current = current?.Element(XName.Get(name, TaskNs));
        }

        return current?.Value;
    }

    [Fact]
    public void The_agent_task_is_well_formed_and_runs_the_agent()
    {
        var root = Parse(ScheduledTasks.BuildAgentTaskXml(AgentExe));

        Assert.Equal(XName.Get("Task", TaskNs), root.Name);
        Assert.Equal(AgentExe, Value(root, "Actions", "Exec", "Command"));
    }

    [Fact]
    public void The_agent_task_triggers_at_logon()
    {
        var root = Parse(ScheduledTasks.BuildAgentTaskXml(AgentExe));
        var triggers = root.Element(XName.Get("Triggers", TaskNs))!;

        Assert.Single(triggers.Elements(XName.Get("LogonTrigger", TaskNs)));
    }

    [Fact]
    public void The_agent_task_runs_as_the_users_group_with_least_privilege()
    {
        // Not SYSTEM: a Session 0 service cannot read the interactive desktop's foreground window
        // (§6), so getting this wrong yields an agent that runs and records nothing.
        var root = Parse(ScheduledTasks.BuildAgentTaskXml(AgentExe));
        var principal = root.Element(XName.Get("Principals", TaskNs))!
            .Element(XName.Get("Principal", TaskNs))!;

        Assert.Equal("S-1-5-32-545", principal.Element(XName.Get("GroupId", TaskNs))!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(XName.Get("RunLevel", TaskNs))!.Value);
    }

    [Fact]
    public void The_agent_task_is_not_time_limited_and_restarts_on_failure()
    {
        var root = Parse(ScheduledTasks.BuildAgentTaskXml(AgentExe));
        var settings = root.Element(XName.Get("Settings", TaskNs))!;

        // PT0S means no execution time limit; anything else would kill a long-running agent.
        Assert.Equal("PT0S", settings.Element(XName.Get("ExecutionTimeLimit", TaskNs))!.Value);
        Assert.Equal("PT1M", Value(settings, "RestartOnFailure", "Interval"));
        Assert.Equal("3", Value(settings, "RestartOnFailure", "Count"));
        Assert.Equal("IgnoreNew", settings.Element(XName.Get("MultipleInstancesPolicy", TaskNs))!.Value);
    }

    [Fact]
    public void Neither_task_is_hidden()
    {
        // §2: no stealth. The tasks stay visible in Task Scheduler.
        foreach (var xml in new[]
                 {
                     ScheduledTasks.BuildAgentTaskXml(AgentExe),
                     ScheduledTasks.BuildWatchdogTaskXml(AgentExe, 5),
                 })
        {
            var settings = Parse(xml).Element(XName.Get("Settings", TaskNs))!;
            Assert.Equal("false", settings.Element(XName.Get("Hidden", TaskNs))!.Value);
        }
    }

    [Fact]
    public void Neither_task_stops_on_battery()
    {
        // A laptop unplugged for the afternoon must keep recording.
        foreach (var xml in new[]
                 {
                     ScheduledTasks.BuildAgentTaskXml(AgentExe),
                     ScheduledTasks.BuildWatchdogTaskXml(AgentExe, 5),
                 })
        {
            var settings = Parse(xml).Element(XName.Get("Settings", TaskNs))!;
            Assert.Equal("false", settings.Element(XName.Get("DisallowStartIfOnBatteries", TaskNs))!.Value);
            Assert.Equal("false", settings.Element(XName.Get("StopIfGoingOnBatteries", TaskNs))!.Value);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    public void The_watchdog_repeats_on_the_requested_interval(int minutes)
    {
        var root = Parse(ScheduledTasks.BuildWatchdogTaskXml(AgentExe, minutes));
        var trigger = root.Element(XName.Get("Triggers", TaskNs))!
            .Element(XName.Get("LogonTrigger", TaskNs))!;

        Assert.Equal($"PT{minutes}M", Value(trigger, "Repetition", "Interval"));
        Assert.Equal("false", Value(trigger, "Repetition", "StopAtDurationEnd"));
    }

    [Fact]
    public void The_watchdog_is_time_limited_unlike_the_agent()
    {
        var settings = Parse(ScheduledTasks.BuildWatchdogTaskXml(AgentExe, 5))
            .Element(XName.Get("Settings", TaskNs))!;

        Assert.Equal("PT5M", settings.Element(XName.Get("ExecutionTimeLimit", TaskNs))!.Value);
    }

    [Fact]
    public void A_path_containing_xml_special_characters_is_escaped()
    {
        // Install folders are admin-chosen and an unescaped ampersand would corrupt the definition.
        var root = Parse(ScheduledTasks.BuildAgentTaskXml(@"C:\Program Files\R&D Tools\Agent.exe"));

        Assert.Equal(@"C:\Program Files\R&D Tools\Agent.exe", Value(root, "Actions", "Exec", "Command"));
    }

    [Fact]
    public void Task_names_live_under_one_folder()
    {
        Assert.Equal(@"FileTimeTracker\Agent", ScheduledTasks.AgentTaskName);
        Assert.Equal(@"FileTimeTracker\Watchdog", ScheduledTasks.WatchdogTaskName);
    }
}
