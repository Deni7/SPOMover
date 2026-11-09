namespace SPOMover.Spo.Throttling;

/// <summary>
/// THR-1, THR-6, THR-10: resource units a client may spend. A token bucket over a short window
/// (a minute for an app, five minutes for the tenant) plus an optional sliding 24-hour counter.
/// Requests wait for room instead of being sent and throttled.
/// </summary>
public sealed class RuBudget
{
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly double _windowSeconds;
    private readonly double _fullCapacity;
    private readonly long _fullDaily;
    private readonly long[] _dailySlots = new long[24 * 60];
    private double _scale = 1;
    private double _tokens;
    private long _lastTicks;
    private long _lastSlotMinute;
    private long _spentTotal;

    /// <param name="capacity">RU allowed per <paramref name="window"/> at 100%.</param>
    /// <param name="perDay">RU allowed per 24 hours at 100%; 0 — no daily limit.</param>
    public RuBudget(string name, double capacity, TimeSpan window, long perDay, TimeProvider? time = null)
    {
        Name = name;
        _time = time ?? TimeProvider.System;
        _fullCapacity = capacity;
        _windowSeconds = window.TotalSeconds;
        _fullDaily = perDay;
        _tokens = capacity;
        _lastTicks = _time.GetTimestamp();
        _lastSlotMinute = CurrentMinute();
    }

    public string Name { get; }

    /// <summary>Share of the limit to use (budget percent from the profile or the current time window).</summary>
    public double Scale
    {
        get { lock (_sync) return _scale; }
        set
        {
            lock (_sync)
            {
                Refill();
                _scale = Math.Clamp(value, 0.01, 1);
                _tokens = Math.Min(_tokens, Capacity);
            }
        }
    }

    public double Capacity => _fullCapacity * _scale;

    public long DailyLimit => (long)(_fullDaily * _scale);

    public long SpentTotal => Interlocked.Read(ref _spentTotal);

    /// <summary>RU spent in the last 24 hours.</summary>
    public long SpentLast24h
    {
        get
        {
            lock (_sync)
            {
                AdvanceSlots();
                return _dailySlots.Sum();
            }
        }
    }

    /// <summary>THR-6: the 24-hour budget is (almost) spent — new files should wait instead of failing on timeouts.</summary>
    public bool DailyExhausted
    {
        get
        {
            if (_fullDaily <= 0)
                return false;
            lock (_sync)
            {
                AdvanceSlots();
                return _dailySlots.Sum() >= DailyLimit * 0.98;
            }
        }
    }

    /// <summary>Share of the short-window budget currently used, 0…1.</summary>
    public double Utilization
    {
        get
        {
            lock (_sync)
            {
                Refill();
                return Capacity <= 0 ? 1 : 1 - (_tokens / Capacity);
            }
        }
    }

    /// <summary>
    /// THR-8: RU actually spent over the last window against its limit, 0…1 — the load to show.
    /// <see cref="Utilization"/> stays near zero below the limit, because the bucket refills faster than it is spent.
    /// The oldest minute is taken in proportion to the part of it still inside the window.
    /// </summary>
    public double WindowLoad
    {
        get
        {
            lock (_sync)
            {
                AdvanceSlots();
                int minutes = Math.Max(1, (int)Math.Round(_windowSeconds / 60));
                double intoMinute = _time.GetUtcNow().ToUnixTimeMilliseconds() % 60_000 / 60_000.0;
                double spent = 0;
                for (int i = 0; i <= minutes; i++)
                {
                    long slot = _dailySlots[(_lastSlotMinute - i) % _dailySlots.Length];
                    spent += i == minutes ? slot * (1 - intoMinute) : slot;
                }
                return Capacity <= 0 ? 1 : Math.Min(1, spent / Capacity);
            }
        }
    }

    public async ValueTask WaitAsync(int cost, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_sync)
            {
                Refill();
                AdvanceSlots();
                double needed = Math.Min(cost, Capacity);
                long dailyCost = Math.Min(cost, Math.Max(1, DailyLimit));
                bool dailyOk = _fullDaily <= 0 || _dailySlots.Sum() + dailyCost <= DailyLimit;
                if (_tokens >= needed && dailyOk)
                {
                    _tokens -= needed;
                    _dailySlots[_lastSlotMinute % _dailySlots.Length] += cost;
                    Interlocked.Add(ref _spentTotal, cost);
                    return;
                }
                wait = dailyOk
                    ? TimeSpan.FromSeconds(Math.Max(0.01, (needed - _tokens) / (Capacity / _windowSeconds)))
                    : TimeSpan.FromMinutes(1);
            }
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        long now = _time.GetTimestamp();
        double elapsed = _time.GetElapsedTime(_lastTicks, now).TotalSeconds;
        _lastTicks = now;
        _tokens = Math.Min(Capacity, _tokens + (elapsed * Capacity / _windowSeconds));
    }

    private void AdvanceSlots()
    {
        long minute = CurrentMinute();
        long steps = Math.Min(minute - _lastSlotMinute, _dailySlots.Length);
        for (long i = 1; i <= steps; i++)
            _dailySlots[(_lastSlotMinute + i) % _dailySlots.Length] = 0;
        if (minute > _lastSlotMinute)
            _lastSlotMinute = minute;
    }

    private long CurrentMinute() => _time.GetUtcNow().ToUnixTimeSeconds() / 60;
}
