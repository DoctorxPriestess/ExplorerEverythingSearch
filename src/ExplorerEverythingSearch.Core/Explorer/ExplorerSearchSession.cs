using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Search;

namespace ExplorerEverythingSearch.Core.Explorer;

/// <summary>
/// State of one Explorer search box ("search session").
///
/// The session outlives the Everything window: as long as the Explorer search box exists, further
/// typing, Enter and idle timeouts keep producing new search requests.
/// </summary>
public sealed class ExplorerSearchSession : IDisposable
{
    private readonly ISearchSubmitter _submitter;
    private readonly AppLogger _logger;
    private readonly Func<AppConfig> _config;
    private readonly Func<bool>? _isSearchViewActive;
    private readonly Action? _onQueryStarted;
    private readonly Func<string?>? _readLiveText;
    private readonly SearchDebouncer _debouncer;
    private readonly object _sync = new();

    private string _text = string.Empty;
    private DateTimeOffset _lastTextChangeAt;
    private DateTimeOffset _lastEnterAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCommitNotifiedAt = DateTimeOffset.MinValue;
    private string _lastCommitTitle = string.Empty;
    private bool _disposed;

    public ExplorerSearchSession(
        long hwnd,
        ISearchSubmitter submitter,
        AppLogger logger,
        Func<AppConfig> config,
        Func<bool>? isSearchViewActive = null,
        Action? onQueryStarted = null,
        Func<string?>? readLiveText = null)
    {
        Hwnd = hwnd;
        _submitter = submitter;
        _logger = logger;
        _config = config;
        _isSearchViewActive = isSearchViewActive;
        _onQueryStarted = onQueryStarted;
        _readLiveText = readLiveText;
        _lastTextChangeAt = DateTimeOffset.Now;
        _debouncer = new SearchDebouncer(OnIdleTimeout, Math.Max(100, config().AutoSearchDelay));
    }

    public long Hwnd { get; }

    public string CurrentText
    {
        get
        {
            lock (_sync) return _text;
        }
    }

    /// <summary>Text changed in the Explorer search box: restart the idle timer.</summary>
    public void OnTextChanged(string text)
    {
        if (_disposed) return;
        text ??= string.Empty;

        bool startedQuery;
        lock (_sync)
        {
            startedQuery = _text.Length == 0 && text.Length > 0;
            _text = text;
            _lastTextChangeAt = DateTimeOffset.Now;
            _debouncer.DelayMs = Math.Max(100, _config().AutoSearchDelay);
        }

        _logger.Debug($"SearchBox text changed: \"{text}\"");

        // The window is still showing a real folder on the first keystroke of a query, so this is the
        // last reliable moment to remember it for the search that is about to be committed.
        if (startedQuery)
        {
            try { _onQueryStarted?.Invoke(); }
            catch (Exception ex) { _logger.Debug($"capturing the window folder failed: {ex.Message}"); }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _debouncer.Cancel();
            return;
        }

        _debouncer.Restart();
    }

    /// <summary>Enter was detected (focus left the search box): submit immediately, without the debounce.</summary>
    public void OnEnterDetected(string reason)
    {
        if (_disposed) return;
        var text = CurrentTextAtSubmit();

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.Debug($"Enter detected ({reason}) but the search box is empty");
            _debouncer.Cancel();
            return;
        }

        // The keyboard hook and the focus fallback both notice the same Enter key press.
        lock (_sync)
        {
            if (DateTimeOffset.Now - _lastEnterAt < TimeSpan.FromMilliseconds(400))
            {
                _logger.Debug($"Enter was already submitted for this window; ignoring the second report ({reason})");
                return;
            }
            _lastEnterAt = DateTimeOffset.Now;
        }

        _logger.Debug($"Enter detected ({reason})");
        _debouncer.SubmitNow(TriggerType.Enter);
    }

    /// <summary>
    /// Explorer changed its window title. Two very different things cause this:
    /// - while typing, Explorer immediately previews the query in the title ("query - folder") without
    ///   committing anything,
    /// - a real search commit (Enter, or Explorer's own automatic search ~800 ms after the last
    ///   keystroke) turns the window into a search result view.
    /// Only a verified commit that happens right after the last keystroke can have been caused by Enter,
    /// so the search result view is checked through the Shell before the heuristic applies.
    /// </summary>
    public void OnExplorerCommitDetected(string detail)
    {
        if (_disposed) return;
        var config = _config();
        if (!config.DetectEnterByCommitTiming)
        {
            _logger.Debug($"Explorer title change ({detail})");
            return;
        }

        TimeSpan elapsed;
        string text;
        lock (_sync)
        {
            // Explorer's window title and the UI Automation name property both report the same change.
            if (string.Equals(detail, _lastCommitTitle, StringComparison.Ordinal)
                && DateTimeOffset.Now - _lastCommitNotifiedAt < TimeSpan.FromMilliseconds(400))
                return;
            _lastCommitTitle = detail;
            _lastCommitNotifiedAt = DateTimeOffset.Now;
            elapsed = DateTimeOffset.Now - _lastTextChangeAt;
            text = _text;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.Debug($"Explorer changed its title to \"{detail}\" while the search box was empty");
            return;
        }

        // Explorer's window title starts with the query that is being shown. A title that does not
        // carry the text of the search box (for example the previous query, re-shown when the search
        // box gets its focus back) is a view update of something else, not a commit of this query.
        if (!detail.TrimStart().StartsWith(text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _logger.Debug($"Explorer title \"{detail}\" does not carry the search box text \"{text}\"; not a commit of this query");
            return;
        }

        if (elapsed > TimeSpan.FromMilliseconds(config.EnterCommitWindowMs))
        {
            _logger.Debug($"Explorer committed a search after {elapsed.TotalMilliseconds:F0} ms (\"{detail}\") - treated as its own auto search");
            return;
        }

        if (_isSearchViewActive is not null && !_isSearchViewActive())
        {
            _logger.Debug($"Explorer only previewed the query in its title after {elapsed.TotalMilliseconds:F0} ms (\"{detail}\") - not a commit");
            return;
        }

        OnEnterDetected($"Explorer commit after {elapsed.TotalMilliseconds:F0} ms (\"{detail}\")");
    }

    private void OnIdleTimeout(TriggerType trigger)
    {
        if (_disposed) return;
        var text = CurrentTextAtSubmit();
        if (string.IsNullOrWhiteSpace(text)) return;
        _submitter.Submit(Hwnd, text, trigger);
    }

    /// <summary>
    /// The text that is actually in the search box at this moment.
    ///
    /// The text tracked from change notifications can lag behind (Explorer clears the box itself when a
    /// search view is left, and a cleared box must never turn into a search), so the box is re-read and
    /// the fresh reading wins. The remembered text is kept when the box cannot be read at all.
    /// </summary>
    private string CurrentTextAtSubmit()
    {
        string tracked;
        lock (_sync) tracked = _text;
        if (_readLiveText is null) return tracked;

        string? live;
        try { live = _readLiveText(); }
        catch (Exception ex)
        {
            _logger.Debug($"could not re-read the search box (hwnd={Hwnd}): {ex.Message}");
            return tracked;
        }

        if (live is null) return tracked;
        if (!string.Equals(live, tracked, StringComparison.Ordinal))
        {
            _logger.Debug($"search box text changed without a notification: \"{tracked}\" -> \"{live}\"");
            lock (_sync) _text = live;
        }
        return live;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debouncer.Dispose();
    }
}
