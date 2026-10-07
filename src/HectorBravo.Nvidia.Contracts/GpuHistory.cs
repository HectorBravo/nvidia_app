namespace HectorBravo.Nvidia.Contracts;

/// <summary>
/// A bounded, thread-safe ring buffer of <see cref="GpuMetricSample"/>s,
/// one series per GPU. Used to feed the live charts in the UI.
/// </summary>
public sealed class GpuHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Queue<GpuMetricSample>> _series = new();
    private int _capacity;

    public GpuHistory(int capacity = 300)
    {
        Capacity = capacity;
    }

    public int Capacity
    {
        get
        {
            lock (_gate)
            {
                return _capacity;
            }
        }
        set
        {
            if (value < 1)
            {
                value = 1;
            }

            lock (_gate)
            {
                _capacity = value;
                TrimAll();
            }
        }
    }

    /// <summary>Appends a sample to its GPU's series, trimming overflow.</summary>
    public void Add(GpuMetricSample sample)
    {
        lock (_gate)
        {
            if (!_series.TryGetValue(sample.GpuIndex, out var q))
            {
                q = new Queue<GpuMetricSample>();
                _series[sample.GpuIndex] = q;
            }

            q.Enqueue(sample);
            while (q.Count > _capacity)
            {
                q.Dequeue();
            }
        }
    }

    /// <summary>Returns a copy of the series for a GPU (oldest → newest).</summary>
    public IReadOnlyList<GpuMetricSample> GetSeries(int gpuIndex)
    {
        lock (_gate)
        {
            return _series.TryGetValue(gpuIndex, out var q)
                ? q.ToArray()
                : Array.Empty<GpuMetricSample>();
        }
    }

    /// <summary>Clears all series (used when NVML becomes unavailable).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _series.Clear();
        }
    }

    /// <summary>Indices of GPUs that have at least one sample stored.</summary>
    public IReadOnlyCollection<int> GpuIndices
    {
        get
        {
            lock (_gate)
            {
                return _series.Keys.ToArray();
            }
        }
    }

    private void TrimAll()
    {
        foreach (var q in _series.Values)
        {
            while (q.Count > _capacity)
            {
                q.Dequeue();
            }
        }
    }
}
