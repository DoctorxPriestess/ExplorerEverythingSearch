using System.Text;

namespace ExplorerEverythingSearch.Core.Search;

/// <summary>What the connected Everything build supports. Detected through the official IPC version query.</summary>
public sealed record EverythingCapabilities(Version? Version)
{
    public static EverythingCapabilities Unknown { get; } = new((Version?)null);

    public bool AtLeast(int major, int minor)
        => Version is not null && (Version.Major > major || (Version.Major == major && Version.Minor >= minor));

    /// <summary>Everything 1.5 added <c>-s*</c> (rest of the command line is literal search text).</summary>
    public bool SupportsLiteralRestArgument => AtLeast(1, 5);

    /// <summary>Everything 1.5 search function used to restrict a search to a set of folders.</summary>
    public bool SupportsAncestorFunction => AtLeast(1, 5);

    public string Display => Version?.ToString() ?? "unknown";
}

/// <summary>A ready-to-run Everything invocation.</summary>
public sealed record EverythingInvocation(
    string RawCommandLine,
    string Query,
    string ScopeDescription);

/// <summary>
/// Translates "Explorer directory + Explorer search text" into an Everything query.
///
/// Two rules matter for correctness and were verified against Everything 1.5.0.1423b:
///  1. a plain folder path inside the search text is matched as a *path substring*
///     (searching in "D:\a\inside" also matched "D:\a\inside2"), therefore the scope is expressed
///     with <c>-path</c> (single folder) or with the <c>ancestor:</c> search function (union);
///  2. the user text is passed through verbatim as Everything search syntax - it is never
///     concatenated into a query string that could change its meaning.
/// </summary>
public static class EverythingQueryBuilder
{
    public static bool TryBuild(
        SearchRequest request,
        EverythingCapabilities capabilities,
        bool reuseWindow,
        out EverythingInvocation? invocation,
        out string? error)
    {
        invocation = null;
        error = null;

        if (string.IsNullOrEmpty(request.SearchText))
        {
            error = "search text is empty";
            return false;
        }

        var windowSwitch = reuseWindow ? "-no-new-window" : "-new-window";
        var args = new StringBuilder(windowSwitch);

        var query = request.SearchText;
        string scopeDescription;

        switch (request.Scope.Kind)
        {
            case SearchScopeKind.CurrentDirectoryAndSubdirectories when request.Scope.Paths.Count == 1:
            {
                var path = request.Scope.Paths[0];
                args.Append(" -path ").Append(QuotePath(path));
                scopeDescription = path;
                break;
            }

            case SearchScopeKind.CurrentDirectoryAndSubdirectories:
            {
                // Defensive: multiple directories without the Home classification still needs an exact union.
                if (!TryBuildUnion(request.Scope.Paths, capabilities, out var union, out error)) return false;
                query = query + " " + union;
                scopeDescription = string.Join(" | ", request.Scope.Paths);
                break;
            }

            case SearchScopeKind.AllVolumes:
            {
                scopeDescription = "<all volumes>";
                break;
            }

            case SearchScopeKind.KnownFoldersUnion:
            {
                if (!TryBuildUnion(request.Scope.Paths, capabilities, out var union, out error)) return false;
                query = query + " " + union;
                scopeDescription = string.Join(" | ", request.Scope.Paths);
                break;
            }

            default:
                error = $"unsupported search scope {request.Scope.Kind}";
                return false;
        }

        args.Append(' ').Append(AppendSearchArguments(query, capabilities));

        invocation = new EverythingInvocation(args.ToString(), query, scopeDescription);
        return true;
    }

    private static bool TryBuildUnion(
        IReadOnlyList<string> paths,
        EverythingCapabilities capabilities,
        out string union,
        out string? error)
    {
        union = string.Empty;
        error = null;
        if (paths.Count == 0)
        {
            error = "search scope contains no directory";
            return false;
        }
        // Every union form uses the ancestor: search function, including the single folder case, so a
        // build that does not support it must be rejected instead of searching for the literal text
        // "ancestor:...".
        if (!capabilities.SupportsAncestorFunction)
        {
            error = "a folder union search scope requires Everything 1.5 or later";
            return false;
        }
        if (paths.Count == 1)
        {
            union = $"ancestor:{QuotePath(paths[0])}";
            return true;
        }

        union = "<" + string.Join("|", paths.Select(p => $"ancestor:{QuotePath(p)}")) + ">";
        return true;
    }

    /// <summary>
    /// Renders the search text as the last arguments of the command line.
    /// Everything 1.5 can take the rest of the command line verbatim (<c>-s*</c>), which avoids all
    /// quote escaping; older builds use <c>-s "..."</c> where a literal quote is written as <c>"""</c>.
    /// </summary>
    private static string AppendSearchArguments(string query, EverythingCapabilities capabilities)
        => capabilities.SupportsLiteralRestArgument
            ? "-s* " + query
            : "-s \"" + query.Replace("\"", "\"\"\"") + "\"";

    /// <summary>Everything accepts a quoted path; quotes are the only characters that need care.</summary>
    private static string QuotePath(string path)
        => path.Contains(' ') || path.Contains('&') || path.Contains('(') || path.Contains(')')
            ? "\"" + path + "\""
            : path;

    /// <summary>Human readable "everything query" used in logs (path scope included).</summary>
    public static string DescribeQuery(SearchRequest request)
    {
        var query = request.SearchText;
        return request.Scope.Kind switch
        {
            SearchScopeKind.AllVolumes => query,
            SearchScopeKind.KnownFoldersUnion => query + " " + string.Join(" ", request.Scope.Paths.Select(p => $"ancestor:{p}")),
            _ => query,
        };
    }
}
