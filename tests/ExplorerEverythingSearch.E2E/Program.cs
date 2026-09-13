using System.Diagnostics;
using ExplorerEverythingSearch.Core.Everything;

namespace ExplorerEverythingSearch.E2E;

/// <summary>
/// End to end harness: drives real Explorer search boxes and checks what the tool does with them.
///
///   dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --scenario all
///   dotnet run --project tests\ExplorerEverythingSearch.E2E -c Debug -- --scenario basic-idle --keep-artifacts
///
/// Exit code 0 means every scenario passed, 1 means at least one failed, 2 means the arguments were wrong.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? scenarioName = null;
        string? root = null;
        var keepArtifacts = false;
        var noBuild = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--scenario":
                case "-s":
                    if (i + 1 < args.Length) scenarioName = args[++i];
                    break;
                case "--root":
                    if (i + 1 < args.Length) root = args[++i];
                    break;
                case "--keep-artifacts":
                    keepArtifacts = true;
                    break;
                case "--no-build":
                    noBuild = true;
                    break;
                case "--list":
                    Console.WriteLine("available scenarios:");
                    foreach (var known in Scenarios.All) Console.WriteLine($"  {known.Name}");
                    return 0;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.WriteLine($"unknown argument '{args[i]}'");
                    PrintUsage();
                    return 2;
            }
        }

        var scenarios = Select(scenarioName, out var selectionError);
        if (selectionError is not null)
        {
            Console.WriteLine(selectionError);
            Console.WriteLine("available scenarios: " + string.Join(", ", Scenarios.All.Select(s => s.Name)));
            return 2;
        }

        Harness? harness = null;
        try
        {
            harness = Harness.Create(root, keepArtifacts);
            Console.WriteLine("Explorer Everything Search - end to end harness");
            Console.WriteLine($"  repository      : {harness.RepoRoot}");
            Console.WriteLine($"  application     : {harness.AppExe}");
            Console.WriteLine($"  working root    : {harness.BaseDirectory}");
            Console.WriteLine($"  Everything      : {(EverythingIpc.IsRunning ? EverythingIpc.Describe(EverythingIpc.TryGetVersion()) : "not running")}"
                              + $", database loaded: {(EverythingIpc.IsDatabaseLoaded() ? "yes" : "no")}");
            Console.WriteLine($"  scenarios       : {string.Join(", ", scenarios.Select(s => s.Name))}");
            Console.WriteLine();

            if (!noBuild)
            {
                Console.WriteLine("building the application under test ...");
                harness.Build();
                Console.WriteLine();
            }

            var passed = new List<string>();
            var failed = new List<(string Name, string Reason)>();
            var total = Stopwatch.StartNew();

            foreach (var scenario in scenarios)
            {
                Console.WriteLine($">>> {scenario.Name} (mode {scenario.Mode})");
                var context = new ScenarioContext();
                var watch = Stopwatch.StartNew();
                try
                {
                    harness.EnsureApp(scenario.Mode);
                    scenario.Run(harness, context);
                    watch.Stop();
                    passed.Add(scenario.Name);
                    Console.WriteLine($"PASS {scenario.Name} {watch.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    watch.Stop();
                    var reason = ex is ScenarioFailure ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                    if (harness.AppHasExited && harness.Mode != AppMode.None)
                    {
                        reason = $"the application process exited during the scenario (exit code {harness.AppExitCode?.ToString() ?? "?"}): {reason}";
                    }
                    failed.Add((scenario.Name, reason));
                    Console.WriteLine($"FAIL {scenario.Name} {watch.ElapsedMilliseconds} ms");
                    Console.WriteLine(Indent("reason: " + reason));
                    if (ex is not ScenarioFailure && ex.StackTrace is { Length: > 0 } stack)
                        Console.WriteLine(Indent(stack.Split(Environment.NewLine)[0]));
                    foreach (var line in harness.ReadAppErrorTail(8)) Console.WriteLine(Indent("app stderr: " + line));
                    DumpLogTail(harness, context);
                }
                finally
                {
                    context.CloseOwnWindows();
                }

                Console.WriteLine();
            }

            total.Stop();
            Console.WriteLine("=========== summary ===========");
            foreach (var name in passed) Console.WriteLine($"PASS {name}");
            foreach (var (name, reason) in failed)
            {
                Console.WriteLine($"FAIL {name}");
                Console.WriteLine(Indent(reason));
            }
            Console.WriteLine($"passed {passed.Count}, failed {failed.Count}, total {total.Elapsed.TotalSeconds:F1}s");
            if (keepArtifacts) Console.WriteLine($"artifacts kept in {harness.BaseDirectory}");
            return failed.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"harness error: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            harness?.Dispose();
        }
    }

    private static IReadOnlyList<Scenario> Select(string? name, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
            return Scenarios.All;

        var matches = Scenarios.All.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            error = $"unknown scenario '{name}'";
            return Array.Empty<Scenario>();
        }
        return matches;
    }

    private static void DumpLogTail(Harness harness, ScenarioContext context)
    {
        if (!File.Exists(harness.LogPath)) return;
        try
        {
            var lines = File.ReadAllLines(harness.LogPath);
            Console.WriteLine(Indent($"last log lines from {harness.LogPath}:"));
            foreach (var line in lines.TakeLast(12)) Console.WriteLine(Indent("  " + line));
        }
        catch (IOException)
        {
            // the log is being written right now
        }
    }

    private static string Indent(string text) => "      " + text;

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Explorer Everything Search - end to end harness

              --scenario <name>|all   scenario to run (default: all)
              --root <directory>      working root instead of a fresh directory below %TEMP%
              --keep-artifacts        keep the working root (configuration, logs, test folders)
              --no-build              use the application as it is, without building it first
              --list                  list the scenarios

            The harness drives real Explorer windows and real Everything windows, so it needs an
            interactive desktop session and a running Everything installation. The explorer-restart
            scenario closes every Explorer window.
            """);
    }
}
