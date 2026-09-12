using ExplorerEverythingSearch.Core.Shell;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>
/// The classifier is fed with the strings a live Windows 11 Explorer reports, so the cases below are the
/// observed values (including the typographic quotes of the localized search result view name).
/// </summary>
public sealed class ShellLocationClassifierTests
{
    private const string ThisPcUpperCase = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const string HomeUpperCase = "::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}";
    private const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    [Fact]
    public void This_pc_is_recognised_regardless_of_the_case_of_the_folder_id()
    {
        var location = ShellLocationClassifier.Classify(ThisPcUpperCase, "This PC");

        Assert.Equal(ShellLocationKind.ThisPc, location.Kind);
        Assert.Equal(ThisPcUpperCase, location.RawLocation);
        Assert.Equal(ThisPcUpperCase, location.FolderId);
        Assert.Equal("This PC", location.DisplayName);
        Assert.Null(location.SearchScopeHint);
    }

    [Fact]
    public void Home_is_recognised_as_its_own_kind()
    {
        var location = ShellLocationClassifier.Classify(HomeUpperCase, "Home");

        Assert.Equal(ShellLocationKind.Home, location.Kind);
        Assert.Equal(HomeUpperCase, location.FolderId);
        Assert.Equal("Home", location.DisplayName);
        Assert.Null(location.SearchScopeHint);
    }

    [Theory]
    [InlineData(@"D:\ASUS\Downloads")]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\folder")]
    public void File_system_locations_are_classified_as_such(string selfPath)
    {
        var location = ShellLocationClassifier.Classify(selfPath, "name");

        Assert.Equal(ShellLocationKind.FileSystemPath, location.Kind);
        Assert.Equal(selfPath, location.RawLocation);
        Assert.Null(location.FolderId);
        Assert.Equal("name", location.DisplayName);
        Assert.Null(location.SearchScopeHint);
    }

    [Fact]
    public void A_search_result_view_is_recognised_and_its_scope_hint_is_extracted()
    {
        const string selfPath = "\u201csmokedata\u201d\u4e2d\u7684\u641c\u7d22\u7ed3\u679c&idletest";

        var location = ShellLocationClassifier.Classify(selfPath, "Search results in smokedata");

        Assert.Equal(ShellLocationKind.SearchResults, location.Kind);
        Assert.Equal(selfPath, location.RawLocation);
        Assert.Null(location.FolderId);
        Assert.Equal("Search results in smokedata", location.DisplayName);
        Assert.Equal("smokedata", location.SearchScopeHint);
    }

    [Fact]
    public void A_search_result_view_written_with_plain_quotes_is_understood_as_well()
    {
        var location = ShellLocationClassifier.Classify("\"Projects\" search results&report", "x");

        Assert.Equal(ShellLocationKind.SearchResults, location.Kind);
        Assert.Equal("Projects", location.SearchScopeHint);
    }

    [Fact]
    public void A_search_result_view_without_quotes_has_no_scope_hint()
    {
        var location = ShellLocationClassifier.Classify("Search results&report", "x");

        Assert.Equal(ShellLocationKind.SearchResults, location.Kind);
        Assert.Null(location.SearchScopeHint);
    }

    [Fact]
    public void Other_virtual_folders_are_not_mistaken_for_a_searchable_location()
    {
        var location = ShellLocationClassifier.Classify(RecycleBin, "Recycle Bin");

        Assert.Equal(ShellLocationKind.OtherVirtualFolder, location.Kind);
        Assert.Equal(RecycleBin, location.FolderId);
        Assert.Equal("Recycle Bin", location.DisplayName);

        var unknownVirtualFolder = ShellLocationClassifier.Classify("::{DEADBEEF-0000-0000-0000-000000000000}", null!);
        Assert.Equal(ShellLocationKind.OtherVirtualFolder, unknownVirtualFolder.Kind);
        Assert.Equal("::{DEADBEEF-0000-0000-0000-000000000000}", unknownVirtualFolder.FolderId);
        Assert.Equal(string.Empty, unknownVirtualFolder.DisplayName);
    }

    [Fact]
    public void An_empty_or_missing_location_is_unknown()
    {
        foreach (var selfPath in new[] { null, string.Empty, "   " })
        {
            var location = ShellLocationClassifier.Classify(selfPath, "whatever");

            Assert.Equal(ShellLocationKind.Unknown, location.Kind);
            Assert.Equal(string.Empty, location.RawLocation);
            Assert.Null(location.FolderId);
            Assert.Null(location.SearchScopeHint);
            Assert.Equal("whatever", location.DisplayName);
        }
    }

    [Fact]
    public void The_raw_location_is_trimmed_before_classification()
    {
        var location = ShellLocationClassifier.Classify("  ::{20d04fe0-3aea-1069-a2d8-08002b30309d}  ", "This PC");

        Assert.Equal(ShellLocationKind.ThisPc, location.Kind);
        Assert.Equal("::{20d04fe0-3aea-1069-a2d8-08002b30309d}", location.RawLocation);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("C:", true)]
    [InlineData(@"d:\folder", true)]
    [InlineData(@"\\server\share", false)]
    [InlineData("::{20d04fe0-3aea-1069-a2d8-08002b30309d}", false)]
    [InlineData("C", false)]
    [InlineData("1:\\x", false)]
    [InlineData("", false)]
    [InlineData(@"C:x", false)]
    public void IsDrivePath_accepts_drive_roots_and_drive_paths_only(string value, bool expected)
    {
        Assert.Equal(expected, ShellLocationClassifier.IsDrivePath(value));
    }

    [Fact]
    public void The_folder_id_constants_match_the_values_reported_by_windows()
    {
        Assert.Equal("::{20d04fe0-3aea-1069-a2d8-08002b30309d}", ShellLocationClassifier.ThisPcFolderId);
        Assert.Equal("::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", ShellLocationClassifier.HomeFolderId);
    }
}
