using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using FileTimeTracker.Agent.Configuration;
using FileTimeTracker.Agent.Runtime;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Security;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Agent;

[SupportedOSPlatform("windows")]
internal static class Program
{
    /// <summary>
    /// Session-local so each signed-in user gets one agent, while the watchdog restarting a dead
    /// agent cannot produce two in the same session (design.md §6).
    /// </summary>
    private const string InstanceMutexName = @"Local\FileTimeTracker.Agent.Instance";

    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();

        return command switch
        {
            "provision" => Provision(args),
            "status" => Status(),
            "install-tasks" => InstallTasks(args),
            "uninstall-tasks" => UninstallTasks(),
            "defender-exclusion" => ConfigureDefender(args),
            "help" => Help(),
            _ when args.Contains("--help") || args.Contains("-h") || args.Contains("/?") => Help(),
            _ => await RunAsync(args).ConfigureAwait(false),
        };
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var console = args.Contains("--console");
        if (console)
        {
            AttachOrAllocConsole();
        }

        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (console)
            {
                Console.Error.WriteLine("Another File-Time Tracker agent is already running in this session.");
            }

            return 3;
        }

        MachineSettings settings;
        TrackerLog log;

        try
        {
            settings = MachineSettings.Load(AgentPaths.MachineSettingsFile);
            AgentPaths.EnsureDataDirectory();
            log = new TrackerLog(AgentPaths.LogFile, console, console ? LogLevel.Debug : settings.ResolveLogLevel());
        }
        catch (Exception ex)
        {
            // Nothing is configured yet, so there is nowhere reliable to log: surface it and exit
            // non-zero so the scheduled task shows the failure.
            if (console)
            {
                Console.Error.WriteLine(ex.Message);
            }

            TryWriteFallbackLog(ex);
            return 2;
        }

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        try
        {
            using var host = new AgentHost(settings, log, console);
            await host.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Agent terminated with an unhandled exception.", ex);
            return 1;
        }
    }

    /// <summary>
    /// Writes machine identity and the DPAPI-encrypted Dropbox refresh token (§4.3, §6). Invoked by
    /// the installer while elevated; also usable by an admin to rotate a leaked token in place.
    /// </summary>
    private static int Provision(string[] args)
    {
        AttachOrAllocConsole();

        var options = ParseOptions(args);

        if (!options.TryGetValue("computer-no", out var computerNo) || string.IsNullOrWhiteSpace(computerNo))
        {
            Console.Error.WriteLine("provision: --computer-no is required.");
            return Help();
        }

        try
        {
            var existing = File.Exists(AgentPaths.MachineSettingsFile)
                ? MachineSettings.Load(AgentPaths.MachineSettingsFile)
                : null;

            var settings = new MachineSettings
            {
                ComputerNo = computerNo.Trim(),
                // Reusing the existing salt matters: regenerating it would make previously hashed
                // documents stop matching new ones for the same file.
                HashSalt = options.GetValueOrDefault("hash-salt")
                           ?? existing?.HashSalt
                           ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ConfigPath = options.GetValueOrDefault("config-path") ?? existing?.ConfigPath ?? "/config.json",
                TimeZoneId = options.GetValueOrDefault("time-zone") ?? existing?.TimeZoneId ?? string.Empty,
                LogLevel = options.GetValueOrDefault("log-level") ?? existing?.LogLevel ?? "Info",
                StorageBackend = RemoteStorageFactory.Canonicalize(
                    options.GetValueOrDefault("storage") ?? existing?.StorageBackend),
                StorageRoot = options.GetValueOrDefault("storage-root")?.Trim()
                              ?? existing?.StorageRoot
                              ?? string.Empty,
            };

            if (RemoteStorageFactory.IsFileSystem(settings.StorageBackend) && string.IsNullOrWhiteSpace(settings.StorageRoot))
            {
                Console.Error.WriteLine("provision: --storage filesystem also needs --storage-root <folder or UNC path>.");
                return Help();
            }

            settings.Save(AgentPaths.MachineSettingsFile);
            Console.WriteLine($"Wrote machine settings to '{AgentPaths.MachineSettingsFile}' (Computer No. {settings.ComputerNo}).");

            if (RemoteStorageFactory.IsFileSystem(settings.StorageBackend))
            {
                Console.WriteLine($"Storage: filesystem at '{settings.StorageRoot}' (no credential needed).");
                AgentPaths.EnsureDataDirectory();
                return 0;
            }

            var appKey = options.GetValueOrDefault("app-key");
            var refreshToken = options.GetValueOrDefault("refresh-token");

            if (!string.IsNullOrWhiteSpace(appKey) && !string.IsNullOrWhiteSpace(refreshToken))
            {
                var credentials = new DropboxCredentials
                {
                    AppKey = appKey.Trim(),
                    AppSecret = options.GetValueOrDefault("app-secret")?.Trim(),
                    RefreshToken = refreshToken.Trim(),
                };

                new DpapiSecretStore(AgentPaths.CredentialFile).Save(credentials);
                Console.WriteLine(
                    $"Wrote DPAPI-encrypted Dropbox credentials to '{AgentPaths.CredentialFile}' " +
                    $"({(credentials.IsPkce ? "PKCE app, no secret stored" : "confidential app")}).");
            }
            else
            {
                Console.WriteLine("No --app-key/--refresh-token given: skipped credential provisioning.");
            }

            AgentPaths.EnsureDataDirectory();
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"provision: access denied writing to '{AgentPaths.Root}'. Run from an elevated prompt. ({ex.Message})");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"provision failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Registers the §6 logon and watchdog tasks. Called by the installer while elevated.</summary>
    private static int InstallTasks(string[] args)
    {
        AttachOrAllocConsole();

        var options = ParseOptions(args);
        var installFolder = options.GetValueOrDefault("install-folder") ?? AppContext.BaseDirectory;

        var minutes = 5;
        if (options.TryGetValue("watchdog-minutes", out var raw) && int.TryParse(raw, out var parsed))
        {
            minutes = Math.Clamp(parsed, 1, 60);
        }

        return Deployment.ScheduledTasks.Install(installFolder.TrimEnd('\\'), minutes, Console.Out);
    }

    private static int UninstallTasks()
    {
        AttachOrAllocConsole();
        return Deployment.ScheduledTasks.Uninstall(Console.Out);
    }

    private static int ConfigureDefender(string[] args)
    {
        AttachOrAllocConsole();

        var options = ParseOptions(args);
        var path = options.GetValueOrDefault("path") ?? AppContext.BaseDirectory.TrimEnd('\\');

        return options.ContainsKey("remove")
            ? Deployment.DefenderExclusion.Remove(path, Console.Out)
            : Deployment.DefenderExclusion.Add(path, Console.Out);
    }

    private static int Status()
    {
        AttachOrAllocConsole();

        if (!File.Exists(AgentPaths.MachineSettingsFile))
        {
            Console.WriteLine("Not provisioned. Run 'FileTimeTracker.Agent.exe provision --computer-no <no>' as administrator.");
            return 2;
        }

        var settings = MachineSettings.Load(AgentPaths.MachineSettingsFile);
        Console.WriteLine($"Computer No.      : {settings.ComputerNo}");
        Console.WriteLine($"Config path       : {settings.ConfigPath}");
        Console.WriteLine($"Credentials       : {(File.Exists(AgentPaths.CredentialFile) ? "provisioned" : "MISSING")}");
        Console.WriteLine($"Buffer            : {AgentPaths.DatabaseFile}");

        if (File.Exists(AgentPaths.DatabaseFile))
        {
            using var store = new SqliteIntervalStore(AgentPaths.DatabaseFile);
            Console.WriteLine($"Pending intervals : {store.PendingCount()}");
        }

        if (File.Exists(AgentPaths.HeartbeatFile))
        {
            var heartbeat = TrackerJson.Deserialize<HeartbeatDocument>(File.ReadAllText(AgentPaths.HeartbeatFile));
            if (heartbeat is not null)
            {
                var age = DateTimeOffset.UtcNow - heartbeat.UpdatedUtc;
                Console.WriteLine($"Heartbeat         : {heartbeat.UpdatedUtc:u} ({age.TotalMinutes:0.0} min ago)");
                Console.WriteLine($"Session state     : {heartbeat.SessionState}");
                Console.WriteLine($"Last upload       : {heartbeat.LastUploadUtc?.ToString("u") ?? "never"}");
            }
        }
        else
        {
            Console.WriteLine("Heartbeat         : none (agent has not run)");
        }

        return 0;
    }

    private static int Help()
    {
        AttachOrAllocConsole();

        Console.WriteLine("""
            File-Time Tracker agent

            Usage:
              FileTimeTracker.Agent.exe                  Run the agent (started by the logon scheduled task).
              FileTimeTracker.Agent.exe --console        Run in the foreground and print intervals as they close.
              FileTimeTracker.Agent.exe status           Show provisioning, buffer depth and last heartbeat.
              FileTimeTracker.Agent.exe provision ...    Write machine settings and credentials (elevated).
              FileTimeTracker.Agent.exe install-tasks    Register the logon and watchdog tasks (elevated).
              FileTimeTracker.Agent.exe uninstall-tasks  Remove both scheduled tasks (elevated).
              FileTimeTracker.Agent.exe defender-exclusion [--remove] [--path <dir>]

            provision options:
              --computer-no <no>       Required. Stamped on every row.
              --app-key <key>          Dropbox app key.
              --refresh-token <token>  Dropbox refresh token (stored DPAPI-encrypted).
              --app-secret <secret>    Only for a confidential app; omit for PKCE.
              --config-path <path>     Central config path in the app folder (default /config.json).
              --time-zone <id>         Windows time-zone id defining the reporting day.
              --hash-salt <salt>       Fleet-wide salt; must match across machines.
              --log-level <level>      Debug | Info | Warn | Error (default Info).
              --storage <backend>      dropbox (default) | filesystem.
              --storage-root <path>    Folder or UNC share when --storage is filesystem. No
                                       credential is stored or needed in that mode.

            install-tasks options:
              --install-folder <dir>   Folder holding the agent and watchdog (default: this folder).
              --watchdog-minutes <n>   Watchdog check interval, 1-60 (default 5).
            """);

        return 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[i][2..];
            var separator = key.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0)
            {
                options[key[..separator]] = key[(separator + 1)..];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = args[++i];
            }
            else
            {
                options[key] = "true";
            }
        }

        return options;
    }

    /// <summary>
    /// The agent is a WinExe so the logon task shows no console window. For the CLI verbs, borrow
    /// the caller's console if there is one and allocate otherwise.
    /// </summary>
    private static void AttachOrAllocConsole()
    {
        const int AttachParentProcess = -1;

        // Already redirected to a file or pipe: keep the redirection rather than attaching a
        // console, which would send output to a window instead of where the caller asked. Still
        // wrap it to flush per line — piloting with `--console > capture.txt` is expected, and a
        // buffered writer loses the tail of the capture whenever the agent is stopped abruptly.
        if (Console.IsOutputRedirected)
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            return;
        }

        if (AttachConsole(AttachParentProcess) || AllocConsole())
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stdout);
        }
    }

    private static void TryWriteFallbackLog(Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "FileTimeTracker.startup-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:u} {ex}{Environment.NewLine}");
        }
        catch
        {
            // Nothing further we can do.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}
