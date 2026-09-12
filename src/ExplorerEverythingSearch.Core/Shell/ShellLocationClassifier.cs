namespace ExplorerEverythingSearch.Core.Shell;

/// <summary>Kind of Shell location an Explorer window is showing.</summary>
public enum ShellLocationKind
{
    /// <summary>A real file system path (local drive or UNC share).</summary>
    FileSystemPath,

    /// <summary>The "This PC" virtual folder.</summary>
    ThisPc,

    /// <summary>The Windows 11 "Home" virtual folder.</summary>
    Home,

    /// <summary>An Explorer search result view (its scope is the folder the search started from).</summary>
    SearchResults,

    /// <summary>Any other virtual folder without a file system scope (Recycle Bin, Network, Control Panel, ...).</summary>
    OtherVirtualFolder,

    /// <summary>Nothing usable could be parsed.</summary>
    Unknown,
}

/// <param name="Kind">Classification of the location.</param>
/// <param name="RawLocation">The parsing name exactly as reported by the Shell.</param>
/// <param name="FolderId">For virtual folders: the <c>::{GUID}</c> identifier.</param>
/// <param name="DisplayName">Explorer's display name for the location.</param>
/// <param name="SearchScopeHint">
/// For search result views: the display name of the folder the search was started from (extracted from
/// the synthetic location, used only as a logging/confidence hint).
/// </param>
public sealed record ShellLocation(
    ShellLocationKind Kind,
    string RawLocation,
    string? FolderId,
    string DisplayName,
    string? SearchScopeHint);

/// <summary>
/// Classifies the string that the Shell reports as the current folder of an Explorer window.
/// Values are taken verbatim from a live Windows 11 (build 26200) session.
/// </summary>
public static class ShellLocationClassifier
{
    /// <summary>CLSID_MyComputer - Explorer's "This PC".</summary>
    public const string ThisPcFolderId = "::{20d04fe0-3aea-1069-a2d8-08002b30309d}";

    /// <summary>Windows 11 "Home" folder.</summary>
    public const string HomeFolderId = "::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";

    public static ShellLocation Classify(string? selfPath, string? displayName)
    {
        var raw = (selfPath ?? string.Empty).Trim();
        var name = displayName ?? string.Empty;

        if (raw.Length == 0) return new ShellLocation(ShellLocationKind.Unknown, raw, null, name, null);

        if (raw.StartsWith(@"\\", StringComparison.Ordinal))
            return new ShellLocation(ShellLocationKind.FileSystemPath, raw, null, name, null);

        if (IsDrivePath(raw))
            return new ShellLocation(ShellLocationKind.FileSystemPath, raw, null, name, null);

        if (raw.StartsWith("::{", StringComparison.Ordinal))
        {
            if (string.Equals(raw, ThisPcFolderId, StringComparison.OrdinalIgnoreCase))
                return new ShellLocation(ShellLocationKind.ThisPc, raw, raw, name, null);
            if (string.Equals(raw, HomeFolderId, StringComparison.OrdinalIgnoreCase))
                return new ShellLocation(ShellLocationKind.Home, raw, raw, name, null);
            return new ShellLocation(ShellLocationKind.OtherVirtualFolder, raw, raw, name, null);
        }

        // Anything else is a synthetic (name based) location. In an Explorer browser window this is a
        // search result view: "<folder display name>中的搜索结果&<query>" / "Search results in <folder>&<query>".
        var hint = ExtractQuotedScopeHint(raw);
        return new ShellLocation(ShellLocationKind.SearchResults, raw, null, name, hint);
    }

    /// <summary>True for "C:\..." and for a bare "C:".</summary>
    public static bool IsDrivePath(string value)
    {
        if (value.Length < 2) return false;
        if (!char.IsLetter(value[0])) return false;
        if (value[1] != ':') return false;
        return value.Length == 2 || value[2] == '\\';
    }

    private static string? ExtractQuotedScopeHint(string raw)
    {
        // Windows uses typographic quotes in some locales and plain quotes in others.
        foreach (var (open, close) in new[] { ('\u201c', '\u201d'), ('"', '"'), ('\u300c', '\u300d') })
        {
            var start = raw.IndexOf(open);
            if (start < 0) continue;
            var end = raw.IndexOf(close, start + 1);
            if (end > start + 1) return raw.Substring(start + 1, end - start - 1);
        }
        return null;
    }
}
