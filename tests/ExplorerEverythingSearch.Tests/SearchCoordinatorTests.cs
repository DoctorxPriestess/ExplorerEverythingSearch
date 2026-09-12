using System.Collections.Generic;
using System.Linq;
using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Everything;
using ExplorerEverythingSearch.Core.Search;
using ExplorerEverythingSearch.Core.Shell;
using ExplorerEverythingSearch.Core.Threading;
using ExplorerEverythingSearch.Tests.TestSupport;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>
/// The coordinator owns the de-duplication rules (supersede, identical target, per window state). Its
/// consequences are observed through its two events and through its log lines; Everything itself is
/// never contacted - the configured "Everything.exe" is either a file that cannot be started or a
/// system utility that exits immediately, paired with a fake Everything window.
/// </summary>
public sealed class SearchCoordinatorTests
{
    private const long Hwnd = 0x5150;
    private const string LiveDirectory = @"D:\ees-tests\folder";

    private sealed class CoordinatorFixture : IDisposable
    {
        private readonly TempWorkspace _workspace = new("coordinator");
        private readonly object _sync = new();
        private readonly List<ScopeResolution> _unresolved = new();
        private readonly List<string> _unresolvedTexts = new();
        private readonly List<string> _unavailableTexts = new();
        private readonly List<string> _unavailableErrors = new();

        public CoordinatorFixture(bool fileLogging = false)
        {
            Config = new AppConfig { AutoSearchDelay = 100 };
            Logger = fileLogging
                ? AppLogger.Create(_workspace.Root, new AppConfig { LoggingEnabled = true, LogLevel = "Debug" })
                : AppLogger.CreateDisabled(_workspace.Root);
            ScopeResolver = new SearchScopeResolver(Locations, new KnownFolderResolver(), Tracker, Logger, _ => true);
            Sta = new StaDispatcher("EES-Tests-STA");
            Bridge = new EverythingBridge(new EverythingLocator(() => Config, Logger), Logger, () => Config);
            Coordinator = new SearchCoordinator(() => Config, Logger, ScopeResolver, Sta, Bridge);
            Coordinator.ScopeNotResolved += (resolution, text) =>
            {
                lock (_sync)
                {
                    _unresolved.Add(resolution);
                    _unresolvedTexts.Add(text);
                }
            };
            Coordinator.EverythingUnavailable += (text, error) =>
            {
                lock (_sync)
                {
                    _unavailableTexts.Add(text);
                    _unavailableErrors.Add(error);
                }
            };
        }

        public AppConfig Config { get; }

        public AppLogger Logger { get; }

        public FakeLocationResolver Locations { get; } = new();

        public WindowScopeTracker Tracker { get; } = new();

        public SearchScopeResolver ScopeResolver { get; }

        public StaDispatcher Sta { get; }

        public EverythingBridge Bridge { get; }

        public SearchCoordinator Coordinator { get; }

        public string LogPath => Path.Combine(_workspace.Root, "logs", "app.log");

        public IReadOnlyList<string> UnavailableTexts
        {
            get { lock (_sync) return _unavailableTexts.ToArray(); }
        }

        public IReadOnlyList<string> UnavailableErrors
        {
            get { lock (_sync) return _unavailableErrors.ToArray(); }
        }

        public IReadOnlyList<ScopeResolution> Unresolved
        {
            get { lock (_sync) return _unresolved.ToArray(); }
        }

        public IReadOnlyList<string> UnresolvedTexts
        {
            get { lock (_sync) return _unresolvedTexts.ToArray(); }
        }

        /// <summary>A file with an .exe name that Windows refuses to start: the bridge fails immediately.</summary>
        public void UseUnstartableEverythingExecutable()
            => Config.EverythingPath = _workspace.CreateFile("fake-everything.exe", "MZ not a real Windows executable");

        /// <summary>A real system utility that exits within a few milliseconds, used for the success path.</summary>
        public void UseFastExitingEverythingExecutable()
        {
            foreach (var name in new[] { "where.exe", "whoami.exe", "hostname.exe" })
            {
                var candidate = Path.Combine(Environment.SystemDirectory, name);
                if (!File.Exists(candidate)) continue;
                Config.EverythingPath = candidate;
                return;
            }
            throw new InvalidOperationException("no fast exiting system utility was found to stand in for Everything.exe");
        }

        public void PointsAt(string selfPath, string displayName = "name")
            => Locations.OnResolve = hwnd => new ExplorerWindowLocation(hwnd, displayName, "url", selfPath, null);

        public void Dispose()
        {
            Coordinator.Dispose();
            Sta.Dispose();
            Logger.Dispose();
            _workspace.Dispose();
        }
    }

    [Fact]
    public void An_empty_search_text_is_never_submitted()
    {
        using var fixture = new CoordinatorFixture();
        fixture.UseUnstartableEverythingExecutable();

        fixture.Coordinator.Submit(Hwnd, string.Empty, TriggerType.Enter);
        fixture.Coordinator.Submit(Hwnd, "   ", TriggerType.IdleTimeout);
        Thread.Sleep(300);

        Assert.Equal(0, fixture.Locations.Calls);
        Assert.Empty(fixture.UnavailableTexts);
        Assert.Empty(fixture.Unresolved);
    }

    [Fact]
    public void A_disabled_monitor_ignores_submissions()
    {
        using var fixture = new CoordinatorFixture();
        fixture.UseUnstartableEverythingExecutable();
        fixture.Config.Enabled = false;

        fixture.Coordinator.Submit(Hwnd, "report", TriggerType.Enter);
        Thread.Sleep(300);

        Assert.Equal(0, fixture.Locations.Calls);
        Assert.Empty(fixture.UnavailableTexts);
    }

    [Fact]
    public void The_search_text_is_trimmed_before_it_is_used()
    {
        using var fixture = new CoordinatorFixture();
        fixture.UseUnstartableEverythingExecutable();
        fixture.PointsAt(LiveDirectory);

        fixture.Coordinator.Submit(Hwnd, "  report  ", TriggerType.Enter);

        Assert.True(Wait.Until(() => fixture.UnavailableTexts.Count == 1, 10_000));
        Assert.Equal("report", fixture.UnavailableTexts[0]);
        Assert.NotEmpty(fixture.UnavailableErrors[0]);
    }

    [Fact]
    public void An_unresolvable_location_is_reported_instead_of_being_searched()
    {
        using var fixture = new CoordinatorFixture();
        fixture.UseUnstartableEverythingExecutable();
        fixture.Locations.OnResolve = _ => null;

        fixture.Coordinator.Submit(Hwnd, "report", TriggerType.Enter);

        Assert.True(Wait.Until(() => fixture.Unresolved.Count == 1, 10_000));
        Assert.Equal(ScopeFailureReason.ExplorerWindowNotFound, fixture.Unresolved[0].Failure);
        Assert.Equal("report", fixture.UnresolvedTexts[0]);
        Assert.Empty(fixture.UnavailableTexts);
    }

    [Fact]
    public void Rapid_submissions_for_one_window_only_run_the_newest_one()
    {
        using var fixture = new CoordinatorFixture();
        fixture.UseUnstartableEverythingExecutable();

        using var gate = new ManualResetEventSlim(false);
        var resolveCalls = 0;
        fixture.Locations.OnResolve = hwnd =>
        {
            Interlocked.Increment(ref resolveCalls);
            gate.Wait(TimeSpan.FromSeconds(30));
            return new ExplorerWindowLocation(hwnd, "folder", "url", LiveDirectory, null);
        };

        // The worker is held inside the first resolution while the remaining requests pile up.
        fixture.Coordinator.Submit(Hwnd, "t1", TriggerType.Enter);
        Assert.True(Wait.Until(() => Volatile.Read(ref resolveCalls) >= 1, 10_000), "the worker never started resolving");
        for (var i = 2; i <= 6; i++) fixture.Coordinator.Submit(Hwnd, $"t{i}", TriggerType.IdleTimeout);
        gate.Set();

        Assert.True(Wait.Until(() => fixture.UnavailableTexts.Count >= 2, 20_000), "the newest search was not executed");
        Thread.Sleep(400);

        // Only the request that was already running and the newest one are executed; 2..5 are obsolete.
        Assert.Equal(new[] { "t1", "t6" }, fixture.UnavailableTexts);
        Assert.Equal(2, Volatile.Read(ref resolveCalls));
        Assert.Empty(fixture.Unresolved);
    }

    [Fact]
    public void The_resolved_scope_is_carried_over_from_the_live_folder_to_the_search_view()
    {
        using var fixture = new CoordinatorFixture(fileLogging: true);
        fixture.UseUnstartableEverythingExecutable();
        fixture.PointsAt(LiveDirectory, "folder");

        fixture.Coordinator.Submit(Hwnd, "report", TriggerType.Enter);
        Assert.True(Wait.Until(() => fixture.UnavailableTexts.Count == 1, 10_000));

        // Explorer has now turned the window into a search result view of the same folder.
        fixture.PointsAt("\u201cfolder\u201d\u4e2d\u7684\u641c\u7d22\u7ed3\u679c&report", "Search results in folder");
        fixture.Coordinator.Submit(Hwnd, "report", TriggerType.Enter);

        Assert.True(Wait.Until(() => fixture.UnavailableTexts.Count == 2, 10_000));
        var log = Wait.ForFileText(fixture.LogPath, t => t.Contains("(carried over from the window's folder)"), 10_000);
        Assert.Contains(@"ResolvedPath=""D:\ees-tests\folder""", log);
        Assert.DoesNotContain("search not redirected", log);
    }

    [Fact]
    public void An_identical_search_for_the_same_window_is_skipped_until_the_window_is_forgotten()
    {
        using var fixture = new CoordinatorFixture(fileLogging: true);
        fixture.UseFastExitingEverythingExecutable();
        fixture.PointsAt(LiveDirectory, "folder");
        var query = "ees-" + Guid.NewGuid().ToString("N")[..8];
        using var everythingWindow = new FakeEverythingSearchWindow(query);

        // The bridge verifies the search by Everything's window title, so the fake window must be found.
        Assert.Contains(everythingWindow.Handle, EverythingIpc.FindSearchWindows());
        Assert.Contains(query, EverythingIpc.GetSearchWindowTitle(everythingWindow.Handle));

        fixture.Coordinator.Submit(Hwnd, query, TriggerType.Enter);
        var log = Wait.ForFileText(fixture.LogPath, t => t.Contains("Search completed in"), 20_000);
        Assert.Contains("Everything window reused", log);

        // The same target for the same window must not produce a second Everything window.
        fixture.Coordinator.Submit(Hwnd, query, TriggerType.Enter);
        Wait.ForFileText(fixture.LogPath, t => t.Contains("identical search already performed for this window; skipped"), 20_000);

        // A closed window forgets what it searched for, so the same search runs again.
        fixture.Coordinator.Forget(Hwnd);
        fixture.Coordinator.Submit(Hwnd, query, TriggerType.Enter);

        Assert.True(Wait.Until(
            () => Wait.CountOccurrences(Wait.ReadShared(fixture.LogPath), "Search completed in") == 2,
            20_000), "the search after Forget was not executed");
        Assert.Equal(1, Wait.CountOccurrences(Wait.ReadShared(fixture.LogPath), "identical search already performed for this window; skipped"));
        Assert.Empty(fixture.Unresolved);
        Assert.Empty(fixture.UnavailableTexts);
    }

    [Fact]
    public void Submitting_after_Dispose_is_ignored()
    {
        var fixture = new CoordinatorFixture();
        fixture.UseUnstartableEverythingExecutable();
        fixture.PointsAt(LiveDirectory);
        fixture.Coordinator.Dispose();

        fixture.Coordinator.Submit(Hwnd, "report", TriggerType.Enter);
        Thread.Sleep(200);

        Assert.Equal(0, fixture.Locations.Calls);
        Assert.Empty(fixture.UnavailableTexts);
        fixture.Dispose();
    }

    [Fact]
    public void Several_windows_are_tracked_independently()
    {
        using var fixture = new CoordinatorFixture();
        fixture.UseUnstartableEverythingExecutable();
        fixture.PointsAt(LiveDirectory);

        fixture.Coordinator.Submit(Hwnd, "report", TriggerType.Enter);
        fixture.Coordinator.Submit(Hwnd + 1, "other", TriggerType.Enter);

        Assert.True(Wait.Until(() => fixture.UnavailableTexts.Count == 2, 10_000));
        Assert.Equal(2, fixture.Locations.Calls);
        Assert.Equal(new[] { "report", "other" }, fixture.UnavailableTexts);
    }
}
