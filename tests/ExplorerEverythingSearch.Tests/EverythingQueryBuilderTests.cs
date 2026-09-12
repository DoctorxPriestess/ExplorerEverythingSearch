using ExplorerEverythingSearch.Core.Search;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>
/// Query building is the part that decides what Everything actually searches for, so the exact command
/// line is asserted - including the quoting rules and the Everything 1.4 fallback.
/// </summary>
public sealed class EverythingQueryBuilderTests
{
    private static readonly EverythingCapabilities V15 = new(new Version(1, 5, 0, 1423));
    private static readonly EverythingCapabilities V14 = new(new Version(1, 4, 1, 1009));
    private static readonly EverythingCapabilities UnknownVersion = EverythingCapabilities.Unknown;

    private static SearchRequest Request(string text, IReadOnlyList<string> paths, SearchScopeKind kind = SearchScopeKind.CurrentDirectoryAndSubdirectories)
        => new(4711, text, new ResolvedScope(kind, paths, ScopeResolutionSource.LiveShellLocation), TriggerType.Enter, DateTimeOffset.Now);

    [Fact]
    public void A_single_directory_scope_uses_path_and_the_literal_rest_argument()
    {
        var request = Request("report", new[] { @"D:\ASUS\Downloads" });

        Assert.True(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: true, out var invocation, out var error));

        Assert.Null(error);
        Assert.NotNull(invocation);
        Assert.Equal(@"-no-new-window -path D:\ASUS\Downloads -s* report", invocation!.RawCommandLine);
        Assert.Equal("report", invocation.Query);
        Assert.Equal(@"D:\ASUS\Downloads", invocation.ScopeDescription);
    }

    [Fact]
    public void A_new_window_is_requested_when_the_existing_window_must_not_be_reused()
    {
        var request = Request("report", new[] { @"D:\ASUS\Downloads" });

        Assert.True(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: false, out var invocation, out _));

        Assert.StartsWith("-new-window -path ", invocation!.RawCommandLine);
        Assert.DoesNotContain("-no-new-window", invocation.RawCommandLine);
    }

    [Theory]
    [InlineData(@"D:\My Docs", @"-path ""D:\My Docs""")]
    [InlineData(@"D:\a&b", @"-path ""D:\a&b""")]
    [InlineData(@"D:\x(y)", @"-path ""D:\x(y)""")]
    [InlineData(@"D:\y)z", @"-path ""D:\y)z""")]
    [InlineData(@"D:\plain\dir", @"-path D:\plain\dir")]
    public void Paths_are_quoted_exactly_when_the_command_line_needs_it(string path, string expectedFragment)
    {
        var request = Request("report", new[] { path });

        Assert.True(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: true, out var invocation, out _));

        Assert.Contains(expectedFragment, invocation!.RawCommandLine);
    }

    [Fact]
    public void A_drive_root_stays_a_plain_path_argument()
    {
        var request = Request("report", new[] { @"D:\" });

        Assert.True(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: true, out var invocation, out _));

        Assert.Equal(@"-no-new-window -path D:\ -s* report", invocation!.RawCommandLine);
        Assert.Equal(@"D:\", invocation.ScopeDescription);
    }

    [Fact]
    public void All_volumes_scope_never_restricts_the_search_to_a_path()
    {
        var request = Request("report", Array.Empty<string>(), SearchScopeKind.AllVolumes);

        Assert.True(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: true, out var invocation, out var error));

        Assert.Null(error);
        Assert.Equal("-no-new-window -s* report", invocation!.RawCommandLine);
        Assert.DoesNotContain("-path", invocation.RawCommandLine);
        Assert.Equal("<all volumes>", invocation.ScopeDescription);
        Assert.Equal("report", invocation.Query);
    }

    [Fact]
    public void A_multi_folder_scope_is_expressed_as_an_ancestor_union()
    {
        var paths = new[] { @"D:\My Docs", @"E:\My Data" };
        var request = Request("report", paths, SearchScopeKind.KnownFoldersUnion);

        Assert.True(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: true, out var invocation, out var error));

        Assert.Null(error);
        var union = "<ancestor:\"D:\\My Docs\"|ancestor:\"E:\\My Data\">";
        Assert.Equal($"report {union}", invocation!.Query);
        Assert.Equal($"-no-new-window -s* report {union}", invocation.RawCommandLine);
        Assert.Equal(@"D:\My Docs | E:\My Data", invocation.ScopeDescription);
    }

    [Fact]
    public void Several_directories_without_the_home_classification_still_produce_the_same_union()
    {
        var paths = new[] { @"D:\My Docs", @"E:\My Data" };
        var asHome = Request("report", paths, SearchScopeKind.KnownFoldersUnion);
        var asDirectories = Request("report", paths, SearchScopeKind.CurrentDirectoryAndSubdirectories);

        Assert.True(EverythingQueryBuilder.TryBuild(asHome, V15, true, out var homeInvocation, out _));
        Assert.True(EverythingQueryBuilder.TryBuild(asDirectories, V15, true, out var directoryInvocation, out _));

        Assert.Equal(homeInvocation!.RawCommandLine, directoryInvocation!.RawCommandLine);
        Assert.Equal(homeInvocation.Query, directoryInvocation.Query);
    }

    [Fact]
    public void A_single_folder_union_uses_the_ancestor_function()
    {
        var request = Request("report", new[] { @"D:\My Docs" }, SearchScopeKind.KnownFoldersUnion);

        Assert.True(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: true, out var invocation, out _));

        Assert.Equal("report ancestor:\"D:\\My Docs\"", invocation!.Query);
        Assert.Contains("-s* report ancestor:\"D:\\My Docs\"", invocation.RawCommandLine);
    }

    [Fact]
    public void Everything_1_4_falls_back_to_the_quoted_s_argument()
    {
        var request = Request("report", new[] { @"D:\ASUS\Downloads" });
        var multiFolder = Request("report", new[] { @"D:\My Docs", @"E:\My Data" }, SearchScopeKind.KnownFoldersUnion);

        Assert.True(EverythingQueryBuilder.TryBuild(request, V14, reuseWindow: true, out var invocation, out _));
        Assert.Equal(@"-no-new-window -path D:\ASUS\Downloads -s ""report""", invocation!.RawCommandLine);
        Assert.DoesNotContain("-s*", invocation.RawCommandLine);

        // A union needs the 1.5 ancestor: function, so a 1.4 build must be refused instead of
        // producing a query that searches for the literal text "ancestor:...".
        Assert.False(EverythingQueryBuilder.TryBuild(multiFolder, V14, reuseWindow: true, out var refused, out var error));
        Assert.Null(refused);
        Assert.Contains("Everything 1.5", error);

        // An unknown version is treated like the safe 1.4 syntax.
        Assert.True(EverythingQueryBuilder.TryBuild(request, UnknownVersion, reuseWindow: true, out var unknownInvocation, out _));
        Assert.Equal(invocation.RawCommandLine, unknownInvocation!.RawCommandLine);
    }

    [Fact]
    public void Quotes_inside_the_search_text_are_escaped_for_everything_1_4()
    {
        const string text = "he said \"hi\"";
        var request = Request(text, new[] { @"D:\ASUS\Downloads" });

        Assert.True(EverythingQueryBuilder.TryBuild(request, V14, reuseWindow: true, out var invocation, out _));

        // Everything 1.4 doubles a literal quote inside a -s value.
        Assert.EndsWith("-s \"he said \"\"\"hi\"\"\"\"", invocation!.RawCommandLine);
        Assert.Equal(text, invocation.Query);
    }

    [Fact]
    public void Quotes_inside_the_search_text_survive_the_literal_rest_argument_unchanged()
    {
        const string text = "he said \"hi\" -path C:\\ignored";
        var request = Request(text, new[] { @"D:\ASUS\Downloads" });

        Assert.True(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: true, out var invocation, out _));

        // -s* hands the rest of the command line over verbatim, so no .NET style escaping is applied.
        Assert.Equal(@"-no-new-window -path D:\ASUS\Downloads -s* he said ""hi"" -path C:\ignored", invocation!.RawCommandLine);
        Assert.EndsWith("-s* " + text, invocation.RawCommandLine);
        Assert.Equal(text, invocation.Query);
    }

    [Fact]
    public void A_single_folder_union_is_rejected_on_a_build_without_the_ancestor_function()
    {
        // Guarded defect (found and fixed): TryBuildUnion used to check SupportsAncestorFunction only
        // for two or more folders, so a Home scope that expanded to a single folder produced the 1.5
        // only ancestor: function for a 1.4 build. Everything 1.4 then treats it as literal search text
        // and the search returns nothing. Every union form uses ancestor:, so all of them are rejected.
        var request = Request("report", new[] { @"D:\My Docs" }, SearchScopeKind.KnownFoldersUnion);

        Assert.False(EverythingQueryBuilder.TryBuild(request, V14, reuseWindow: true, out var invocation, out var error));

        Assert.Null(invocation);
        Assert.Contains("Everything 1.5", error);
    }

    [Fact]
    public void A_single_folder_union_still_uses_the_ancestor_function_on_1_5()
    {
        var request = Request("report", new[] { @"D:\My Docs" }, SearchScopeKind.KnownFoldersUnion);

        Assert.True(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: true, out var invocation, out var error));

        Assert.Null(error);
        Assert.Equal("report ancestor:\"D:\\My Docs\"", invocation!.Query);
    }

    [Fact]
    public void An_empty_search_text_is_rejected()
    {
        var request = Request(string.Empty, new[] { @"D:\ASUS\Downloads" });

        Assert.False(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: true, out var invocation, out var error));

        Assert.Null(invocation);
        Assert.Equal("search text is empty", error);
    }

    [Fact]
    public void A_union_without_directories_is_rejected()
    {
        var request = Request("report", Array.Empty<string>(), SearchScopeKind.KnownFoldersUnion);

        Assert.False(EverythingQueryBuilder.TryBuild(request, V15, reuseWindow: true, out var invocation, out var error));

        Assert.Null(invocation);
        Assert.Equal("search scope contains no directory", error);
    }

    [Fact]
    public void DescribeQuery_includes_the_ancestor_scope_for_home_searches()
    {
        Assert.Equal("report", EverythingQueryBuilder.DescribeQuery(Request("report", Array.Empty<string>(), SearchScopeKind.AllVolumes)));
        Assert.Equal(@"report ancestor:D:\a ancestor:E:\b",
            EverythingQueryBuilder.DescribeQuery(Request("report", new[] { @"D:\a", @"E:\b" }, SearchScopeKind.KnownFoldersUnion)));
        Assert.Equal(@"report",
            EverythingQueryBuilder.DescribeQuery(Request("report", new[] { @"D:\a" }, SearchScopeKind.CurrentDirectoryAndSubdirectories)));
    }

    [Fact]
    public void Capabilities_report_the_features_of_the_connected_build()
    {
        Assert.True(V15.SupportsLiteralRestArgument);
        Assert.True(V15.SupportsAncestorFunction);
        Assert.True(V15.AtLeast(1, 5));
        Assert.False(V15.AtLeast(1, 6));
        Assert.False(V14.SupportsLiteralRestArgument);
        Assert.False(V14.SupportsAncestorFunction);
        Assert.True(V14.AtLeast(1, 4));
        Assert.False(V14.AtLeast(1, 5));
        Assert.False(UnknownVersion.SupportsLiteralRestArgument);
        Assert.False(UnknownVersion.SupportsAncestorFunction);
        Assert.Equal("unknown", UnknownVersion.Display);
        Assert.Equal("1.5.0.1423", V15.Display);
    }
}
