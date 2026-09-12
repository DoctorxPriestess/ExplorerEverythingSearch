using System.Diagnostics;
using ExplorerEverythingSearch.Core.Diagnostics;

namespace ExplorerEverythingSearch.Core.Everything;

public sealed record EsQueryResult(bool Success, IReadOnlyList<string> Paths, string? Error);

/// <summary>
/// Optional <c>es.exe</c> client (Everything's official command line search tool).
///
/// It is not used for the primary search path - Everything's own search window is driven instead -
/// but it is genuinely useful for diagnostics and for the automated end-to-end tests, which use it to
/// cross-check the search scope independently of the application's own logging.
///
/// Note (measured with ES 1.1.0.37): arguments are passed as an argv array, because that build
/// mishandles a single argument that contains spaces. Search text is therefore handed over as
/// separate tokens; quoted phrases inside the text are not preserved by this build.
/// </summary>
public sealed class EsCliClient
{
    private readonly EverythingLocator _locator;
    private readonly AppLogger _logger;

    public EsCliClient(EverythingLocator locator, AppLogger logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public string? ExecutablePath => _locator.EsExe;

    public bool IsAvailable => _locator.EsExe is not null;

    /// <summary>
    /// Runs a search and returns the matching full paths. <paramref name="pathScope"/> restricts the
    /// search to a folder and its subfolders (es.exe <c>-path</c>), <paramref name="maxResults"/> limits
    /// the result count.
    /// </summary>
    public EsQueryResult Query(string searchText, string? pathScope = null, int maxResults = 0, TimeSpan? timeout = null)
    {
        var exe = _locator.EsExe;
        if (exe is null) return new EsQueryResult(false, Array.Empty<string>(), "es.exe not found");

        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(pathScope))
        {
            startInfo.ArgumentList.Add("-path");
            startInfo.ArgumentList.Add(pathScope);
        }
        if (maxResults > 0)
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(maxResults.ToString());
        }
        foreach (var token in (searchText ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            startInfo.ArgumentList.Add(token);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new EsQueryResult(false, Array.Empty<string>(), "could not start es.exe");

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(20)).TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new EsQueryResult(false, Array.Empty<string>(), "es.exe timed out");
            }

            var stdout = output.GetAwaiter().GetResult();
            var stderr = error.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                return new EsQueryResult(false, Array.Empty<string>(), $"es.exe exited with {process.ExitCode}: {stderr.Trim()}");

            var paths = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .ToArray();
            return new EsQueryResult(true, paths, null);
        }
        catch (Exception ex)
        {
            _logger.Debug($"es.exe invocation failed: {ex.Message}");
            return new EsQueryResult(false, Array.Empty<string>(), ex.Message);
        }
    }
}
