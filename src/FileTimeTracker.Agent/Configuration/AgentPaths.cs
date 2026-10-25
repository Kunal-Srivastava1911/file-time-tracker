namespace FileTimeTracker.Agent.Configuration;

/// <summary>
/// Fixed on-disk locations for the agent.
/// </summary>
/// <remarks>
/// Split into two tiers on purpose. The root holds installer-written files and is ACL'd
/// administrators-write / users-read, which is what keeps an employee from repointing the upload
/// destination or reading out a usable credential path (§2, §4.3). <see cref="DataDirectory"/> is
/// user-writable because the agent runs as the logged-on employee and must be able to buffer.
/// </remarks>
public static class AgentPaths
{
    public const string ProductFolder = "FileTimeTracker";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductFolder);

    /// <summary>Admin-written machine identity (Computer No., salt). Users read only.</summary>
    public static string MachineSettingsFile => Path.Combine(Root, "machine.json");

    /// <summary>DPAPI-encrypted Dropbox refresh token. Users read only.</summary>
    public static string CredentialFile => Path.Combine(Root, "dropbox.dat");

    /// <summary>User-writable working area.</summary>
    public static string DataDirectory => Path.Combine(Root, "data");

    public static string DatabaseFile => Path.Combine(DataDirectory, "agent.db");

    public static string LogFile => Path.Combine(DataDirectory, "agent.log");

    /// <summary>Last good central config, so an offline start is not an unconfigured start.</summary>
    public static string ConfigCacheFile => Path.Combine(DataDirectory, "config.cache.json");

    /// <summary>Liveness marker the watchdog polls (§6).</summary>
    public static string HeartbeatFile => Path.Combine(DataDirectory, "heartbeat.json");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
