using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bergdahl.NodePad.WebApp.Logging;

public sealed class FileLoggerOptions
{
    public string? Directory { get; set; } = "Logs";
    public string FileName { get; set; } = "nodepad-"; // prefix
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public bool UseUtcTimestamp { get; set; } = true;
    public bool DailyRolling { get; set; } = true;
}

internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly Func<FileLoggerOptions> _getCurrentConfig;
    private readonly FileWriter _writer;

    public FileLogger(string categoryName, Func<FileLoggerOptions> getCurrentConfig, FileWriter writer)
    {
        _categoryName = categoryName;
        _getCurrentConfig = getCurrentConfig;
        _writer = writer;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => default!;

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= _getCurrentConfig().MinimumLevel;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var opts = _getCurrentConfig();
        var ts = opts.UseUtcTimestamp ? DateTime.UtcNow : DateTime.Now;
        var sb = new StringBuilder();
        sb.Append(ts.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(" ");
        sb.Append(logLevel.ToString());
        sb.Append(" ");
        sb.Append(_categoryName);
        sb.Append("[");
        sb.Append(eventId.Id);
        sb.Append("] ");
        try
        {
            sb.Append(formatter(state, exception));
        }
        catch
        {
            // Avoid logger exceptions bubbling up
        }
        if (exception != null)
        {
            sb.AppendLine();
            sb.Append(exception);
        }
        var line = sb.ToString();
        _writer.WriteLine(line);
    }
}

internal sealed class FileWriter
{
    private readonly Func<FileLoggerOptions> _getCurrentConfig;
    private readonly object _sync = new();
    private string _currentPath = string.Empty;
    private StreamWriter? _stream;

    public FileWriter(Func<FileLoggerOptions> getCurrentConfig)
    {
        _getCurrentConfig = getCurrentConfig;
    }

    private string GetLogFilePath()
    {
        var opts = _getCurrentConfig();
        var baseDir = opts.Directory;
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = "Logs";
        if (!Path.IsPathRooted(baseDir))
        {
            baseDir = Path.Combine(Directory.GetCurrentDirectory(), baseDir);
        }
        Directory.CreateDirectory(baseDir);
        var datePart = opts.DailyRolling ? (opts.UseUtcTimestamp ? DateTime.UtcNow : DateTime.Now).ToString("yyyyMMdd") : string.Empty;
        var fileName = string.IsNullOrEmpty(datePart)
            ? $"{opts.FileName.TrimEnd('-')}.log"
            : $"{opts.FileName}{datePart}.log";
        return Path.Combine(baseDir, fileName);
    }

    public void WriteLine(string message)
    {
        lock (_sync)
        {
            var path = GetLogFilePath();
            if (!string.Equals(path, _currentPath, StringComparison.Ordinal))
            {
                // rotate
                _stream?.Dispose();
                _stream = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = true };
                _currentPath = path;
            }
            _stream ??= new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = true };
            _stream.WriteLine(message);
        }
    }
}

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly IDisposable? _onChangeToken;
    private FileLoggerOptions _currentConfig;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly FileWriter _writer;

    public FileLoggerProvider(IOptionsMonitor<FileLoggerOptions> config)
    {
        _currentConfig = config.CurrentValue;
        _writer = new FileWriter(() => _currentConfig);
        _onChangeToken = config.OnChange(updated => _currentConfig = updated);
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, () => _currentConfig, _writer));

    public void Dispose()
    {
        _loggers.Clear();
        _onChangeToken?.Dispose();
    }
}
