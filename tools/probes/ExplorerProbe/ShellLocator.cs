using System.Runtime.InteropServices;

namespace ExplorerProbe;

/// <summary>
/// Resolves "which Shell location is this Explorer window showing".
///
/// Empirically established constraint: Explorer runs out of process and only its *dual*
/// (IDispatch-marshaled) automation interfaces cross the process boundary. Vtable interfaces
/// such as IShellBrowser / IFolderView (and therefore PIDL access) fail with E_NOINTERFACE
/// from an external process, so the Shell.Application automation object model is used.
/// </summary>
internal static class ShellLocator
{
    public sealed record ShellWindowInfo(
        int Hwnd,
        string LocationName,
        string LocationUrl,
        string SelfPath,
        string ParentPath,
        string? Error);

    public static List<ShellWindowInfo> GetWindows()
    {
        var list = new List<ShellWindowInfo>();
        object? app = null;
        try
        {
            app = CreateShellApplication();
            dynamic windows = ((dynamic)app).Windows();
            int count = (int)windows.Count;
            for (int i = 0; i < count; i++) list.Add(Read((dynamic)windows.Item(i)));
        }
        catch (Exception ex)
        {
            list.Add(new ShellWindowInfo(-1, "", "", "", "", Describe(ex)));
        }
        finally
        {
            Release(app);
        }
        return list;
    }

    public static ShellWindowInfo? GetWindow(int hwnd)
    {
        object? app = null;
        try
        {
            app = CreateShellApplication();
            dynamic windows = ((dynamic)app).Windows();
            int count = (int)windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic w = windows.Item(i);
                int candidate = (int)w.HWND;
                if (candidate == hwnd) return Read(w);
            }
            return null;
        }
        finally
        {
            Release(app);
        }
    }

    private static object CreateShellApplication()
    {
        var type = Type.GetTypeFromProgID("Shell.Application")
                   ?? throw new InvalidOperationException("Shell.Application ProgID is not registered");
        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException("could not create Shell.Application");
    }

    private static ShellWindowInfo Read(dynamic w)
    {
        int hwnd = -1;
        string name = "", url = "", self = "", parent = "";
        try
        {
            hwnd = (int)w.HWND;
            name = (string)(w.LocationName ?? "");
            url = (string)(w.LocationURL ?? "");
            dynamic folder = w.Document.Folder;
            self = (string)(folder.Self.Path ?? "");
            try
            {
                dynamic pf = folder.ParentFolder;
                parent = pf is null ? "" : (string)(pf.Self.Path ?? "");
            }
            catch (Exception ex) { parent = "<" + Describe(ex) + ">"; }
        }
        catch (Exception ex)
        {
            return new ShellWindowInfo(hwnd, name, url, self, parent, Describe(ex));
        }
        return new ShellWindowInfo(hwnd, name, url, self, parent, null);
    }

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
    }
}
