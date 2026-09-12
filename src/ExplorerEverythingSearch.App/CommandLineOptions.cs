namespace ExplorerEverythingSearch.App;

/// <summary>
/// Command line of the application.
///
/// The portable layout (config.json, logs\ next to the EXE) is the default; <c>--root</c> exists so
/// the automated tests (and users who want to keep their data elsewhere) can redirect it explicitly.
/// </summary>
public sealed record CommandLineOptions
{
    public bool Startup { get; init; }

    public bool OpenSettings { get; init; }

    public bool RequestExit { get; init; }

    public bool ShowHelp { get; init; }

    public bool ShowVersion { get; init; }

    public string? RootDirectory { get; init; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--startup":
                    options = options with { Startup = true };
                    break;
                case "--settings":
                    options = options with { OpenSettings = true };
                    break;
                case "--exit":
                    options = options with { RequestExit = true };
                    break;
                case "--help":
                case "-h":
                case "/?":
                    options = options with { ShowHelp = true };
                    break;
                case "--version":
                    options = options with { ShowVersion = true };
                    break;
                case "--root":
                    if (i + 1 < args.Length) options = options with { RootDirectory = args[++i] };
                    break;
                default:
                    if (arg.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
                        options = options with { RootDirectory = arg["--root=".Length..] };
                    break;
            }
        }
        return options;
    }
}
