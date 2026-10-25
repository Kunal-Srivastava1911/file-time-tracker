using System.Runtime.Versioning;
using FileTimeTracker.Agent.Configuration;
using FileTimeTracker.Core.Configuration;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Tray;

/// <summary>
/// Moves an install created before <see cref="MachineSettings.PublishRoot"/> existed onto the split
/// layout: spreadsheets in the publish folder, everything else in the local working store.
/// </summary>
/// <remarks>
/// <para>The earlier single-user setup pointed <c>storage_root</c> straight at a folder inside
/// Dropbox and wrote everything there — <c>config.json</c>, the per-machine JSONL logs, the
/// heartbeat and the session spreadsheets side by side. That makes Dropbox unreadable: the one file
/// a person wants is four levels down past three kinds of plumbing they have no use for.</para>
/// <para>Runs on every start and is a no-op once done, because the condition it tests for —
/// a filesystem install with no publish folder — stops being true the moment it finishes. Nothing
/// is deleted: logs and config move to the working store, where the app still reads them, so an
/// existing employee mapping and the whole interval history survive.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StorageMigration
{
    /// <summary>Subfolders of the legacy root that hold the app's own working files.</summary>
    private static readonly string[] WorkingFolders = { "logs", "archive" };

    /// <summary>
    /// Returns the settings to run with — migrated and saved when there was something to migrate,
    /// and <paramref name="settings"/> unchanged otherwise.
    /// </summary>
    public static MachineSettings Apply(MachineSettings settings, TrackerLog log)
    {
        if (!NeedsMigration(settings))
        {
            return settings;
        }

        var legacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.StorageRoot));
        var publishRoot = ChoosePublishRoot(legacyRoot);

        log.Info($"Migrating '{legacyRoot}' to the split layout: spreadsheets to '{publishRoot}', working files to '{SetupService.WorkingStore}'.");

        try
        {
            Directory.CreateDirectory(SetupService.WorkingStore);
            Directory.CreateDirectory(publishRoot);

            // Read the employee map before the config moves: it is what turns a legacy
            // "Kunal-Srivastava" folder back into the person's actual name.
            var names = ReadEmployeeNames(legacyRoot, settings.ConfigPath, log);

            MoveSessions(legacyRoot, publishRoot, names, log);
            MoveWorkingFiles(legacyRoot, log);
            PruneEmptyFolders(legacyRoot, log);

            settings.StorageRoot = SetupService.WorkingStore;
            settings.PublishRoot = publishRoot;
            settings.Save(SetupService.SettingsFile);

            log.Info("Migration complete. Dropbox now holds spreadsheets only.");

            return settings;
        }
        catch (Exception ex)
        {
            // A half-migrated folder still works: the app reads both the old and new locations for
            // logs, and the settings are only rewritten once every move has succeeded. Leaving the
            // install as it was beats refusing to start.
            log.Error("Could not migrate the storage layout; continuing with the existing one.", ex);
            return settings;
        }
    }

    /// <summary>
    /// True for a filesystem install that still keeps its spreadsheets in the working store. The
    /// fleet case is excluded by the backend test: its destination is the admin's app folder, which
    /// is not a path on this machine and is not ours to rearrange.
    /// </summary>
    private static bool NeedsMigration(MachineSettings settings) =>
        RemoteStorageFactory.IsFileSystem(settings.StorageBackend)
        && !settings.PublishesSeparately
        && !string.IsNullOrWhiteSpace(settings.StorageRoot)
        && Directory.Exists(settings.StorageRoot);

    /// <summary>
    /// Where spreadsheets should land. Prefers a sibling of the legacy root, so a folder that was
    /// inside Dropbox stays inside the same Dropbox even if the client reports a different account
    /// today; falls back to asking the client, then to a subfolder of the legacy root.
    /// </summary>
    private static string ChoosePublishRoot(string legacyRoot)
    {
        var parent = Path.GetDirectoryName(legacyRoot);

        if (!string.IsNullOrEmpty(parent))
        {
            return Path.Combine(parent, DropboxFolderLocator.PublishSubfolder);
        }

        return DropboxFolderLocator.FindPublishRoot()
               ?? Path.Combine(legacyRoot, DropboxFolderLocator.PublishSubfolder);
    }

    /// <summary>
    /// Maps each legacy folder name to the employee name it was derived from, e.g.
    /// <c>Kunal-Srivastava</c> back to <c>Kunal Srivastava</c>. Empty when there is no usable config.
    /// </summary>
    private static Dictionary<string, string> ReadEmployeeNames(
        string legacyRoot, string configPath, TrackerLog log)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = ToLocal(legacyRoot, configPath);

        if (!File.Exists(file))
        {
            return names;
        }

        try
        {
            var config = TrackerJson.Deserialize<TrackerConfig>(File.ReadAllText(file).TrimStart('﻿'));

            foreach (var employee in config?.ComputerEmployeeMap.Values ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(employee))
                {
                    names[RemotePath.SafeSegment(employee)] = RemotePath.SafeDisplaySegment(employee);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            // Only costs the rename: an unmatched folder moves across under its existing name.
            log.Warn("Could not read the employee map; legacy folder names will be kept as they are.", ex);
        }

        return names;
    }

    /// <summary>Moves <c>sessions/&lt;person&gt;/*.xlsx</c> up to the publish root's top level.</summary>
    private static void MoveSessions(
        string legacyRoot, string publishRoot, Dictionary<string, string> names, TrackerLog log)
    {
        var sessions = Path.Combine(legacyRoot, "sessions");

        if (!Directory.Exists(sessions))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(sessions))
        {
            var legacyName = Path.GetFileName(folder);
            var destination = Path.Combine(
                publishRoot,
                names.TryGetValue(legacyName, out var proper) ? proper : legacyName);

            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                MoveFile(file, Path.Combine(destination, Path.GetFileName(file)), log);
            }
        }

        // Any spreadsheet written straight into sessions/ rather than a person's folder.
        foreach (var file in Directory.EnumerateFiles(sessions))
        {
            MoveFile(file, Path.Combine(publishRoot, Path.GetFileName(file)), log);
        }
    }

    /// <summary>Moves the config, the logs and anything else left at the legacy root's top level.</summary>
    private static void MoveWorkingFiles(string legacyRoot, TrackerLog log)
    {
        foreach (var name in WorkingFolders)
        {
            var source = Path.Combine(legacyRoot, name);
            if (Directory.Exists(source))
            {
                MoveTree(source, Path.Combine(SetupService.WorkingStore, name), log);
            }
        }

        // config.json and any stray master workbook; the sessions tree is already handled, and
        // Dropbox's own .dropbox marker is not ours to touch.
        foreach (var file in Directory.EnumerateFiles(legacyRoot))
        {
            var name = Path.GetFileName(file);

            if (name.Equals(".dropbox", StringComparison.OrdinalIgnoreCase)
                || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            MoveFile(file, Path.Combine(SetupService.WorkingStore, name), log);
        }
    }

    /// <summary>Merges a directory into its destination, file by file, keeping its shape.</summary>
    private static void MoveTree(string source, string destination, TrackerLog log)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            MoveFile(file, target, log);
        }
    }

    /// <summary>
    /// Moves one file, and on a name clash keeps both. Overwriting would be the quiet way to lose a
    /// spreadsheet from a previous install that happens to cover the same minute.
    /// </summary>
    private static void MoveFile(string source, string destination, TrackerLog log)
    {
        try
        {
            File.Move(source, Unique(destination), overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place rather than lost; the next start tries again.
            log.Warn($"Could not move '{source}'; it has been left where it is.", ex);
        }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Removes the folders the move emptied, innermost first, and the legacy root itself once
    /// nothing is left in it. Stops at the first non-empty folder rather than forcing anything.
    /// </summary>
    private static void PruneEmptyFolders(string legacyRoot, TrackerLog log)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(legacyRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(f => f.Length))
            {
                TryRemoveEmpty(folder);
            }

            TryRemoveEmpty(legacyRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Debug($"Left the legacy folder '{legacyRoot}' in place: {ex.Message}");
        }
    }

    private static void TryRemoveEmpty(string folder)
    {
        if (Directory.Exists(folder)
            && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
        }
    }

    /// <summary>Resolves a storage-relative path such as <c>/config.json</c> under a root.</summary>
    private static string ToLocal(string root, string remotePath) =>
        Path.Combine(root, RemotePath.Normalize(remotePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
}
