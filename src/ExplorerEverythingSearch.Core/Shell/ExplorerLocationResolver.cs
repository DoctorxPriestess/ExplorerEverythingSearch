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
    private readonly Diagnostics.AppLogger? _logger;

    public ShellAutomationLocationResolver(Diagnostics.AppLogger? logger = null) => _logger = logger;

    /// <summary>
    /// Finds the Shell browser window that belongs to <paramref name="hwnd"/>.
    ///
    /// Every entry is handled on its own: ShellWindows keeps dead entries behind after an Explorer
    /// process is killed (they come back as null items), and reading the HWND of such an entry throws
    /// a binder exception. Letting one of them escape would hide the real window that follows it in
    /// the collection - which would make *every* search on the machine unresolvable - so a bad entry is
    /// skipped and only its own resolution is lost.
    /// </summary>
    public ExplorerWindowLocation? TryResolve(long hwnd)
    {
        object? app = null;
        try
        {
            app = CreateShellApplication();
            dynamic windows = ((dynamic)app).Windows();
            var count = (int)windows.Count;
            var stale = 0;

            for (var i = 0; i < count; i++)
            {
                object? item;
                try
                {
                    item = windows.Item(i);
                }
                catch (Exception ex)
                {
                    stale++;
                    _logger?.Debug($"ShellWindows[{i}] could not be read ({Describe(ex)}); skipped");
                    continue;
                }

                if (item is null)
                {
                    stale++;
                    _logger?.Debug($"ShellWindows[{i}] is a stale entry left behind by a killed Explorer process; skipped");
                    continue;
                }

                try
                {
                    dynamic window = item;
                    if ((int)window.HWND != hwnd) continue;
                    return Read(window);
                }
                catch (Exception ex)
                {
                    stale++;
                    _logger?.Debug($"ShellWindows[{i}] could not be inspected ({Describe(ex)}); skipped");
                }
            }

            if (stale > 0)
                _logger?.Debug($"{stale} of {count} ShellWindows entries were stale while looking for hwnd={hwnd}");
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
                try
                {
                    object? item = windows.Item(i);
                    if (item is null)
                    {
                        // A killed Explorer process leaves a null entry behind; report it instead of
                        // letting the binder exception end the whole enumeration.
                        list.Add(new ExplorerWindowLocation(-1, string.Empty, string.Empty, string.Empty, "stale ShellWindows entry"));
                        continue;
                    }
                    list.Add(Read((dynamic)item));
                }
                catch (Exception ex)
                {
                    list.Add(new ExplorerWindowLocation(-1, string.Empty, string.Empty, string.Empty, Describe(ex)));
                }
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
