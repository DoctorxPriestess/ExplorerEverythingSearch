using System.Runtime.InteropServices;

namespace ExplorerEverythingSearch.Core.Shell;

/// <summary>Resolves the real file system paths of the user's known folders.</summary>
public sealed class KnownFolderResolver
{
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    public static readonly Guid Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    public static readonly Guid Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    public static readonly Guid Music = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    public static readonly Guid Profile = new("5E6C858F-0E22-4760-9AFE-EA331CDA2176");

    /// <summary>
    /// The folders covered by the Explorer "Home" search scope. The paths always come from the Shell,
    /// so a user who relocated "Downloads" to another drive gets the relocated path.
    /// </summary>
    public static readonly IReadOnlyList<Guid> HomeSearchScopeFolders = new[]
    {
        Desktop, Documents, Downloads, Pictures, Videos, Music,
    };

    /// <summary>Returns the folders of the Home search scope that currently exist, without duplicates.</summary>
    public IReadOnlyList<string> GetHomeSearchScopeFolders()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in HomeSearchScopeFolders)
        {
            var path = TryGetPath(id);
            if (path is null) continue;
            if (!seen.Add(path)) continue;
            result.Add(path);
        }
        return result;
    }

    /// <summary>Resolves one known folder; returns null when unavailable (never throws).</summary>
    public static string? TryGetPath(Guid folderId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var id = folderId;
            Interop.NativeMethods.SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out buffer);
            if (buffer == IntPtr.Zero) return null;
            var path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                try { Interop.NativeMethods.CoTaskMemFree(buffer); } catch { }
            }
        }
    }
}
