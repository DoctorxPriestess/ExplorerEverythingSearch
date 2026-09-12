using System.Collections.Generic;
using System.Linq;
using ExplorerEverythingSearch.Core.Search;
using ExplorerEverythingSearch.Core.Shell;
using ExplorerEverythingSearch.Core.Startup;

namespace ExplorerEverythingSearch.Tests.TestSupport;

/// <summary>Records every submit so the tests can assert what was (and was not) submitted.</summary>
internal sealed class RecordingSubmitter : ISearchSubmitter
{
    private readonly object _sync = new();
    private readonly List<(long Hwnd, string Text, TriggerType Trigger)> _submissions = new();

    public int Count
    {
        get { lock (_sync) return _submissions.Count; }
    }

    public IReadOnlyList<(long Hwnd, string Text, TriggerType Trigger)> Submissions
    {
        get { lock (_sync) return _submissions.ToArray(); }
    }

    public (long Hwnd, string Text, TriggerType Trigger) Last
    {
        get
        {
            lock (_sync)
            {
                if (_submissions.Count == 0) throw new InvalidOperationException("nothing was submitted");
                return _submissions[^1];
            }
        }
    }

    public void Submit(long explorerHwnd, string searchText, TriggerType trigger)
    {
        lock (_sync) _submissions.Add((explorerHwnd, searchText, trigger));
    }

    public bool WaitForCount(int expected, int timeoutMs = 5_000)
        => Wait.Until(() => Count >= expected, timeoutMs);
}

/// <summary>Location resolver with a programmable answer, counting how often it was asked.</summary>
internal sealed class FakeLocationResolver : IExplorerLocationResolver
{
    private int _calls;

    public Func<long, ExplorerWindowLocation?> OnResolve { get; set; } = _ => null;

    public int Calls => Volatile.Read(ref _calls);

    public ExplorerWindowLocation? TryResolve(long hwnd)
    {
        Interlocked.Increment(ref _calls);
        return OnResolve(hwnd);
    }

    public IReadOnlyList<ExplorerWindowLocation> Enumerate() => Array.Empty<ExplorerWindowLocation>();
}

/// <summary>In-memory stand-in for HKCU\...\Run; no test ever writes the user's registry.</summary>
internal sealed class MemoryStartupRegistry : IStartupRegistry
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool FailWrites { get; set; }

    public bool FailDeletes { get; set; }

    public int WriteCount { get; private set; }

    public IReadOnlyDictionary<string, string> Values => _values;

    public string? ReadValue(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public bool WriteValue(string name, string value)
    {
        if (FailWrites) return false;
        WriteCount++;
        _values[name] = value;
        return true;
    }

    public bool DeleteValue(string name)
    {
        if (FailDeletes) return false;
        _values.Remove(name);
        return true;
    }

    public void Seed(string name, string value) => _values[name] = value;
}
