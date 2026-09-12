using System.Collections.Generic;
using System.Linq;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Search;
using ExplorerEverythingSearch.Core.Shell;
using ExplorerEverythingSearch.Tests.TestSupport;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>
/// The scope resolver decides *where* Everything searches, so every branch is asserted against a fake
/// Shell location resolver and a fake directory existence check - no real Explorer window is involved.
/// </summary>
public sealed class SearchScopeResolverTests
{
    private const long Hwnd = 0x1234;

    private const string LiveDirectory = @"D:\ASUS\Downloads";
    private const string SearchView = "\u201cDownloads\u201d\u4e2d\u7684\u641c\u7d22\u7ed3\u679c&report";
    private const string ThisPcFolderId = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const string HomeFolderId = "::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}";
    private const string RecycleBinFolderId = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    private sealed class Fixture : IDisposable
    {
        private readonly TempWorkspace _workspace = new("scope");

        public Fixture()
        {
            Logger = AppLogger.CreateDisabled(_workspace.Root);
        }

        public AppLogger Logger { get; }

        public FakeLocationResolver Locations { get; } = new();

        public WindowScopeTracker Tracker { get; } = new();

        public KnownFolderResolver KnownFolders { get; } = new();

        public Func<string, bool> DirectoryExists { get; set; } = _ => true;

        public SearchScopeResolver CreateResolver() => new(Locations, KnownFolders, Tracker, Logger, path => DirectoryExists(path));

        public void PointsAt(string selfPath, string displayName = "name", string? error = null)
            => Locations.OnResolve = hwnd => new ExplorerWindowLocation(hwnd, displayName, "url", selfPath, error);

        public void Dispose()
        {
            Logger.Dispose();
            _workspace.Dispose();
        }
    }

    [Fact]
    public void A_live_file_system_folder_scopes_the_search_to_that_folder_and_is_remembered()
    {
        using var fixture = new Fixture();
        fixture.PointsAt(LiveDirectory, "Downloads");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.True(resolution.Succeeded);
        Assert.Equal(ScopeFailureReason.None, resolution.Failure);
        Assert.Equal(SearchScopeKind.CurrentDirectoryAndSubdirectories, resolution.Scope!.Kind);
        Assert.Equal(ScopeResolutionSource.LiveShellLocation, resolution.Scope.Source);
        Assert.Equal(new[] { LiveDirectory }, resolution.Scope.Paths);
        Assert.Equal(LiveDirectory, resolution.Detail);
        Assert.Equal(LiveDirectory, resolution.RawLocation);
        Assert.False(resolution.IsSearchResultView);
        Assert.Equal(ShellLocationKind.FileSystemPath, resolution.Kind);

        // Remembering the folder is what makes a later search from the resulting search view possible.
        var tracked = fixture.Tracker.Get(Hwnd);
        Assert.NotNull(tracked);
        Assert.Equal(LiveDirectory, tracked!.Path);
        Assert.Equal("Downloads", tracked.DisplayName);
        Assert.Equal("live-shell", tracked.Origin);
        Assert.Equal(ShellLocationKind.FileSystemPath, tracked.Kind);
        Assert.True(DateTimeOffset.Now - tracked.ObservedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void The_folder_is_normalized_before_it_is_checked_and_remembered()
    {
        using var fixture = new Fixture();
        var checkedPaths = new List<string>();
        fixture.DirectoryExists = path =>
        {
            checkedPaths.Add(path);
            return true;
        };
        fixture.PointsAt(LiveDirectory + @"\", "Downloads");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.Equal(LiveDirectory, resolution.Detail);
        Assert.Equal(new[] { LiveDirectory }, checkedPaths);
        Assert.Equal(LiveDirectory, fixture.Tracker.Get(Hwnd)!.Path);
    }

    [Fact]
    public void This_pc_scopes_the_search_to_every_volume_and_records_nothing()
    {
        using var fixture = new Fixture();
        fixture.PointsAt(ThisPcFolderId, "This PC");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.True(resolution.Succeeded);
        Assert.Equal(SearchScopeKind.AllVolumes, resolution.Scope!.Kind);
        Assert.Empty(resolution.Scope.Paths);
        Assert.Equal(ScopeResolutionSource.AllVolumes, resolution.Scope.Source);
        Assert.Equal("<all volumes>", resolution.Detail);
        Assert.Equal(ShellLocationKind.ThisPc, resolution.Kind);
        Assert.False(resolution.IsSearchResultView);
        Assert.Null(fixture.Tracker.Get(Hwnd));
    }

    [Fact]
    public void Home_scopes_the_search_to_the_known_folders_that_exist()
    {
        using var fixture = new Fixture();
        fixture.PointsAt(HomeFolderId, "Home");
        var known = fixture.KnownFolders.GetHomeSearchScopeFolders();

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        if (known.Count == 0)
        {
            // Only possible on a machine whose user profile has no known folder at all.
            Assert.Equal(ScopeFailureReason.SearchScopeUnknown, resolution.Failure);
            return;
        }

        Assert.True(resolution.Succeeded);
        Assert.Equal(SearchScopeKind.KnownFoldersUnion, resolution.Scope!.Kind);
        Assert.Equal(ScopeResolutionSource.KnownFolders, resolution.Scope.Source);
        Assert.Equal(known, resolution.Scope.Paths);
        Assert.Equal(string.Join(" | ", known), resolution.Detail);
        Assert.Equal(ShellLocationKind.Home, resolution.Kind);
        Assert.All(resolution.Scope.Paths, path => Assert.True(Path.IsPathRooted(path)));
    }

    [Fact]
    public void Home_fails_when_none_of_the_known_folders_exists()
    {
        using var fixture = new Fixture();
        fixture.DirectoryExists = _ => false;
        fixture.PointsAt(HomeFolderId, "Home");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.False(resolution.Succeeded);
        Assert.Equal(ScopeFailureReason.SearchScopeUnknown, resolution.Failure);
        Assert.Null(resolution.Scope);
        Assert.Equal(HomeFolderId, resolution.RawLocation);
        Assert.Contains("known folder", resolution.Detail);
    }

    [Fact]
    public void A_search_result_view_without_a_remembered_folder_cannot_be_scoped()
    {
        using var fixture = new Fixture();
        fixture.PointsAt(SearchView, "Search results in Downloads");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.False(resolution.Succeeded);
        Assert.Equal(ScopeFailureReason.SearchScopeUnknown, resolution.Failure);
        Assert.Null(resolution.Scope);
        Assert.True(resolution.IsSearchResultView);
        Assert.Equal(ShellLocationKind.SearchResults, resolution.Kind);
        Assert.Equal(SearchView, resolution.RawLocation);
        Assert.NotEmpty(resolution.Detail);
    }

    [Fact]
    public void A_search_result_view_uses_the_folder_the_window_showed_before_the_search()
    {
        using var fixture = new Fixture();
        fixture.Tracker.Remember(Hwnd, LiveDirectory, "Downloads", "live-shell");
        fixture.PointsAt(SearchView, "Search results in Downloads");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.True(resolution.Succeeded);
        Assert.Equal(SearchScopeKind.CurrentDirectoryAndSubdirectories, resolution.Scope!.Kind);
        Assert.Equal(ScopeResolutionSource.WindowScopeBeforeSearch, resolution.Scope.Source);
        Assert.Equal(new[] { LiveDirectory }, resolution.Scope.Paths);
        Assert.Equal(LiveDirectory, resolution.Detail);
        Assert.True(resolution.IsSearchResultView);
        Assert.Equal(ShellLocationKind.SearchResults, resolution.Kind);
        Assert.NotNull(fixture.Tracker.Get(Hwnd));
    }

    [Fact]
    public void A_mismatching_scope_hint_does_not_prevent_the_search_but_keeps_the_remembered_folder()
    {
        using var fixture = new Fixture();
        fixture.Tracker.Remember(Hwnd, LiveDirectory, "SomethingElse", "live-shell");
        fixture.PointsAt("\u201cCompletelyDifferent\u201d\u4e2d\u7684\u641c\u7d22\u7ed3\u679c&report", "x");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.True(resolution.Succeeded);
        Assert.Equal(new[] { LiveDirectory }, resolution.Scope!.Paths);
        Assert.Equal(LiveDirectory, fixture.Tracker.Get(Hwnd)!.Path);
    }

    [Fact]
    public void A_search_result_view_whose_remembered_folder_is_gone_forgets_the_window()
    {
        using var fixture = new Fixture();
        fixture.Tracker.Remember(Hwnd, LiveDirectory, "Downloads", "live-shell");
        fixture.DirectoryExists = _ => false;
        fixture.PointsAt(SearchView, "x");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.False(resolution.Succeeded);
        Assert.Equal(ScopeFailureReason.DirectoryUnavailable, resolution.Failure);
        Assert.True(resolution.IsSearchResultView);
        Assert.Equal(LiveDirectory, resolution.Detail);
        Assert.Null(fixture.Tracker.Get(Hwnd));
    }

    [Fact]
    public void A_search_started_from_this_pc_falls_back_to_every_volume()
    {
        using var fixture = new Fixture();
        fixture.Tracker.Remember(Hwnd, string.Empty, "This PC", "live-shell", ShellLocationKind.ThisPc);
        fixture.PointsAt(SearchView, "x");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.True(resolution.Succeeded);
        Assert.Equal(SearchScopeKind.AllVolumes, resolution.Scope!.Kind);
        Assert.Empty(resolution.Scope.Paths);
        Assert.True(resolution.IsSearchResultView);
    }

    [Fact]
    public void A_search_started_from_home_falls_back_to_the_known_folders()
    {
        using var fixture = new Fixture();
        fixture.Tracker.Remember(Hwnd, string.Empty, "Home", "live-shell", ShellLocationKind.Home);
        fixture.PointsAt(SearchView, "x");
        var known = fixture.KnownFolders.GetHomeSearchScopeFolders();

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        if (known.Count == 0)
        {
            Assert.Equal(ScopeFailureReason.SearchScopeUnknown, resolution.Failure);
            return;
        }

        Assert.True(resolution.Succeeded);
        Assert.Equal(SearchScopeKind.KnownFoldersUnion, resolution.Scope!.Kind);
        Assert.Equal(known, resolution.Scope.Paths);
        Assert.True(resolution.IsSearchResultView);
    }

    [Fact]
    public void An_unsupported_virtual_folder_is_reported_with_its_display_name()
    {
        using var fixture = new Fixture();
        fixture.PointsAt(RecycleBinFolderId, "Recycle Bin");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.False(resolution.Succeeded);
        Assert.Equal(ScopeFailureReason.UnsupportedShellNamespace, resolution.Failure);
        Assert.Equal("Recycle Bin", resolution.Detail);
        Assert.Equal(RecycleBinFolderId, resolution.RawLocation);
        Assert.Equal(ShellLocationKind.OtherVirtualFolder, resolution.Kind);
        Assert.False(resolution.IsSearchResultView);
    }

    [Fact]
    public void An_unsupported_virtual_folder_without_a_display_name_is_reported_with_its_folder_id()
    {
        using var fixture = new Fixture();
        fixture.PointsAt(RecycleBinFolderId, string.Empty);

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.Equal(ScopeFailureReason.UnsupportedShellNamespace, resolution.Failure);
        Assert.Equal(RecycleBinFolderId, resolution.Detail);
    }

    [Fact]
    public void A_live_folder_that_no_longer_exists_is_reported_and_not_remembered()
    {
        using var fixture = new Fixture();
        fixture.DirectoryExists = _ => false;
        fixture.PointsAt(LiveDirectory, "Downloads");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.False(resolution.Succeeded);
        Assert.Equal(ScopeFailureReason.DirectoryUnavailable, resolution.Failure);
        Assert.Equal(LiveDirectory, resolution.Detail);
        Assert.Equal(LiveDirectory, resolution.RawLocation);
        Assert.Null(fixture.Tracker.Get(Hwnd));
    }

    [Fact]
    public void Unc_paths_are_used_without_an_existence_check()
    {
        using var fixture = new Fixture();
        fixture.DirectoryExists = _ => throw new InvalidOperationException("the existence check must be skipped for UNC paths");
        fixture.PointsAt(@"\\server\share", "share");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.True(resolution.Succeeded);
        Assert.Equal(new[] { @"\\server\share" }, resolution.Scope!.Paths);
        Assert.Equal(@"\\server\share", fixture.Tracker.Get(Hwnd)!.Path);
    }

    [Fact]
    public void A_search_result_view_of_a_unc_folder_is_used_without_an_existence_check()
    {
        using var fixture = new Fixture();
        fixture.Tracker.Remember(Hwnd, @"\\server\share", "share", "live-shell");
        fixture.DirectoryExists = _ => throw new InvalidOperationException("the existence check must be skipped for UNC paths");
        fixture.PointsAt(SearchView, "x");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.True(resolution.Succeeded);
        Assert.Equal(ScopeResolutionSource.WindowScopeBeforeSearch, resolution.Scope!.Source);
        Assert.Equal(@"\\server\share", resolution.Detail);
    }

    [Fact]
    public void A_closed_explorer_window_is_reported_as_not_found()
    {
        using var fixture = new Fixture();
        fixture.Locations.OnResolve = _ => null;

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.False(resolution.Succeeded);
        Assert.Equal(ScopeFailureReason.ExplorerWindowNotFound, resolution.Failure);
        Assert.Contains("no Shell window", resolution.Detail);
    }

    [Fact]
    public void A_failing_shell_lookup_is_reported_instead_of_thrown()
    {
        using var fixture = new Fixture();
        fixture.Locations.OnResolve = _ => throw new InvalidOperationException("Shell.Application is gone");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.False(resolution.Succeeded);
        Assert.Equal(ScopeFailureReason.LocationUnavailable, resolution.Failure);
        Assert.Equal("Shell.Application is gone", resolution.Detail);
    }

    [Fact]
    public void A_window_whose_location_could_not_be_read_is_reported_as_unavailable()
    {
        using var fixture = new Fixture();
        fixture.PointsAt(string.Empty, string.Empty, "COMException: the window is gone");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.False(resolution.Succeeded);
        Assert.Equal(ScopeFailureReason.LocationUnavailable, resolution.Failure);
        Assert.Equal("COMException: the window is gone", resolution.Detail);
    }

    [Fact]
    public void An_unrecognised_location_is_reported_as_unavailable()
    {
        using var fixture = new Fixture();
        fixture.PointsAt(string.Empty, "somewhere");

        var resolution = fixture.CreateResolver().Resolve(Hwnd);

        Assert.False(resolution.Succeeded);
        Assert.Equal(ScopeFailureReason.LocationUnavailable, resolution.Failure);
        Assert.Equal("unrecognised Shell location", resolution.Detail);
    }

    [Fact]
    public void TryRecordLiveScope_remembers_a_live_folder_and_rejects_the_search_view()
    {
        using var fixture = new Fixture();
        var resolver = fixture.CreateResolver();
        fixture.PointsAt(LiveDirectory + @"\", "Downloads");

        Assert.True(resolver.TryRecordLiveScope(Hwnd));
        Assert.Equal(LiveDirectory, fixture.Tracker.Get(Hwnd)!.Path);

        // A search result view must never overwrite the folder the search was started from.
        fixture.PointsAt(SearchView, "x");
        Assert.False(resolver.TryRecordLiveScope(Hwnd));
        Assert.Equal(LiveDirectory, fixture.Tracker.Get(Hwnd)!.Path);
    }

    [Fact]
    public void TryRecordLiveScope_remembers_this_pc_and_home_with_their_kind()
    {
        using var fixture = new Fixture();
        var resolver = fixture.CreateResolver();

        fixture.PointsAt(ThisPcFolderId, "This PC");
        Assert.True(resolver.TryRecordLiveScope(Hwnd));
        Assert.Equal(ShellLocationKind.ThisPc, fixture.Tracker.Get(Hwnd)!.Kind);
        Assert.Equal(string.Empty, fixture.Tracker.Get(Hwnd)!.Path);

        fixture.PointsAt(HomeFolderId, "Home");
        Assert.True(resolver.TryRecordLiveScope(Hwnd));
        Assert.Equal(ShellLocationKind.Home, fixture.Tracker.Get(Hwnd)!.Kind);
    }

    [Fact]
    public void TryRecordLiveScope_ignores_a_folder_that_does_not_exist_or_cannot_be_read()
    {
        using var fixture = new Fixture();
        var resolver = fixture.CreateResolver();

        fixture.DirectoryExists = _ => false;
        fixture.PointsAt(LiveDirectory, "Downloads");
        Assert.False(resolver.TryRecordLiveScope(Hwnd));
        Assert.Null(fixture.Tracker.Get(Hwnd));

        fixture.Locations.OnResolve = _ => null;
        Assert.False(resolver.TryRecordLiveScope(Hwnd));

        fixture.Locations.OnResolve = _ => throw new InvalidOperationException("boom");
        Assert.False(resolver.TryRecordLiveScope(Hwnd));
        Assert.Null(fixture.Tracker.Get(Hwnd));
    }

    [Theory]
    [InlineData(@"D:\a\b\", @"D:\a\b")]
    [InlineData(@"D:\a\b", @"D:\a\b")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData("D:", @"D:\")]
    [InlineData(@"C:\a\\", @"C:\a")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    [InlineData("  D:\\x  ", @"D:\x")]
    [InlineData("", "")]
    public void NormalizeDirectory_removes_the_trailing_separator_but_keeps_a_drive_root(string input, string expected)
    {
        Assert.Equal(expected, SearchScopeResolver.NormalizeDirectory(input));
    }

    [Fact]
    public void NormalizeDirectory_tolerates_null()
    {
        Assert.Equal(string.Empty, SearchScopeResolver.NormalizeDirectory(null!));
    }

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server", true)]
    [InlineData(@"\\", true)]
    [InlineData(@"D:\local", false)]
    [InlineData(@"D:\a\\b", false)]
    [InlineData("", false)]
    public void IsUncPath_recognises_network_paths(string path, bool expected)
    {
        Assert.Equal(expected, SearchScopeResolver.IsUncPath(path));
    }
}
