using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ExplorerEverythingSearch.Core.Everything;

namespace ExplorerEverythingSearch.E2E;

internal enum AppMode
{
    /// <summary>No application instance is needed by the scenario.</summary>
    None,

    /// <summary>Application running with logging enabled (the normal configuration).</summary>
    Default,

    /// <summary>Application running with <c>loggingEnabled=false</c>.</summary>
    NoLogging,
}

/// <summary>Thrown when a scenario's expectation is not met.</summary>
internal sealed class ScenarioFailure : Exception
{
    public ScenarioFailure(string message) : base(message) { }
}

/// <summary>
/// Owns everything a scenario needs: the built application, a throw-away root directory with its
/// configuration, the running process, and helpers for the log and for Everything.
/// </summary>
internal sealed class Harness : IDisposable
{
    private readonly bool _keepArtifacts;

    private Harness(string baseDirectory, string repoRoot, bool keepArtifacts)
    {
        BaseDirectory = baseDirectory;
        RepoRoot = repoRoot;
        _keepArtifacts = keepArtifacts;
        AppRoot = Path.Combine(baseDirectory, "app");
        DataRoot = Path.Combine(baseDirectory, "data");
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(DataRoot);
        AppExe = LocateAppExecutable(repoRoot);
    }

    public string BaseDirectory { get; }
    public string RepoRoot { get; }
    public string AppRoot { get; }
    public string DataRoot { get; }
    public string AppExe { get; }
    public string ConfigPath => Path.Combine(AppRoot, "config.json");
    public string LogPath => Path.Combine(AppRoot, "logs", "app.log");

    /// <summary>Standard output and error of the application; runtime crashes are reported here.</summary>
    public string AppErrorLogPath => Path.Combine(AppRoot, "app-stderr.log");
    public Process? App { get; private set; }
    public AppMode Mode { get; private set; } = AppMode.None;

    public static Harness Create(string? rootOverride, bool keepArtifacts)
    {
        var repoRoot = FindRepoRoot() ?? throw new InvalidOperationException(
            "could not locate the repository root (no ExplorerEverythingSearch.sln above the harness executable)");
        var baseDirectory = string.IsNullOrWhiteSpace(rootOverride)
            ? Path.Combine(Path.GetTempPath(), "ees-e2e-" + Guid.NewGuid().ToString("N")[..8])
            : Path.GetFullPath(rootOverride);
        Directory.CreateDirectory(baseDirectory);
        return new Harness(baseDirectory, repoRoot, keepArtifacts);
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExplorerEverythingSearch.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static string LocateAppExecutable(string repoRoot)
    {
        var projectDirectory = Path.Combine(repoRoot, "src", "ExplorerEverythingSearch.App");
        var candidates = Directory.Exists(projectDirectory)
            ? Directory.EnumerateFiles(projectDirectory, "ExplorerEverythingSearch.exe", SearchOption.AllDirectories).ToList()
            : new List<string>();
        var debug = candidates.FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var chosen = debug ?? candidates.FirstOrDefault();
        return chosen ?? Path.Combine(projectDirectory, "bin", "Debug", "net8.0-windows", "ExplorerEverythingSearch.exe");
    }

    /// <summary>Builds the application under test (Debug, same configuration as the harness needs).</summary>
    public void Build()
    {
        var project = Path.Combine(RepoRoot, "src", "ExplorerEverythingSearch.App", "ExplorerEverythingSearch.App.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepoRoot,
        };
        foreach (var argument in new[] { "build", project, "-c", "Debug", "--nologo" }) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start dotnet build");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(300_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet build failed (exit {process.ExitCode}):{Environment.NewLine}{output}");
        if (!File.Exists(AppExe))
            throw new InvalidOperationException($"the application was not found at {AppExe} after building");
    }

    /// <summary>Writes the configuration the scenarios run with.</summary>
    public void WriteConfig(bool loggingEnabled)
    {
        var config = new
        {
            enabled = true,
            autoSearchDelay = 1000,
            everythingPath = string.Empty,
            esPath = string.Empty,
            startWithWindows = false,
            showNotifications = false,
            logLevel = "Debug",
            loggingEnabled,
            reuseEverythingWindow = true,
            detectEnterByKeyboardHook = true,
            detectEnterByFocusChange = true,
            detectEnterByCommitTiming = true,
            enterCommitWindowMs = 400,
            explorerRescanSeconds = 60,
            language = "auto",
            maxLogFileSizeMb = 5,
            maxLogFiles = 5,
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Makes sure the application runs in the requested mode, restarting it when needed.</summary>
    public void EnsureApp(AppMode mode)
    {
        if (mode == AppMode.None)
        {
            StopApp();
            return;
        }

        if (App is { HasExited: false } && Mode == mode) return;

        StopApp();
        WriteConfig(loggingEnabled: mode == AppMode.Default);
        if (mode == AppMode.NoLogging)
        {
            var logs = Path.Combine(AppRoot, "logs");
            if (Directory.Exists(logs))
            {
                foreach (var file in Directory.EnumerateFiles(logs)) File.Delete(file);
            }
        }

        var startInfo = new ProcessStartInfo(AppExe)
        {
            UseShellExecute = false,
            WorkingDirectory = AppRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(AppRoot);
        App = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start the application");

        // A crashing .NET process explains itself on stderr ("Process terminated. ..."); keep it so a
        // failing scenario can show why the application disappeared.
        var app = App;
        app.OutputDataReceived += (_, e) => AppendAppOutput(e.Data);
        app.ErrorDataReceived += (_, e) => AppendAppOutput(e.Data);
        app.BeginOutputReadLine();
        app.BeginErrorReadLine();

        Mode = mode;

        if (mode == AppMode.Default)
        {
            // Startup is only complete once the monitor is running; the log says so.
            var tail = new LogTail(LogPath);
            tail.WaitForLine(line => line.Contains("Explorer search monitoring started", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20), "reporting that Explorer monitoring started");
        }
        else
        {
            Thread.Sleep(3000);
            if (App.HasExited) throw new ScenarioFailure($"the application exited during startup (exit code {App.ExitCode})");
        }
    }

    public void StopApp()
    {
        var process = App;
        App = null;
        Mode = AppMode.None;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                // Ask the running instance to stop through its own command line, then make sure it did.
                var stopInfo = new ProcessStartInfo(AppExe) { UseShellExecute = false, WorkingDirectory = AppRoot };
                foreach (var argument in new[] { "--exit", "--root", AppRoot }) stopInfo.ArgumentList.Add(argument);
                using var stopper = Process.Start(stopInfo);
                stopper?.WaitForExit(5000);
                if (!process.WaitForExit(8000)) process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        finally
        {
            process.Dispose();
        }
    }

    public LogTail Tail() => new(LogPath);

    /// <summary>True when the application process is gone (started and exited, or never started).</summary>
    public bool AppHasExited => App is null || App.HasExited;

    public int? AppExitCode => App is { HasExited: true } exited ? exited.ExitCode : null;

    private void AppendAppOutput(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        try
        {
            File.AppendAllText(AppErrorLogPath, line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The last lines the application wrote to its standard output or error.</summary>
    public IReadOnlyList<string> ReadAppErrorTail(int lines)
    {
        try
        {
            return File.Exists(AppErrorLogPath) ? File.ReadAllLines(AppErrorLogPath).TakeLast(lines).ToList() : Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Creates a fresh directory with test files below the harness data root.</summary>
    public string CreateDataDirectory(string name, params string[] fileNames)
    {
        var directory = Path.Combine(DataRoot, name);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        foreach (var fileName in fileNames)
        {
            File.WriteAllText(Path.Combine(directory, fileName), "explorer everything search e2e");
        }
        return directory;
    }

    // ------------------------------------------------------------------ Everything

    public IReadOnlyList<IntPtr> EverythingSearchWindows() => EverythingIpc.FindSearchWindows();

    /// <summary>Closes every Everything search window so the next search has to create one.</summary>
    public void CloseEverythingSearchWindows()
    {
        foreach (var hwnd in EverythingSearchWindows()) Native.Close(hwnd);
        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(8);
        while (DateTimeOffset.Now < deadline && EverythingSearchWindows().Count > 0) Thread.Sleep(150);
    }

    /// <summary>Waits until an Everything window shows the query and returns its title.</summary>
    public string WaitForEverythingTitle(string query, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        var seen = new List<string>();
        while (true)
        {
            foreach (var hwnd in EverythingSearchWindows())
            {
                var title = EverythingIpc.GetSearchWindowTitle(hwnd);
                if (!string.IsNullOrEmpty(title)) seen.Add(title);
                if (title.Contains(query, StringComparison.Ordinal)) return title;
            }

            if (DateTimeOffset.Now >= deadline)
                throw new ScenarioFailure(
                    $"no Everything window title contains \"{query}\" within {timeout.TotalSeconds:F0}s; seen: [{string.Join(" | ", seen.Distinct())}]");
            Thread.Sleep(150);
        }
    }

    public void Dispose()
    {
        StopApp();
        if (_keepArtifacts)
        {
            Console.WriteLine($"      artifacts kept at {BaseDirectory}");
            return;
        }

        try
        {
            if (Directory.Exists(BaseDirectory)) Directory.Delete(BaseDirectory, recursive: true);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"      could not remove {BaseDirectory}: {ex.Message}");
        }
    }
}

/// <summary>Small assertion helpers so failures carry the information needed to diagnose them.</summary>
internal static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new ScenarioFailure(message);
    }

    public static void Equal(string? expected, string? actual, string what)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new ScenarioFailure($"{what}: expected \"{expected}\" but found \"{actual}\"");
    }

    public static void Equal(long expected, long? actual, string what)
    {
        if (actual != expected)
            throw new ScenarioFailure($"{what}: expected {expected} but found {actual?.ToString() ?? "<none>"}");
    }

    public static void Contains(string haystack, string needle, string what)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
            throw new ScenarioFailure($"{what}: \"{haystack}\" does not contain \"{needle}\"");
    }

    public static void NotContains(string haystack, string needle, string what)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
            throw new ScenarioFailure($"{what}: \"{haystack}\" unexpectedly contains \"{needle}\"");
    }

    public static void FileExists(string path, bool expected)
    {
        var exists = File.Exists(path);
        if (exists != expected)
            throw new ScenarioFailure($"{(expected ? "missing" : "unexpected")} file {path}"
                                      + (exists ? $" ({new FileInfo(path).Length} bytes)" : string.Empty));
    }
}

/// <summary>One scenario: a name, the application state it needs, and its work.</summary>
internal sealed record Scenario(string Name, AppMode Mode, Action<Harness, ScenarioContext> Run);

/// <summary>Per-scenario bookkeeping: windows the harness opened and lines worth reporting.</summary>
internal sealed class ScenarioContext
{
    private readonly List<IntPtr> _ownWindows = new();

    public List<string> Evidence { get; } = new();

    public IntPtr Track(IntPtr hwnd, bool created)
    {
        if (created) _ownWindows.Add(hwnd);
        LastHwnd = hwnd.ToInt64();
        return hwnd;
    }

    /// <summary>The window this scenario opened last; used to attribute log lines to the right window.</summary>
    public long? LastHwnd { get; private set; }

    public void Note(string line)
    {
        Evidence.Add(line);
        Console.WriteLine($"      {line}");
    }

    /// <summary>Closes the windows this scenario opened; never touches windows it did not create.</summary>
    public void CloseOwnWindows()
    {
        foreach (var hwnd in _ownWindows)
        {
            try { ExplorerUi.Close(hwnd, TimeSpan.FromSeconds(5)); } catch { }
        }
        _ownWindows.Clear();
    }
}
