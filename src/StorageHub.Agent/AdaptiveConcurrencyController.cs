namespace StorageHub.Agent;

/// <summary>
/// Conservatively tunes worker concurrency from bounded throughput observations. It starts at the
/// configured floor, ramps after sustained healthy work, and backs off immediately on errors or a
/// material throughput regression.
/// </summary>
public sealed class AdaptiveConcurrencyController
{
    private readonly object _gate = new();
    private readonly bool _enabled;
    private readonly int _minimum;
    private readonly int _maximum;
    private int _current;
    private int _healthyStreak;
    private double _throughputEwma;

    public AdaptiveConcurrencyController(bool enabled, int minimum, int maximum)
    {
        if (minimum < 1 || maximum < minimum || maximum > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "Concurrency bounds must be ordered between 1 and 32.");
        }

        _enabled = enabled;
        _minimum = minimum;
        _maximum = maximum;
        _current = enabled ? minimum : maximum;
    }

    public int CurrentLimit => Volatile.Read(ref _current);

    public int MaximumLimit => _maximum;

    public void ReportSuccess(long workUnits, TimeSpan elapsed)
    {
        if (!_enabled || workUnits < 0 || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var throughput = Math.Max(1, workUnits) / elapsed.TotalSeconds;
        lock (_gate)
        {
            var prior = _throughputEwma;
            _throughputEwma = prior == 0 ? throughput : (prior * 0.75) + (throughput * 0.25);
            if (prior > 0 && throughput < prior * 0.60)
            {
                _current = Math.Max(_minimum, _current - 1);
                _healthyStreak = 0;
                return;
            }

            if (prior == 0 || throughput >= prior * 0.85)
            {
                _healthyStreak++;
                if (_healthyStreak >= 2 && _current < _maximum)
                {
                    _current++;
                    _healthyStreak = 0;
                }
            }
            else
            {
                _healthyStreak = 0;
            }
        }
    }

    public void ReportFailure()
    {
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            _current = Math.Max(_minimum, _current - 1);
            _healthyStreak = 0;
        }
    }
}
