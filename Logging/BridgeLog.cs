using System.Globalization;
using WGL2Bridge.Config;

namespace WGL2Bridge.Logging;

/// <summary>Severity levels</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

/// <summary>
/// Logging facade used everywhere in WGL2Bridge. Levels are explicit method calls.
/// Should never be used in the "hot path" (per-packet processing Pumps), except for exceptional conditions (first packet etc.).
/// Levels for the console and file sinks are configured independently.
/// </summary>
public static class BridgeLog
{
    private static readonly object Sync = new();
    private static readonly List<ILogSink> Sinks = [];
    private static bool _initialized;

    /// <summary>True once <see cref="Initialize"/> has succeeded.</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>Builds the console and file sinks from the resolved configuration.</summary>
    public static void Initialize(BridgeConfig config)
    {
        lock (Sync)
        {
            Sinks.Clear();
            Sinks.Add(new ConsoleLogSink(config.ConsoleLogLevel));

            if (!string.IsNullOrWhiteSpace(config.LogFilePath))
            {
                try
                {
                    Sinks.Add(new FileLogSink(config.FileLogLevel, config.LogFilePath, config.LogMaxBytes));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to open log file '{config.LogFilePath}': {ex.Message}");
                }
            }

            _initialized = true;
        }
    }

    /// <summary>Logs a lifecycle or milestone event.</summary>
    public static void Info(string message, string category = "Bridge") => Write(LogLevel.Information, message, category);

    /// <summary>Logs diagnostic detail (resolved config, retry counters, cached peer reuse).</summary>
    public static void Debug(string message, string category = "Bridge") => Write(LogLevel.Debug, message, category);

    /// <summary>Logs a recoverable or remedial condition.</summary>
    public static void Warning(string message, string category = "Bridge") => Write(LogLevel.Warning, message, category);

    /// <summary>Logs a fatal or configuration failure.</summary>
    public static void Error(string message, string category = "Bridge") => Write(LogLevel.Error, message, category);

    private static void Write(LogLevel level, string message, string category)
    {
        lock (Sync)
        {
            foreach (var sink in Sinks)
            {
                if (level >= sink.MinimumLevel)
                {
                    sink.Write(level, message, category);
                }
            }
        }
    }
}

/// <summary>A single logging destination with its own minimum level.</summary>
internal interface ILogSink
{
    LogLevel MinimumLevel { get; }

    void Write(LogLevel level, string message, string category);
}

/// <summary>
/// Console sink in the Serilog "SimpleConsole" style: timestamp HH:mm:ss.fff, single line,
/// no color (so redirected output stays clean).
/// </summary>
internal sealed class ConsoleLogSink : ILogSink
{
    public ConsoleLogSink(LogLevel minimumLevel) => MinimumLevel = minimumLevel;

    public LogLevel MinimumLevel { get; }

    public void Write(LogLevel level, string message, string category) =>
        Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message}");
}

/// <summary>
/// Plain-text file sink: yyyy-MM-dd HH:mm:ss.fff [Level] category: message. Rotates to '.1'
/// once the configured maximum size is exceeded.
/// </summary>
internal sealed class FileLogSink : ILogSink
{
    private readonly string _fullPath;
    private readonly long _maxBytes;
    private StreamWriter _writer;
    private long _bytesWritten;

    public FileLogSink(LogLevel minimumLevel, string path, long maxBytes)
    {
        MinimumLevel = minimumLevel;
        _maxBytes = maxBytes;
        _fullPath = Path.GetFullPath(path);

        string? directory = Path.GetDirectoryName(_fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = OpenWriter();
    }

    public LogLevel MinimumLevel { get; }

    public void Write(LogLevel level, string message, string category)
    {
        string line =
            $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {category}: {message}";

        if (_bytesWritten + line.Length + Environment.NewLine.Length > _maxBytes)
        {
            Rotate();
        }

        _writer.WriteLine(line);
        _bytesWritten += line.Length + Environment.NewLine.Length;
    }

    private void Rotate()
    {
        _writer.Dispose();

        string rotated = _fullPath + ".1";
        if (File.Exists(rotated))
        {
            File.Delete(rotated);
        }

        File.Move(_fullPath, rotated);
        _writer = OpenWriter();
        _bytesWritten = 0;
    }

    private StreamWriter OpenWriter() =>
        new(new FileStream(_fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
}
