namespace SwarmRT.Model;

/// <summary>
/// Paces backend calls to fit the free-tier budget described in design §2's
/// rate-limit note (~10-15 requests/minute, concurrency 2). Calls are spaced by a
/// minimum interval and capped by a concurrency semaphore; a 429 pushes the whole
/// schedule forward so every waiter backs off, not just the call that was refused.
/// </summary>
public sealed class ModelThrottle : IDisposable
{
    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim _scheduleLock = new(1, 1);
    private readonly TimeSpan _minInterval;
    private readonly TimeProvider _time;
    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

    public ModelThrottle(int requestsPerMinute, int maxConcurrency, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        RequestsPerMinute = requestsPerMinute;
        MaxConcurrency = maxConcurrency;
        _minInterval = TimeSpan.FromSeconds(60.0 / requestsPerMinute);
        _concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _time = timeProvider ?? TimeProvider.System;
    }

    public int RequestsPerMinute { get; }

    public int MaxConcurrency { get; }

    public TimeSpan MinimumInterval => _minInterval;

    /// <summary>Total time spent waiting on the throttle, surfaced in the run footer.</summary>
    public TimeSpan TotalWait { get; private set; }

    /// <summary>
    /// Reserves the next slot and waits for it. Dispose the returned lease when the
    /// call finishes to free the concurrency slot.
    /// </summary>
    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        TimeSpan wait;

        await _scheduleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            DateTimeOffset slot = _nextSlot > now ? _nextSlot : now;
            _nextSlot = slot + _minInterval;
            wait = slot - now;
        }
        finally
        {
            _scheduleLock.Release();
        }

        if (wait > TimeSpan.Zero)
        {
            TotalWait += wait;
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this);
    }

    /// <summary>
    /// Delays every future slot by <paramref name="backoff"/> after the backend has
    /// signalled that we are going too fast.
    /// </summary>
    public async Task PenalizeAsync(TimeSpan backoff, CancellationToken cancellationToken = default)
    {
        if (backoff <= TimeSpan.Zero)
        {
            return;
        }

        await _scheduleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            DateTimeOffset baseline = _nextSlot > now ? _nextSlot : now;
            _nextSlot = baseline + backoff;
        }
        finally
        {
            _scheduleLock.Release();
        }
    }

    public void Dispose()
    {
        _concurrency.Dispose();
        _scheduleLock.Dispose();
    }

    public readonly struct Lease(ModelThrottle owner) : IDisposable
    {
        public void Dispose() => owner._concurrency.Release();
    }
}
