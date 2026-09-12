using ExplorerEverythingSearch.Core.Startup;
using ExplorerEverythingSearch.Tests.TestSupport;
using Xunit;

namespace ExplorerEverythingSearch.Tests;

/// <summary>
/// Every test uses an in-memory registry: the real HKCU\...\Run key is never written, so running the
/// suite can never change how this machine starts.
/// </summary>
public sealed class StartupRegistrationTests
{
    [Fact]
    public void The_expected_value_quotes_the_executable_and_appends_the_startup_argument()
    {
        var sut = new StartupRegistration(new MemoryStartupRegistry(), @"C:\Apps\EES\EES.App.exe");

        Assert.Equal(@"""C:\Apps\EES\EES.App.exe"" --startup", sut.ExpectedValue);
        Assert.Equal("ExplorerEverythingSearch", StartupRegistration.ValueName);
        Assert.Equal("--startup", StartupRegistration.StartupArgument);
        Assert.Equal(@"C:\Apps\EES\EES.App.exe", sut.ExecutablePath);
    }

    [Fact]
    public void GetStatus_reports_nothing_registered_when_the_value_is_absent_or_blank()
    {
        var registry = new MemoryStartupRegistry();
        var sut = new StartupRegistration(registry, @"C:\Apps\EES\EES.App.exe");

        var missing = sut.GetStatus();

        Assert.False(missing.Registered);
        Assert.False(missing.PointsToCurrentExecutable);
        Assert.Null(missing.RegisteredValue);
        Assert.Null(missing.RegisteredPath);
        Assert.False(sut.IsStale());

        registry.Seed(StartupRegistration.ValueName, "   ");
        Assert.False(sut.GetStatus().Registered);
    }

    [Fact]
    public void Register_writes_the_expected_value_and_GetStatus_confirms_it()
    {
        using var workspace = new TempWorkspace("startup");
        var exe = workspace.CreateFile("EES.App.exe");
        var registry = new MemoryStartupRegistry();
        var sut = new StartupRegistration(registry, exe);

        Assert.True(sut.Register(out var error));

        Assert.Null(error);
        Assert.Equal(1, registry.WriteCount);
        Assert.Equal(sut.ExpectedValue, registry.ReadValue(StartupRegistration.ValueName));

        var status = sut.GetStatus();
        Assert.True(status.Registered);
        Assert.True(status.PointsToCurrentExecutable);
        Assert.Equal(sut.ExpectedValue, status.RegisteredValue);
        Assert.Equal(exe, status.RegisteredPath);
        Assert.False(sut.IsStale());
    }

    [Fact]
    public void Register_reports_failure_when_the_registry_rejects_the_write()
    {
        using var workspace = new TempWorkspace("startup-fail");
        var exe = workspace.CreateFile("EES.App.exe");
        var registry = new MemoryStartupRegistry { FailWrites = true };
        var sut = new StartupRegistration(registry, exe);

        Assert.False(sut.Register(out var error));

        Assert.NotNull(error);
        Assert.Contains("Run", error);
        Assert.Empty(registry.Values);
        Assert.False(sut.GetStatus().Registered);
    }

    [Fact]
    public void Unregister_removes_the_entry_and_reports_a_failure()
    {
        using var workspace = new TempWorkspace("startup-remove");
        var exe = workspace.CreateFile("EES.App.exe");
        var registry = new MemoryStartupRegistry();
        var sut = new StartupRegistration(registry, exe);
        Assert.True(sut.Register(out _));

        Assert.True(sut.Unregister(out var error));
        Assert.Null(error);
        Assert.Null(registry.ReadValue(StartupRegistration.ValueName));
        Assert.False(sut.GetStatus().Registered);

        registry.Seed(StartupRegistration.ValueName, sut.ExpectedValue);
        registry.FailDeletes = true;
        Assert.False(sut.Unregister(out var failure));
        Assert.NotNull(failure);
        Assert.True(sut.GetStatus().Registered);
    }

    [Fact]
    public void IsStale_detects_an_entry_that_points_to_a_moved_executable()
    {
        using var workspace = new TempWorkspace("startup-stale");
        var exe = workspace.CreateFile("EES.App.exe");
        var registry = new MemoryStartupRegistry();
        var sut = new StartupRegistration(registry, exe);
        var movedPath = workspace.PathOf(@"elsewhere\EES.App.exe");
        registry.Seed(StartupRegistration.ValueName, $"\"{movedPath}\" --startup");

        var status = sut.GetStatus();

        Assert.True(status.Registered);
        Assert.False(status.PointsToCurrentExecutable);
        Assert.Equal(movedPath, status.RegisteredPath);
        Assert.True(sut.IsStale());

        Assert.True(sut.Repair(out var error));
        Assert.Null(error);
        Assert.Equal(sut.ExpectedValue, registry.ReadValue(StartupRegistration.ValueName));
        Assert.False(sut.IsStale());
        Assert.True(sut.GetStatus().PointsToCurrentExecutable);
    }

    [Fact]
    public void An_entry_pointing_to_another_existing_copy_is_not_current_but_not_stale()
    {
        using var workspace = new TempWorkspace("startup-other");
        var current = workspace.CreateFile("current.exe");
        var other = workspace.CreateFile("other.exe");
        var registry = new MemoryStartupRegistry();
        var sut = new StartupRegistration(registry, current);
        registry.Seed(StartupRegistration.ValueName, $"\"{other}\" --startup");

        var status = sut.GetStatus();

        Assert.True(status.Registered);
        Assert.False(status.PointsToCurrentExecutable);
        Assert.Equal(other, status.RegisteredPath);
        Assert.False(sut.IsStale());
    }

    [Fact]
    public void An_entry_without_a_usable_path_is_stale()
    {
        using var workspace = new TempWorkspace("startup-unusable");
        var exe = workspace.CreateFile("EES.App.exe");
        var registry = new MemoryStartupRegistry();
        var sut = new StartupRegistration(registry, exe);
        registry.Seed(StartupRegistration.ValueName, "\"");

        var status = sut.GetStatus();

        Assert.True(status.Registered);
        Assert.Null(status.RegisteredPath);
        Assert.False(status.PointsToCurrentExecutable);
        Assert.True(sut.IsStale());
        Assert.True(sut.Repair(out _));
        Assert.False(sut.IsStale());
    }

    [Theory]
    [InlineData("\"C:\\Apps\\app.exe\" --startup", @"C:\Apps\app.exe")]
    [InlineData("\"C:\\Apps\\app.exe\"", @"C:\Apps\app.exe")]
    [InlineData("\"C:\\Program Files\\app.exe\" --startup", @"C:\Program Files\app.exe")]
    [InlineData("  \"C:\\Apps\\app.exe\"  --startup ", @"C:\Apps\app.exe")]
    [InlineData(@"C:\Apps\app.exe --startup", @"C:\Apps\app.exe")]
    [InlineData(@"C:\Apps\app.exe", @"C:\Apps\app.exe")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\"", null)]
    [InlineData("\"unterminated", null)]
    [InlineData(null, null)]
    public void ExtractPath_reads_the_executable_out_of_a_run_value(string? commandLine, string? expected)
    {
        Assert.Equal(expected, StartupRegistration.ExtractPath(commandLine));
    }

    /// <summary>
    /// The real registry is never touched by the suite. This is the documented recipe for a manual
    /// check on a disposable machine (kept as a skipped test so it cannot run by accident).
    /// </summary>
    [Fact(Skip = "Optional manual check: writes HKCU\\...\\Run. Run it by hand on a throwaway profile only.")]
    public void Real_hkcu_run_registration_manual_check()
    {
        var registry = new HkcuRunRegistry();
        var sut = new StartupRegistration(registry, Path.Combine(AppContext.BaseDirectory, "EES.App.exe"));
        try
        {
            Assert.True(sut.Register(out var error));
            Assert.Null(error);
            Assert.True(sut.GetStatus().Registered);
        }
        finally
        {
            sut.Unregister(out _);
        }
    }

    [Fact]
    public void Reading_the_real_run_key_is_safe_and_never_writes()
    {
        // Read-only access to the user's own Run key: no value is created, changed or deleted.
        var registry = new HkcuRunRegistry();

        var value = registry.ReadValue(StartupRegistration.ValueName);

        if (value is not null) Assert.IsType<string>(value);
    }
}
