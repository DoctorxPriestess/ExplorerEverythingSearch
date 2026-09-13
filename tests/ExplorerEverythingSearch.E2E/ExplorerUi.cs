using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ExplorerEverythingSearch.E2E;

/// <summary>
/// Drives real Explorer windows: opens them, finds the search box through UI Automation and types
/// into it exactly like a user does (SendKeys goes to the foreground window, so the target window is
/// always brought to the front first).
/// </summary>
internal static class ExplorerUi
{
    public const string WindowClass = "CabinetWClass";

    public static HashSet<IntPtr> OpenWindows() => Native.TopLevelWindows(WindowClass).ToHashSet();

    /// <summary>
    /// Opens <paramref name="location"/> (a path or a shell: target) in Explorer and returns its window.
    /// <paramref name="created"/> is false when Explorer reused a window that was already open - such a
    /// window belongs to whoever opened it and must not be closed by the harness. The title hints are
    /// only used for that reuse case, hence several spellings (the shell's display language).
    /// </summary>
    public static IntPtr OpenLocation(string location, string[] titleHints, TimeSpan timeout, out bool created)
    {
        var before = OpenWindows();
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = location };
        using (var process = Process.Start(startInfo))
        {
            process?.WaitForExit(5000);
        }

        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            foreach (var hwnd in OpenWindows())
            {
                if (before.Contains(hwnd)) continue;
                if (!Native.IsWindowVisible(hwnd)) continue;
                created = true;
                return hwnd;
            }

            Thread.Sleep(100);
        }

        // Explorer may have reused an existing window (for example a "This PC" window that was already
        // open). Use it, but remember that it is not ours.
        foreach (var hint in titleHints)
        {
            var existing = FindByTitle(hint);
            if (existing is not null)
            {
                created = false;
                return existing.Value;
            }
        }

        throw new TimeoutException($"Explorer did not open a window for '{location}' within {timeout.TotalSeconds:F0}s");
    }

    public static IntPtr? FindByTitle(string titleSubstring)
    {
        foreach (var hwnd in OpenWindows())
        {
            if (Native.TitleOf(hwnd).Contains(titleSubstring, StringComparison.OrdinalIgnoreCase)) return hwnd;
        }
        return null;
    }

    /// <summary>Waits until the search box of the window can be reached through UI Automation.</summary>
    public static AutomationElement WaitForSearchBox(IntPtr hwnd, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (true)
        {
            var box = FindSearchBox(hwnd);
            if (box is not null) return box;
            if (DateTimeOffset.Now >= deadline)
                throw new TimeoutException($"the search box of hwnd={hwnd} did not appear within {timeout.TotalSeconds:F0}s");
            Thread.Sleep(150);
        }
    }

    public static AutomationElement? FindSearchBox(IntPtr hwnd)
    {
        try
        {
            var window = AutomationElement.FromHandle(hwnd);
            if (window is null) return null;
            var host = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "FileExplorerSearchBox"));
            if (host is null) return null;
            return host.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    public static void FocusSearchBox(IntPtr hwnd, AutomationElement box)
    {
        Native.ForceForeground(hwnd);
        box.SetFocus();
        Thread.Sleep(120);
    }

    public static void ClearSearchBox(AutomationElement box)
    {
        if (!box.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("the search box does not support ValuePattern");
        ((ValuePattern)pattern).SetValue(string.Empty);
        Thread.Sleep(120);
    }

    /// <summary>Types text through the keyboard, like a user would.</summary>
    public static void TypeText(string text) => SendKeys.SendWait(text);

    public static void PressEnter() => SendKeys.SendWait("{ENTER}");

    public static string ReadSearchBox(AutomationElement box)
    {
        try
        {
            if (box.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                return ((ValuePattern)pattern).Current.Value ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
        }
        return string.Empty;
    }

    /// <summary>True when the keyboard focus currently sits inside the search box of this window.</summary>
    public static bool IsSearchBoxFocused(IntPtr hwnd)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            var box = FindSearchBox(hwnd);
            if (focused is null || box is null) return false;
            var current = focused;
            for (var depth = 0; depth < 6 && current is not null; depth++)
            {
                if (current.Equals(box)) return true;
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        return false;
    }

    /// <summary>Closes a window this harness created and waits until it is gone.</summary>
    public static void Close(IntPtr hwnd, TimeSpan timeout)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.Close(hwnd);
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (!Native.IsWindow(hwnd)) return;
            Thread.Sleep(100);
        }
    }
}
