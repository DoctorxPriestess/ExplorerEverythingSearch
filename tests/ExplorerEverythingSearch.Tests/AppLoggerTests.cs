using System.IO;
using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Tests.TestSupport;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>
/// The logger is the only place that writes files, so its contract is asserted end to end: no file at
/// all while disabled, the documented line format, rotation, safe cleanup and runtime switching.
/// </summary>
public sealed class AppLoggerTests
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void LoggingEnabled_false_produces_no_log_file_at_all()
    {
        using var workspace = new TempWorkspace("log-off");
        var config = new AppConfig { LoggingEnabled = false, LogLevel = "Trace" };
        var logDirectory = Path.Combine(workspace.Root, "logs");

        using (var logger = AppLogger.Create(workspace.Root, config))
        {
            Assert.False(logger.FileLoggingEnabled);

            logger.Trace("trace");
            logger.Debug("debug");
            logger.Info("info");
            logger.Warn("warn");
            logger.Error("error");
            logger.Error("error", new InvalidOperationException("boom"));
            logger.Flush(FlushTimeout);
            Thread.Sleep(150);

            Assert.False(Directory.Exists(logDirectory));
        }

        Assert.False(Directory.Exists(logDirectory));
    }

    [Fact]
    public void LogLevel_None_produces_no_log_file_at_all()
    {
        using var workspace = new TempWorkspace("log-none");
        var config = new AppConfig { LoggingEnabled = true, LogLevel = "None" };
        var logDirectory = Path.Combine(workspace.Root, "logs");

        using var logger = AppLogger.Create(workspace.Root, config);

        Assert.Equal(LogLevel.None, logger.Level);
        Assert.False(logger.FileLoggingEnabled);
        Assert.False(logger.IsEnabled(LogLevel.Error));

        logger.Error("must not be written");
        logger.Flush(FlushTimeout);
        Thread.Sleep(150);

        Assert.False(Directory.Exists(logDirectory));
    }

    [Fact]
    public void CreateDisabled_never_touches_the_disk()
    {
        using var workspace = new TempWorkspace("log-disabled");

        using var logger = AppLogger.CreateDisabled(workspace.Root);

        Assert.Equal(LogLevel.None, logger.Level);
        Assert.False(logger.FileLoggingEnabled);
        Assert.Equal(Path.Combine(workspace.Root, "logs"), logger.LogDirectory);
        Assert.Equal(Path.Combine(workspace.Root, "logs", "app.log"), logger.CurrentLogPath);

        logger.Info("nothing");
        logger.Flush(FlushTimeout);

        Assert.False(Directory.Exists(logger.LogDirectory));
    }

    [Fact]
    public void Writes_the_documented_line_format_and_drops_entries_below_the_level()
    {
        using var workspace = new TempWorkspace("log-format");
        var config = new AppConfig { LoggingEnabled = true, LogLevel = "Information" };
        using var logger = AppLogger.Create(workspace.Root, config);

        logger.Info("hello world");
        logger.Debug("below the configured level");
        logger.Error("bad thing");

        var text = Wait.ForFileText(logger.CurrentLogPath, t => t.Contains("bad thing"), 10_000);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] \[INFO\] hello world$", lines[0]);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] \[ERROR\] bad thing$", lines[1]);
        Assert.DoesNotContain("below the configured level", text);
        Assert.Equal(LogLevel.Information, logger.Level);
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.Equal(Path.Combine(workspace.Root, "logs", "app.log"), logger.CurrentLogPath);
    }

    [Fact]
    public void Error_with_an_exception_appends_its_type_and_message()
    {
        using var workspace = new TempWorkspace("log-exception");
        using var logger = AppLogger.Create(workspace.Root, new AppConfig { LogLevel = "Error" });

        logger.Error("read failed", new InvalidOperationException("boom"));

        var text = Wait.ForFileText(logger.CurrentLogPath, t => t.Contains("read failed"), 10_000);
        Assert.Contains("[ERROR] read failed | InvalidOperationException: boom", text);
    }

    [Fact]
    public void Rotates_once_the_file_exceeds_maxLogFileSizeMb_and_never_keeps_more_than_maxLogFiles()
    {
        using var workspace = new TempWorkspace("log-rotate");
        var config = new AppConfig { LoggingEnabled = true, LogLevel = "Information", MaxLogFileSizeMb = 1, MaxLogFiles = 2 };
        using var logger = AppLogger.Create(workspace.Root, config);
        var logDirectory = Path.Combine(workspace.Root, "logs");
        var firstBackup = Path.Combine(logDirectory, "app.1.log");
        var filler = new string('x', 900);

        // Roughly 1.3 MB in a single batch, i.e. above maxLogFileSizeMb.
        for (var i = 0; i < 1400; i++) logger.Info($"round0-{i:D5} {filler}");
        logger.Flush(FlushTimeout);

        Assert.True(Wait.Until(() => File.Exists(firstBackup), 20_000), "app.1.log was not created after exceeding maxLogFileSizeMb");
        Assert.True(new FileInfo(firstBackup).Length >= 1024 * 1024, "the rotated file should hold the overflow");
        Assert.True(Wait.Until(() => LogFiles(logDirectory).Length <= config.MaxLogFiles, 10_000));

        // A second overflow must rotate again, and the oldest file is dropped instead of piling up.
        for (var i = 0; i < 1400; i++) logger.Info($"round1-{i:D5} {filler}");
        logger.Flush(FlushTimeout);

        Wait.ForFileText(firstBackup, t => t.Contains("round1-00000"), 20_000);
        // The rotated file is not the one the writer holds open, but read it shareable anyway.
        Assert.False(Wait.ReadShared(firstBackup).Contains("round0-00000", StringComparison.Ordinal), "the oldest rotation must be dropped");
        Assert.True(LogFiles(logDirectory).Length <= config.MaxLogFiles, "more files than maxLogFiles were kept");

        // After rotating, the logger must reopen app.log and keep writing.
        logger.Info("after-rotation-marker");
        var current = Wait.ForFileText(logger.CurrentLogPath, t => t.Contains("after-rotation-marker"), 10_000);
        Assert.True(current.Contains("after-rotation-marker", StringComparison.Ordinal));
        Assert.True(LogFiles(logDirectory).Length <= config.MaxLogFiles);
    }

    // ---------------------------------------------------------------------------------------------
    // ClearLogs
    //
    // These tests guard a defect that was found and fixed: the writer used to hand the "the file is
    // closed now" state over with _maintenanceDone.Set() immediately followed by
    // _maintenanceDone.Reset(), and the caller's RequestMaintenance could miss that pulse
    // (ManualResetEventSlim spins for tens of milliseconds before it settles on the wait handle, while
    // Set+Reset takes microseconds). ClearLogs then returned false and deleted nothing, and
    // SetFileLogging(false) blocked the calling UI thread for 5 seconds. The logger now uses a fresh
    // one-shot event per request (see AppLogger.RequestMaintenance / CompleteMaintenance).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ClearLogs_removes_the_files_and_logging_continues_afterwards()
    {
        using var workspace = new TempWorkspace("log-clear");
        using var logger = AppLogger.Create(workspace.Root, new AppConfig { LoggingEnabled = true, LogLevel = "Information" });
        var logDirectory = Path.Combine(workspace.Root, "logs");

        logger.Info("before-clear-marker");
        Wait.ForFileText(logger.CurrentLogPath, t => t.Contains("before-clear-marker"), 10_000);

        Assert.True(logger.ClearLogs(out var error));
        Assert.Null(error);
        Assert.False(File.Exists(logger.CurrentLogPath));
        Assert.Empty(LogFiles(logDirectory));

        logger.Info("after-clear-marker");
        var text = Wait.ForFileText(logger.CurrentLogPath, t => t.Contains("after-clear-marker"), 10_000);

        Assert.DoesNotContain("before-clear-marker", text);
        Assert.Contains("after-clear-marker", text);
    }

    [Fact]
    public void ClearLogs_removes_the_rotated_files_as_well()
    {
        using var workspace = new TempWorkspace("log-clear-rotated");
        var config = new AppConfig { LoggingEnabled = true, LogLevel = "Information", MaxLogFileSizeMb = 1, MaxLogFiles = 3 };
        using var logger = AppLogger.Create(workspace.Root, config);
        var logDirectory = Path.Combine(workspace.Root, "logs");
        var filler = new string('x', 900);

        for (var i = 0; i < 1400; i++) logger.Info($"rotate-{i:D5} {filler}");
        logger.Flush(FlushTimeout);
        Assert.True(Wait.Until(() => File.Exists(Path.Combine(logDirectory, "app.1.log")), 20_000), "the log file was not rotated");

        Assert.True(logger.ClearLogs(out var error));

        Assert.Null(error);
        Assert.Empty(LogFiles(logDirectory));
    }

    [Fact]
    public void ClearLogs_never_reports_success_without_having_removed_the_files()
    {
        using var workspace = new TempWorkspace("log-clear-nothrow");
        using var logger = AppLogger.Create(workspace.Root, new AppConfig { LoggingEnabled = true, LogLevel = "Information" });
        var logDirectory = Path.Combine(workspace.Root, "logs");

        logger.Info("kept-marker");
        Wait.ForFileText(logger.CurrentLogPath, t => t.Contains("kept-marker"), 10_000);

        var cleared = logger.ClearLogs(out var error);

        // The reported outcome has to match the file system: success means the logs are gone, and a
        // refusal means they are still there and the reason was reported.
        if (cleared)
        {
            Assert.Null(error);
            Assert.Empty(LogFiles(logDirectory));
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.NotEmpty(LogFiles(logDirectory));
        }
    }

    [Fact]
    public void SetFileLogging_false_stops_writing_and_true_resumes_it()
    {
        using var workspace = new TempWorkspace("log-switch");
        using var logger = AppLogger.Create(workspace.Root, new AppConfig { LoggingEnabled = true, LogLevel = "Information" });

        logger.Info("kept-marker");
        Wait.ForFileText(logger.CurrentLogPath, t => t.Contains("kept-marker"), 10_000);

        // Note: this call also waits for the writer hand-over; it does stop writing either way,
        // because the flag is set before the hand-over is attempted.
        logger.SetFileLogging(false);
        Assert.False(logger.FileLoggingEnabled);

        logger.Info("dropped-marker");
        logger.Flush(FlushTimeout);
        Thread.Sleep(250);
        Assert.DoesNotContain("dropped-marker", Wait.ReadShared(logger.CurrentLogPath));

        logger.SetFileLogging(true);
        Assert.True(logger.FileLoggingEnabled);

        logger.Info("resumed-marker");
        var text = Wait.ForFileText(logger.CurrentLogPath, t => t.Contains("resumed-marker"), 10_000);
        Assert.Contains("kept-marker", text);
        Assert.DoesNotContain("dropped-marker", text);
    }

    [Fact]
    public void SetLevel_to_None_switches_file_logging_off_and_raising_the_level_brings_it_back()
    {
        // Guarded defect (found and fixed): SetLevel(None) used to make file logging sticky, because
        // the enabled flag was derived from itself ("enabled && level != None"). Raising the level
        // afterwards could not switch logging back on. The logger now remembers what the configuration
        // asked for separately from the effective state.
        using var workspace = new TempWorkspace("log-setlevel");
        using var logger = AppLogger.Create(workspace.Root, new AppConfig { LoggingEnabled = true, LogLevel = "Information" });
        Assert.True(logger.FileLoggingEnabled);

        logger.SetLevel(LogLevel.None);
        Assert.Equal(LogLevel.None, logger.Level);
        Assert.False(logger.FileLoggingEnabled);

        logger.SetLevel(LogLevel.Information);
        Assert.True(logger.FileLoggingEnabled);

        logger.SetFileLogging(false);
        Assert.False(logger.FileLoggingEnabled);

        // Turning file logging off is what has to be undone explicitly.
        logger.SetFileLogging(true);
        Assert.True(logger.FileLoggingEnabled);
    }

    [Fact]
    public void Writing_after_Dispose_is_ignored()
    {
        using var workspace = new TempWorkspace("log-dispose");
        var logger = AppLogger.Create(workspace.Root, new AppConfig { LoggingEnabled = true, LogLevel = "Information" });

        logger.Info("before-dispose");
        Wait.ForFileText(logger.CurrentLogPath, t => t.Contains("before-dispose"), 10_000);

        logger.Dispose();
        logger.Dispose(); // must be idempotent
        logger.Info("after-dispose");
        logger.Flush(FlushTimeout);
        Thread.Sleep(150);

        Assert.DoesNotContain("after-dispose", Wait.ReadShared(logger.CurrentLogPath));
    }

    private static string[] LogFiles(string logDirectory)
        => Directory.Exists(logDirectory) ? Directory.GetFiles(logDirectory, "*.log") : Array.Empty<string>();
}

/// <summary>Mapping rules between the config file text and the <see cref="LogLevel"/> enum.</summary>
public sealed class LogLevelsTests
{
    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("None", LogLevel.None)]
    [InlineData("not-a-level", LogLevel.Information)]
    [InlineData("", LogLevel.Information)]
    [InlineData(null, LogLevel.Information)]
    public void Parse_maps_config_text_to_a_level_and_defaults_to_Information(string? value, LogLevel expected)
    {
        Assert.Equal(expected, LogLevels.Parse(value));
    }

    [Theory]
    [InlineData("debug", "Debug")]
    [InlineData("WARNING", "Warning")]
    [InlineData("nonsense", "Information")]
    [InlineData(null, "Information")]
    public void Normalize_returns_the_canonical_spelling(string? value, string expected)
    {
        Assert.Equal(expected, LogLevels.Normalize(value));
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRACE")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARN")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.None, "NONE")]
    public void ToTag_uses_the_documented_short_tags(LogLevel level, string expected)
    {
        Assert.Equal(expected, LogLevels.ToTag(level));
    }
}
