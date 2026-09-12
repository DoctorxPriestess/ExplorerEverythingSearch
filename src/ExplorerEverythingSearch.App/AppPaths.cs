namespace ExplorerEverythingSearch.App;

/// <summary>
/// Resolves the portable application root: the directory that holds config.json and logs\.
/// Never %AppData%, never %LocalAppData% - unless the user explicitly asks for it via --root.
/// </summary>
public static class AppPaths
{
    /// <summary>Directory the executable lives in (for single file publish, the folder of the EXE).</summary>
    public static string ExecutableDirectory
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                var directory = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrEmpty(directory)) return directory;
            }
            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(ExecutableDirectory, "ExplorerEverythingSearch.exe");

    /// <summary>The root directory used for config.json and logs\.</summary>
    public static string ResolveRoot(string? overrideDirectory)
    {
        if (string.IsNullOrWhiteSpace(overrideDirectory)) return ExecutableDirectory;
        return Path.GetFullPath(overrideDirectory);
    }
}
