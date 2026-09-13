using System.Reflection;
using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Explorer;
using ExplorerEverythingSearch.Core.Interop;
using ExplorerEverythingSearch.Core.Shell;
using ExplorerEverythingSearch.Core.Threading;
using ExplorerEverythingSearch.Tests.TestSupport;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>
/// Guards the lifetime of the callbacks that are handed to Win32.
///
/// Regression: <see cref="ExplorerWindowMonitor"/> used to pass a *local*
/// <c>NativeMethods.WinEventDelegate</c> to <c>SetWinEventHook</c>. Only the hook handle was stored,
/// so the garbage collector was free to collect the delegate; the next Explorer window event then
/// called into freed memory and the CLR terminated the process outright (this is not a catchable
/// exception). An end-to-end run hit it. Every native callback therefore has to be rooted in a field
/// for as long as the hook exists.
/// </summary>
public sealed class ExplorerWindowMonitorTests
{
    private static object? ReadPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(instance);
    }

    [Fact]
    public void Native_callbacks_are_rooted_in_fields_so_the_gc_cannot_collect_them()
    {
        using var workspace = new TempWorkspace("monitor-delegates");
        using var logger = AppLogger.CreateDisabled(workspace.Root);
        using var sta = new StaDispatcher("EES-Test-STA");
        var config = new AppConfig { LoggingEnabled = false, DetectEnterByKeyboardHook = true, DetectEnterByFocusChange = true };
        var submitter = new RecordingSubmitter();
        var scopes = new SearchScopeResolver(
            new FakeLocationResolver(), new KnownFolderResolver(), new WindowScopeTracker(), logger);

        var monitor = new ExplorerWindowMonitor(() => config, logger, sta, submitter, null, scopes);

        var winEvent = ReadPrivateField(monitor, "_winEventHandler");
        var keyDown = ReadPrivateField(monitor, "_keyDownHandler");
        var focus = ReadPrivateField(monitor, "_focusHandler");

        Assert.NotNull(winEvent);
        Assert.NotNull(keyDown);
        Assert.NotNull(focus);

        // The hook callbacks must be the monitor's own methods, not something unrelated that merely
        // happens to be non-null.
        Assert.Equal("OnWinEvent", ((Delegate)winEvent!).Method.Name);
        Assert.Equal("OnGlobalKeyDown", ((Delegate)keyDown!).Method.Name);
        Assert.Equal("OnFocusChanged", ((Delegate)focus!).Method.Name);

        // The delegate types are the ones the native signatures expect.
        Assert.IsType<NativeMethods.WinEventDelegate>(winEvent);
        Assert.IsType<NativeMethods.LowLevelKeyboardProc>(keyDown);
    }

    [Fact]
    public void The_monitor_can_be_created_and_dropped_without_hooks_installed()
    {
        using var workspace = new TempWorkspace("monitor-lifecycle");
        using var logger = AppLogger.CreateDisabled(workspace.Root);
        using var sta = new StaDispatcher("EES-Test-STA-2");
        var monitor = new ExplorerWindowMonitor(
            () => new AppConfig { LoggingEnabled = false }, logger, sta, new RecordingSubmitter());

        Assert.False(monitor.IsRunning);
        Assert.Equal(0, monitor.TrackedWindowCount);
        Assert.Equal(0, monitor.AttachedSearchBoxCount);

        // Stopping a monitor that was never started must be a no-op, and disposing twice must not throw.
        monitor.Stop();
        monitor.Dispose();
        monitor.Dispose();
    }
}
