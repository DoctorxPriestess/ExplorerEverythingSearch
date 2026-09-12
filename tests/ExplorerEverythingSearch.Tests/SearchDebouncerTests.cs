using System.Collections.Generic;
using System.Linq;
using ExplorerEverythingSearch.Core.Search;
using ExplorerEverythingSearch.Tests.TestSupport;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>
/// The idle trigger must fire exactly once per settled query, and never after Enter, Cancel or Dispose.
/// </summary>
public sealed class SearchDebouncerTests
{
    private sealed class Recorder
    {
        private readonly List<TriggerType> _triggers = new();

        public IReadOnlyList<TriggerType> Triggers
        {
            get { lock (_triggers) return _triggers.ToArray(); }
        }

        public int Count
        {
            get { lock (_triggers) return _triggers.Count; }
        }

        public void OnSubmit(TriggerType trigger)
        {
            lock (_triggers) _triggers.Add(trigger);
        }

        public bool WaitForCount(int expected, int timeoutMs = 5_000) => Wait.Until(() => Count >= expected, timeoutMs);
    }

    [Fact]
    public void An_idle_timeout_submits_exactly_once()
    {
        var recorder = new Recorder();
        using var debouncer = new SearchDebouncer(recorder.OnSubmit, 60);

        debouncer.Restart();

        Assert.True(debouncer.IsPending);
        Assert.True(recorder.WaitForCount(1));
        Assert.False(debouncer.IsPending);

        // Nothing may fire a second time for the same text.
        Thread.Sleep(300);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(TriggerType.IdleTimeout, recorder.Triggers[0]);
    }

    [Fact]
    public void Every_restart_cancels_the_previous_timer_so_only_the_last_query_is_submitted()
    {
        var recorder = new Recorder();
        using var debouncer = new SearchDebouncer(recorder.OnSubmit, 120);

        debouncer.Restart();
        Thread.Sleep(40);
        debouncer.Restart();
        Thread.Sleep(40);
        debouncer.Restart();

        Assert.True(recorder.WaitForCount(1));
        Thread.Sleep(300);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(TriggerType.IdleTimeout, recorder.Triggers.Single());
    }

    [Fact]
    public void Cancel_prevents_the_pending_submit()
    {
        var recorder = new Recorder();
        using var debouncer = new SearchDebouncer(recorder.OnSubmit, 80);

        debouncer.Restart();
        Assert.True(debouncer.IsPending);
        debouncer.Cancel();

        Assert.False(debouncer.IsPending);
        Thread.Sleep(350);
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void SubmitNow_submits_immediately_and_never_twice()
    {
        var recorder = new Recorder();
        using var debouncer = new SearchDebouncer(recorder.OnSubmit, 5_000);

        debouncer.Restart();
        debouncer.SubmitNow(TriggerType.Enter);

        // Enter submits synchronously, without waiting for the idle delay.
        Assert.Equal(1, recorder.Count);
        Assert.Equal(TriggerType.Enter, recorder.Triggers[0]);
        Assert.False(debouncer.IsPending);

        // Waiting for the (now cancelled) idle delay must not produce a second submit.
        Thread.Sleep(400);
        Assert.Equal(1, recorder.Count);

        // Enter without any pending idle timer still submits; the duplicate suppression lives one level up.
        debouncer.SubmitNow(TriggerType.Enter);
        Assert.Equal(2, recorder.Count);
    }

    [Fact]
    public void DelayMs_can_be_changed_at_runtime_and_applies_to_the_next_restart()
    {
        var recorder = new Recorder();
        using var debouncer = new SearchDebouncer(recorder.OnSubmit, 30_000);

        debouncer.DelayMs = 60;
        Assert.Equal(60, debouncer.DelayMs);
        debouncer.Restart();
        Assert.True(recorder.WaitForCount(1, 3_000), "the shortened delay was not used");

        // A longer delay must really delay the submit.
        debouncer.DelayMs = 1_200;
        debouncer.Restart();
        Thread.Sleep(400);
        Assert.Equal(1, recorder.Count);
        Assert.True(recorder.WaitForCount(2, 3_000));
    }

    [Fact]
    public void The_constructor_clamps_the_delay()
    {
        using var debouncer = new SearchDebouncer(_ => { }, -5);
        Assert.Equal(0, debouncer.DelayMs);
    }

    [Fact]
    public void Dispose_prevents_a_pending_submit_and_further_use()
    {
        var recorder = new Recorder();
        var debouncer = new SearchDebouncer(recorder.OnSubmit, 80);

        debouncer.Restart();
        debouncer.Dispose();

        Thread.Sleep(300);
        Assert.Equal(0, recorder.Count);

        debouncer.Restart();
        debouncer.SubmitNow(TriggerType.Enter);
        Thread.Sleep(150);
        Assert.Equal(0, recorder.Count);

        // Dispose is idempotent.
        debouncer.Dispose();
    }

    [Fact]
    public void A_missing_submit_callback_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SearchDebouncer(null!, 100));
    }

    [Fact]
    public void A_failing_submit_is_swallowed_on_the_idle_path()
    {
        var debouncer = new SearchDebouncer(_ => throw new InvalidOperationException("submit failed"), 60);

        debouncer.Restart();
        Thread.Sleep(300);

        // No unhandled exception on the timer thread: the process survives a failing submit.
        Assert.False(debouncer.IsPending);
        debouncer.Dispose();
    }

    [Fact]
    public void A_failing_submit_does_not_propagate_on_the_enter_path_either()
    {
        // Guarded defect (found and fixed): OnTimer guarded the callback but SubmitNow did not, so a
        // throwing submitter escaped into the Enter key / keyboard hook / UI Automation callback thread.
        using var debouncer = new SearchDebouncer(_ => throw new InvalidOperationException("submit failed"), 60);

        debouncer.SubmitNow(TriggerType.Enter); // must not throw
    }
}
