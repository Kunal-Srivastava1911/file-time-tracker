using System.Runtime.Versioning;
using System.Security.Cryptography;
using FileTimeTracker.Agent.Configuration;
using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Security;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Tray;

/// <summary>
/// First-run setup for the desktop app: works out where Dropbox is, writes the machine settings and
/// seeds a <c>config.json</c> the user can tweak later.
/// </summary>
/// <remarks>
/// The fleet install does this from an elevated MSI custom action (§6). Here it has to happen with
/// no admin rights and no questions beyond a display name, so settings go under the user's own
/// LocalAppData rather than ProgramData.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SetupService
{
    /// <summary>
    /// Per-user settings location. Not ProgramData: this app runs unelevated, and the data belongs
    /// to the one person using it rather than to the machine.
    /// </summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileTimeTracker");

    public static string SettingsFile => Path.Combine(Root, "machine.json");

    public static string LogFile => Path.Combine(Root, "tracker.log");

    /// <summary>
    /// Working area for the raw JSONL logs, the heartbeat and <c>config.json</c>.
    /// </summary>
    /// <remarks>
    /// Local on purpose, and never inside Dropbox. These are the app's own files; syncing them puts
    /// four kinds of plumbing in front of the one spreadsheet the user opened Dropbox to find, and
    /// the logs in particular churn every upload interval for no reader's benefit.
    /// </remarks>
    public static string WorkingStore { get; } = Path.Combine(Root, "store");

    public static bool IsConfigured => File.Exists(SettingsFile);

    public static MachineSettings Load() => MachineSettings.Load(SettingsFile);

    /// <summary>
    /// True when an administrator has provisioned Dropbox credentials for this machine, which is
    /// the fleet case: reports go to the admin's app folder and the employee is asked nothing.
    /// </summary>
    public static bool IsFleetProvisioned => new DpapiSecretStore(AgentPaths.CredentialFile).Exists;

    /// <summary>
    /// Fleet setup, with no dialog. The administrator supplied the destination at install time, so
    /// the only thing left to decide is the display name, and Windows already knows it.
    /// </summary>
    public static MachineSettings ConfigureForFleet(TrackerLog log)
    {
        Directory.CreateDirectory(Root);

        var machine = File.Exists(AgentPaths.MachineSettingsFile)
            ? MachineSettings.Load(AgentPaths.MachineSettingsFile)
            : null;

        var settings = new MachineSettings
        {
            // The installer's Computer No. wins: it is what the admin's employee map is keyed on.
            ComputerNo = machine?.ComputerNo ?? MakeComputerNo(),
            HashSalt = machine?.HashSalt ?? string.Empty,
            ConfigPath = machine?.ConfigPath ?? "/config.json",
            TimeZoneId = machine?.TimeZoneId ?? string.Empty,
            LogLevel = machine?.LogLevel ?? "Info",
            StorageBackend = RemoteStorageFactory.Dropbox,
            StorageRoot = string.Empty,

            // Fleet reports go to the admin's app folder, not to a folder on this laptop.
            PublishRoot = string.Empty,
        };

        settings.Save(SettingsFile);
        log.Info($"Configured for the fleet Dropbox app folder as '{settings.ComputerNo}'.");

        return settings;
    }

    /// <summary>
    /// Suggested folder for finished spreadsheets, or null when the Dropbox client cannot be found.
    /// </summary>
    public static string? SuggestPublishRoot() => DropboxFolderLocator.FindPublishRoot();

    /// <summary>Suggested display name, from Windows rather than asking twice for the same thing.</summary>
    public static string SuggestDisplayName()
    {
        var name = Environment.UserName;
        return string.IsNullOrWhiteSpace(name) ? "Me" : name;
    }

    /// <summary>
    /// Writes settings and seeds the central config. Returns the settings the session should use.
    /// </summary>
    /// <param name="publishRoot">
    /// Where finished spreadsheets go — the folder the user picked, normally inside Dropbox. The
    /// working store is not asked about: it is always <see cref="WorkingStore"/>, because there is
    /// no reason for anyone to choose where the app buffers its own logs.
    /// </param>
    public static MachineSettings Configure(string displayName, string publishRoot, TrackerLog log)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("A name is required so the report can be labelled.", nameof(displayName));
        }

        if (string.IsNullOrWhiteSpace(publishRoot))
        {
            throw new ArgumentException("A folder is required.", nameof(publishRoot));
        }

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(WorkingStore);

        var existing = IsConfigured ? Load() : null;

        var settings = new MachineSettings
        {
            // One machine, one user: a stable identifier derived from the machine name is friendlier
            // than asking for a "Computer No." that would mean nothing to someone tracking themselves.
            ComputerNo = existing?.ComputerNo ?? MakeComputerNo(),
            HashSalt = existing?.HashSalt ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ConfigPath = existing?.ConfigPath ?? "/config.json",
            TimeZoneId = existing?.TimeZoneId ?? string.Empty,
            LogLevel = existing?.LogLevel ?? "Info",
            StorageBackend = RemoteStorageFactory.FileSystem,
            StorageRoot = WorkingStore,
            PublishRoot = publishRoot.Trim(),
        };

        settings.Save(SettingsFile);

        SeedConfig(settings, displayName.Trim(), log);

        return settings;
    }

    private static string MakeComputerNo()
    {
        var machine = Environment.MachineName;
        return string.IsNullOrWhiteSpace(machine) ? "PC-001" : RemotePath.SafeSegment(machine);
    }

    /// <summary>
    /// Ensures <c>config.json</c> maps this machine to the display name, so the workbook's Employee
    /// column is filled in.
    /// </summary>
    /// <remarks>
    /// Merges rather than skipping when the file already exists. Skipping looks tidier but leaves
    /// Employee permanently blank whenever a config written by some other install is already in the
    /// folder — with nothing on screen to explain why. Every other setting in the file is preserved,
    /// and an existing name for this machine is not overwritten.
    /// </remarks>
    private static void SeedConfig(MachineSettings settings, string displayName, TrackerLog log)
    {
        try
        {
            var storage = RemoteStorageFactory.CreateFileSystem(settings.StorageRoot, log);

            var existing = storage.DownloadTextAsync(settings.ConfigPath).GetAwaiter().GetResult();
            var config = Parse(existing, log) ?? new TrackerConfig();

            if (config.ComputerEmployeeMap.TryGetValue(settings.ComputerNo, out var mapped)
                && !string.IsNullOrWhiteSpace(mapped))
            {
                return;
            }

            config.ComputerEmployeeMap[settings.ComputerNo] = displayName;

            storage.UploadTextAsync(settings.ConfigPath, TrackerJson.SerializePretty(config))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            // Not fatal: without the mapping the report still totals correctly, just unlabelled.
            log.Warn("Could not update config.json; the Employee column may be blank.", ex);
        }
    }

    /// <summary>
    /// Null when the file is simply absent, so the caller starts fresh. Rethrows on malformed JSON
    /// so the caller abandons the write rather than replacing someone's settings with defaults.
    /// </summary>
    private static TrackerConfig? Parse(string? json, TrackerLog log)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            // A BOM from a text editor would otherwise throw and silently cost the user their
            // existing settings when this rewrote the file from defaults.
            return TrackerJson.Deserialize<TrackerConfig>(json.TrimStart('﻿'));
        }
        catch (System.Text.Json.JsonException ex)
        {
            log.Warn("config.json could not be parsed; leaving it alone rather than overwriting it.", ex);
            throw;
        }
    }
}
