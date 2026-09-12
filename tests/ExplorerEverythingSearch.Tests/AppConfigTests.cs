using System.Linq;
using System.Text.Json;
using ExplorerEverythingSearch.Core.Configuration;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>Contract of config.json: defaults, clamping of hand edited values, JSON shape and cloning.</summary>
public sealed class AppConfigTests
{
    [Fact]
    public void Defaults_match_the_documented_configuration()
    {
        var config = new AppConfig();

        Assert.True(config.Enabled);
        Assert.Equal(1000, config.AutoSearchDelay);
        Assert.Equal(string.Empty, config.EverythingPath);
        Assert.Equal(string.Empty, config.EsPath);
        Assert.True(config.StartWithWindows);
        Assert.False(config.ShowNotifications);
        Assert.Equal("Information", config.LogLevel);
        Assert.True(config.LoggingEnabled);
        Assert.True(config.ReuseEverythingWindow);
        Assert.True(config.DetectEnterByKeyboardHook);
        Assert.True(config.DetectEnterByFocusChange);
        Assert.True(config.DetectEnterByCommitTiming);
        Assert.Equal(400, config.EnterCommitWindowMs);
        Assert.Equal(60, config.ExplorerRescanSeconds);
        Assert.Equal("auto", config.Language);
        Assert.Equal(5, config.MaxLogFileSizeMb);
        Assert.Equal(5, config.MaxLogFiles);
        Assert.Null(config.LastError);

        // The idle search delay default is part of the documented behaviour, not an implementation detail.
        Assert.Equal(1000, AppConfig.DefaultAutoSearchDelayMs);
        Assert.Equal(AppConfig.DefaultAutoSearchDelayMs, config.AutoSearchDelay);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-5000, 100)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(2500, 2500)]
    [InlineData(60000, 60000)]
    [InlineData(60001, 60000)]
    [InlineData(int.MaxValue, 60000)]
    public void Normalize_clamps_autoSearchDelay_to_100_60000(int input, int expected)
    {
        var config = new AppConfig { AutoSearchDelay = input }.Normalize();

        Assert.Equal(expected, config.AutoSearchDelay);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(49, 50)]
    [InlineData(50, 50)]
    [InlineData(400, 400)]
    [InlineData(5000, 5000)]
    [InlineData(5001, 5000)]
    public void Normalize_clamps_enterCommitWindowMs_to_50_5000(int input, int expected)
    {
        var config = new AppConfig { EnterCommitWindowMs = input }.Normalize();

        Assert.Equal(expected, config.EnterCommitWindowMs);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(60, 60)]
    [InlineData(3600, 3600)]
    [InlineData(3601, 3600)]
    public void Normalize_clamps_explorerRescanSeconds_to_0_3600(int input, int expected)
    {
        var config = new AppConfig { ExplorerRescanSeconds = input }.Normalize();

        Assert.Equal(expected, config.ExplorerRescanSeconds);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 1024)]
    public void Normalize_clamps_maxLogFileSizeMb_to_1_1024(int input, int expected)
    {
        var config = new AppConfig { MaxLogFileSizeMb = input }.Normalize();

        Assert.Equal(expected, config.MaxLogFileSizeMb);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public void Normalize_clamps_maxLogFiles_to_1_100(int input, int expected)
    {
        var config = new AppConfig { MaxLogFiles = input }.Normalize();

        Assert.Equal(expected, config.MaxLogFiles);
    }

    [Fact]
    public void Normalize_returns_the_same_instance_so_callers_can_chain()
    {
        var config = new AppConfig();

        Assert.Same(config, config.Normalize());
    }

    [Theory]
    [InlineData("  \"C:\\Apps\\Everything.exe\"  ", "C:\\Apps\\Everything.exe")]
    [InlineData("C:\\Apps\\Everything.exe", "C:\\Apps\\Everything.exe")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    public void Normalize_trims_and_unquotes_the_executable_paths(string input, string expected)
    {
        var config = new AppConfig { EverythingPath = input, EsPath = input }.Normalize();

        Assert.Equal(expected, config.EverythingPath);
        Assert.Equal(expected, config.EsPath);
    }

    [Fact]
    public void Normalize_treats_null_paths_as_empty()
    {
        var config = new AppConfig { EverythingPath = null!, EsPath = null! }.Normalize();

        Assert.Equal(string.Empty, config.EverythingPath);
        Assert.Equal(string.Empty, config.EsPath);
    }

    [Theory]
    [InlineData("debug", "Debug")]
    [InlineData("WARNING", "Warning")]
    [InlineData("None", "None")]
    [InlineData("trace", "Trace")]
    [InlineData("not-a-level", "Information")]
    [InlineData("", "Information")]
    public void Normalize_canonicalises_the_log_level(string input, string expected)
    {
        var config = new AppConfig { LogLevel = input }.Normalize();

        Assert.Equal(expected, config.LogLevel);
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-TW", "zh-CN")]
    [InlineData("en", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", "auto")]
    [InlineData("", "auto")]
    public void Normalize_canonicalises_the_language(string input, string expected)
    {
        var config = new AppConfig { Language = input }.Normalize();

        Assert.Equal(expected, config.Language);
    }

    [Fact]
    public void Serialization_uses_camel_case_names_and_skips_the_runtime_only_error()
    {
        var config = new AppConfig { AutoSearchDelay = 2500, EnterCommitWindowMs = 250, LastError = "boom" };

        var json = JsonSerializer.Serialize(config, AppConfig.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Contains("autoSearchDelay", names);
        Assert.Contains("enterCommitWindowMs", names);
        Assert.Contains("explorerRescanSeconds", names);
        Assert.Contains("maxLogFileSizeMb", names);
        Assert.Contains("loggingEnabled", names);

        // PascalCase spellings and the session-only LastError must never be persisted.
        Assert.DoesNotContain("AutoSearchDelay", names);
        Assert.DoesNotContain("LastError", names);
        Assert.DoesNotContain("SerializerOptions", names);

        // The file is hand editable, so it is written indented.
        Assert.Contains(Environment.NewLine, json);
        Assert.Equal(2500, document.RootElement.GetProperty("autoSearchDelay").GetInt32());
    }

    [Fact]
    public void Json_round_trip_preserves_every_persisted_property()
    {
        var original = new AppConfig
        {
            Enabled = false,
            AutoSearchDelay = 2500,
            EverythingPath = @"C:\Tools\Everything.exe",
            EsPath = @"C:\Tools\es.exe",
            StartWithWindows = false,
            ShowNotifications = true,
            LogLevel = "Debug",
            LoggingEnabled = false,
            ReuseEverythingWindow = false,
            DetectEnterByKeyboardHook = false,
            DetectEnterByFocusChange = false,
            DetectEnterByCommitTiming = false,
            EnterCommitWindowMs = 250,
            ExplorerRescanSeconds = 0,
            Language = "zh-CN",
            MaxLogFileSizeMb = 2,
            MaxLogFiles = 3,
        };

        var json = JsonSerializer.Serialize(original, AppConfig.SerializerOptions);
        var restored = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions);

        Assert.NotNull(restored);
        AssertSameConfiguration(original, restored!);
    }

    [Fact]
    public void Unknown_json_fields_are_ignored_and_missing_fields_keep_their_default()
    {
        const string json = """
            {
              "autoSearchDelay": 500,
              "someFutureOption": true,
              "nested": { "a": [1, 2, 3] }
            }
            """;

        var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions);

        Assert.NotNull(config);
        Assert.Equal(500, config!.AutoSearchDelay);

        // Everything that was not in the file keeps its documented default.
        var defaults = new AppConfig();
        Assert.True(config.Enabled);
        Assert.Equal(defaults.LogLevel, config.LogLevel);
        Assert.Equal(defaults.MaxLogFiles, config.MaxLogFiles);
        Assert.Equal(defaults.Language, config.Language);
        Assert.Equal(defaults.ExplorerRescanSeconds, config.ExplorerRescanSeconds);
    }

    [Fact]
    public void An_empty_json_object_produces_the_defaults()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}", AppConfig.SerializerOptions);

        Assert.NotNull(config);
        AssertSameConfiguration(new AppConfig(), config!);
    }

    [Fact]
    public void Clone_is_independent_from_the_original()
    {
        var original = new AppConfig { AutoSearchDelay = 1500, LogLevel = "Warning", LastError = "boom" };

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.Equal(1500, clone.AutoSearchDelay);
        Assert.Equal("Warning", clone.LogLevel);
        Assert.Equal("boom", clone.LastError);

        clone.AutoSearchDelay = 2000;
        clone.LogLevel = "Error";
        clone.EverythingPath = @"C:\moved\Everything.exe";
        clone.Enabled = false;
        clone.LastError = "other";

        Assert.Equal(1500, original.AutoSearchDelay);
        Assert.Equal("Warning", original.LogLevel);
        Assert.Equal(string.Empty, original.EverythingPath);
        Assert.True(original.Enabled);
        Assert.Equal("boom", original.LastError);

        original.MaxLogFiles = 42;
        Assert.Equal(5, clone.MaxLogFiles);
    }

    private static void AssertSameConfiguration(AppConfig expected, AppConfig actual)
    {
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.AutoSearchDelay, actual.AutoSearchDelay);
        Assert.Equal(expected.EverythingPath, actual.EverythingPath);
        Assert.Equal(expected.EsPath, actual.EsPath);
        Assert.Equal(expected.StartWithWindows, actual.StartWithWindows);
        Assert.Equal(expected.ShowNotifications, actual.ShowNotifications);
        Assert.Equal(expected.LogLevel, actual.LogLevel);
        Assert.Equal(expected.LoggingEnabled, actual.LoggingEnabled);
        Assert.Equal(expected.ReuseEverythingWindow, actual.ReuseEverythingWindow);
        Assert.Equal(expected.DetectEnterByKeyboardHook, actual.DetectEnterByKeyboardHook);
        Assert.Equal(expected.DetectEnterByFocusChange, actual.DetectEnterByFocusChange);
        Assert.Equal(expected.DetectEnterByCommitTiming, actual.DetectEnterByCommitTiming);
        Assert.Equal(expected.EnterCommitWindowMs, actual.EnterCommitWindowMs);
        Assert.Equal(expected.ExplorerRescanSeconds, actual.ExplorerRescanSeconds);
        Assert.Equal(expected.Language, actual.Language);
        Assert.Equal(expected.MaxLogFileSizeMb, actual.MaxLogFileSizeMb);
        Assert.Equal(expected.MaxLogFiles, actual.MaxLogFiles);
    }
}
