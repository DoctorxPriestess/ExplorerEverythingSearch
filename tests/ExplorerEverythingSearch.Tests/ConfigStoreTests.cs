using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Tests.TestSupport;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>
/// ConfigStore must never throw: a missing, corrupt or unwritable config.json degrades to in-memory
/// defaults instead of preventing startup.
/// </summary>
public sealed class ConfigStoreTests
{
    [Fact]
    public void Load_creates_the_default_config_file_when_it_is_missing()
    {
        using var workspace = new TempWorkspace("cfg-create");
        using var logger = AppLogger.CreateDisabled(workspace.Root);
        var root = workspace.PathOf("not-created-yet");
        var store = new ConfigStore(root, logger);

        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(store.ConfigPath));

        var config = store.Load();

        Assert.True(File.Exists(store.ConfigPath));
        Assert.False(store.IsPersistDisabled);
        Assert.Null(store.LastError);
        Assert.Equal(new AppConfig().AutoSearchDelay, config.AutoSearchDelay);
        Assert.Same(config, store.Current);

        // The file that was written must load back to the same values.
        var reloaded = new ConfigStore(root, logger).Load();
        Assert.Equal(config.AutoSearchDelay, reloaded.AutoSearchDelay);
        Assert.Equal(config.LogLevel, reloaded.LogLevel);
        Assert.Equal(config.MaxLogFiles, reloaded.MaxLogFiles);
    }

    [Fact]
    public void Load_falls_back_to_defaults_on_corrupt_json_without_throwing()
    {
        using var workspace = new TempWorkspace("cfg-corrupt");
        using var logger = AppLogger.CreateDisabled(workspace.Root);
        var store = new ConfigStore(workspace.Root, logger);
        const string corrupt = "{ this is not valid json ";
        File.WriteAllText(store.ConfigPath, corrupt);

        var config = store.Load();

        Assert.Equal(new AppConfig().AutoSearchDelay, config.AutoSearchDelay);
        Assert.Equal("auto", config.Language);
        Assert.NotNull(store.LastError);
        Assert.False(store.IsPersistDisabled);

        // The broken file is left alone so the user can inspect or fix it.
        Assert.Equal(corrupt, File.ReadAllText(store.ConfigPath));

        // Loading twice must stay stable and must not throw either.
        Assert.Equal(new AppConfig().AutoSearchDelay, store.Load().AutoSearchDelay);
    }

    [Fact]
    public void Load_falls_back_to_defaults_when_a_field_has_the_wrong_type()
    {
        using var workspace = new TempWorkspace("cfg-wrongtype");
        using var logger = AppLogger.CreateDisabled(workspace.Root);
        var store = new ConfigStore(workspace.Root, logger);
        File.WriteAllText(store.ConfigPath, """{ "autoSearchDelay": "as fast as possible" }""");

        var config = store.Load();

        Assert.Equal(new AppConfig().AutoSearchDelay, config.AutoSearchDelay);
        Assert.NotNull(store.LastError);
    }

    [Fact]
    public void Save_then_Load_round_trips_and_normalizes_what_it_writes()
    {
        using var workspace = new TempWorkspace("cfg-save");
        using var logger = AppLogger.CreateDisabled(workspace.Root);
        var store = new ConfigStore(workspace.Root, logger);

        var saved = store.Save(new AppConfig { AutoSearchDelay = 3000, Language = "zh-CN", MaxLogFiles = 7 }, out var error);

        Assert.True(saved);
        Assert.Null(error);
        Assert.False(store.IsPersistDisabled);
        Assert.Null(store.LastError);
        Assert.Equal(3000, store.Current.AutoSearchDelay);

        var reloaded = new ConfigStore(workspace.Root, logger).Load();
        Assert.Equal(3000, reloaded.AutoSearchDelay);
        Assert.Equal("zh-CN", reloaded.Language);
        Assert.Equal(7, reloaded.MaxLogFiles);
    }

    [Fact]
    public void Save_writes_the_normalized_value_not_the_raw_one()
    {
        using var workspace = new TempWorkspace("cfg-normalize");
        using var logger = AppLogger.CreateDisabled(workspace.Root);
        var store = new ConfigStore(workspace.Root, logger);

        Assert.True(store.Save(new AppConfig { AutoSearchDelay = 1, EnterCommitWindowMs = 99999 }, out _));

        Assert.Equal(100, store.Current.AutoSearchDelay);
        Assert.Equal(5000, store.Current.EnterCommitWindowMs);
        Assert.Equal(100, new ConfigStore(workspace.Root, logger).Load().AutoSearchDelay);
    }

    [Fact]
    public void Load_degrades_to_defaults_when_the_root_directory_cannot_be_created()
    {
        using var workspace = new TempWorkspace("cfg-readonly");
        // A file where the directory has to go makes every write fail, exactly like read-only media.
        var blockedRoot = workspace.CreateFile("blocked", "not a directory");
        using var logger = AppLogger.CreateDisabled(workspace.Root);
        var store = new ConfigStore(blockedRoot, logger);

        var config = store.Load();

        Assert.True(store.IsPersistDisabled);
        Assert.NotNull(store.LastError);
        Assert.Equal(new AppConfig().AutoSearchDelay, config.AutoSearchDelay);
        Assert.False(File.Exists(Path.Combine(blockedRoot, "config.json")));
    }

    [Fact]
    public void Save_reports_failure_and_sets_IsPersistDisabled_on_an_unwritable_root()
    {
        using var workspace = new TempWorkspace("cfg-savefail");
        var blockedRoot = workspace.CreateFile("blocked", "not a directory");
        using var logger = AppLogger.CreateDisabled(workspace.Root);
        var store = new ConfigStore(blockedRoot, logger);

        var saved = store.Save(new AppConfig { AutoSearchDelay = 2000 }, out var error);

        Assert.False(saved);
        Assert.NotNull(error);
        Assert.True(store.IsPersistDisabled);
        Assert.Equal(error, store.LastError);
        // The in-memory configuration keeps the previous value: nothing was persisted.
        Assert.Equal(new AppConfig().AutoSearchDelay, store.Current.AutoSearchDelay);
    }

    [Fact]
    public void A_failed_save_does_not_leave_a_temporary_file_behind_that_breaks_the_next_save()
    {
        using var workspace = new TempWorkspace("cfg-temp");
        using var logger = AppLogger.CreateDisabled(workspace.Root);
        var store = new ConfigStore(workspace.Root, logger);

        Assert.True(store.Save(new AppConfig { AutoSearchDelay = 4000 }, out _));
        Assert.True(store.Save(new AppConfig { AutoSearchDelay = 4100 }, out _));

        Assert.False(File.Exists(store.ConfigPath + ".tmp"));
        Assert.Equal(4100, new ConfigStore(workspace.Root, logger).Load().AutoSearchDelay);
    }
}
