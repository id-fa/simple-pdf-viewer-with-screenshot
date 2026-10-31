using System.Collections.Concurrent;

namespace LibraryServer.Server;

public sealed record LogLine(DateTimeOffset Time, LogLevel Level, string Category, string Message, string? Exception);

/// <summary>直近のログをメモリに持つ (設定画面の「ログ」タブ用)。ファイルには書かない</summary>
public sealed class RingLog
{
    private readonly ConcurrentQueue<LogLine> _lines = new();
    private readonly int _capacity;
    private long _seq;

    public RingLog(int capacity = 500) => _capacity = capacity;

    public event Action<LogLine>? Added;

    public long Sequence => Interlocked.Read(ref _seq);

    public void Add(LogLine line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > _capacity && _lines.TryDequeue(out _)) { }
        Interlocked.Increment(ref _seq);
        try { Added?.Invoke(line); } catch (Exception) { }
    }

    public IReadOnlyList<LogLine> Snapshot(int max = 500) => _lines.TakeLast(max).ToList();
}

public sealed class RingLoggerProvider : ILoggerProvider
{
    private readonly RingLog _log;
    private readonly LogLevel _min;

    public RingLoggerProvider(RingLog log, LogLevel min = LogLevel.Information)
    {
        _log = log;
        _min = min;
    }

    public ILogger CreateLogger(string categoryName) => new RingLogger(_log, categoryName, _min);

    public void Dispose() { }

    private sealed class RingLogger(RingLog log, string category, LogLevel min) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= min && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var cat = category;
            var dot = cat.LastIndexOf('.');
            if (dot >= 0) cat = cat[(dot + 1)..];
            log.Add(new LogLine(DateTimeOffset.Now, logLevel, cat, formatter(state, exception), exception?.ToString()));
        }
    }
}
