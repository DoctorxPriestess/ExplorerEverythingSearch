using System.Security.Cryptography;
using System.Text;

namespace ExplorerEverythingSearch.App;

/// <summary>
/// Keeps a single instance of the tool alive and lets a second launch talk to it
/// (<c>--settings</c> opens the settings window of the running instance, <c>--exit</c> shuts it down).
///
/// An explicit <c>--root</c> (used by the automated tests) produces an independent instance, so a
/// running "real" instance never interferes with a test run.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexNamePrefix = "ExplorerEverythingSearch.SingleInstance";
    private const string SettingsSignalPrefix = "ExplorerEverythingSearch.Signal.Settings";
    private const string ExitSignalPrefix = "ExplorerEverythingSearch.Signal.Exit";

    private readonly string _mutexName;
    private readonly string _settingsSignalName;
    private readonly string _exitSignalName;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _settingsSignal;
    private readonly EventWaitHandle _exitSignal;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly bool _ownsMutex;
    private Thread? _listener;

    public SingleInstanceGuard(string? instanceKey = null)
    {
        var suffix = instanceKey is null ? string.Empty : "." + ShortHash(instanceKey);
        _mutexName = MutexNamePrefix + suffix;
        _settingsSignalName = SettingsSignalPrefix + suffix;
        _exitSignalName = ExitSignalPrefix + suffix;

        _mutex = new Mutex(initiallyOwned: true, _mutexName, out _ownsMutex);
        _settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, _settingsSignalName);
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, _exitSignalName);
    }

    /// <summary>True when this process is the first (and therefore responsible) instance.</summary>
    public bool IsPrimary => _ownsMutex;

    public static void RequestExistingInstanceOpenSettings(string? instanceKey = null)
        => Signal(SettingsSignalPrefix + SuffixFor(instanceKey));

    public static void RequestExistingInstanceExit(string? instanceKey = null)
        => Signal(ExitSignalPrefix + SuffixFor(instanceKey));

    private static string SuffixFor(string? instanceKey)
        => instanceKey is null ? string.Empty : "." + ShortHash(instanceKey);

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.Unicode.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }

    private static void Signal(string name)
    {
        try
        {
            using var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            handle.Set();
        }
        catch
        {
            // the other instance is gone or not reachable
        }
    }

    /// <summary>Starts listening for signals; only meaningful for the primary instance.</summary>
    public void StartListening(Action onOpenSettings, Action onExit)
    {
        if (!IsPrimary || _listener is not null) return;

        _listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _settingsSignal, _exitSignal, _shutdown.Token.WaitHandle };
            while (!_shutdown.IsCancellationRequested)
            {
                int index;
                try { index = WaitHandle.WaitAny(handles); }
                catch { return; }

                if (index == 0)
                {
                    try { onOpenSettings(); } catch { }
                }
                else if (index == 1)
                {
                    try { onExit(); } catch { }
                    return;
                }
                else
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "EES-SingleInstance",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        try { _shutdown.Cancel(); } catch { }
        try { _settingsSignal.Dispose(); } catch { }
        try { _exitSignal.Dispose(); } catch { }
        try { _shutdown.Dispose(); } catch { }
        try
        {
            if (_ownsMutex) _mutex.ReleaseMutex();
        }
        catch { }
        try { _mutex.Dispose(); } catch { }
    }
}
