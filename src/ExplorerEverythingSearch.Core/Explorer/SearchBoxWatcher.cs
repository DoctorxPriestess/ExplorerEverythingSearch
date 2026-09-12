using System.Windows.Automation;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Interop;
using ExplorerEverythingSearch.Core.Threading;

namespace ExplorerEverythingSearch.Core.Explorer;

/// <summary>
/// Watches the search box of one Explorer window through UI Automation.
///
/// Verified behaviour on Windows 11 build 26200 (Explorer's search box is a WinUI control):
/// - the search box is the <c>Edit</c> inside the element with AutomationId
///   <c>FileExplorerSearchBox</c> and supports both ValuePattern and TextPattern,
/// - every keystroke raises a ValuePattern value change and a TextPattern text change,
/// - pressing Enter moves the keyboard focus out of the search box into the result list
///   (~20-170 ms later), while Explorer's own automatic search keeps the focus in the search box,
/// - the window title changes when Explorer commits its search (~86 ms after Enter, ~800 ms after
///   the last keystroke of an idle search), which is used as a fallback Enter signal.
///
/// All UI Automation calls happen on the shared STA dispatcher thread, which is also the thread that
/// pumps messages - UI Automation events are only delivered to a pumping thread.
/// </summary>
public sealed class SearchBoxWatcher : IDisposable
{
    private const string SearchBoxAutomationId = "FileExplorerSearchBox";
    private const string AddressBarAutomationId = "PART_AutoSuggestBox";

    private readonly long _hwnd;
    private readonly StaDispatcher _sta;
    private readonly ExplorerSearchSession _session;
    private readonly AppLogger _logger;

    private AutomationElement? _window;
    private AutomationElement? _searchBox;
    private AutomationPropertyChangedEventHandler? _valueHandler;
    private AutomationEventHandler? _textHandler;
    private AutomationPropertyChangedEventHandler? _nameHandler;
    private bool _attached;
    private bool _disposed;
    private int _attachAttempts;

    public SearchBoxWatcher(long hwnd, StaDispatcher sta, ExplorerSearchSession session, AppLogger logger)
    {
        _hwnd = hwnd;
        _sta = sta;
        _session = session;
        _logger = logger;
    }

    public long Hwnd => _hwnd;

    /// <summary>True when the search box element is currently attached and subscribed.</summary>
    public bool IsAttached => _attached;

    public AutomationElement? SearchBox => _searchBox;

    /// <summary>Finds the search box and subscribes to its events. Runs on the STA thread.</summary>
    public bool Attach()
    {
        if (_disposed) return false;
        try
        {
            DetachHandlers();
            var handle = new IntPtr(_hwnd);
            if (!NativeMethods.IsWindow(handle))
            {
                _logger.Debug($"Explorer window {_hwnd} no longer exists");
                return false;
            }

            _window = AutomationElement.FromHandle(handle);
            if (_window is null) return false;

            var searchBox = FindSearchBox(_window);
            if (searchBox is null)
            {
                _attachAttempts++;
                if (_attachAttempts <= 3 || _attachAttempts % 10 == 0)
                    _logger.Debug($"search box not found yet in hwnd={_hwnd} (attempt {_attachAttempts})");
                return false;
            }

            if (!IsSearchBoxElementShape(searchBox))
            {
                // Defensive: never subscribe to something that is not the search box (column editors, ...).
                _attachAttempts++;
                _logger.Debug($"element with AutomationId {SearchBoxAutomationId} has an unexpected shape in hwnd={_hwnd}; retrying");
                return false;
            }

            _searchBox = searchBox;
            _valueHandler = OnValueChanged;
            _textHandler = OnTextChanged;
            Automation.AddAutomationPropertyChangedEventHandler(
                searchBox, TreeScope.Element, _valueHandler, ValuePattern.ValueProperty);
            try
            {
                Automation.AddAutomationEventHandler(
                    TextPattern.TextChangedEvent, searchBox, TreeScope.Element, _textHandler);
            }
            catch (Exception ex)
            {
                _logger.Debug($"TextPattern handler not available for hwnd={_hwnd}: {ex.Message}");
            }

            _nameHandler = OnWindowNameChanged;
            try
            {
                Automation.AddAutomationPropertyChangedEventHandler(
                    _window, TreeScope.Element, _nameHandler, AutomationElement.NameProperty);
            }
            catch (Exception ex)
            {
                _logger.Debug($"window title handler not available for hwnd={_hwnd}: {ex.Message}");
            }

            _attached = true;
            _attachAttempts = 0;
            _logger.Info($"SearchBox detected hwnd={_hwnd} name=\"{SafeName(searchBox)}\"");
            return true;
        }
        catch (ElementNotAvailableException)
        {
            _attached = false;
            return false;
        }
        catch (Exception ex)
        {
            _attached = false;
            _logger.Debug($"search box attach failed for hwnd={_hwnd}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads the current text of the search box. Runs on the STA thread.</summary>
    public string ReadText() => TryReadText() ?? _session.CurrentText;

    /// <summary>
    /// Reads the text that is in the search box right now, or null when it cannot be read.
    /// Called at submit time, where a value that is only remembered from change notifications must not
    /// be used blindly: the box can be cleared without a notification reaching the watcher.
    /// </summary>
    public string? TryReadText()
    {
        var box = _searchBox;
        if (box is null) return null;
        try
        {
            if (box.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
                return ((ValuePattern)valuePattern).Current.Value ?? string.Empty;
            if (box.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
                return ((TextPattern)textPattern).DocumentRange.GetText(512)?.TrimEnd('\r', '\n') ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            MarkStale("search box disappeared while reading its text");
        }
        catch (Exception ex)
        {
            _logger.Debug($"could not read the search box text (hwnd={_hwnd}): {ex.Message}");
        }
        return null;
    }

    /// <summary>Same as <see cref="TryReadText"/>, but safe to call from any thread.</summary>
    public string? TryReadTextThreadSafe()
    {
        try
        {
            return _sta.Invoke(TryReadText);
        }
        catch (Exception ex)
        {
            _logger.Debug($"could not read the search box text on the UI thread (hwnd={_hwnd}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Puts the keyboard focus back into the search box. Runs on the STA thread.
    ///
    /// Everything can take the focus when it handles a query; the user keeps typing in the Explorer
    /// search box, so the focus belongs there. A refusal by Explorer (the box may not be focusable in
    /// the current view) is not an error - the window and its results are unaffected.
    /// </summary>
    public bool FocusSearchBox()
    {
        var box = _searchBox;
        if (box is null) return false;
        try
        {
            box.SetFocus();
            return true;
        }
        catch (ElementNotAvailableException)
        {
            MarkStale("search box disappeared while giving it the focus back");
        }
        catch (Exception ex)
        {
            _logger.Debug($"could not focus the search box (hwnd={_hwnd}): {ex.Message}");
        }
        return false;
    }

    /// <summary>Same as <see cref="FocusSearchBox"/>, but safe to call from any thread.</summary>
    public void FocusSearchBoxThreadSafe()
    {
        try
        {
            _sta.Post(() => FocusSearchBox());
        }
        catch (Exception ex)
        {
            _logger.Debug($"could not give the search box its focus back (hwnd={_hwnd}): {ex.Message}");
        }
    }

    /// <summary>True when the element is the search box of this window or one of its descendants.</summary>
    public bool IsSearchBoxElement(AutomationElement? element)
    {
        var box = _searchBox;
        if (element is null || box is null) return false;
        return IsDescendantOf(element, box);
    }

    private static bool IsDescendantOf(AutomationElement element, AutomationElement ancestor)
    {
        try
        {
            var current = element;
            for (var depth = 0; depth < 8 && current is not null; depth++)
            {
                if (current.Equals(ancestor)) return true;
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
        }
        catch
        {
            // treat as not a descendant
        }
        return false;
    }

    /// <summary>Called by the monitor when the window title changed (Explorer committed a search).</summary>
    public void NotifyExplorerCommit(string detail) => _session.OnExplorerCommitDetected(detail);

    private void OnValueChanged(object sender, AutomationPropertyChangedEventArgs e)
    {
        try
        {
            ReportTextChange(e.NewValue as string);
        }
        catch (ElementNotAvailableException)
        {
            MarkStale("search box disappeared during a value change");
        }
        catch (Exception ex)
        {
            _logger.Debug($"value change handling failed (hwnd={_hwnd}): {ex.Message}");
        }
    }

    private void OnTextChanged(object sender, AutomationEventArgs e)
    {
        try
        {
            ReportTextChange(TryReadText());
        }
        catch (ElementNotAvailableException)
        {
            MarkStale("search box disappeared during a text change");
        }
        catch (Exception ex)
        {
            _logger.Debug($"text change handling failed (hwnd={_hwnd}): {ex.Message}");
        }
    }

    /// <summary>
    /// ValuePattern and TextPattern both fire per keystroke, and the text pattern can report a stale or
    /// partial document, so the value pattern is read again and wins. Only a real change is reported.
    /// </summary>
    private void ReportTextChange(string? reported)
    {
        var value = TryReadValueText() ?? reported ?? string.Empty;
        if (string.Equals(value, _lastReportedText, StringComparison.Ordinal)) return;
        _lastReportedText = value;
        _session.OnTextChanged(value);
    }

    /// <summary>Reads the search box through the value pattern only; null when that is not possible.</summary>
    private string? TryReadValueText()
    {
        var box = _searchBox;
        if (box is null) return null;
        try
        {
            if (box.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                return ((ValuePattern)pattern).Current.Value ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            MarkStale("search box disappeared while reading its value");
        }
        catch (Exception ex)
        {
            _logger.Debug($"could not read the search box value (hwnd={_hwnd}): {ex.Message}");
        }
        return null;
    }

    private string? _lastReportedText;

    private void OnWindowNameChanged(object sender, AutomationPropertyChangedEventArgs e)
    {
        try
        {
            var title = e.NewValue as string ?? string.Empty;
            NotifyExplorerCommit(title);
        }
        catch (ElementNotAvailableException)
        {
            MarkStale("Explorer window disappeared");
        }
    }

    /// <summary>Marks the subscription as stale so the monitor re-attaches it.</summary>
    public void MarkStale(string reason)
    {
        if (!_attached) return;
        _attached = false;
        _logger.Debug($"search box subscription stale (hwnd={_hwnd}): {reason}");
    }

    public void DetachHandlers()
    {
        if (_searchBox is not null)
        {
            try { if (_valueHandler is not null) Automation.RemoveAutomationPropertyChangedEventHandler(_searchBox, _valueHandler); } catch { }
            try { if (_textHandler is not null) Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, _searchBox, _textHandler); } catch { }
        }
        if (_window is not null)
        {
            try { if (_nameHandler is not null) Automation.RemoveAutomationPropertyChangedEventHandler(_window, _nameHandler); } catch { }
        }
        _valueHandler = null;
        _textHandler = null;
        _nameHandler = null;
        _attached = false;
    }

    private static string SafeName(AutomationElement? element)
    {
        try { return element?.Current.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Locates the search box. Primary key is the well known AutomationId; the fallback accepts the
    /// Edit of any <c>AutoSuggestBox</c> that is not the address bar. A generic "first Edit in the
    /// window" fallback is deliberately NOT used: the details view contains Edit controls for the
    /// column values (observed name "名称"/"Name"), which are not the search box.
    /// </summary>
    internal static AutomationElement? FindSearchBox(AutomationElement window)
    {
        var byId = window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, SearchBoxAutomationId));
        if (byId is not null)
        {
            var edit = byId.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edit is not null) return edit;
        }

        var hosts = window.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, "AutoSuggestBox"));
        for (var i = 0; i < hosts.Count; i++)
        {
            AutomationElement host;
            try { host = hosts[i]; }
            catch { continue; }

            try
            {
                if (host.Current.AutomationId == AddressBarAutomationId) continue;
            }
            catch
            {
                continue;
            }

            var edit = host.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edit is not null) return edit;
        }

        return null;
    }

    /// <summary>Sanity check: the element must be the edit of an AutoSuggestBox host.</summary>
    private static bool IsSearchBoxElementShape(AutomationElement element)
    {
        try
        {
            var current = TreeWalker.ControlViewWalker.GetParent(element);
            for (var depth = 0; depth < 3 && current is not null; depth++)
            {
                if (current.Current.ClassName == "AutoSuggestBox")
                    return current.Current.AutomationId != AddressBarAutomationId;
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
        }
        catch
        {
            // treat as unknown shape
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_sta is not null)
        {
            try { _sta.Invoke(DetachHandlers); }
            catch { }
        }
        _disposed = true;
        _searchBox = null;
        _window = null;
    }
}
