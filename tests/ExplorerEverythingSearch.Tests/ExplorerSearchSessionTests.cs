using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Explorer;
using ExplorerEverythingSearch.Core.Search;
using ExplorerEverythingSearch.Tests.TestSupport;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>
/// The session turns raw Explorer events (text change, Enter report, title change) into exactly one
/// submitted search, which is where wrong input would produce surprising searches.
/// </summary>
public sealed class ExplorerSearchSessionTests
{
    private const long Hwnd = 0x777;

    private sealed class SessionFixture : IDisposable
    {
        private readonly TempWorkspace _workspace = new("session");

        public SessionFixture(int autoSearchDelayMs = 100)
        {
            Config = new AppConfig { AutoSearchDelay = autoSearchDelayMs };
            Logger = AppLogger.CreateDisabled(_workspace.Root);
        }

        public AppConfig Config { get; }

        public AppLogger Logger { get; }

        public RecordingSubmitter Submitter { get; } = new();

        public Func<bool>? IsSearchViewActive { get; set; }

        public Action? OnQueryStarted { get; set; }

        public Func<string?>? ReadLiveText { get; set; }

        public ExplorerSearchSession CreateSession()
            => new(Hwnd, Submitter, Logger, () => Config, IsSearchViewActive, OnQueryStarted, ReadLiveText);

        public void Dispose()
        {
            Logger.Dispose();
            _workspace.Dispose();
        }
    }

    [Fact]
    public void An_empty_search_box_never_submits()
    {
        using var fixture = new SessionFixture();
        using var session = fixture.CreateSession();

        session.OnTextChanged(string.Empty);
        session.OnTextChanged("   ");
        session.OnTextChanged(null!);

        Thread.Sleep(400);

        Assert.Equal(0, fixture.Submitter.Count);
        Assert.Equal(string.Empty, session.CurrentText);
    }

    [Fact]
    public void The_idle_timeout_submits_the_last_text_once()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 120);
        using var session = fixture.CreateSession();

        session.OnTextChanged("a");
        Thread.Sleep(40);
        session.OnTextChanged("ab");

        Assert.True(fixture.Submitter.WaitForCount(1));
        var submission = fixture.Submitter.Last;
        Assert.Equal(Hwnd, submission.Hwnd);
        Assert.Equal("ab", submission.Text);
        Assert.Equal(TriggerType.IdleTimeout, submission.Trigger);

        Thread.Sleep(400);
        Assert.Equal(1, fixture.Submitter.Count);
    }

    [Fact]
    public void Enter_submits_immediately_without_waiting_for_the_idle_delay()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        using var session = fixture.CreateSession();
        session.OnTextChanged("query");

        session.OnEnterDetected("keyboard hook");

        Assert.Equal(1, fixture.Submitter.Count);
        Assert.Equal("query", fixture.Submitter.Last.Text);
        Assert.Equal(TriggerType.Enter, fixture.Submitter.Last.Trigger);

        // The pending idle timer was cancelled: no second submit follows.
        Thread.Sleep(500);
        Assert.Equal(1, fixture.Submitter.Count);
    }

    [Fact]
    public void A_second_enter_report_for_the_same_key_press_is_ignored()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        using var session = fixture.CreateSession();
        session.OnTextChanged("query");

        session.OnEnterDetected("keyboard hook");
        session.OnEnterDetected("focus left the search box");

        Assert.Equal(1, fixture.Submitter.Count);

        // The suppression is a time window, not a permanent latch.
        Thread.Sleep(500);
        session.OnEnterDetected("focus left the search box");
        Assert.Equal(2, fixture.Submitter.Count);
        Assert.All(fixture.Submitter.Submissions, s => Assert.Equal(TriggerType.Enter, s.Trigger));
        Assert.All(fixture.Submitter.Submissions, s => Assert.Equal("query", s.Text));
    }

    [Fact]
    public void Enter_on_an_empty_search_box_is_ignored()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        using var session = fixture.CreateSession();

        session.OnEnterDetected("keyboard hook");

        Assert.Equal(0, fixture.Submitter.Count);
    }

    [Fact]
    public void A_title_change_that_does_not_carry_the_search_text_is_not_a_commit()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        using var session = fixture.CreateSession();
        session.OnTextChanged("alpha");

        session.OnExplorerCommitDetected("beta - Downloads");

        Assert.Equal(0, fixture.Submitter.Count);
    }

    [Fact]
    public void A_commit_that_happens_late_is_explorers_own_automatic_search()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        fixture.Config.EnterCommitWindowMs = 400;
        using var session = fixture.CreateSession();
        session.OnTextChanged("alpha");

        Thread.Sleep(500);
        session.OnExplorerCommitDetected("alpha - Downloads");

        Assert.Equal(0, fixture.Submitter.Count);
    }

    [Fact]
    public void A_commit_while_the_search_box_is_empty_is_ignored()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        using var session = fixture.CreateSession();

        session.OnExplorerCommitDetected("alpha - Downloads");

        Assert.Equal(0, fixture.Submitter.Count);
    }

    [Fact]
    public void A_commit_that_really_turned_the_window_into_a_search_view_counts_as_enter()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        fixture.IsSearchViewActive = () => true;
        using var session = fixture.CreateSession();
        session.OnTextChanged("alpha");

        session.OnExplorerCommitDetected("alpha - Downloads");

        Assert.Equal(1, fixture.Submitter.Count);
        Assert.Equal("alpha", fixture.Submitter.Last.Text);
        Assert.Equal(TriggerType.Enter, fixture.Submitter.Last.Trigger);

        // Explorer reports the same title change twice; the second one is a duplicate.
        session.OnExplorerCommitDetected("alpha - Downloads");
        Assert.Equal(1, fixture.Submitter.Count);
    }

    [Fact]
    public void A_title_preview_without_a_search_view_change_is_not_a_commit()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        fixture.IsSearchViewActive = () => false;
        using var session = fixture.CreateSession();
        session.OnTextChanged("alpha");

        session.OnExplorerCommitDetected("alpha - Downloads");

        Assert.Equal(0, fixture.Submitter.Count);
    }

    [Fact]
    public void The_commit_heuristic_can_be_switched_off()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        fixture.Config.DetectEnterByCommitTiming = false;
        fixture.IsSearchViewActive = () => true;
        using var session = fixture.CreateSession();
        session.OnTextChanged("alpha");

        session.OnExplorerCommitDetected("alpha - Downloads");

        Assert.Equal(0, fixture.Submitter.Count);
    }

    [Fact]
    public void The_live_search_box_text_wins_over_the_remembered_one()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        fixture.ReadLiveText = () => "live";
        using var session = fixture.CreateSession();
        session.OnTextChanged("typed");

        session.OnEnterDetected("keyboard hook");

        Assert.Equal(1, fixture.Submitter.Count);
        Assert.Equal("live", fixture.Submitter.Last.Text);
        Assert.Equal("live", session.CurrentText);
    }

    [Fact]
    public void A_search_box_that_was_cleared_meanwhile_does_not_submit_on_enter()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        fixture.ReadLiveText = () => string.Empty;
        using var session = fixture.CreateSession();
        session.OnTextChanged("typed");

        session.OnEnterDetected("keyboard hook");

        Assert.Equal(0, fixture.Submitter.Count);
    }

    [Fact]
    public void A_search_box_that_was_cleared_meanwhile_does_not_submit_on_the_idle_timeout()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 100);
        fixture.ReadLiveText = () => string.Empty;
        using var session = fixture.CreateSession();

        session.OnTextChanged("typed");
        Thread.Sleep(400);

        Assert.Equal(0, fixture.Submitter.Count);
    }

    [Fact]
    public void An_unreadable_search_box_falls_back_to_the_remembered_text()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        using var session = fixture.CreateSession();
        session.OnTextChanged("typed");

        fixture.ReadLiveText = () => null;
        session.OnEnterDetected("keyboard hook");
        Assert.Equal("typed", fixture.Submitter.Last.Text);

        fixture.ReadLiveText = () => throw new InvalidOperationException("the window is gone");
        Thread.Sleep(450);
        session.OnEnterDetected("keyboard hook");
        Assert.Equal(2, fixture.Submitter.Count);
        Assert.Equal("typed", fixture.Submitter.Last.Text);
    }

    [Fact]
    public void OnQueryStarted_fires_only_when_a_new_query_begins()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 60_000);
        var started = 0;
        fixture.OnQueryStarted = () => Interlocked.Increment(ref started);
        using var session = fixture.CreateSession();

        session.OnTextChanged("a");
        Assert.Equal(1, started);

        session.OnTextChanged("ab");
        Assert.Equal(1, started);

        session.OnTextChanged(string.Empty);
        Assert.Equal(1, started);

        session.OnTextChanged("x");
        Assert.Equal(2, started);
        Assert.Equal("x", session.CurrentText);
    }

    [Fact]
    public void A_failing_query_started_callback_does_not_break_the_session()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 100);
        fixture.OnQueryStarted = () => throw new InvalidOperationException("the Shell is gone");
        using var session = fixture.CreateSession();

        session.OnTextChanged("a");

        Assert.True(fixture.Submitter.WaitForCount(1));
        Assert.Equal("a", fixture.Submitter.Last.Text);
    }

    [Fact]
    public void Dispose_stops_every_further_submission()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 100);
        var session = fixture.CreateSession();

        session.OnTextChanged("typed");
        session.Dispose();

        Thread.Sleep(400);
        Assert.Equal(0, fixture.Submitter.Count);

        session.OnTextChanged("typed again");
        session.OnEnterDetected("keyboard hook");
        Thread.Sleep(200);
        Assert.Equal(0, fixture.Submitter.Count);

        session.Dispose(); // must be idempotent
    }

    [Fact]
    public void The_idle_delay_follows_the_configuration_at_runtime()
    {
        using var fixture = new SessionFixture(autoSearchDelayMs: 100);
        using var session = fixture.CreateSession();

        fixture.Config.AutoSearchDelay = 2_000;
        session.OnTextChanged("typed");
        Thread.Sleep(500);
        Assert.Equal(0, fixture.Submitter.Count);

        // The shortened delay applies to the next text change.
        fixture.Config.AutoSearchDelay = 120;
        session.OnTextChanged("typed more");
        Assert.True(fixture.Submitter.WaitForCount(1));
        Assert.Equal("typed more", fixture.Submitter.Last.Text);
    }
}
