using System.Text.RegularExpressions;

namespace ExplorerEverythingSearch.E2E;

/// <summary>One submitted search as it appears in the application log.</summary>
internal sealed record SubmitRecord
{
    public required string Trigger { get; init; }
    public string? Text { get; set; }
    public long? Hwnd { get; set; }
    public string? ResolvedPath { get; set; }
    public string? Scope { get; set; }
    public bool ScopeCarriedOver { get; set; }
    public string? Query { get; set; }
    public string? WindowAction { get; set; }
    public long? LatencyMs { get; set; }
    public string? NotRedirected { get; set; }

    public bool Completed => LatencyMs is not null || NotRedirected is not null;

    public override string ToString()
        => $"trigger={Trigger} text=\"{Text}\" hwnd={Hwnd} path=\"{ResolvedPath}\" scope={Scope} query=\"{Query}\" "
           + $"window={WindowAction} latency={LatencyMs?.ToString() ?? "-"}ms"
           + (NotRedirected is null ? string.Empty : $" notRedirected={NotRedirected}");
}

/// <summary>
/// Incremental reader for the application log.
///
/// The application writes its log asynchronously, so every wait here polls the file until the expected
/// line shows up instead of sleeping for a guessed duration.
/// </summary>
internal sealed class LogTail
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(80);

    private readonly string _path;
    private readonly List<string> _all = new();
    private long _offset;

    public LogTail(string path)
    {
        _path = path;
    }

    public IReadOnlyList<string> AllLines => _all;

    /// <summary>
    /// Forgets everything written so far: the next reads only see what happens from now on. Scenarios
    /// use this so a previous scenario's lines can never satisfy their expectations.
    /// </summary>
    public void Mark()
    {
        try
        {
            _offset = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        }
        catch (IOException)
        {
            _offset = 0;
        }
        _all.Clear();
    }

    /// <summary>Reads every line written since the previous read.</summary>
    public List<string> ReadNew()
    {
        var fresh = new List<string>();
        try
        {
            if (!File.Exists(_path)) return fresh;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _offset)
            {
                // The log was rotated or cleared: continue from the start of the new file.
                _offset = 0;
            }

            stream.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null) fresh.Add(line);
            _offset = stream.Position;
        }
        catch (IOException)
        {
            // The writer is between two flushes; the next poll picks the lines up.
        }

        _all.AddRange(fresh);
        return fresh;
    }

    /// <summary>Waits until a line matching <paramref name="predicate"/> was written, and returns it.</summary>
    public string WaitForLine(Func<string, bool> predicate, TimeSpan timeout, string description)
    {
        var found = WaitForLines(predicate, timeout, description);
        return found[^1];
    }

    /// <summary>Waits until at least one line matching <paramref name="predicate"/> was written.</summary>
    public List<string> WaitForLines(Func<string, bool> predicate, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.Now + timeout;
        var matches = new List<string>();
        while (true)
        {
            matches.AddRange(ReadNew().Where(predicate));
            if (matches.Count > 0) return matches;
            if (DateTimeOffset.Now >= deadline)
            {
                throw new TimeoutException(
                    $"no log line {description} within {timeout.TotalSeconds:F0}s; last lines:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, _all.TakeLast(12)));
            }
            Thread.Sleep(PollInterval);
        }
    }

    public bool TryWaitForLine(Func<string, bool> predicate, TimeSpan timeout, out string? line)
    {
        try
        {
            line = WaitForLine(predicate, timeout, "expected");
            return true;
        }
        catch (TimeoutException)
        {
            line = null;
            return false;
        }
    }

    /// <summary>
    /// Waits for the next complete search submit block and parses it. A block starts with
    /// "Search submitted trigger=..." and ends with "Search completed in ... ms", or with a
    /// "search not redirected" warning.
    /// </summary>
    /// <param name="timeout">How long to wait for the block to complete.</param>
    /// <param name="sourceHwnd">
    /// When given, submits of other Explorer windows are skipped. The tool monitors every Explorer
    /// window in the session, so a stale window of an earlier run can submit while this scenario waits;
    /// without the filter such a foreign submit would be mistaken for the one under test.
    /// </param>
    public SubmitRecord WaitForSubmit(TimeSpan timeout, long? sourceHwnd = null)
    {
        var deadline = DateTimeOffset.Now + timeout;
        SubmitRecord? current = null;

        while (true)
        {
            foreach (var line in ReadNew())
            {
                if (current is null)
                {
                    var start = Regex.Match(line, @"Search submitted trigger=(\w+)");
                    if (start.Success) current = new SubmitRecord { Trigger = start.Groups[1].Value };
                    continue;
                }

                Fill(current, line);
                if (!current.Completed) continue;

                // SourceExplorerHwnd only appears inside the block, so the filter can be applied here.
                if (sourceHwnd is null || current.Hwnd == sourceHwnd) return current;
                current = null;
            }

            if (DateTimeOffset.Now >= deadline)
            {
                throw new TimeoutException(
                    $"no completed search submit within {timeout.TotalSeconds:F0}s"
                    + (current is null ? string.Empty : $" (incomplete: {current})")
                    + $"; last lines:{Environment.NewLine}" + string.Join(Environment.NewLine, _all.TakeLast(12)));
            }

            Thread.Sleep(PollInterval);
        }
    }

    private static void Fill(SubmitRecord record, string line)
    {
        var text = Regex.Match(line, "SearchText=\"(.*)\"$");
        if (text.Success) { record.Text = text.Groups[1].Value; return; }

        var hwnd = Regex.Match(line, @"SourceExplorerHwnd=(\d+)");
        if (hwnd.Success) { record.Hwnd = long.Parse(hwnd.Groups[1].Value); return; }

        var path = Regex.Match(line, "ResolvedPath=\"(.*)\"$");
        if (path.Success) { record.ResolvedPath = path.Groups[1].Value; return; }

        var scope = Regex.Match(line, @"SearchScope=(\w+)(.*)$");
        if (scope.Success)
        {
            record.Scope = scope.Groups[1].Value;
            record.ScopeCarriedOver = scope.Groups[2].Value.Contains("carried over", StringComparison.Ordinal);
            return;
        }

        var query = Regex.Match(line, "Query=\"(.*)\"$");
        if (query.Success) { record.Query = query.Groups[1].Value; return; }

        if (line.Contains("Everything window created", StringComparison.Ordinal)) { record.WindowAction = "created"; return; }
        if (line.Contains("Everything window reused", StringComparison.Ordinal)) { record.WindowAction = "reused"; return; }

        var latency = Regex.Match(line, @"Search completed in (\d+) ms");
        if (latency.Success) { record.LatencyMs = long.Parse(latency.Groups[1].Value); return; }

        var failed = Regex.Match(line, @"search not redirected: (\w+)");
        if (failed.Success) record.NotRedirected = failed.Groups[1].Value;
    }
}
