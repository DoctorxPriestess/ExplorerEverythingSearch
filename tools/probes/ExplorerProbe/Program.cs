using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ExplorerProbe;

/// <summary>
/// Developer probe. Two experiments:
///   resolve  - dump every Explorer window location through the Shell COM chain.
///   events   - drive a real Explorer search box and record which UI Automation / window
///              signals actually fire (typing vs. idle auto-commit vs. Enter).
/// This is a dev/diagnostic tool; it is not part of the shipped application.
/// </summary>
internal static class Program
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static void Log(string channel, string message)
        => Console.WriteLine($"{Clock.ElapsedMilliseconds,7}ms [{channel}] {message}");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 2; }
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "resolve" => Resolve(),
                "events" => Events(args),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL " + ex);
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.WriteLine("ERROR: " + message);
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ExplorerProbe - developer/diagnostic probe

              resolve                       list Explorer windows and their resolved Shell locations
              events --title <substr> [--text <t>] [--mode idle|enter] [--idleMs <n>]
                                            drive a real Explorer search box and log every observable signal
            """);
    }

    // ---------------------------------------------------------------- resolve

    private static int Resolve()
    {
        Console.WriteLine("--- Shell.Application windows (IDispatch) matched against top-level CabinetWClass HWNDs ---");
        foreach (var hwnd in Win32.FindTopLevelWindows("CabinetWClass"))
        {
            var location = ShellLocator.GetWindow(hwnd);
            Console.WriteLine($"hwnd={hwnd} title=[{Win32.GetWindowText(hwnd)}]");
            if (location is null) { Console.WriteLine("    <no Shell window for this HWND>"); continue; }
            Console.WriteLine($"    locationName=[{location.LocationName}] url=[{location.LocationUrl}]");
            Console.WriteLine($"    selfPath    =[{location.SelfPath}]");
            Console.WriteLine($"    parentPath  =[{location.ParentPath}]");
            if (location.Error is not null) Console.WriteLine($"    error       ={location.Error}");
        }

        Console.WriteLine();
        Console.WriteLine("--- all Shell.Application windows ---");
        foreach (var location in ShellLocator.GetWindows())
        {
            Console.WriteLine($"    hwnd={location.Hwnd} name=[{location.LocationName}] url=[{location.LocationUrl}] self=[{location.SelfPath}] err=[{location.Error}]");
        }
        return 0;
    }

    // ---------------------------------------------------------------- events

    private static int Events(string[] args)
    {
        string title = ArgValue(args, "--title") ?? throw new ArgumentException("--title is required");
        string text = ArgValue(args, "--text") ?? "probe";
        string mode = (ArgValue(args, "--mode") ?? "idle").ToLowerInvariant();
        int idleMs = int.TryParse(ArgValue(args, "--idleMs"), out int v) ? v : 4000;

        int hwnd = -1;
        foreach (var h in Win32.FindTopLevelWindows("CabinetWClass"))
        {
            if (Win32.GetWindowText(h).Contains(title, StringComparison.OrdinalIgnoreCase)) { hwnd = h; break; }
        }
        if (hwnd < 0) return Fail($"no CabinetWClass window whose title contains '{title}'");

        Log("probe", $"target hwnd={hwnd} title=[{Win32.GetWindowText(hwnd)}] mode={mode}");

        var window = AutomationElement.FromHandle((IntPtr)hwnd);
        var searchBox = FindSearchBox(window);
        if (searchBox is null) return Fail("FileExplorerSearchBox/Edit not found in target window");
        Log("probe", $"searchbox name=[{searchBox.Current.Name}] aid=[{searchBox.Current.AutomationId}] value=[{GetValue(searchBox)}]");

        int valueEvents = 0, textEvents = 0, focusEvents = 0, structureEvents = 0;
        string lastValue = GetValue(searchBox);
        string lastTitle = Win32.GetWindowText(hwnd);

        var valueHandler = new AutomationPropertyChangedEventHandler((sender, e) =>
        {
            valueEvents++;
            Log("UIA-VALUE", $"new=[{e.NewValue}] old=[{e.OldValue}] now=[{GetValue(Safe(sender))}]");
        });
        Automation.AddAutomationPropertyChangedEventHandler(
            searchBox, TreeScope.Element, valueHandler, ValuePattern.ValueProperty);

        var textHandler = new AutomationEventHandler((sender, e) =>
        {
            textEvents++;
            Log("UIA-TEXT", $"changed value=[{GetValue(Safe(sender))}]");
        });
        try
        {
            Automation.AddAutomationEventHandler(
                TextPattern.TextChangedEvent, searchBox, TreeScope.Element, textHandler);
            Log("probe", "TextPattern.TextChanged handler registered");
        }
        catch (Exception ex) { Log("probe", "TextPattern registration failed: " + ex.Message); }

        var focusHandler = new AutomationFocusChangedEventHandler((sender, e) =>
        {
            focusEvents++;
            var s = Safe(sender);
            Log("UIA-FOCUS", $"aid=[{s?.Current.AutomationId}] name=[{s?.Current.Name}] cls=[{s?.Current.ClassName}]");
        });
        Automation.AddAutomationFocusChangedEventHandler(focusHandler);

        var structureHandler = new StructureChangedEventHandler((sender, e) =>
        {
            structureEvents++;
            var s = Safe(sender);
            Log("UIA-STRUCT", $"type={e.StructureChangeType} sender=[{s?.Current.AutomationId}]/[{(s?.Current.Name ?? "").Substring(0, Math.Min(60, (s?.Current.Name ?? "").Length))}]");
        });
        try
        {
            Automation.AddStructureChangedEventHandler(window, TreeScope.Subtree, structureHandler);
            Log("probe", "StructureChanged handler registered");
        }
        catch (Exception ex) { Log("probe", "StructureChanged registration failed: " + ex.Message); }

        Log("probe", "handlers registered - entering pump loop");

        // ---- timeline ----
        int step = 0;
        long nextAction = 500;
        long idleDeadline = -1;
        long endAt = long.MaxValue;
        bool typedSecond = false, pressedEnter = false;

        while (Clock.ElapsedMilliseconds < endAt)
        {
            Application.DoEvents();

            // reference polling (independent of UIA events) to know ground truth
            string value = GetValue(searchBox);
            if (value != lastValue)
            {
                lastValue = value;
                Log("POLL-VALUE", $"[{value}]");
            }
            string winTitle = Win32.GetWindowText(hwnd);
            if (winTitle != lastTitle)
            {
                lastTitle = winTitle;
                Log("POLL-TITLE", $"[{winTitle}]");
            }

            long now = Clock.ElapsedMilliseconds;
            if (step == 0 && now >= nextAction)
            {
                step = 1;
                Win32.ForceForeground((IntPtr)hwnd);
                try { searchBox.SetFocus(); } catch (Exception ex) { Log("probe", "SetFocus failed: " + ex.Message); }
                try
                {
                    var vp = (ValuePattern)searchBox.GetCurrentPattern(ValuePattern.Pattern);
                    vp.SetValue("");
                }
                catch (Exception ex) { Log("probe", "SetValue(\"\") failed: " + ex.Message); }
                Log("probe", "focused search box and cleared it");
                nextAction = now + 800;
            }
            else if (step == 1 && now >= nextAction)
            {
                step = 2;
                Log("probe", $"TYPING '{text}'");
                SendKeys.SendWait(text);
                Log("probe", "typed; start idle observation");
                idleDeadline = now + idleMs;
                nextAction = idleDeadline;
            }
            else if (step == 2 && now >= nextAction)
            {
                step = 3;
                Log("probe", $"IDLE OBSERVATION DONE (value=[{GetValue(searchBox)}] title=[{Win32.GetWindowText(hwnd)}])");
                var focused = AutomationElement.FocusedElement;
                Log("probe", $"focused element now aid=[{focused?.Current.AutomationId}] cls=[{focused?.Current.ClassName}]");
                if (mode == "enter")
                {
                    Log("probe", $"TYPING '{text}d'");
                    SendKeys.SendWait("d");
                    typedSecond = true;
                    nextAction = now + 400;
                    endAt = now + 8000;
                    step = 4;
                }
                else
                {
                    endAt = now;
                }
            }
            else if (step == 4 && now >= nextAction)
            {
                step = 5;
                Log("probe", "PRESSING ENTER");
                SendKeys.SendWait("{ENTER}");
                pressedEnter = true;
                nextAction = now + 3500;
                endAt = now + 3600;
            }
            else if (step == 5 && now >= nextAction)
            {
                var focused = AutomationElement.FocusedElement;
                Log("probe", $"post-enter focused aid=[{focused?.Current.AutomationId}] name=[{focused?.Current.Name}] cls=[{focused?.Current.ClassName}]");
                step = 6;
                endAt = now;
            }

            Thread.Sleep(10);
        }

        Log("probe", $"DONE value=[{GetValue(searchBox)}] title=[{Win32.GetWindowText(hwnd)}] typedSecond={typedSecond} pressedEnter={pressedEnter}");
        Log("probe", $"EVENT COUNTS: value={valueEvents} text={textEvents} focus={focusEvents} structure={structureEvents}");

        Automation.RemoveAllEventHandlers();
        return 0;
    }

    private static AutomationElement? Safe(object? sender)
    {
        if (sender is not AutomationElement element) return null;
        try { return element.Current.Name is null ? null : element; }
        catch { return null; }
    }

    private static AutomationElement? FindSearchBox(AutomationElement window)
    {
        var hostCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, "FileExplorerSearchBox");
        var host = window.FindFirst(TreeScope.Descendants, hostCondition);
        if (host is null) return null;
        var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        return host.FindFirst(TreeScope.Descendants, editCondition);
    }

    private static string GetValue(AutomationElement? element)
    {
        if (element is null) return "<null>";
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? p))
                return ((ValuePattern)p!).Current.Value ?? "";
            return "<no-value-pattern>";
        }
        catch (ElementNotAvailableException) { return "<stale>"; }
        catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}

internal static class Win32
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public static string GetWindowText(int hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText((IntPtr)hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static List<int> FindTopLevelWindows(string className)
    {
        var result = new List<int>();
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == className) result.Add((int)h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static void ForceForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /*SW_RESTORE*/);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint currentThread = GetCurrentThreadId();
        AttachThreadInput(currentThread, targetThread, true);
        ShowWindow(hwnd, 5 /*SW_SHOW*/);
        SetForegroundWindow(hwnd);
        AttachThreadInput(currentThread, targetThread, false);
    }
}
