using System.Runtime.InteropServices;

namespace ExplorerEverythingSearch.Tests.TestSupport;

/// <summary>
/// A real top level window whose class name is Everything's search window class, so
/// <c>EverythingBridge.Execute</c> can be exercised end to end without Everything being installed.
///
/// The window lives on its own message pumping thread: the bridge calls ShowWindow/SetWindowPos on it,
/// and those calls are cross thread SendMessages that only complete while the owning thread pumps.
/// The window is created off screen and is destroyed again on dispose.
/// </summary>
internal sealed class FakeEverythingSearchWindow : IDisposable
{
    /// <summary>Mirrors EverythingIpc.SearchWindowClass.</summary>
    public const string WindowClass = "EVERYTHING";

    private const int WsPopup = unchecked((int)0x80000000);
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;

    private delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Held in static fields: the delegate must outlive the window class registration.
    private static readonly WindowProc ProcCallback = WindowProcImpl;
    private static readonly object ClassLock = new();
    private static bool _classRegistered;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _failure;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public IntPtr LpszMenuName;
        public IntPtr LpszClassName;
        public IntPtr HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint LPrivate;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    public FakeEverythingSearchWindow(string query)
    {
        EnsureClassRegistered();
        Title = query + " - Everything";
        _thread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "EES-Tests-FakeEverything",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(15)))
            throw new InvalidOperationException("the fake Everything window thread did not start");
        if (_failure is not null) throw new InvalidOperationException("could not create the fake Everything window", _failure);
    }

    public IntPtr Handle { get; private set; }

    public string Title { get; }

    private void PumpLoop()
    {
        try
        {
            Handle = CreateWindowExW(0, WindowClass, Title, WsPopup, -20000, -20000, 1, 1,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowExW failed (win32 error {Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            _failure = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        // The pump is what lets the bridge's cross thread ShowWindow/SetWindowPos calls complete.
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;
            var className = Marshal.StringToHGlobalUni(WindowClass);
            try
            {
                var wc = new WndClassEx
                {
                    CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                    Style = 0,
                    LpfnWndProc = Marshal.GetFunctionPointerForDelegate(ProcCallback),
                    HInstance = GetModuleHandleW(null),
                    LpszClassName = className,
                };
                if (RegisterClassExW(ref wc) == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    // ERROR_CLASS_ALREADY_EXISTS (1410) means another test already registered it.
                    if (error != 1410)
                        throw new InvalidOperationException($"could not register the '{WindowClass}' window class (win32 error {error})");
                }
                _classRegistered = true;
            }
            finally
            {
                Marshal.FreeHGlobal(className);
            }
        }
    }

    private static IntPtr WindowProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmClose:
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        PostMessageW(Handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(5));
        Handle = IntPtr.Zero;
        _ready.Dispose();
    }
}
