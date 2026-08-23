using System.Text;
using Microsoft.Extensions.Logging;

namespace WGL2Bridge;

internal sealed class PlainTextFileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private bool _disposed;

    public PlainTextFileLoggerProvider(string path)
    {
        string fullPath = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName) => new PlainTextFileLogger(this, categoryName);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    private void Write(LogLevel logLevel, string categoryName, string message, Exception? exception)
    {
        lock (_gate)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {categoryName}: {message}";
            _writer.WriteLine(line);

            if (exception is not null)
            {
                _writer.WriteLine(exception.ToString());
            }
        }
    }

    private sealed class PlainTextFileLogger(PlainTextFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
            {
                return;
            }

            provider.Write(logLevel, categoryName, message, exception);
        }
    }
}
