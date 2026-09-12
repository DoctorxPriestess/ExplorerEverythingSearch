using System.Collections.Concurrent;

namespace ExplorerEverythingSearch.Core.Shell;

/// <summary>A folder - or another searchable location - that an Explorer window was showing.</summary>
public sealed record TrackedWindowScope(
    string Path,
    string DisplayName,
    DateTimeOffset ObservedAt,
    string Origin,
    ShellLocationKind Kind = ShellLocationKind.FileSystemPath);

/// <summary>
/// Remembers, per Explorer window, the last real location it was showing.
///
/// Why this exists: Explorer turns the window into a search result view as soon as a search is
/// committed (either by Enter or by its own ~800 ms auto search). At that moment the Shell location
/// becomes a synthetic name (<c>"folder"中的搜索结果&amp;query</c>) and the folder is no longer
/// directly resolvable - while the search scope *is* that folder. The scope is therefore captured
/// from live Shell resolutions, which happen while the window still shows a real location (when its
/// search box is found, when it navigates, and on the first keystroke of a query).
/// "This PC" and "Home" are captured the same way, because a search started there has to cover all
/// volumes or the known folders. A window whose scope is unknown is reported as unresolvable instead
/// of being searched in the wrong place.
/// </summary>
public sealed class WindowScopeTracker
{
    private readonly ConcurrentDictionary<long, TrackedWindowScope> _scopes = new();

    public void Remember(long hwnd, string path, string displayName, string origin, ShellLocationKind kind = ShellLocationKind.FileSystemPath)
        => _scopes[hwnd] = new TrackedWindowScope(path, displayName, DateTimeOffset.Now, origin, kind);

    public void Forget(long hwnd) => _scopes.TryRemove(hwnd, out _);

    public TrackedWindowScope? Get(long hwnd) => _scopes.TryGetValue(hwnd, out var scope) ? scope : null;

    public void Clear() => _scopes.Clear();
}
