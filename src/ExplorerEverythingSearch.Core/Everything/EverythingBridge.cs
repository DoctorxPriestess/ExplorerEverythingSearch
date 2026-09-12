using System.Diagnostics;
using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Interop;
using ExplorerEverythingSearch.Core.Search;

namespace ExplorerEverythingSearch.Core.Everything;

public sealed record EverythingConnectionState(bool Available, Version? Version, string? ExecutablePath, string? Error)
{
    public string Display => Available
        ? $"Connected ({Version?.ToString() ?? "unknown version"})"
        : "Unavailable";
}

/// <param name="Success">The Everything window was located and activated.</param>
/// <param name="WindowCreated">A new Everything search window had to be created.</param>
/// <param name="WindowReused">An existing Everything search window was updated.</param>
/// <param name="LatencyMs">Wall clock time from issuing the command line until the window showed the query.</param>
/// <param name="TitleReflectsQuery">Everything's window title already contained the query (verification signal).</param>
public sealed record EverythingExecutionResult(
    bool Success,
    bool WindowCreated,
    bool WindowReused,
    long LatencyMs,
    long WindowHandle,
    bool TitleReflectsQuery,
    string? Error)
{
    public static EverythingExecutionResult Failure(string error, long latencyMs, bool reused = false)
        => new(false, false, reused, latencyMs, 0, false, error);
}

/// <summary>
/// Drives the Everything search window through Everything's official command line
/// (<c>-s / -s* / -path / -no-new-window</c>) - the integration path that was verified on the
/// development machine to scope results exactly and to reuse an existing window.
///
/// (Everything's IPC command line message <c>EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8</c> was also
/// tested: Everything 1.5.0.1423b accepts the message but does not act on it, so the official
/// command line is used instead. The IPC version query is still used to detect the running version.)
/// </summary>
public sealed class EverythingBridge
{
    private const int LauncherExitWaitMs = 2_000;

    private readonly EverythingLocator _locator;
    private readonly AppLogger _logger;
    private readonly Func<AppConfig> _config;
    private readonly Action<long>? _restoreSearchBoxFocus;
    private readonly object _executionLock = new();

    public EverythingBridge(
        EverythingLocator locator,
        AppLogger logger,
        Func<AppConfig> config,
        Action<long>? restoreSearchBoxFocus = null)
    {
        _locator = locator;
        _logger = logger;
        _config = config;
        _restoreSearchBoxFocus = restoreSearchBoxFocus;
    }

    /// <summary>
    /// Everything can take the keyboard focus when it handles the command line. The Explorer search box
    /// is where the user keeps typing, so the focus is put back there - unless the user has produced
    /// input of their own since (then they are doing something else and must not be interrupted).
    /// </summary>
    private void RestoreSearchBoxFocus(long explorerHwnd, IntPtr everythingWindow, uint userInputBefore)
    {
        if (explorerHwnd == 0 || _restoreSearchBoxFocus is null) return;

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != everythingWindow)
        {
            _logger.Debug("Everything did not take the keyboard focus; the Explorer search box keeps it");
            return;
        }

        if (NativeMethods.LastInputTick() != userInputBefore)
        {
            _logger.Debug("the user produced input after the search; leaving the keyboard focus alone");
            return;
        }

        _logger.Debug($"returning the keyboard focus to the Explorer search box (hwnd={explorerHwnd})");
        _restoreSearchBoxFocus(explorerHwnd);
    }

    public EverythingLocator Locator => _locator;

    /// <summary>Current connection state; cheap enough to call for every submit and for tray status.</summary>
    public EverythingConnectionState Probe()
    {
        var exe = _locator.EverythingExe;
        var version = EverythingIpc.TryGetVersion();
        if (exe is null && version is null)
            return new EverythingConnectionState(false, null, null, "Everything not found");
        return new EverythingConnectionState(true, version, exe, version is null ? "Everything is not running" : null);
    }

    /// <summary>Capabilities used for query building (unknown version falls back to the safe 1.4 syntax).</summary>
    public EverythingCapabilities GetCapabilities() => new(EverythingIpc.TryGetVersion());

    /// <summary>Used by the tray's "reconnect Everything" action.</summary>
    public EverythingConnectionState Reconnect()
    {
        _locator.Invalidate();
        var state = Probe();
        _logger.Info($"Everything connection: {state.Display}"
                     + (state.ExecutablePath is null ? string.Empty : $" path={state.ExecutablePath}"));
        return state;
    }

    /// <summary>
    /// Runs one search: starts or updates the Everything window through the official command line,
    /// waits until the window shows the query, and puts that window in front of Explorer without
    /// taking the keyboard focus away from the Explorer search box.
    /// </summary>
    /// <param name="invocation">Command line and query to run.</param>
    /// <param name="explorerHwnd">Explorer window the search came from; used to give the search box its focus back.</param>
    /// <param name="windowWaitMs">How long to wait for the Everything window to show the query.</param>
    public EverythingExecutionResult Execute(EverythingInvocation invocation, long explorerHwnd = 0, int windowWaitMs = 15_000)
    {
        // Serialize window updates so two Explorer windows cannot interleave their searches.
        lock (_executionLock)
        {
            var stopwatch = Stopwatch.StartNew();
            var exe = _locator.EverythingExe;
            if (exe is null)
            {
                _logger.Error("Everything not found: no Everything.exe configured or detected");
                return EverythingExecutionResult.Failure("Everything not found", stopwatch.ElapsedMilliseconds);
            }

            var windowsBefore = EverythingIpc.FindSearchWindows();
            var reusedExpected = windowsBefore.Count > 0 && _config().ReuseEverythingWindow;

            try
            {
                var startInfo = new ProcessStartInfo(exe)
                {
                    Arguments = invocation.RawCommandLine,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                };

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    _logger.Error($"could not start {exe}");
                    return EverythingExecutionResult.Failure("could not start Everything", stopwatch.ElapsedMilliseconds, reusedExpected);
                }

                // While Everything is already running the launcher forwards the command line through IPC
                // and exits after ~100 ms. When Everything is not running this process *is* the new
                // instance, so it must neither be waited for (it would block until the user closes it)
                // nor killed.
                if (!process.WaitForExit(LauncherExitWaitMs))
                    _logger.Debug("Everything launcher still running: Everything was started by this call");
            }
            catch (Exception ex)
            {
                _logger.Error($"failed to run Everything: {ex.Message}", ex);
                return EverythingExecutionResult.Failure(ex.Message, stopwatch.ElapsedMilliseconds, reusedExpected);
            }

            var (window, matched) = WaitForSearchWindow(windowsBefore, invocation.Query, windowWaitMs, stopwatch);
            if (window == IntPtr.Zero)
            {
                var message = $"Everything search window did not appear within {windowWaitMs} ms";
                _logger.Error(message);
                return EverythingExecutionResult.Failure(message, stopwatch.ElapsedMilliseconds, reusedExpected);
            }

            var userInputBefore = NativeMethods.LastInputTick();
            NativeMethods.BringToFrontWithoutActivating(window);
            RestoreSearchBoxFocus(explorerHwnd, window, userInputBefore);
            stopwatch.Stop();

            var created = windowsBefore.Count == 0;
            if (!matched)
                _logger.Warn("Everything window title does not reflect the query yet; using the located window");

            return new EverythingExecutionResult(true, created, !created, stopwatch.ElapsedMilliseconds, (long)window, matched, null);
        }
    }

    /// <summary>
    /// Waits until an Everything search window shows the expected query. Prefers a window whose title
    /// already contains the query prefix; falls back to a newly created (or the first existing) window.
    /// </summary>
    private (IntPtr Window, bool Matched) WaitForSearchWindow(
        IReadOnlyList<IntPtr> windowsBefore,
        string query,
        int timeoutMs,
        Stopwatch stopwatch)
    {
        var probe = BuildTitleProbe(query);
        var known = new HashSet<IntPtr>(windowsBefore);
        var deadline = stopwatch.ElapsedMilliseconds + timeoutMs;
        var delay = 20;
        var fallback = IntPtr.Zero;

        while (true)
        {
            var windows = EverythingIpc.FindSearchWindows();
            foreach (var candidate in windows)
            {
                if (probe is not null &&
                    EverythingIpc.GetSearchWindowTitle(candidate).Contains(probe, StringComparison.OrdinalIgnoreCase))
                {
                    return (candidate, true);
                }
            }

            if (fallback == IntPtr.Zero)
            {
                foreach (var candidate in windows)
                {
                    if (!known.Contains(candidate)) { fallback = candidate; break; }
                }
                if (fallback == IntPtr.Zero && windows.Count > 0) fallback = windows[0];
            }

            if (stopwatch.ElapsedMilliseconds >= deadline) return (fallback, false);
            Thread.Sleep(delay);
            delay = Math.Min(delay * 2, 200);
        }
    }

    /// <summary>The title holds the query, but long queries are elided, so only a prefix is compared.</summary>
    private static string? BuildTitleProbe(string query)
    {
        var value = query.Trim();
        if (value.Length == 0) return null;
        return value.Length <= 24 ? value : value[..24];
    }
}
