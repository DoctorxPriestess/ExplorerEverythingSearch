using System.Collections.Concurrent;
using System.Windows.Automation;
using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Interop;
using ExplorerEverythingSearch.Core.Search;
using ExplorerEverythingSearch.Core.Shell;
using ExplorerEverythingSearch.Core.Threading;

namespace ExplorerEverythingSearch.Core.Explorer;

/// <summary>
/// Discovers Explorer windows and keeps a <see cref="SearchBoxWatcher"/> attached to each one.
///
/// The design is event driven on purpose (the specification forbids high frequency polling):
/// - window discovery uses an out-of-context WinEvent hook (create / destroy / show / hide / title change),
/// - the search box is watched through UI Automation events,
/// - one low frequency self healing rescan (default 60 s, configurable, 0 = off) repairs a missed
///   window event and retries windows whose search box was not ready yet.
/// While nothing happens the process does not wake up at all.
/// </summary>
public sealed class ExplorerWindowMonitor : IDisposable
{
    public const string ExplorerWindowClassName = "CabinetWClass";

    private readonly Func<AppConfig> _config;
    private readonly AppLogger _logger;
    private readonly StaDispatcher _sta;
    private readonly ISearchSubmitter _submitter;
    private readonly Action<long>? _onWindowClosed;
    private readonly SearchScopeResolver? _scopes;

    private readonly ConcurrentDictionary<long, WindowEntry> _windows = new();
    private readonly List<IntPtr> _hooks = new();
    private readonly NativeMethods.LowLevelKeyboardProc _keyDownHandler;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private readonly AutomationFocusChangedEventHandler _focusHandler;

    /// <summary>
    /// Must stay referenced for as long as the WinEvent hooks exist: the native side only stores the
    /// function pointer, so a delegate that is not rooted here can be collected and the next window
    /// event then calls into freed memory, which terminates the process (not a catchable exception).
    /// </summary>
    private readonly NativeMethods.WinEventDelegate _winEventHandler;

    private Timer? _timer;
    private long _focusedSearchBoxHwnd;

    /// <summary>
    /// How long after the last keyboard or mouse input a focus change may still count as an Enter press.
    /// UI Automation delivers the focus change a little after the key, while Explorer's own view
    /// updates move the focus hundreds of milliseconds after the last keystroke.
    /// </summary>
    private const uint EnterInputFreshnessMs = 500;
    private DateTimeOffset _nextAttachRetryAt = DateTimeOffset.MaxValue;
    private DateTimeOffset _nextRescanAt = DateTimeOffset.MaxValue;
    private bool _seenExplorer;
    private bool _allClosedSince;
    private volatile bool _started;
    private volatile bool _disposed;

    /// <summary>Only touched on the STA thread, which is the thread UI Automation requires here.</summary>
    private bool _focusHandlerRegistered;

    private sealed class WindowEntry
    {
        public required long Hwnd { get; init; }
        public required SearchBoxWatcher Watcher { get; init; }
        public required ExplorerSearchSession Session { get; init; }
        public int AttachAttempts;
        public DateTimeOffset NextAttachAttemptAt = DateTimeOffset.MinValue;
    }

    public ExplorerWindowMonitor(
        Func<AppConfig> config,
        AppLogger logger,
        StaDispatcher sta,
        ISearchSubmitter submitter,
        Action<long>? onWindowClosed = null,
        SearchScopeResolver? scopes = null)
    {
        _config = config;
        _logger = logger;
        _sta = sta;
        _submitter = submitter;
        _onWindowClosed = onWindowClosed;
        _scopes = scopes;
        _focusHandler = OnFocusChanged;
        _keyDownHandler = OnGlobalKeyDown;
        _winEventHandler = OnWinEvent;
    }

    public bool IsRunning => _started && !_disposed;

    public int TrackedWindowCount => _windows.Count;

    public int AttachedSearchBoxCount => _windows.Values.Count(w => w.Watcher.IsAttached);

    /// <summary>Starts monitoring; safe to call from any thread.</summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _timer = new Timer(_ => _sta.Post(Tick), null, Timeout.Infinite, Timeout.Infinite);
        _sta.Post(() =>
        {
            InstallHooks();
            AddFocusHandler();
            Rescan("startup");
        });
        _logger.Info("Explorer search monitoring started");
    }

    /// <summary>Stops monitoring and releases every subscription.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        try { _timer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try
        {
            _sta.Invoke<object?>(() =>
            {
                UninstallHooks();
                foreach (var entry in _windows.Values) Detach(entry);
                _windows.Clear();
                return null;
            }, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.Debug($"stopping the Explorer monitor reported: {ex.Message}");
        }

        // Removing the UI Automation focus handler is measured at about six seconds of blocking inside
        // UI Automation (docs/verification.md section 12.9) and it has to run on the STA thread, so it is
        // posted rather than invoked: waiting for it would freeze the caller (the tray menu and the
        // settings dialog call Stop on the UI thread) and it used to trip the invoke timeout above on
        // every shutdown. By the time it runs the monitor is stopped and the handler returns at once.
        // Posted outside the try so it still happens if the synchronous part above timed out.
        _sta.Post(RemoveFocusHandler);
        _logger.Info("Explorer search monitoring stopped");
    }

    /// <summary>
    /// Runs on the STA thread, which is the thread the focus handler was registered on. The flag keeps
    /// a stop/start cycle from registering the handler twice while a removal is still queued.
    /// </summary>
    private void AddFocusHandler()
    {
        if (_focusHandlerRegistered) return;
        try
        {
            Automation.AddAutomationFocusChangedEventHandler(_focusHandler);
            _focusHandlerRegistered = true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"could not subscribe to UI Automation focus changes: {ex.Message}");
        }
    }

    /// <summary>Runs on the STA thread; see <see cref="AddFocusHandler"/>.</summary>
    private void RemoveFocusHandler()
    {
        if (!_focusHandlerRegistered) return;
        _focusHandlerRegistered = false;
        try { Automation.RemoveAutomationFocusChangedEventHandler(_focusHandler); } catch { }
    }

    /// <summary>Forces a rescan; used by the tray menu.</summary>
    public void RescanNow() => _sta.Post(() => Rescan("manual"));

    /// <summary>
    /// Gives the keyboard focus back to the search box of an Explorer window. Called when the
    /// Everything window appeared so that the user can carry on typing where they were.
    /// </summary>
    public void FocusSearchBox(long hwnd)
    {
        if (_disposed) return;
        if (_windows.TryGetValue(hwnd, out var entry)) entry.Watcher.FocusSearchBoxThreadSafe();
    }

    // ------------------------------------------------------------------ WinEvent hooks

    private void InstallHooks()
    {
        if (_hooks.Count > 0) return;
        var ranges = new (uint Min, uint Max)[]
        {
            (NativeMethods.EVENT_OBJECT_CREATE, NativeMethods.EVENT_OBJECT_HIDE),
            (NativeMethods.EVENT_OBJECT_NAMECHANGE, NativeMethods.EVENT_OBJECT_NAMECHANGE),
        };

        foreach (var (min, max) in ranges)
        {
            var hook = NativeMethods.SetWinEventHook(
                min, max, IntPtr.Zero, _winEventHandler, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
            if (hook != IntPtr.Zero) _hooks.Add(hook);
            else _logger.Warn($"could not install the WinEvent hook for 0x{min:X}-0x{max:X}");
        }

        _logger.Debug($"WinEvent hooks installed: {_hooks.Count}");
    }

    private void UninstallHooks()
    {
        UninstallKeyboardHook();
        foreach (var hook in _hooks)
        {
            try { NativeMethods.UnhookWinEvent(hook); } catch { }
        }
        _hooks.Clear();
    }

    /// <summary>
    /// Watches for the Enter key while an Explorer search box has the keyboard focus.
    ///
    /// This is the only exact Enter signal: pressing Enter moves the focus out of the box, but
    /// Explorer moves it out on its own as well (its search view is entered and refreshed while the
    /// user types), so the focus alone cannot be told apart. The hook is installed only while a search
    /// box is focused - outside of that the tool does not observe the keyboard at all.
    /// </summary>
    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero || _disposed) return;
        if (!_config().DetectEnterByKeyboardHook) return;
        try
        {
            var module = NativeMethods.GetModuleHandle(null);
            var hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyDownHandler, module, 0);
            if (hook == IntPtr.Zero)
            {
                _logger.Warn("could not install the keyboard hook for Enter detection; falling back to the focus and commit heuristics");
                return;
            }
            _keyboardHook = hook;
            _logger.Debug("Enter detection: keyboard hook installed (an Explorer search box has the focus)");
        }
        catch (Exception ex)
        {
            _logger.Warn($"could not install the keyboard hook for Enter detection: {ex.Message}");
        }
    }

    private void UninstallKeyboardHook()
    {
        var hook = _keyboardHook;
        if (hook == IntPtr.Zero) return;
        _keyboardHook = IntPtr.Zero;
        try { NativeMethods.UnhookWindowsHookEx(hook); } catch { }
        _logger.Debug("Enter detection: keyboard hook removed (no Explorer search box has the focus)");
    }

    /// <summary>Low level keyboard callback; delivered on the STA thread, so it must stay fast.</summary>
    private IntPtr OnGlobalKeyDown(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN)
                && NativeMethods.KeyCodeFromHookData(lParam) == NativeMethods.VK_RETURN)
            {
                var hwnd = Interlocked.Read(ref _focusedSearchBoxHwnd);
                if (hwnd != 0 && _windows.TryGetValue(hwnd, out var entry))
                    entry.Session.OnEnterDetected("the Enter key was pressed in the search box");
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"keyboard hook handling failed: {ex.Message}");
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>Out-of-context WinEvent callback; delivered on the STA thread, which pumps messages.</summary>
    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;

        var handle = (long)hwnd;
        switch (eventType)
        {
            case NativeMethods.EVENT_OBJECT_CREATE:
            case NativeMethods.EVENT_OBJECT_SHOW:
                if (NativeMethods.GetClassNameString(hwnd) == ExplorerWindowClassName) AddWindow(hwnd);
                break;

            case NativeMethods.EVENT_OBJECT_DESTROY:
            case NativeMethods.EVENT_OBJECT_HIDE:
                RemoveWindow(handle);
                break;

            case NativeMethods.EVENT_OBJECT_NAMECHANGE:
                if (_windows.TryGetValue(handle, out var entry))
                {
                    entry.Watcher.NotifyExplorerCommit(NativeMethods.GetWindowTextString(hwnd));

                    // With no query in the search box the window may be showing a real folder again
                    // (navigation, or leaving a search view), so the remembered folder is refreshed.
                    if (entry.Session.CurrentText.Length == 0) CaptureLiveScope(handle);
                }
                break;
        }
    }

    /// <summary>
    /// True when the Shell reports this window as a search result view. Runs on the STA thread (the
    /// caller is a WinEvent or UI Automation callback), which is also the thread Shell automation
    /// objects must be used from.
    /// </summary>
    private bool IsSearchResultsView(long hwnd)
    {
        if (_scopes is null) return true; // no Shell access available: keep the heuristic working
        try
        {
            var window = _scopes.Locations.TryResolve(hwnd);
            if (window is null) return false;
            var location = ShellLocationClassifier.Classify(window.SelfPath, window.LocationName);
            return location.Kind == ShellLocationKind.SearchResults;
        }
        catch (Exception ex)
        {
            _logger.Debug($"could not verify whether hwnd={hwnd} shows search results: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Remembers the folder a window is showing right now. Runs on the STA thread, which is also the
    /// thread Shell automation objects must be used from.
    /// </summary>
    private void CaptureLiveScope(long hwnd)
    {
        if (_scopes is null) return;
        _scopes.TryRecordLiveScope(hwnd);
    }

    // ------------------------------------------------------------------ window bookkeeping

    private void AddWindow(IntPtr hwnd)
    {
        var handle = (long)hwnd;
        if (_windows.ContainsKey(handle)) return;

        var session = new ExplorerSearchSession(
            handle, _submitter, _logger, _config,
            () => IsSearchResultsView(handle),
            () => CaptureLiveScope(handle),
            () => _windows.TryGetValue(handle, out var e) ? e.Watcher.TryReadTextThreadSafe() : null);
        var watcher = new SearchBoxWatcher(handle, _sta, session, _logger);
        var entry = new WindowEntry { Hwnd = handle, Watcher = watcher, Session = session };

        if (!_windows.TryAdd(handle, entry))
        {
            session.Dispose();
            return;
        }

        _logger.Info($"Explorer detected hwnd={handle}");
        if (_allClosedSince && _seenExplorer)
            _logger.Info("Explorer restarted: monitoring re-established");
        _seenExplorer = true;
        _allClosedSince = false;

        AttachOrSchedule(entry, immediate: true);
        ScheduleNextTick();
    }

    private void RemoveWindow(long handle)
    {
        if (!_windows.TryRemove(handle, out var entry)) return;
        Detach(entry);
        _logger.Info($"Explorer closed hwnd={handle}");
        if (_windows.Count == 0) _allClosedSince = true;
        ScheduleNextTick();
    }

    private void Detach(WindowEntry entry)
    {
        try { entry.Watcher.Dispose(); } catch { }
        try { entry.Session.Dispose(); } catch { }
        try { _onWindowClosed?.Invoke(entry.Hwnd); } catch { }
        if (Interlocked.Read(ref _focusedSearchBoxHwnd) == entry.Hwnd)
        {
            Interlocked.Exchange(ref _focusedSearchBoxHwnd, 0);
            // The focused search box is gone, so the Enter key does not have to be watched any more.
            UninstallKeyboardHook();
        }
    }

    private void AttachOrSchedule(WindowEntry entry, bool immediate)
    {
        if (entry.Watcher.IsAttached) return;

        if (!NativeMethods.IsWindow(new IntPtr(entry.Hwnd)))
        {
            RemoveWindow(entry.Hwnd);
            return;
        }

        if (!immediate && DateTimeOffset.Now < entry.NextAttachAttemptAt) return;

        if (entry.Watcher.Attach())
        {
            entry.AttachAttempts = 0;
            // The window is still in its live view here; remember the folder before any search is committed.
            CaptureLiveScope(entry.Hwnd);
            return;
        }

        entry.AttachAttempts++;
        var delayMs = Math.Min(30_000, 300 * (int)Math.Pow(2, Math.Min(entry.AttachAttempts, 7)));
        entry.NextAttachAttemptAt = DateTimeOffset.Now.AddMilliseconds(delayMs);
        if (entry.NextAttachAttemptAt < _nextAttachRetryAt) _nextAttachRetryAt = entry.NextAttachAttemptAt;
        ScheduleNextTick();
    }

    // ------------------------------------------------------------------ rescan and scheduling

    private void Rescan(string reason)
    {
        if (_disposed) return;
        try
        {
            var found = new HashSet<long>();
            foreach (var hwnd in NativeMethods.FindTopLevelWindows(ExplorerWindowClassName))
            {
                found.Add((long)hwnd);
                AddWindow(hwnd);
            }

            foreach (var handle in _windows.Keys)
            {
                if (!found.Contains(handle) || !NativeMethods.IsWindow(new IntPtr(handle)))
                    RemoveWindow(handle);
            }

            foreach (var entry in _windows.Values) AttachOrSchedule(entry, immediate: false);

            _logger.Debug($"Explorer rescan ({reason}): windows={_windows.Count} searchBoxes={AttachedSearchBoxCount}");
        }
        catch (Exception ex)
        {
            _logger.Error("Explorer rescan failed", ex);
        }
        ScheduleNextTick();
    }

    /// <summary>Single self-scheduling tick: the next attach retry or the periodic self healing rescan.</summary>
    private void Tick()
    {
        if (_disposed || !_started) return;
        try
        {
            var config = _config();
            var now = DateTimeOffset.Now;

            if (now >= _nextAttachRetryAt)
            {
                _nextAttachRetryAt = DateTimeOffset.MaxValue;
                foreach (var entry in _windows.Values) AttachOrSchedule(entry, immediate: false);
            }

            if (config.ExplorerRescanSeconds > 0 && now >= _nextRescanAt)
            {
                _nextRescanAt = now.AddSeconds(config.ExplorerRescanSeconds);
                Rescan("periodic");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"monitor tick failed: {ex.Message}");
        }
        ScheduleNextTick();
    }

    private void ScheduleNextTick()
    {
        if (_timer is null || _disposed) return;
        try
        {
            var config = _config();
            var now = DateTimeOffset.Now;

            if (config.ExplorerRescanSeconds <= 0) _nextRescanAt = DateTimeOffset.MaxValue;
            else if (_nextRescanAt == DateTimeOffset.MaxValue) _nextRescanAt = now.AddSeconds(config.ExplorerRescanSeconds);

            var due = _nextAttachRetryAt <= _nextRescanAt ? _nextAttachRetryAt : _nextRescanAt;
            if (due == DateTimeOffset.MaxValue)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            var delay = due - now;
            _timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.Debug($"could not schedule the next monitor tick: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ focus (Enter detection)

    /// <summary>
    /// Pressing Enter in the Explorer search box moves the keyboard focus into the result view, while
    /// Explorer's own automatic search keeps the focus in the search box. A focus change that leaves the
    /// search box for another element of the same window is therefore treated as Enter - unless it was
    /// caused by a mouse click (the button is still down when the focus change is reported).
    /// </summary>
    private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
    {
        if (_disposed || !_started) return;
        try
        {
            var focused = sender as AutomationElement;
            var hwnd = TryGetTopLevelHwnd(focused);
            var previous = Interlocked.Read(ref _focusedSearchBoxHwnd);

            if (hwnd != 0 && _windows.TryGetValue(hwnd, out var entry) && entry.Watcher.IsSearchBoxElement(focused))
            {
                if (Interlocked.Exchange(ref _focusedSearchBoxHwnd, hwnd) != hwnd)
                {
                    _logger.Debug($"search box has the focus (hwnd={hwnd})");
                    InstallKeyboardHook();
                }
                return;
            }

            if (previous == 0)
            {
                if (hwnd != 0 && _windows.ContainsKey(hwnd))
                    _logger.Debug($"focus moved inside Explorer hwnd={hwnd} -> {Describe(focused)} while the search box was not focused");
                return;
            }

            // Explorer moves the focus into the input site that hosts the search box by itself, both
            // when its search view is entered and when it is refreshed while the user types - measured
            // as little as 140 ms after a keystroke. Typing still reaches the search box from there, so
            // the Enter key keeps being watched, but the move itself is no Enter press: a real Enter
            // moves the focus into the result area (a result item, or the text of the empty state when
            // nothing matches).
            if (hwnd == previous && IsSearchViewInputSite(focused))
            {
                _logger.Debug("Explorer moved the focus into its own search view input site; not treated as Enter");
                return;
            }

            Interlocked.Exchange(ref _focusedSearchBoxHwnd, 0);
            UninstallKeyboardHook();

            if (previous != hwnd)
            {
                _logger.Debug("focus left the Explorer search box for another window; not treated as Enter");
                return;
            }

            if (!_config().DetectEnterByFocusChange)
            {
                _logger.Debug("focus left the search box (Enter-by-focus detection disabled)");
                return;
            }

            if (NativeMethods.IsAnyMouseButtonDown())
            {
                _logger.Debug("focus left the search box while a mouse button is down; not treated as Enter");
                return;
            }

            // Explorer's own view updates move the focus out of the box without a keypress of its own
            // (the input site case above, and the search box focus restore done by the search itself).
            // A real Enter is a keypress, so the focus change has to follow user input closely.
            var sinceInput = unchecked(NativeMethods.GetTickCount() - NativeMethods.LastInputTick());
            if (sinceInput > EnterInputFreshnessMs)
            {
                _logger.Debug($"focus left the search box {sinceInput} ms after the last user input; not treated as Enter");
                return;
            }

            if (_windows.TryGetValue(previous, out var sessionEntry))
                sessionEntry.Session.OnEnterDetected($"focus left the search box -> {Describe(focused)}");
        }
        catch (ElementNotAvailableException)
        {
            // the focused element disappeared before it could be inspected
        }
        catch (Exception ex)
        {
            _logger.Debug($"focus change handling failed: {ex.Message}");
        }
    }

    /// <summary>True when the element is the input site that hosts the search box inside a search view.</summary>
    private static bool IsSearchViewInputSite(AutomationElement? element)
    {
        if (element is null) return false;
        try
        {
            return element.Current.ControlType == ControlType.Pane
                   && string.Equals(element.Current.ClassName, "InputSiteWindowClass", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string Describe(AutomationElement? element)
    {
        if (element is null) return "<null>";
        try
        {
            return $"{element.Current.ControlType.ProgrammaticName} class={element.Current.ClassName} name=\"{element.Current.Name}\"";
        }
        catch
        {
            return "<unavailable>";
        }
    }

    /// <summary>Walks up to the owning top level window handle of an automation element.</summary>
    private static long TryGetTopLevelHwnd(AutomationElement? element)
    {
        if (element is null) return 0;
        try
        {
            var current = element;
            for (var depth = 0; depth < 12 && current is not null; depth++)
            {
                if (current.Current.ControlType == ControlType.Window)
                {
                    var handle = current.Current.NativeWindowHandle;
                    return handle;
                }
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
        }
        catch
        {
            // element went away
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        try { _timer?.Dispose(); } catch { }
    }
}
