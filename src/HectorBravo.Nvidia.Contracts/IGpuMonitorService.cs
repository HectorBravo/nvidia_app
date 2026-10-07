namespace HectorBravo.Nvidia.Contracts;

/// <summary>
/// The core monitoring service. Implemented once (headless, NVML-backed);
/// consumed by both the WPF app and the CLI. All members are non-blocking
/// and thread-safe; <see cref="RefreshAsync"/> performs one full polling pass
/// and returns a fresh snapshot.
/// </summary>
public interface IGpuMonitorService
{
    /// <summary>True once NVML has been initialized successfully.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// One-line human description of availability. If NVML is not usable
    /// (missing library / driver / no GPU) this explains why, so the UI
    /// can show a friendly fallback screen.
    /// </summary>
    string StatusMessage { get; }

    /// <summary>
    /// Attempts NVML initialization (idempotent). Returns true on success.
    /// Failure is recorded in <see cref="StatusMessage"/> and does not throw.
    /// </summary>
    Task<bool> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a single full polling pass (all GPUs + all processes) and
    /// returns the resulting snapshot. If NVML is unavailable the snapshot
    /// is empty and <see cref="StatusMessage"/> explains why.
    /// </summary>
    Task<SystemSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Releases NVML resources. Safe to call multiple times.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A push-style monitor for UI consumption. Wraps a polling source and
/// emits <see cref="SnapshotAvailable"/> on each refresh, plus
/// <see cref="StatusChanged"/> when availability flips.
/// </summary>
public interface IGpuPollingService : IDisposable
{
    /// <summary>Raised on the UI thread after each successful refresh.</summary>
    event EventHandler<SystemSnapshot>? SnapshotAvailable;

    /// <summary>Raised when availability or the status message changes.</summary>
    event EventHandler<string>? StatusChanged;

    bool IsRunning { get; }

    /// <summary>Current refresh interval; changes apply immediately.</summary>
    TimeSpan Interval { get; set; }

    void Start();
    void Pause();
    void Resume();

    /// <summary>Force an immediate refresh regardless of the timer.</summary>
    Task RefreshNowAsync();
}
