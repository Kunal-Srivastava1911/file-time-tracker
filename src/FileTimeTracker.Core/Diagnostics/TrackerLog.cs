using System.Globalization;
using System.Text;

namespace FileTimeTracker.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Minimal size-capped rolling file log. The agent runs unattended for months on ~50 laptops, so
/// logging must never fail loudly, never grow without bound, and never need a NuGet dependency.
/// </summary>
public sealed class TrackerLog : IDisposable
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int KeepGenerations = 3;

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly bool _console;

    public TrackerLog(string? path, bool console = false, LogLevel minimum = LogLevel.Info)
    {
        _path = path;
        _console = console;
        Minimum = minimum;

        if (_path is not null)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }

    public LogLevel Minimum { get; set; }

    public static TrackerLog Null { get; } = new(path: null);

    public void Debug(string message) => Write(LogLevel.Debug, message, null);

    public void Info(string message) => Write(LogLevel.Info, message, null);

    public void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);

    public void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public void Write(LogLevel level, string message, Exception? ex)
    {
        if (level < Minimum)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(level.ToString().ToUpperInvariant().PadRight(5)).Append("] ")
            .Append(message);

        if (ex is not null)
        {
            line.Append(" :: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            if (ex.StackTrace is not null)
            {
                line.AppendLine().Append(ex.StackTrace);
            }
        }

        var text = line.ToString();

        if (_console)
        {
            Console.WriteLine(text);
        }

        if (_path is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                RollIfNeeded();
                File.AppendAllText(_path, text + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // A log write must never take the agent down.
            }
        }
    }

    private void RollIfNeeded()
    {
        if (_path is null || !File.Exists(_path) || new FileInfo(_path).Length < MaxBytes)
        {
            return;
        }

        var oldest = _path + "." + KeepGenerations;
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = KeepGenerations - 1; i >= 1; i--)
        {
            var from = _path + "." + i;
            if (File.Exists(from))
            {
                File.Move(from, _path + "." + (i + 1), overwrite: true);
            }
        }

        File.Move(_path, _path + ".1", overwrite: true);
    }

    public void Dispose()
    {
    }
}
