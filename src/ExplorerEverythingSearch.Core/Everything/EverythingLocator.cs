using Microsoft.Win32;
using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;

namespace ExplorerEverythingSearch.Core.Everything;

/// <summary>
/// Finds the Everything installation. Nothing is hard coded to a developer machine:
/// the configured path wins, then the running instance, then the uninstall registry entries,
/// then the usual install locations, then PATH.
/// </summary>
public sealed class EverythingLocator
{
    private readonly Func<AppConfig> _config;
    private readonly AppLogger _logger;
    private readonly object _sync = new();
    private string? _everythingExe;
    private string? _esExe;
    private bool _resolved;

    public EverythingLocator(Func<AppConfig> config, AppLogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public string? EverythingExe
    {
        get
        {
            EnsureResolved();
            return _everythingExe;
        }
    }

    public string? EsExe
    {
        get
        {
            EnsureResolved();
            return _esExe;
        }
    }

    /// <summary>Drops the cached result (used after the user edits the paths in settings).</summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _resolved = false;
            _everythingExe = null;
            _esExe = null;
        }
    }

    private void EnsureResolved()
    {
        lock (_sync)
        {
            if (_resolved) return;
            _everythingExe = ResolveEverythingExe();
            _esExe = ResolveEsExe(_everythingExe);
            _resolved = true;
        }
    }

    public string? ResolveEverythingExe()
    {
        var configured = _config().EverythingPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)) return configured;
            _logger.Warn($"configured everythingPath does not exist: {configured}");
        }

        var running = EverythingIpc.TryGetRunningExecutablePath();
        if (running is not null) return running;

        foreach (var candidate in CandidateExecutables("Everything.exe"))
        {
            if (File.Exists(candidate)) return candidate;
        }

        var onPath = FindOnPath("Everything.exe");
        if (onPath is not null) return onPath;

        _logger.Warn("Everything.exe could not be located");
        return null;
    }

    public string? ResolveEsExe(string? everythingExe)
    {
        var configured = _config().EsPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)) return configured;
            _logger.Warn($"configured esPath does not exist: {configured}");
        }

        var candidates = new List<string>();
        if (everythingExe is not null)
            candidates.Add(Path.Combine(Path.GetDirectoryName(everythingExe) ?? string.Empty, "es.exe"));

        candidates.AddRange(CandidateExecutables("es.exe"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return FindOnPath("es.exe");
    }

    private IEnumerable<string> CandidateExecutables(string fileName)
    {
        var roots = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "Everything", fileName);
        }

        foreach (var install in RegistryInstallLocations())
        {
            if (string.IsNullOrEmpty(install)) continue;
            yield return Path.Combine(install, fileName);
        }
    }

    private List<string> RegistryInstallLocations()
    {
        var result = new List<string>();
        var subKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        var hives = new[] { Registry.CurrentUser, Registry.LocalMachine };

        foreach (var hive in hives)
        {
            foreach (var subKey in subKeys)
            {
                string[] names;
                try
                {
                    using var key = hive.OpenSubKey(subKey);
                    if (key is null) continue;
                    names = key.GetSubKeyNames();
                }
                catch
                {
                    continue;
                }

                foreach (var name in names)
                {
                    try
                    {
                        using var entry = hive.OpenSubKey(subKey + "\\" + name);
                        var displayName = entry?.GetValue("DisplayName") as string;
                        if (displayName is null || !displayName.Contains("Everything", StringComparison.OrdinalIgnoreCase)) continue;

                        if (entry?.GetValue("InstallLocation") is string location && location.Length > 0)
                            Add(result, location.Trim('"'));

                        if (entry?.GetValue("DisplayIcon") is string icon && icon.Length > 0)
                        {
                            var path = icon.Trim('"').Split(',')[0];
                            var directory = Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(directory)) Add(result, directory);
                        }
                    }
                    catch
                    {
                        // ignore malformed entries
                    }
                }
            }
        }

        return result;
    }

    private static void Add(List<string> target, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!target.Contains(value, StringComparer.OrdinalIgnoreCase)) target.Add(value);
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // ignore invalid PATH entries
            }
        }
        return null;
    }
}
