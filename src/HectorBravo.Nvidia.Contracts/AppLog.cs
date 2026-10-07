namespace HectorBravo.Nvidia.Contracts;

/// <summary>
/// One entry in the app's activity log (the "Log" tab). Also used for
/// file logging to disk in the future.
/// </summary>
public sealed record AppLogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public AppLogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;

    public string Formatted =>
        $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff}  {Level,-8}  {Message}";
}

public enum AppLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A bounded, thread-safe in-memory log used by the UI's Log tab.
/// </summary>
public sealed class AppLog
{
    private readonly object _gate = new();
    private readonly Queue<AppLogEntry> _entries = new();
    private readonly int _capacity;
    private EventHandler<AppLogEntry>? _handler;

    public AppLog(int capacity = 1000)
    {
        _capacity = capacity;
    }

    public event EventHandler<AppLogEntry>? EntryAdded
    {
        add
        {
            lock (_gate)
            {
                _handler += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _handler -= value;
            }
        }
    }

    public void Add(AppLogLevel level, string message)
    {
        var entry = new AppLogEntry
        {
            Level = level,
            Message = message,
        };

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }

            _handler?.Invoke(this, entry);
        }
    }

    public void Info(string message) => Add(AppLogLevel.Info, message);
    public void Warning(string message) => Add(AppLogLevel.Warning, message);
    public void Error(string message) => Add(AppLogLevel.Error, message);

    public IReadOnlyList<AppLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _handler = null;
        }
    }
}
