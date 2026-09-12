using System.Collections.Concurrent;

namespace ExplorerEverythingSearch.Core.Threading;

/// <summary>
/// A single dedicated STA thread with a Win32 message pump.
///
/// Both frameworks used by this tool need it:
/// - UI Automation only delivers events to a thread that pumps messages,
/// - the Shell automation object model (Shell.Application / ShellWindows) is apartment threaded.
///
/// The pump is strictly event driven: it blocks in MsgWaitForMultipleObjectsEx until either work is
/// queued or a window message arrives, so an idle process does not wake up at all.
/// </summary>
public sealed class StaDispatcher : IDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;
    private volatile bool _disposed;

    public StaDispatcher(string name)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException($"{name} STA thread did not start");
    }

    public uint ThreadId => _threadId;

    /// <summary>Executes <paramref name="action"/> on the STA thread and waits for its result.</summary>
    public T Invoke<T>(Func<T> action, TimeSpan? timeout = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StaDispatcher));
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId) return action();

        using var completed = new ManualResetEventSlim(false);
        T result = default!;
        Exception? failure = null;
        Enqueue(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
            finally { completed.Set(); }
        });

        if (!completed.Wait(timeout ?? TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA dispatcher did not complete the requested work in time");
        if (failure is not null) throw new InvalidOperationException("STA work item failed", failure);
        return result;
    }

    public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    /// <summary>Fire and forget; used for work that must not block a UIA event handler.</summary>
    public void Post(Action action)
    {
        if (_disposed) return;
        Enqueue(action);
    }

    private void Enqueue(Action action)
    {
        _queue.Enqueue(action);
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    private void Run()
    {
        _threadId = Interop.NativeMethods.GetCurrentThreadId();
        _ready.Set();
        var handles = new[] { _signal.AvailableWaitHandle.SafeWaitHandle.DangerousGetHandle() };

        try
        {
            while (!_disposed)
            {
                while (_queue.TryDequeue(out var action))
                {
                    try { action(); }
                    catch { /* a failing work item must not kill the pump */ }
                }

                // Nothing queued: wait for new work or for a window message, without polling.
                uint waitResult;
                try
                {
                    waitResult = Interop.NativeMethods.MsgWaitForMultipleObjectsEx(
                        1, handles, Interop.NativeMethods.INFINITE, Interop.NativeMethods.QS_ALLINPUT, Interop.NativeMethods.MWMO_INPUTAVAILABLE);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (waitResult == Interop.NativeMethods.WAIT_OBJECT_0)
                {
                    try { _signal.Wait(0); } catch { }
                }
                else if (waitResult == Interop.NativeMethods.WAIT_OBJECT_0 + 1 || waitResult == Interop.NativeMethods.WAIT_TIMEOUT)
                {
                    PumpMessages();
                }
                else
                {
                    // WAIT_FAILED or abandoned: yield briefly so we never spin.
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception)
        {
            // The dispatcher thread must never take the process down.
        }
    }

    /// <summary>Dispatches every pending window message without blocking.</summary>
    private static void PumpMessages()
    {
        var msg = default(Interop.NativeMethods.MSG);
        while (Interop.NativeMethods.PeekMessage(out msg, IntPtr.Zero, 0, 0, Interop.NativeMethods.PM_REMOVE))
        {
            Interop.NativeMethods.TranslateMessage(ref msg);
            Interop.NativeMethods.DispatchMessage(ref msg);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_threadId != 0)
                Interop.NativeMethods.PostThreadMessage(_threadId, Interop.NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
        try { _signal.Release(); } catch { }
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { }
        try { _signal.Dispose(); } catch { }
        try { _ready.Dispose(); } catch { }
    }
}
