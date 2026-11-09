namespace SPOMover.Spo.Throttling;

/// <summary>
/// THR-3, THR-7, RSL-2: throttling state of one app. A 429/503 with Retry-After pauses every request of the app,
/// not just the one that got it; concurrency is halved and then grows back one step at a time (AIMD).
/// Only 503 responses for a long time mean the app is probably blocked.
/// </summary>
public sealed class ThrottleState
{
    private const int SuccessesPerStep = 20;
    private readonly TimeProvider _time;
    private readonly TimeSpan _blockedAfter;
    private readonly object _sync = new();
    private readonly int _maxConcurrency;
    private int _limit;
    private int _inFlight;
    private int _successes;
    private DateTimeOffset _pausedUntil;
    private DateTimeOffset? _only503Since;
    private TaskCompletionSource _changed = NewSignal();

    public ThrottleState(string app, int maxConcurrency, TimeSpan blockedAfter, TimeProvider? time = null)
    {
        App = app;
        _time = time ?? TimeProvider.System;
        _maxConcurrency = Math.Max(1, maxConcurrency);
        _limit = _maxConcurrency;
        _blockedAfter = blockedAfter;
    }

    public string App { get; }

    public long Throttled429;
    public long Throttled503;
    public long Requests;

    public int ConcurrencyLimit { get { lock (_sync) return _limit; } }

    public int InFlight { get { lock (_sync) return _inFlight; } }

    public DateTimeOffset PausedUntil { get { lock (_sync) return _pausedUntil; } }

    public bool IsPaused => PausedUntil > _time.GetUtcNow();

    /// <summary>How long the app has been getting only 503.</summary>
    public TimeSpan Only503For
    {
        get
        {
            lock (_sync)
                return _only503Since is { } since ? _time.GetUtcNow() - since : TimeSpan.Zero;
        }
    }

    public bool IsBlocked
    {
        get
        {
            lock (_sync)
                return _only503Since is { } since && _time.GetUtcNow() - since >= _blockedAfter;
        }
    }

    /// <summary>Waits for the end of a pause and for a free slot under the current limit.</summary>
    public async ValueTask EnterAsync(CancellationToken ct)
    {
        while (true)
        {
            Task signal;
            TimeSpan pause;
            lock (_sync)
            {
                pause = _pausedUntil - _time.GetUtcNow();
                if (pause <= TimeSpan.Zero && _inFlight < _limit)
                {
                    _inFlight++;
                    return;
                }
                signal = _changed.Task;
            }
            if (pause > TimeSpan.Zero)
                await Task.Delay(pause, _time, ct).ConfigureAwait(false);
            else
                await signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void Exit()
    {
        lock (_sync)
        {
            _inFlight--;
            Signal();
        }
    }

    public void OnSuccess()
    {
        Interlocked.Increment(ref Requests);
        lock (_sync)
        {
            _only503Since = null;
            if (_limit < _maxConcurrency && ++_successes >= SuccessesPerStep)
            {
                _successes = 0;
                _limit++;
                Signal();
            }
        }
    }

    /// <summary>A response that is neither success nor throttling still proves the app is not blocked.</summary>
    public void OnOtherResponse()
    {
        Interlocked.Increment(ref Requests);
        lock (_sync)
            _only503Since = null;
    }

    public void OnThrottled(TimeSpan retryAfter, bool is503)
    {
        Interlocked.Increment(ref Requests);
        if (is503)
            Interlocked.Increment(ref Throttled503);
        else
            Interlocked.Increment(ref Throttled429);
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            var until = now + retryAfter;
            if (until > _pausedUntil)
                _pausedUntil = until;
            _limit = Math.Max(1, _limit / 2);
            _successes = 0;
            if (is503)
                _only503Since ??= now;
            else
                _only503Since = null;
            Signal();
        }
    }

    private void Signal()
    {
        var old = _changed;
        _changed = NewSignal();
        old.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
