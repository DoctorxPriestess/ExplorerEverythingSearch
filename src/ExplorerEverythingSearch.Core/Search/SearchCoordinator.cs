using System.Collections.Concurrent;
using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Everything;
using ExplorerEverythingSearch.Core.Shell;
using ExplorerEverythingSearch.Core.Threading;

namespace ExplorerEverythingSearch.Core.Search;

/// <summary>Receives a submitted search (implemented by the coordinator, faked in unit tests).</summary>
public interface ISearchSubmitter
{
    void Submit(long explorerHwnd, string searchText, TriggerType trigger);
}

/// <summary>
/// Turns "Explorer search box told us X" into an Everything search.
///
/// Responsibilities:
/// - resolve the real directory of the *triggering* Explorer window on every submit,
/// - build the Everything query (never degrading the Explorer search scope),
/// - suppress duplicate and superseded requests,
/// - emit the structured log lines documented in the specification,
/// - report unresolvable scopes to the UI layer (never silently).
///
/// Submits are handled on a private worker thread: the UIA event thread must stay responsive and the
/// Explorer UI must never be blocked by Shell or Everything work.
/// </summary>
public sealed class SearchCoordinator : ISearchSubmitter, IDisposable
{
    private readonly Func<AppConfig> _config;
    private readonly AppLogger _logger;
    private readonly SearchScopeResolver _scopeResolver;
    private readonly StaDispatcher _sta;
    private readonly EverythingBridge _bridge;

    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _worker;
    private readonly ConcurrentDictionary<long, SearchRequest> _lastSubmitted = new();
    private readonly ConcurrentDictionary<long, long> _latestSequence = new();
    private long _sequence;
    private volatile bool _disposed;

    private sealed record WorkItem(long Sequence, long Hwnd, string Text, TriggerType Trigger);

    public SearchCoordinator(
        Func<AppConfig> config,
        AppLogger logger,
        SearchScopeResolver scopeResolver,
        StaDispatcher sta,
        EverythingBridge bridge)
    {
        _config = config;
        _logger = logger;
        _scopeResolver = scopeResolver;
        _sta = sta;
        _bridge = bridge;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "EES-SearchWorker",
        };
        _worker.Start();
    }

    /// <summary>Raised when a location cannot be turned into an Everything search scope.</summary>
    public event Action<ScopeResolution, string>? ScopeNotResolved;

    /// <summary>Raised when Everything could not be driven.</summary>
    public event Action<string, string>? EverythingUnavailable;

    public void Submit(long explorerHwnd, string searchText, TriggerType trigger)
    {
        if (_disposed) return;
        var text = (searchText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            _logger.Debug("search text is empty; nothing submitted");
            return;
        }

        var sequence = Interlocked.Increment(ref _sequence);
        _latestSequence[explorerHwnd] = sequence;
        try
        {
            _queue.Add(new WorkItem(sequence, explorerHwnd, text, trigger));
        }
        catch (InvalidOperationException)
        {
            // shutting down
        }
    }

    private void WorkerLoop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                Process(item);
            }
            catch (Exception ex)
            {
                _logger.Error("unexpected failure while submitting a search", ex);
            }
        }
    }

    private void Process(WorkItem item)
    {
        // A newer request for the same Explorer window makes this one obsolete (fast typing).
        if (_latestSequence.TryGetValue(item.Hwnd, out var latest) && latest != item.Sequence)
        {
            _logger.Debug($"search superseded hwnd={item.Hwnd} text=\"{item.Text}\"");
            return;
        }

        var config = _config();
        if (!config.Enabled)
        {
            _logger.Debug("search ignored: monitoring is disabled");
            return;
        }

        _logger.Info($"Search submitted trigger={item.Trigger}");
        _logger.Info($"SourceExplorerHwnd={item.Hwnd}");
        _logger.Info($"SearchText=\"{item.Text}\"");

        ScopeResolution resolution;
        try
        {
            resolution = _sta.Invoke(() => _scopeResolver.Resolve(item.Hwnd), TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            _logger.Error($"could not resolve the Explorer search scope (hwnd={item.Hwnd})", ex);
            ScopeNotResolved?.Invoke(
                ScopeResolution.Failed(ScopeFailureReason.LocationUnavailable, string.Empty, ex.Message),
                item.Text);
            return;
        }

        if (!resolution.Succeeded)
        {
            _logger.Warn($"search not redirected: {resolution.Failure} detail=\"{resolution.Detail}\" raw=\"{resolution.RawLocation}\"");
            ScopeNotResolved?.Invoke(resolution, item.Text);
            return;
        }

        var scope = resolution.Scope!;
        _logger.Info($"ResolvedPath=\"{resolution.Detail}\"");
        _logger.Info($"SearchScope={scope.Kind}"
                     + (scope.Source is ScopeResolutionSource.WindowScopeBeforeSearch ? " (carried over from the window's folder)" : string.Empty));

        var request = new SearchRequest(item.Hwnd, item.Text, scope, item.Trigger, DateTimeOffset.Now);

        if (_lastSubmitted.TryGetValue(item.Hwnd, out var previous) && request.HasSameTarget(previous))
        {
            _logger.Debug("identical search already performed for this window; skipped");
            return;
        }

        var capabilities = _bridge.GetCapabilities();
        if (!EverythingQueryBuilder.TryBuild(request, capabilities, config.ReuseEverythingWindow, out var invocation, out var buildError))
        {
            _logger.Error($"could not build the Everything query: {buildError}");
            EverythingUnavailable?.Invoke(item.Text, buildError ?? "unknown error");
            return;
        }

        _logger.Info($"Query=\"{invocation!.Query}\"");

        var result = _bridge.Execute(invocation!, item.Hwnd);
        if (!result.Success)
        {
            _logger.Error($"Everything search failed: {result.Error}");
            EverythingUnavailable?.Invoke(item.Text, result.Error ?? "unknown error");
            return;
        }

        _lastSubmitted[item.Hwnd] = request;
        _logger.Info(result.WindowCreated ? "Everything window created" : "Everything window reused");
        _logger.Info($"Search completed in {result.LatencyMs} ms");
    }

    /// <summary>Drops the remembered request for a window (for example when the window is closed).</summary>
    public void Forget(long explorerHwnd)
    {
        _lastSubmitted.TryRemove(explorerHwnd, out _);
        _latestSequence.TryRemove(explorerHwnd, out _);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _queue.CompleteAdding(); } catch { }
        try { _worker.Join(TimeSpan.FromSeconds(3)); } catch { }
        try { _queue.Dispose(); } catch { }
    }
}
