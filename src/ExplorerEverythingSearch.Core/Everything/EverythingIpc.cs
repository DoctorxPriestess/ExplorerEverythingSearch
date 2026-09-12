using System.Text;
using ExplorerEverythingSearch.Core.Interop;

namespace ExplorerEverythingSearch.Core.Everything;

/// <summary>
/// Everything's official message based IPC (documented in the Everything SDK's
/// <c>everything_ipc.h</c>). Only the parts that are actually supported by Everything 1.5.0.1423b
/// are used here - it was verified on the test machine that the documented
/// <c>EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8</c> message is accepted but has no effect, therefore
/// searches are issued through the officially supported command line instead.
/// </summary>
public static class EverythingIpc
{
    /// <summary>Window class of Everything's taskbar notification window (always exists while Everything runs).</summary>
    public const string NotificationWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";

    /// <summary>Window class of an Everything search window.</summary>
    public const string SearchWindowClass = "EVERYTHING";

    private const uint EverythingWmIpc = 0x0400; // WM_USER

    private const int IpcGetMajorVersion = 0;
    private const int IpcGetMinorVersion = 1;
    private const int IpcGetRevision = 2;
    private const int IpcGetBuildNumber = 3;
    private const int IpcIsNtfsDriveIndexed = 400;
    private const int IpcIsDbLoaded = 401;

    public static IntPtr FindNotificationWindow() => NativeMethods.FindWindow(NotificationWindowClass, null);

    public static bool IsRunning => FindNotificationWindow() != IntPtr.Zero;

    /// <summary>Queries the version of the running Everything through IPC; null when it is not running.</summary>
    public static Version? TryGetVersion()
    {
        var hwnd = FindNotificationWindow();
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            var major = (int)NativeMethods.SendMessage(hwnd, EverythingWmIpc, IpcGetMajorVersion, IntPtr.Zero);
            var minor = (int)NativeMethods.SendMessage(hwnd, EverythingWmIpc, IpcGetMinorVersion, IntPtr.Zero);
            var revision = (int)NativeMethods.SendMessage(hwnd, EverythingWmIpc, IpcGetRevision, IntPtr.Zero);
            var build = (int)NativeMethods.SendMessage(hwnd, EverythingWmIpc, IpcGetBuildNumber, IntPtr.Zero);
            if (major <= 0) return null;
            return new Version(major, Math.Max(0, minor), Math.Max(0, revision), Math.Max(0, build));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when Everything has finished loading its database.</summary>
    public static bool IsDatabaseLoaded()
    {
        var hwnd = FindNotificationWindow();
        if (hwnd == IntPtr.Zero) return false;
        try { return NativeMethods.SendMessage(hwnd, EverythingWmIpc, IpcIsDbLoaded, IntPtr.Zero) != IntPtr.Zero; }
        catch { return false; }
    }

    /// <summary>True when Everything indexes the given NTFS drive (used to explain empty results).</summary>
    public static bool? IsDriveIndexed(char driveLetter)
    {
        var hwnd = FindNotificationWindow();
        if (hwnd == IntPtr.Zero) return null;
        var index = char.ToUpperInvariant(driveLetter) - 'A';
        if (index < 0 || index > 25) return null;
        try { return NativeMethods.SendMessage(hwnd, EverythingWmIpc, IpcIsNtfsDriveIndexed, index) != IntPtr.Zero; }
        catch { return null; }
    }

    public static IReadOnlyList<IntPtr> FindSearchWindows()
        => NativeMethods.FindTopLevelWindows(SearchWindowClass);

    /// <summary>Everything puts the effective search text into the window title ("&lt;query&gt; - Everything").</summary>
    public static string GetSearchWindowTitle(IntPtr hwnd) => NativeMethods.GetWindowTextString(hwnd);

    /// <summary>Result list handle of an Everything search window (used by tests and diagnostics).</summary>
    public static IntPtr FindResultListView(IntPtr searchWindow)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumChildWindows(searchWindow, (h, _) =>
        {
            if (NativeMethods.GetClassNameString(h) == "SysListView32")
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Number of results currently shown by an Everything search window.</summary>
    public static int GetResultCount(IntPtr searchWindow)
    {
        var listView = FindResultListView(searchWindow);
        if (listView == IntPtr.Zero) return -1;
        const uint LvmGetItemCount = 0x1000 + 4;
        try { return (int)NativeMethods.SendMessage(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero); }
        catch { return -1; }
    }

    /// <summary>Directory of the running Everything executable, when it can be determined.</summary>
    public static string? TryGetRunningExecutablePath()
    {
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("Everything"))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                    }
                    catch
                    {
                        // access denied for another user's instance - ignore
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    public static string Describe(Version? version) => version is null ? "unavailable" : version.ToString();
}
