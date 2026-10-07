using System;
using System.Threading;
using System.Threading.Tasks;
using HectorBravo.Nvidia.Contracts;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// A self-contained, thread-safe <see cref="Contracts.IGpuPollingService"/> that
/// periodically drives an <see cref="Contracts.IGpuMonitorService"/>'s
/// <c>RefreshAsync</c> and republishes the results as events.
///
/// This is the app's OWN poller (no dependency on a NvmlCore concrete poller),
/// so it works whether the monitor is a real NVML engine or the local placeholder
/// used before the core is wired. Events fire on a background thread; the
/// subscriber (MainWindow) is responsible for marshalling to the UI thread.
/// </summary>
public sealed class GpuPollingService : Contracts.IGpuPollingService
{
    private readonly Contracts.IGpuMonitorService _monitor;
    private readonly AppLog? _log;
    private readonly object _gate = new();
    private string _statusKey;

    private System.Threading.Timer? _timer;
    private int _intervalMs;
    private int _refreshing;     // 0 idle, 1 in-flight (lock-free single-flight guard)
    private bool _paused;
    private bool _running;

    public GpuPollingService(Contracts.IGpuMonitorService monitor, AppLog? log = null, int intervalMs = 2000)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _log = log;
        _intervalMs = Math.Max(200, intervalMs);
        _statusKey = StatusKey(monitor);
    }

    public event EventHandler<SystemSnapshot>? SnapshotAvailable;
    public event EventHandler<string>? StatusChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _running && !_paused;
        }
    }

    public TimeSpan Interval
    {
        get { lock (_gate) return TimeSpan.FromMilliseconds(_intervalMs); }
        set
        {
            var ms = Math.Max(200, (int)value.TotalMilliseconds);
            lock (_gate)
            {
                _intervalMs = ms;
            }
            RecreateTimer();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _paused = false;
            _timer = new System.Threading.Timer(OnTick, null, 0, _intervalMs);
        }
        _log?.Info($"Polling started ({_intervalMs / 1000.0:0.##}s interval).");
    }

    public void Pause()
    {
        lock (_gate) _paused = true;
        _log?.Info("Polling paused.");
    }

    public void Resume()
    {
        lock (_gate) _paused = false;
        // fire immediately, then continue on interval
        RecreateTimer();
        _log?.Info("Polling resumed.");
    }

    public Task RefreshNowAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return Task.CompletedTask;
        return DoRefreshAsync();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    // ── internals ──────────────────────────────────────────────

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = DoRefreshAsync();
    }

    private async Task DoRefreshAsync()
    {
        try
        {
            var snapshot = await _monitor.RefreshAsync().ConfigureAwait(false);
            RaiseStatusIfChanged();
            SnapshotAvailable?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            _log?.Error($"Refresh failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void RaiseStatusIfChanged()
    {
        var key = StatusKey(_monitor);
        if (string.Equals(key, _statusKey, StringComparison.Ordinal)) return;
        _statusKey = key;
        StatusChanged?.Invoke(this, _monitor.StatusMessage);
    }

    private static string StatusKey(Contracts.IGpuMonitorService m) =>
        (m.IsAvailable ? "1" : "0") + "|" + (m.StatusMessage ?? string.Empty);

    private void RecreateTimer()
    {
        int ms;
        bool active;
        lock (_gate)
        {
            active = _running && !_paused;
            ms = _intervalMs;
            _timer?.Dispose();
            _timer = active ? new System.Threading.Timer(OnTick, null, 0, ms) : null;
        }
    }
}
