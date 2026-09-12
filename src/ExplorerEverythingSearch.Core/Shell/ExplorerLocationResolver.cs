using System.Runtime.InteropServices;

namespace ExplorerEverythingSearch.Core.Shell;

/// <summary>Raw location information for one Explorer window.</summary>
public sealed record ExplorerWindowLocation(
    long Hwnd,
    string LocationName,
    string LocationUrl,
    string SelfPath,
    string? Error);

/// <summary>Reads the current location of an Explorer window. Implementations must be used on the STA thread.</summary>
public interface IExplorerLocationResolver
{
    ExplorerWindowLocation? TryResolve(long hwnd);

    /// <summary>Enumerates every open Shell browser window (used for diagnostics).</summary>
    IReadOnlyList<ExplorerWindowLocation> Enumerate();
}

/// <summary>
/// Resolves Explorer window locations through the Shell automation object model.
///
/// Only the dual (IDispatch marshaled) interfaces can be used from an external process: vtable
/// interfaces such as IShellBrowser/IFolderView are not marshaled across the Explorer process
/// boundary, so the folder is read as <c>Document.Folder.Self.Path</c> - the same value Explorer's
/// own address bar is built from, and therefore the real (possibly relocated) file system path.
/// </summary>
public sealed class ShellAutomationLocationResolver : IExplorerLocationResolver
{
    public ExplorerWindowLocation? TryResolve(long hwnd)
    {
        object? app = null;
        try
        {
            app = CreateShellApplication();
            dynamic windows = ((dynamic)app).Windows();
            var count = (int)windows.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic window = windows.Item(i);
                if ((int)window.HWND != hwnd) continue;
                return Read(window);
            }
            return null;
        }
        catch (Exception ex)
        {
            return new ExplorerWindowLocation(hwnd, string.Empty, string.Empty, string.Empty, Describe(ex));
        }
        finally
        {
            Release(app);
        }
    }

    public IReadOnlyList<ExplorerWindowLocation> Enumerate()
    {
        var list = new List<ExplorerWindowLocation>();
        object? app = null;
        try
        {
            app = CreateShellApplication();
            dynamic windows = ((dynamic)app).Windows();
            var count = (int)windows.Count;
            for (var i = 0; i < count; i++)
            {
                try { list.Add(Read((dynamic)windows.Item(i))); }
                catch (Exception ex) { list.Add(new ExplorerWindowLocation(-1, string.Empty, string.Empty, string.Empty, Describe(ex))); }
            }
        }
        catch (Exception ex)
        {
            list.Add(new ExplorerWindowLocation(-1, string.Empty, string.Empty, string.Empty, Describe(ex)));
        }
        finally
        {
            Release(app);
        }
        return list;
    }

    private static ExplorerWindowLocation Read(dynamic window)
    {
        var hwnd = -1L;
        try
        {
            hwnd = (int)window.HWND;
            var name = (string)(window.LocationName ?? string.Empty);
            var url = (string)(window.LocationURL ?? string.Empty);
            dynamic folder = window.Document.Folder;
            var self = (string)(folder.Self.Path ?? string.Empty);
            return new ExplorerWindowLocation(hwnd, name, url, self, null);
        }
        catch (Exception ex)
        {
            return new ExplorerWindowLocation(hwnd, string.Empty, string.Empty, string.Empty, Describe(ex));
        }
    }

    private static object CreateShellApplication()
    {
        var type = Type.GetTypeFromProgID("Shell.Application")
                   ?? throw new InvalidOperationException("Shell.Application is not registered");
        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException("Shell.Application could not be created");
    }

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.ReleaseComObject(comObject); } catch { }
        }
    }
}
