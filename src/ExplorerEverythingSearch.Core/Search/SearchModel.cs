namespace ExplorerEverythingSearch.Core.Search;

/// <summary>What caused a search to be submitted.</summary>
public enum TriggerType
{
    /// <summary>The user pressed Enter in the Explorer search box: submit immediately, no debounce.</summary>
    Enter,

    /// <summary>The user stopped typing and the configured auto search delay elapsed.</summary>
    IdleTimeout,
}

/// <summary>Semantic scope of an Explorer search.</summary>
public enum SearchScopeKind
{
    /// <summary>Search inside the current directory and all of its subdirectories.</summary>
    CurrentDirectoryAndSubdirectories,

    /// <summary>Explorer is showing "This PC": the equivalent Everything scope is every volume.</summary>
    AllVolumes,

    /// <summary>Explorer is showing "Home": the union of the user's known folders.</summary>
    KnownFoldersUnion,
}

/// <summary>Where the resolved directory came from (kept for logging and troubleshooting).</summary>
public enum ScopeResolutionSource
{
    /// <summary>Resolved live from the Shell while submitting.</summary>
    LiveShellLocation,

    /// <summary>Explorer is showing a search result view; the scope is the folder it was started from.</summary>
    WindowScopeBeforeSearch,

    /// <summary>Expanded from the Shell known folders (Home).</summary>
    KnownFolders,

    /// <summary>This PC.</summary>
    AllVolumes,
}

/// <summary>The concrete Everything search scope that a <see cref="SearchRequest"/> maps to.</summary>
public sealed record ResolvedScope(
    SearchScopeKind Kind,
    IReadOnlyList<string> Paths,
    ScopeResolutionSource Source);

/// <summary>One submitted search. Immutable, and always bound to the Explorer window that triggered it.</summary>
public sealed record SearchRequest(
    long SourceExplorerHwnd,
    string SearchText,
    ResolvedScope Scope,
    TriggerType Trigger,
    DateTimeOffset Timestamp)
{
    /// <summary>Primary directory (empty for <see cref="SearchScopeKind.AllVolumes"/>).</summary>
    public string ResolvedPath => Scope.Paths.Count > 0 ? Scope.Paths[0] : string.Empty;

    public bool HasSameTarget(SearchRequest other)
        => SourceExplorerHwnd == other.SourceExplorerHwnd
           && Scope.Kind == other.Scope.Kind
           && string.Equals(SearchText, other.SearchText, StringComparison.Ordinal)
           && Scope.Paths.SequenceEqual(other.Scope.Paths, StringComparer.OrdinalIgnoreCase);
}
