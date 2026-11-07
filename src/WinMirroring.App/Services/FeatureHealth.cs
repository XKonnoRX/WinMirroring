using System.Collections.Concurrent;
using Serilog;

namespace WinMirroring.App.Services;

public sealed record FeatureStatus(string Name, bool Ok, string Detail);

/// <summary>Start-up result of every feature, so a failure on one device is visible instead of silent.</summary>
public sealed class FeatureHealth
{
    private readonly ConcurrentDictionary<string, FeatureStatus> _features = new();
    private readonly List<string> _order = [];

    public event Action? Changed;

    public IReadOnlyList<FeatureStatus> Snapshot
    {
        get
        {
            lock (_order)
            {
                return _order.Select(name => _features[name]).ToList();
            }
        }
    }

    public IEnumerable<FeatureStatus> Failures => Snapshot.Where(f => !f.Ok);

    /// <summary>Runs <paramref name="start"/>; any exception marks the feature as failed instead of stopping the app.</summary>
    public void Start(string name, Action start)
    {
        try
        {
            start();
            Set(name, true, "работает");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Feature {Feature} failed to start", name);
            var inner = ex is AggregateException { InnerException: { } first } ? first : ex;
            Set(name, false, $"{inner.GetType().Name}: {inner.Message}");
        }
    }

    public void Set(string name, bool ok, string detail)
    {
        lock (_order)
        {
            if (!_order.Contains(name))
            {
                _order.Add(name);
            }

            _features[name] = new FeatureStatus(name, ok, detail);
        }

        Changed?.Invoke();
    }
}
