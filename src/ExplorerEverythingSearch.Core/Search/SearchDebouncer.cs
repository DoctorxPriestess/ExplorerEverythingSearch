namespace ExplorerEverythingSearch.Core.Search;

/// <summary>
/// Idle ("user stopped typing") trigger.
///
/// Semantics required by the specification:
/// - every text change cancels the pending timer and restarts it,
/// - Enter cancels the pending timer and submits immediately,
/// - a cancelled timer must never fire, and it must never fire twice for the same text.
/// </summary>
public sealed class SearchDebouncer : IDisposable
{
    private readonly Action<TriggerType> _onSubmit;
    private readonly Timer _timer;
    private readonly object _sync = new();
    private long _generation;
    private bool _disposed;

    public SearchDebouncer(Action<TriggerType> onSubmit, int delayMs)
    {
        _onSubmit = onSubmit ?? throw new ArgumentNullException(nameof(onSubmit));
        DelayMs = Math.Clamp(delayMs, 0, 60_000);
        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public int DelayMs { get; set; }

    /// <summary>True while an idle submit is pending.</summary>
    public bool IsPending
    {
        get
        {
            lock (_sync) return _pending;
        }
    }

    private bool _pending;

    /// <summary>Called for every TextChanged: cancels the previous timer and restarts it.</summary>
    public void Restart()
    {
        lock (_sync)
        {
            if (_disposed) return;
            var generation = ++_generation;
            _pending = true;
            _timer.Change(DelayMs, Timeout.Infinite);
            _ = generation;
        }
    }

    /// <summary>Enter: cancel the pending idle timer and submit right away.</summary>
    public void SubmitNow(TriggerType trigger)
    {
        bool shouldFire;
        lock (_sync)
        {
            if (_disposed) return;
            shouldFire = _pending;
            _pending = false;
            _generation++;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // Enter must always submit, even when no idle timer was pending (for example when the user
        // presses Enter twice in a row - the duplicate suppression lives one level up).
        _ = shouldFire;
        try
        {
            _onSubmit(trigger);
        }
        catch
        {
            // The callback runs on the Enter / keyboard hook / UI Automation thread; a failing submit
            // must never travel back into Explorer's or the hook's callback.
        }
    }

    /// <summary>Cancels any pending idle submit (for example when the search box is cleared).</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            _pending = false;
            _generation++;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void OnTimer(object? state)
    {
        lock (_sync)
        {
            if (_disposed || !_pending) return;
            _pending = false;
            _generation++;
        }

        try
        {
            _onSubmit(TriggerType.IdleTimeout);
        }
        catch
        {
            // submit failures are handled (and logged) by the coordinator
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = false;
            _generation++;
        }
        _timer.Dispose();
    }
}
