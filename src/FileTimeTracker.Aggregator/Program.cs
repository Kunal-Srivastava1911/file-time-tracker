using System.Runtime.Versioning;
using FileTimeTracker.Aggregator.Configuration;
using FileTimeTracker.Aggregator.Reporting;
using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Security;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Aggregator;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? "run";

        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?") || command == "help")
        {
            return Help();
        }

        var options = ParseOptions(args);

        return command switch
        {
            "provision" => Provision(options),
            "init-config" => await InitConfigAsync(options).ConfigureAwait(false),
            "run" => await RunAsync(options).ConfigureAwait(false),
            _ => Unknown(command),
        };
    }

    private static async Task<int> RunAsync(Dictionary<string, string> options)
    {
        var settings = AggregatorSettings.Load(AggregatorPaths.SettingsFile);
        using var log = new TrackerLog(AggregatorPaths.LogFile, console: true, settings.ResolveLogLevel());

        var dryRun = options.ContainsKey("dry-run");

        var remote = CreateRemote(settings, log);
        if (remote is null)
        {
            return 2;
        }

        try
        {
            var configPath = options.GetValueOrDefault("config-path") ?? settings.ConfigPath;
            var config = await LoadConfigAsync(remote, configPath, log).ConfigureAwait(false);

            var runner = new AggregationRunner(remote, log, settings.ResolveTimeZone(log));
            var report = await runner.RunAsync(config, upload: !dryRun).ConfigureAwait(false);

            if (dryRun)
            {
                var output = options.GetValueOrDefault("out")
                             ?? Path.Combine(Environment.CurrentDirectory, "TimeLog_DryRun.xlsx");
                await File.WriteAllBytesAsync(output, report.Workbook).ConfigureAwait(false);
                log.Info($"Dry run: nothing was uploaded. Workbook written to '{output}'.");
            }

            PrintSummary(report, dryRun);
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Aggregation run failed.", ex);
            return 1;
        }
        finally
        {
            // Only the Dropbox backend holds an HttpClient; the filesystem one has nothing to release.
            (remote as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Resolves the configured backend, or returns null after logging why it could not be built.
    /// </summary>
    private static IRemoteStorage? CreateRemote(AggregatorSettings settings, TrackerLog log)
    {
        if (RemoteStorageFactory.IsFileSystem(settings.StorageBackend))
        {
            return RemoteStorageFactory.CreateFileSystem(settings.StorageRoot, log);
        }

        var credentials = new DpapiSecretStore(AggregatorPaths.CredentialFile).Load();
        if (credentials is null)
        {
            log.Error(
                $"No Dropbox credentials at '{AggregatorPaths.CredentialFile}'. " +
                "Run 'FileTimeTracker.Aggregator.exe provision --app-key ... --refresh-token ...' first, " +
                "or switch to filesystem storage with 'provision --storage filesystem --storage-root <path>'.");
            return null;
        }

        return new DropboxRemoteStorage(credentials, "FileTimeTracker.Aggregator/1.0", log);
    }

    private static async Task<TrackerConfig> LoadConfigAsync(IRemoteStorage remote, string configPath, TrackerLog log)
    {
        var json = await remote.DownloadTextAsync(configPath).ConfigureAwait(false);

        if (json is null)
        {
            log.Warn($"No '{configPath}' in the app folder; using built-in defaults. Run 'init-config' to create it.");
            return new TrackerConfig().Sanitized();
        }

        var config = TrackerJson.Deserialize<TrackerConfig>(json)
                     ?? throw new InvalidOperationException($"'{configPath}' is not valid configuration JSON.");

        return config.Sanitized();
    }

    /// <summary>Writes a starter <c>config.json</c> so the fleet has something to fetch on day one (§4.4).</summary>
    private static async Task<int> InitConfigAsync(Dictionary<string, string> options)
    {
        var settings = AggregatorSettings.Load(AggregatorPaths.SettingsFile);
        using var log = new TrackerLog(AggregatorPaths.LogFile, console: true, settings.ResolveLogLevel());

        var remote = CreateRemote(settings, log);
        if (remote is null)
        {
            return 2;
        }

        try
        {
            var configPath = options.GetValueOrDefault("config-path") ?? settings.ConfigPath;

            if (await remote.ExistsAsync(configPath).ConfigureAwait(false) && !options.ContainsKey("force"))
            {
                log.Warn($"'{configPath}' already exists. Pass --force to overwrite it.");
                return 3;
            }

            var config = new TrackerConfig();
            config.ComputerEmployeeMap["PC-001"] = string.Empty;

            await remote.UploadTextAsync(configPath, TrackerJson.SerializePretty(config)).ConfigureAwait(false);
            log.Info($"Wrote starter configuration to '{configPath}'. Edit it to map Computer No. to employee names.");

            return 0;
        }
        catch (Exception ex)
        {
            log.Error("init-config failed.", ex);
            return 1;
        }
        finally
        {
            (remote as IDisposable)?.Dispose();
        }
    }

    private static int Provision(Dictionary<string, string> options)
    {
        var appKey = options.GetValueOrDefault("app-key");
        var refreshToken = options.GetValueOrDefault("refresh-token");

        var useFileSystem = RemoteStorageFactory.IsFileSystem(options.GetValueOrDefault("storage"));
        var storageRoot = options.GetValueOrDefault("storage-root");

        if (useFileSystem)
        {
            if (string.IsNullOrWhiteSpace(storageRoot))
            {
                Console.Error.WriteLine("provision: --storage filesystem also needs --storage-root <folder or UNC path>.");
                return Help();
            }
        }
        else if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(refreshToken))
        {
            Console.Error.WriteLine(
                "provision: --app-key and --refresh-token are required, " +
                "or use --storage filesystem --storage-root <path> for no-credential storage.");
            return Help();
        }

        try
        {
            var settings = AggregatorSettings.Load(AggregatorPaths.SettingsFile);

            if (options.TryGetValue("config-path", out var configPath))
            {
                settings.ConfigPath = configPath;
            }

            if (options.TryGetValue("time-zone", out var timeZone))
            {
                settings.TimeZoneId = timeZone;
            }

            if (options.TryGetValue("log-level", out var logLevel))
            {
                settings.LogLevel = logLevel;
            }

            if (useFileSystem)
            {
                settings.StorageBackend = RemoteStorageFactory.FileSystem;
                settings.StorageRoot = storageRoot!.Trim();
            }
            else if (options.ContainsKey("storage"))
            {
                settings.StorageBackend = RemoteStorageFactory.Canonicalize(options["storage"]);
            }

            settings.Save(AggregatorPaths.SettingsFile);

            if (useFileSystem)
            {
                Console.WriteLine($"Wrote settings to '{AggregatorPaths.SettingsFile}'.");
                Console.WriteLine($"Storage: filesystem at '{settings.StorageRoot}' (no credential stored).");
            }
            else
            {
                new DpapiSecretStore(AggregatorPaths.CredentialFile).Save(new DropboxCredentials
                {
                    AppKey = appKey!.Trim(),
                    AppSecret = options.GetValueOrDefault("app-secret")?.Trim(),
                    RefreshToken = refreshToken!.Trim(),
                });

                Console.WriteLine($"Wrote settings to '{AggregatorPaths.SettingsFile}'.");
                Console.WriteLine($"Wrote DPAPI-encrypted credentials to '{AggregatorPaths.CredentialFile}'.");
            }

            Console.WriteLine($"Time zone: {settings.ResolveTimeZone().Id}");

            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"provision: access denied writing to '{AggregatorPaths.Root}'. Run elevated. ({ex.Message})");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"provision failed: {ex.Message}");
            return 1;
        }
    }

    private static void PrintSummary(AggregationReport report, bool dryRun)
    {
        Console.WriteLine();
        Console.WriteLine($"Quarter            : {report.CurrentQuarter.Label}");
        Console.WriteLine($"Log files read     : {report.LogFiles}");
        Console.WriteLine($"Intervals (current): {report.Intervals}");
        Console.WriteLine($"Duplicates dropped : {report.DuplicatesDropped}");
        Console.WriteLine($"Malformed lines    : {report.MalformedLines}");
        Console.WriteLine($"Daily total rows   : {report.DailyTotalRows}");
        Console.WriteLine($"Master workbook    : {(dryRun ? "(dry run)" : report.UploadedTo)}");

        foreach (var archive in report.Archives)
        {
            var state = archive.Finalised
                ? $"finalised, {archive.LogFilesMoved} log file(s) moved"
                : "grace window open, will rebuild next run";

            Console.WriteLine($"Archived {archive.Quarter.Label}   : {archive.Intervals} interval(s) -> '{archive.WorkbookPath}' ({state})");
        }

        var stale = report.Machines
            .Where(m => m.LastHeartbeatUtc is null || DateTimeOffset.UtcNow - m.LastHeartbeatUtc > TimeSpan.FromHours(24))
            .ToList();

        if (stale.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Machines with no heartbeat in 24h ({stale.Count}):");
            foreach (var machine in stale.OrderBy(m => m.ComputerNo, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {machine.ComputerNo,-12} {machine.LastHeartbeatUtc?.ToString("u") ?? "never reported"}");
            }
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        return Help();
    }

    private static int Help()
    {
        Console.WriteLine("""
            File-Time Tracker aggregator

            Usage:
              FileTimeTracker.Aggregator.exe run [--dry-run] [--out <file>] [--config-path <path>]
              FileTimeTracker.Aggregator.exe provision --app-key <key> --refresh-token <token> [options]
              FileTimeTracker.Aggregator.exe init-config [--config-path <path>] [--force]

            run options:
              --dry-run           Build the workbook but upload nothing.
              --out <file>        Where a dry run writes the workbook (default ./TimeLog_DryRun.xlsx).
              --config-path <p>   Override the central config path for this run.

            provision options:
              --app-key <key>          Admin Dropbox app key.
              --refresh-token <token>  Admin refresh token (stored DPAPI-encrypted).
              --app-secret <secret>    Only for a confidential app; omit for PKCE.
              --config-path <path>     Central config path (default /config.json).
              --time-zone <id>         Windows time-zone id defining the reporting day and quarter.
              --log-level <level>      Debug | Info | Warn | Error.
              --storage <backend>      dropbox (default) | filesystem.
              --storage-root <path>    Folder or UNC share when --storage is filesystem. Must be the
                                       same location the agents write to. No credential is stored.

            Schedule 'run' hourly as a Scheduled Task on the always-on admin machine.
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
}
