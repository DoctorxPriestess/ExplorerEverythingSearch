using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Search;

namespace ExplorerEverythingSearch.Core.Shell;

/// <summary>Why a search scope could not be resolved.</summary>
public enum ScopeFailureReason
{
    None,

    /// <summary>The Explorer window no longer exists (closed while typing).</summary>
    ExplorerWindowNotFound,

    /// <summary>The Shell did not report a location for the window.</summary>
    LocationUnavailable,

    /// <summary>The window shows a virtual folder without a file system search scope.</summary>
    UnsupportedShellNamespace,

    /// <summary>Explorer is showing search results and the folder the search started from is unknown.</summary>
    SearchScopeUnknown,

    /// <summary>The directory does not exist (any more).</summary>
    DirectoryUnavailable,
}

/// <summary>Outcome of resolving the search scope for one Explorer window.</summary>
public sealed record ScopeResolution(
    ResolvedScope? Scope,
    ScopeFailureReason Failure,
    string RawLocation,
    string Detail,
    bool IsSearchResultView,
    ShellLocationKind Kind)
{
    public bool Succeeded => Failure == ScopeFailureReason.None && Scope is not null;

    public static ScopeResolution Failed(ScopeFailureReason reason, string rawLocation, string detail, bool searchView = false, ShellLocationKind kind = ShellLocationKind.Unknown)
        => new(null, reason, rawLocation, detail, searchView, kind);
}

/// <summary>
/// Resolves "the real file system directory that this Explorer window is searching in" for every
/// submitted search. The resolution is performed on every submit; nothing is read from the config
/// and nothing is inferred from the foreground window.
/// </summary>
public sealed class SearchScopeResolver
{
    private readonly IExplorerLocationResolver _locations;
    private readonly KnownFolderResolver _knownFolders;
    private readonly WindowScopeTracker _tracker;
    private readonly AppLogger _logger;
    private readonly Func<string, bool> _directoryExists;

    public SearchScopeResolver(
        IExplorerLocationResolver locations,
        KnownFolderResolver knownFolders,
        WindowScopeTracker tracker,
        AppLogger logger,
        Func<string, bool>? directoryExists = null)
    {
        _locations = locations;
        _knownFolders = knownFolders;
        _tracker = tracker;
        _logger = logger;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    public ScopeResolution Resolve(long hwnd)
    {
        ExplorerWindowLocation? window;
        try
        {
            window = _locations.TryResolve(hwnd);
        }
        catch (Exception ex)
        {
            _logger.Error($"Shell location lookup failed for hwnd={hwnd}", ex);
            return ScopeResolution.Failed(ScopeFailureReason.LocationUnavailable, string.Empty, ex.Message);
        }

        if (window is null)
            return ScopeResolution.Failed(ScopeFailureReason.ExplorerWindowNotFound, string.Empty, $"no Shell window for hwnd={hwnd}");

        if (!string.IsNullOrEmpty(window.Error) && string.IsNullOrEmpty(window.SelfPath))
            return ScopeResolution.Failed(ScopeFailureReason.LocationUnavailable, string.Empty, window.Error);

        var location = ShellLocationClassifier.Classify(window.SelfPath, window.LocationName);
        _logger.Debug($"Shell location hwnd={hwnd} kind={location.Kind} self=\"{location.RawLocation}\"");

        switch (location.Kind)
        {
            case ShellLocationKind.FileSystemPath:
            {
                var path = NormalizeDirectory(location.RawLocation);
                if (path.Length == 0)
                    return ScopeResolution.Failed(ScopeFailureReason.LocationUnavailable, location.RawLocation, "empty file system path");

                if (!IsUncPath(path) && !_directoryExists(path))
                {
                    _logger.Warn($"resolved directory does not exist: \"{path}\"");
                    return ScopeResolution.Failed(ScopeFailureReason.DirectoryUnavailable, location.RawLocation, path);
                }

                // Remember for this window so a later search from the resulting search view stays in scope.
                _tracker.Remember(hwnd, path, location.DisplayName, "live-shell");
                var scope = new ResolvedScope(SearchScopeKind.CurrentDirectoryAndSubdirectories, new[] { path }, ScopeResolutionSource.LiveShellLocation);
                return new ScopeResolution(scope, ScopeFailureReason.None, location.RawLocation, path, false, location.Kind);
            }

            case ShellLocationKind.ThisPc:
            {
                var scope = new ResolvedScope(SearchScopeKind.AllVolumes, Array.Empty<string>(), ScopeResolutionSource.AllVolumes);
                return new ScopeResolution(scope, ScopeFailureReason.None, location.RawLocation, "<all volumes>", false, location.Kind);
            }

            case ShellLocationKind.Home:
            {
                var folders = _knownFolders.GetHomeSearchScopeFolders();
                var existing = folders.Where(f => IsUncPath(f) || _directoryExists(f)).ToArray();
                if (existing.Length == 0)
                    return ScopeResolution.Failed(ScopeFailureReason.SearchScopeUnknown, location.RawLocation, "no known folder of the Home scope could be resolved", false, location.Kind);

                var scope = new ResolvedScope(SearchScopeKind.KnownFoldersUnion, existing, ScopeResolutionSource.KnownFolders);
                return new ScopeResolution(scope, ScopeFailureReason.None, location.RawLocation, string.Join(" | ", existing), false, location.Kind);
            }

            case ShellLocationKind.SearchResults:
            {
                var tracked = _tracker.Get(hwnd);
                if (tracked is null)
                {
                    return ScopeResolution.Failed(
                        ScopeFailureReason.SearchScopeUnknown,
                        location.RawLocation,
                        "Explorer is showing a search result view and the folder it was started from is unknown",
                        true,
                        location.Kind);
                }

                // The search was started from "This PC" or from "Home": those locations have no path,
                // so they are resolved the same way a live window showing them would be.
                if (tracked.Kind == ShellLocationKind.ThisPc)
                {
                    var allVolumes = new ResolvedScope(SearchScopeKind.AllVolumes, Array.Empty<string>(), ScopeResolutionSource.AllVolumes);
                    return new ScopeResolution(allVolumes, ScopeFailureReason.None, location.RawLocation, "<all volumes>", true, location.Kind);
                }

                if (tracked.Kind == ShellLocationKind.Home)
                {
                    var homeFolders = _knownFolders.GetHomeSearchScopeFolders()
                        .Where(f => IsUncPath(f) || _directoryExists(f))
                        .ToArray();
                    if (homeFolders.Length == 0)
                        return ScopeResolution.Failed(ScopeFailureReason.SearchScopeUnknown, location.RawLocation, "no known folder of the Home scope could be resolved", true, location.Kind);

                    var homeScope = new ResolvedScope(SearchScopeKind.KnownFoldersUnion, homeFolders, ScopeResolutionSource.KnownFolders);
                    return new ScopeResolution(homeScope, ScopeFailureReason.None, location.RawLocation, string.Join(" | ", homeFolders), true, location.Kind);
                }

                if (!IsUncPath(tracked.Path) && !_directoryExists(tracked.Path))
                {
                    _tracker.Forget(hwnd);
                    return ScopeResolution.Failed(ScopeFailureReason.DirectoryUnavailable, location.RawLocation, tracked.Path, true, location.Kind);
                }

                if (location.SearchScopeHint is { Length: > 0 } hint)
                {
                    var folderName = Path.GetFileName(tracked.Path.TrimEnd('\\'));
                    var matches = string.Equals(hint, folderName, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(hint, tracked.DisplayName, StringComparison.OrdinalIgnoreCase);
                    if (!matches)
                        _logger.Warn($"search view scope hint \"{hint}\" differs from tracked folder \"{tracked.Path}\" (hwnd={hwnd})");
                }

                var scope = new ResolvedScope(
                    SearchScopeKind.CurrentDirectoryAndSubdirectories,
                    new[] { tracked.Path },
                    ScopeResolutionSource.WindowScopeBeforeSearch);
                return new ScopeResolution(scope, ScopeFailureReason.None, location.RawLocation, tracked.Path, true, location.Kind);
            }

            case ShellLocationKind.OtherVirtualFolder:
                return ScopeResolution.Failed(
                    ScopeFailureReason.UnsupportedShellNamespace,
                    location.RawLocation,
                    string.IsNullOrEmpty(location.DisplayName) ? location.FolderId ?? location.RawLocation : location.DisplayName,
                    false,
                    location.Kind);

            default:
                return ScopeResolution.Failed(ScopeFailureReason.LocationUnavailable, location.RawLocation, "unrecognised Shell location", false, location.Kind);
        }
    }

    public static bool IsUncPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>The Shell location reader this resolver uses; shared with the Explorer monitor.</summary>
    public IExplorerLocationResolver Locations => _locations;

    /// <summary>
    /// Records the folder a window is currently showing, if it really is a file system folder.
    ///
    /// This must be called while the window is still in its live view - right after the search box was
    /// found, after a navigation, and on the first keystroke of a query. Explorer replaces the Shell
    /// location of the window with a synthetic search view as soon as a search is committed (Enter, or
    /// its own automatic search about 800 ms after the last keystroke), and from then on the folder the
    /// search has to be scoped to is only known from this record.
    /// </summary>
    public bool TryRecordLiveScope(long hwnd)
    {
        try
        {
            var window = _locations.TryResolve(hwnd);
            if (window is null) return false;
            if (!string.IsNullOrEmpty(window.Error) && string.IsNullOrEmpty(window.SelfPath)) return false;

            var location = ShellLocationClassifier.Classify(window.SelfPath, window.LocationName);

            // "This PC" and "Home" are searchable locations of their own: remembering them keeps a
            // search that Explorer has already turned into a search view a full volume search or a
            // known folder search instead of an unresolvable scope. A search result view itself is
            // skipped so it cannot overwrite the location the search was started from.
            if (location.Kind is ShellLocationKind.ThisPc or ShellLocationKind.Home)
            {
                var known = _tracker.Get(hwnd);
                _tracker.Remember(hwnd, string.Empty, location.DisplayName, "live-shell", location.Kind);
                if (known is null || known.Kind != location.Kind)
                    _logger.Debug($"window scope recorded hwnd={hwnd} kind={location.Kind}");
                return true;
            }

            if (location.Kind != ShellLocationKind.FileSystemPath) return false;

            var path = NormalizeDirectory(location.RawLocation);
            if (path.Length == 0) return false;
            if (!IsUncPath(path) && !_directoryExists(path))
            {
                _logger.Debug($"window scope not recorded, directory is unavailable: \"{path}\"");
                return false;
            }

            var previous = _tracker.Get(hwnd);
            _tracker.Remember(hwnd, path, location.DisplayName, "live-shell");
            if (previous is null || !string.Equals(previous.Path, path, StringComparison.OrdinalIgnoreCase))
                _logger.Debug($"window scope recorded hwnd={hwnd} path=\"{path}\"");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug($"could not record the folder of hwnd={hwnd}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Removes the trailing separator (except for a drive root) so paths compare and log consistently.</summary>
    public static string NormalizeDirectory(string path)
    {
        var value = (path ?? string.Empty).Trim();
        if (value.Length == 0) return value;
        if (value.Length == 3 && char.IsLetter(value[0]) && value[1] == ':' && value[2] == '\\') return value;
        if (ShellLocationClassifier.IsDrivePath(value) && value.Length == 2) return value + "\\";
        return value.TrimEnd('\\') is { Length: > 0 } trimmed ? trimmed : value;
    }
}
