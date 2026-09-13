using System.Diagnostics;
using System.Windows.Automation;
using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Everything;

namespace ExplorerEverythingSearch.E2E;

/// <summary>
/// The scenarios of the task specification: every one drives a real Explorer search box and checks the
/// outcome in the application log and in the Everything window.
/// </summary>
internal static class Scenarios
{
    private const string ThisPcLocation = "shell:MyComputerFolder";
    private const string HomeLocation = "shell:::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    private const string RecycleBinLocation = "shell:RecycleBinFolder";

    public static readonly IReadOnlyList<Scenario> All = new[]
    {
        new Scenario("basic-idle", AppMode.Default, BasicIdle),
        new Scenario("enter-immediate", AppMode.Default, EnterImmediate),
        new Scenario("continuous", AppMode.Default, Continuous),
        new Scenario("this-pc", AppMode.Default, ThisPc),
        new Scenario("home", AppMode.Default, Home),
        new Scenario("multi-window", AppMode.Default, MultiWindow),
        new Scenario("explorer-restart", AppMode.Default, ExplorerRestart),
        new Scenario("everything-closed", AppMode.Default, EverythingClosed),
        new Scenario("window-reuse", AppMode.Default, WindowReuse),
        new Scenario("unsupported-namespace", AppMode.Default, UnsupportedNamespace),
        new Scenario("unicode-path", AppMode.Default, UnicodePath),
        new Scenario("logging-disabled", AppMode.NoLogging, LoggingDisabled),
        new Scenario("log-clear", AppMode.Default, LogClear),
    };

    // ------------------------------------------------------------------ scenarios

    /// <summary>Typing and letting the idle timeout pass submits the query scoped to the folder.</summary>
    private static void BasicIdle(Harness harness, ScenarioContext context)
    {
        var directory = harness.CreateDataDirectory("basic-idle", "alpha-token-file.txt", "beta-token-file.txt");
        var tail = harness.Tail();
        OpenAndPrepare(harness, context, tail, directory, new[] { "basic-idle" });

        ExplorerUi.TypeText("e2ealphatoken");
        var submit = tail.WaitForSubmit(TimeSpan.FromSeconds(10), context.LastHwnd);
        context.Note(submit.ToString());

        Check.Equal("IdleTimeout", submit.Trigger, "trigger");
        Check.Equal("e2ealphatoken", submit.Text, "search text");
        Check.Equal(directory.TrimEnd('\\'), submit.ResolvedPath?.TrimEnd('\\'), "resolved path");
        Check.Equal("CurrentDirectoryAndSubdirectories", submit.Scope, "search scope");
        Check.Equal("e2ealphatoken", submit.Query, "query");
        Check.That(submit.WindowAction is "created" or "reused", $"unexpected Everything window action '{submit.WindowAction}'");
        Check.That(submit.LatencyMs is > 0 and < 5000, $"unexpected end to end latency {submit.LatencyMs} ms");

        var title = harness.WaitForEverythingTitle("e2ealphatoken", TimeSpan.FromSeconds(10));
        context.Note($"Everything title: {title}");
        Check.Contains(title, "e2ealphatoken", "Everything window title");

        context.CloseOwnWindows();
    }

    /// <summary>Enter submits immediately instead of waiting for the idle timeout.</summary>
    private static void EnterImmediate(Harness harness, ScenarioContext context)
    {
        var directory = harness.CreateDataDirectory("enter-immediate", "enter-token-file.txt");
        var tail = harness.Tail();
        OpenAndPrepare(harness, context, tail, directory, new[] { "enter-immediate" });

        const string typed = "e2eenterprobe";
        ExplorerUi.TypeText(typed);
        var pressed = Stopwatch.StartNew();
        ExplorerUi.PressEnter();

        var submit = tail.WaitForSubmit(TimeSpan.FromSeconds(10), context.LastHwnd);
        var elapsed = pressed.ElapsedMilliseconds;
        context.Note($"Enter -> submit visible after {elapsed} ms; {submit}");

        Check.Equal("Enter", submit.Trigger, "trigger");
        Check.Contains(submit.Text ?? string.Empty, typed, "search text");
        Check.Equal(directory.TrimEnd('\\'), submit.ResolvedPath?.TrimEnd('\\'), "resolved path");
        Check.That(elapsed < 800,
            $"the submit appeared {elapsed} ms after Enter, which is inside the 1000 ms idle window: it was not handled as Enter");

        context.CloseOwnWindows();
    }

    /// <summary>After the idle submit the search box keeps the keyboard focus, so typing keeps working.</summary>
    private static void Continuous(Harness harness, ScenarioContext context)
    {
        var directory = harness.CreateDataDirectory("continuous", "continuous-token-file.txt");
        var tail = harness.Tail();
        var hwnd = OpenAndPrepare(harness, context, tail, directory, new[] { "continuous" });

        ExplorerUi.TypeText("e2econtone");
        var first = tail.WaitForSubmit(TimeSpan.FromSeconds(10), hwnd.ToInt64());
        context.Note($"first: {first}");
        Check.Equal("IdleTimeout", first.Trigger, "first trigger");
        Check.Equal("e2econtone", first.Text, "first search text");

        Thread.Sleep(400);
        context.Note(ExplorerUi.IsSearchBoxFocused(hwnd)
            ? "the search box still has the keyboard focus after the search"
            : "note: the focus sits elsewhere in the window; typing must still reach the search box");

        // Deliberately no SetFocus: the tool is expected to have given the focus back to the search box.
        Native.ForceForeground(hwnd);
        ExplorerUi.TypeText("xtra");
        ExplorerUi.PressEnter();

        var second = tail.WaitForSubmit(TimeSpan.FromSeconds(10), hwnd.ToInt64());
        context.Note($"second: {second}");
        Check.Equal("Enter", second.Trigger, "second trigger");
        Check.Contains(second.Text ?? string.Empty, "xtra", "second search text");
        Check.That(!string.Equals(second.Text, first.Text, StringComparison.Ordinal),
            $"the second search text did not change (\"{second.Text}\")");
        Check.Equal(directory.TrimEnd('\\'), second.ResolvedPath?.TrimEnd('\\'), "second resolved path");

        context.CloseOwnWindows();
    }

    /// <summary>A search started in "This PC" covers every volume.</summary>
    private static void ThisPc(Harness harness, ScenarioContext context)
    {
        var tail = harness.Tail();
        OpenAndPrepare(harness, context, tail, ThisPcLocation, new[] { "此电脑", "This PC" });

        ExplorerUi.TypeText("e2evolumescan");
        var submit = tail.WaitForSubmit(TimeSpan.FromSeconds(10), context.LastHwnd);
        context.Note(submit.ToString());

        Check.Equal("AllVolumes", submit.Scope, "search scope");
        Check.Equal("<all volumes>", submit.ResolvedPath, "resolved path");
        Check.NotContains(submit.Query ?? string.Empty, "-path", "query");
        Check.NotContains(submit.Query ?? string.Empty, "ancestor:", "query");
        context.Note($"query: {submit.Query}");

        context.CloseOwnWindows();
    }

    /// <summary>A search started in "Home" covers the known folders.</summary>
    private static void Home(Harness harness, ScenarioContext context)
    {
        var tail = harness.Tail();
        OpenAndPrepare(harness, context, tail, HomeLocation, new[] { "主文件夹", "Home" });

        ExplorerUi.TypeText("e2ehometoken");
        var submit = tail.WaitForSubmit(TimeSpan.FromSeconds(10), context.LastHwnd);
        context.Note(submit.ToString());

        Check.Equal("KnownFoldersUnion", submit.Scope, "search scope");
        var query = submit.Query ?? string.Empty;
        var ancestors = query.Split("ancestor:", StringSplitOptions.None).Length - 1;
        Check.That(ancestors >= 2, $"the query has {ancestors} ancestor: clauses; expected the known folder union");
        context.Note($"query: {query}");
        context.Note($"resolved path: {submit.ResolvedPath}");

        context.CloseOwnWindows();
    }

    /// <summary>Two Explorer windows never share their search scope.</summary>
    private static void MultiWindow(Harness harness, ScenarioContext context)
    {
        var firstDirectory = harness.CreateDataDirectory("multi-window-a", "multi-a.txt");
        var secondDirectory = harness.CreateDataDirectory("multi-window-b", "multi-b.txt");
        var tail = harness.Tail();

        var firstHwnd = OpenAndPrepare(harness, context, tail, firstDirectory, new[] { "multi-window-a" });
        var secondHwnd = OpenAndPrepare(harness, context, tail, secondDirectory, new[] { "multi-window-b" });
        Check.That(firstHwnd != secondHwnd, $"both windows are the same window (hwnd={firstHwnd})");

        ExplorerUi.FocusSearchBox(firstHwnd, ExplorerUi.WaitForSearchBox(firstHwnd, TimeSpan.FromSeconds(5)));
        tail.Mark();
        ExplorerUi.TypeText("e2ealpha");
        var first = tail.WaitForSubmit(TimeSpan.FromSeconds(10), firstHwnd.ToInt64());
        context.Note($"window A: {first}");

        ExplorerUi.FocusSearchBox(secondHwnd, ExplorerUi.WaitForSearchBox(secondHwnd, TimeSpan.FromSeconds(5)));
        tail.Mark();
        ExplorerUi.TypeText("e2ebeta");
        var second = tail.WaitForSubmit(TimeSpan.FromSeconds(10), secondHwnd.ToInt64());
        context.Note($"window B: {second}");

        Check.Equal(firstHwnd.ToInt64(), first.Hwnd, "window A source handle");
        Check.Equal(secondHwnd.ToInt64(), second.Hwnd, "window B source handle");
        Check.Equal(firstDirectory.TrimEnd('\\'), first.ResolvedPath?.TrimEnd('\\'), "window A resolved path");
        Check.Equal(secondDirectory.TrimEnd('\\'), second.ResolvedPath?.TrimEnd('\\'), "window B resolved path");

        context.CloseOwnWindows();
    }

    /// <summary>Monitoring re-establishes itself after Explorer is restarted.</summary>
    private static void ExplorerRestart(Harness harness, ScenarioContext context)
    {
        var directory = harness.CreateDataDirectory("explorer-restart", "restart-token.txt");
        var tail = harness.Tail();

        // A window has to be tracked before the restart, otherwise there is nothing for the tool to
        // notice: it reports the restart when its window set goes from "some" to "none" and back.
        var before = ExplorerUi.OpenLocation(directory, new[] { "explorer-restart" }, TimeSpan.FromSeconds(20), out var createdBefore);
        context.Track(before, createdBefore);
        tail.WaitForLine(line => line.Contains($"SearchBox detected hwnd={before}", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), $"reporting the search box of hwnd={before} before the restart");
        context.Note($"window tracked before the restart: hwnd={before}");
        context.CloseOwnWindows();

        Console.WriteLine("      WARNING: this scenario closes every Explorer window and restarts explorer.exe");
        using (var killer = Process.Start(new ProcessStartInfo("taskkill", "/F /IM explorer.exe")
               {
                   UseShellExecute = false,
                   RedirectStandardOutput = true,
                   RedirectStandardError = true,
               }))
        {
            killer?.WaitForExit(10_000);
        }

        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.Now < deadline && Process.GetProcessesByName("explorer").Length > 0) Thread.Sleep(200);
        Thread.Sleep(2000);

        if (Process.GetProcessesByName("explorer").Length == 0)
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            Thread.Sleep(4000);
        }

        context.Note("explorer.exe restarted; opening a window again");
        var hwnd = ExplorerUi.OpenLocation(directory, new[] { "explorer-restart" }, TimeSpan.FromSeconds(20), out var created);
        context.Track(hwnd, created);

        var reestablished = tail.WaitForLine(
            line => line.Contains("Explorer restarted: monitoring re-established", StringComparison.Ordinal),
            TimeSpan.FromSeconds(25), "reporting that monitoring was re-established after the Explorer restart");
        context.Note($"log: {reestablished}");

        var box = ExplorerUi.WaitForSearchBox(hwnd, TimeSpan.FromSeconds(15));
        ExplorerUi.FocusSearchBox(hwnd, box);
        ExplorerUi.ClearSearchBox(box);
        tail.Mark();
        ExplorerUi.TypeText("e2eafterrestart");
        var submit = tail.WaitForSubmit(TimeSpan.FromSeconds(12), hwnd.ToInt64());
        context.Note(submit.ToString());
        Check.Equal("e2eafterrestart", submit.Text, "search text");
        Check.Equal(directory.TrimEnd('\\'), submit.ResolvedPath?.TrimEnd('\\'), "resolved path");

        context.CloseOwnWindows();
    }

    /// <summary>A search recreates the Everything window when none is open.</summary>
    private static void EverythingClosed(Harness harness, ScenarioContext context)
    {
        var directory = harness.CreateDataDirectory("everything-closed", "everything-closed.txt");
        var tail = harness.Tail();
        OpenAndPrepare(harness, context, tail, directory, new[] { "everything-closed" });

        harness.CloseEverythingSearchWindows();
        context.Note($"Everything search windows open now: {harness.EverythingSearchWindows().Count}");
        Check.That(harness.EverythingSearchWindows().Count == 0, "could not close the existing Everything search windows");

        tail.Mark();
        ExplorerUi.TypeText("e2eclosedscan");
        var submit = tail.WaitForSubmit(TimeSpan.FromSeconds(15), context.LastHwnd);
        context.Note(submit.ToString());
        Check.Equal("created", submit.WindowAction, "Everything window action");
        harness.WaitForEverythingTitle("e2eclosedscan", TimeSpan.FromSeconds(10));

        context.CloseOwnWindows();
    }

    /// <summary>The second search reuses the Everything window.</summary>
    private static void WindowReuse(Harness harness, ScenarioContext context)
    {
        var directory = harness.CreateDataDirectory("window-reuse", "window-reuse.txt");
        var tail = harness.Tail();
        var hwnd = OpenAndPrepare(harness, context, tail, directory, new[] { "window-reuse" });

        ExplorerUi.TypeText("e2ereuseone");
        var first = tail.WaitForSubmit(TimeSpan.FromSeconds(15), hwnd.ToInt64());
        context.Note($"first: {first}");

        // A fresh query: clear the box first (the "continuous" scenario covers appending on purpose).
        var box = ExplorerUi.WaitForSearchBox(hwnd, TimeSpan.FromSeconds(5));
        ExplorerUi.FocusSearchBox(hwnd, box);
        ExplorerUi.ClearSearchBox(box);
        tail.Mark();
        ExplorerUi.TypeText("e2ereusetwo");
        var second = tail.WaitForSubmit(TimeSpan.FromSeconds(15), hwnd.ToInt64());
        context.Note($"second: {second}");

        Check.Equal("reused", second.WindowAction, "second Everything window action");
        Check.Equal("e2ereusetwo", second.Query, "second query");
        Check.Equal("e2ereusetwo", second.Text, "second search text");
        Check.That(first.WindowAction is "created" or "reused", $"unexpected first Everything window action '{first.WindowAction}'");

        context.CloseOwnWindows();
    }

    /// <summary>An unsupported Shell namespace is reported instead of being searched.</summary>
    private static void UnsupportedNamespace(Harness harness, ScenarioContext context)
    {
        var tail = harness.Tail();
        OpenAndPrepare(harness, context, tail, RecycleBinLocation, new[] { "回收站", "Recycle Bin" });

        ExplorerUi.TypeText("e2erecyclescan");
        var refused = tail.WaitForLine(
            line => line.Contains("search not redirected: UnsupportedShellNamespace", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), "reporting an unsupported Shell namespace");
        context.Note($"log: {refused}");

        // No Everything query may be sent for a location that cannot be searched.
        Thread.Sleep(1500);
        var late = tail.ReadNew();
        Check.That(!late.Any(line => line.Contains("Query=\"e2erecyclescan\"", StringComparison.Ordinal)),
            "an Everything query was produced for the Recycle Bin");
        foreach (var everythingWindow in harness.EverythingSearchWindows())
        {
            var title = EverythingIpc.GetSearchWindowTitle(everythingWindow);
            Check.NotContains(title, "e2erecyclescan", "Everything window title");
        }

        context.Note("no Everything query was produced, as required");
        context.CloseOwnWindows();
    }

    /// <summary>A path with non ASCII characters and a space is resolved correctly.</summary>
    private static void UnicodePath(Harness harness, ScenarioContext context)
    {
        var unicodeDirectory = harness.CreateDataDirectory("数据 目录", "文件alpha.txt");
        var tail = harness.Tail();
        OpenAndPrepare(harness, context, tail, unicodeDirectory, new[] { "数据 目录" });

        ExplorerUi.TypeText("文件alpha");
        var submit = tail.WaitForSubmit(TimeSpan.FromSeconds(10), context.LastHwnd);
        context.Note(submit.ToString());

        Check.Equal(unicodeDirectory.TrimEnd('\\'), submit.ResolvedPath?.TrimEnd('\\'), "resolved path");
        Check.Equal("文件alpha", submit.Text, "search text");
        Check.Contains(submit.Query ?? string.Empty, "文件alpha", "query");
        Check.That(submit.NotRedirected is null, $"the search was not redirected: {submit.NotRedirected}");

        context.CloseOwnWindows();
    }

    /// <summary>With logging disabled no log file is produced, but searches still work.</summary>
    private static void LoggingDisabled(Harness harness, ScenarioContext context)
    {
        var directory = harness.CreateDataDirectory("logging-disabled", "nolog-token.txt");
        var hwnd = ExplorerUi.OpenLocation(directory, new[] { "logging-disabled" }, TimeSpan.FromSeconds(15), out var created);
        context.Track(hwnd, created);
        var box = ExplorerUi.WaitForSearchBox(hwnd, TimeSpan.FromSeconds(15));
        ExplorerUi.FocusSearchBox(hwnd, box);
        ExplorerUi.ClearSearchBox(box);

        ExplorerUi.TypeText("e2enologscan");
        var title = harness.WaitForEverythingTitle("e2enologscan", TimeSpan.FromSeconds(15));
        context.Note($"Everything title: {title}");

        Thread.Sleep(1000);
        Check.FileExists(harness.LogPath, expected: false);
        context.Note($"no log file at {harness.LogPath}, and the search still reached Everything");

        context.CloseOwnWindows();
    }

    /// <summary>Clearing the log removes the files and logging continues afterwards.</summary>
    private static void LogClear(Harness harness, ScenarioContext context)
    {
        var root = Path.Combine(harness.BaseDirectory, "log-clear");
        Directory.CreateDirectory(root);
        using var logger = AppLogger.Create(root, new AppConfig { LoggingEnabled = true, LogLevel = "Debug" });
        var logPath = logger.CurrentLogPath;

        // Several rounds: clearing the log has to work every time, and the writer handshake is exactly
        // the kind of thing that only breaks now and then.
        const int rounds = 5;
        var cleared = 0;
        var problems = new List<string>();

        for (var round = 0; round < rounds; round++)
        {
            for (var i = 0; i < 3; i++) logger.Info($"e2e log line {round}-{i}");
            logger.Flush(TimeSpan.FromSeconds(3));

            if (!File.Exists(logPath))
            {
                problems.Add($"round {round}: nothing was written to {logPath}");
                continue;
            }

            var size = new FileInfo(logPath).Length;
            var ok = logger.ClearLogs(out var error);
            var stillThere = File.Exists(logPath);
            if (ok && !stillThere)
            {
                cleared++;
            }
            else
            {
                problems.Add($"round {round}: ClearLogs={ok} error={error ?? "<none>"} fileStillExists={stillThere}"
                             + (stillThere ? $" ({new FileInfo(logPath).Length} bytes, was {size})" : string.Empty));
            }

            // Logging must keep working after a successful clear.
            logger.Info($"e2e log line after clearing {round}");
            logger.Flush(TimeSpan.FromSeconds(3));
        }

        context.Note($"ClearLogs succeeded in {cleared}/{rounds} rounds");
        Check.That(cleared == rounds, $"ClearLogs did not clean up every round: {string.Join(" | ", problems)}");
        context.Note("logging continued after every clear");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Opens a location, waits until the tool has attached to its search box, clears the box and marks
    /// the log so that only what follows counts for the scenario.
    /// </summary>
    private static IntPtr OpenAndPrepare(
        Harness harness, ScenarioContext context, LogTail tail, string location, string[] titleHints)
    {
        var hwnd = ExplorerUi.OpenLocation(location, titleHints, TimeSpan.FromSeconds(20), out var created);
        context.Track(hwnd, created);
        context.Note($"opened {location} -> hwnd={hwnd} title=\"{Native.TitleOf(hwnd)}\" (created={created})");

        try
        {
            tail.WaitForLine(line => line.Contains($"SearchBox detected hwnd={hwnd}", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8), $"reporting the search box of hwnd={hwnd}");
        }
        catch (TimeoutException ex)
        {
            // A reused window may have been attached before this scenario started; not an error yet.
            context.Note("note: " + ex.Message.Split(Environment.NewLine)[0]);
        }

        var box = ExplorerUi.WaitForSearchBox(hwnd, TimeSpan.FromSeconds(10));
        ExplorerUi.FocusSearchBox(hwnd, box);
        ExplorerUi.ClearSearchBox(box);
        tail.Mark();
        return hwnd;
    }
}
