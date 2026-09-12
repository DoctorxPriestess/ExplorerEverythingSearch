using Microsoft.Win32;

namespace ExplorerEverythingSearch.Core.Startup;

/// <summary>Registry abstraction so the start-with-Windows logic is unit testable without touching HKCU.</summary>
public interface IStartupRegistry
{
    string? ReadValue(string name);

    bool WriteValue(string name, string value);

    bool DeleteValue(string name);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no administrator rights required).</summary>
public sealed class HkcuRunRegistry : IStartupRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? ReadValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    public bool WriteValue(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;
            key.SetValue(name, value, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool DeleteValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record StartupStatus(
    bool Registered,
    bool PointsToCurrentExecutable,
    string? RegisteredValue,
    string? RegisteredPath);

/// <summary>
/// Manages the "start with Windows" entry.
///
/// The entry stores the absolute path of this executable, which means a user who moves the folder
/// leaves a stale entry behind. <see cref="GetStatus"/> detects that (the stored path no longer
/// exists or points somewhere else) and the settings window offers to repair it.
/// </summary>
public sealed class StartupRegistration
{
    public const string ValueName = "ExplorerEverythingSearch";
    public const string StartupArgument = "--startup";

    private readonly IStartupRegistry _registry;

    public StartupRegistration(IStartupRegistry registry, string executablePath)
    {
        _registry = registry;
        ExecutablePath = executablePath;
    }

    public string ExecutablePath { get; }

    public string ExpectedValue => $"\"{ExecutablePath}\" {StartupArgument}";

    public StartupStatus GetStatus()
    {
        var value = _registry.ReadValue(ValueName);
        if (string.IsNullOrWhiteSpace(value))
            return new StartupStatus(false, false, null, null);

        var path = ExtractPath(value);
        var pointsToCurrent = path is not null
                              && string.Equals(Path.GetFullPath(path), Path.GetFullPath(ExecutablePath), StringComparison.OrdinalIgnoreCase);
        return new StartupStatus(true, pointsToCurrent, value, path);
    }

    /// <summary>True when an entry exists but it is stale (for example the EXE was moved).</summary>
    public bool IsStale()
    {
        var status = GetStatus();
        if (!status.Registered) return false;
        if (status.PointsToCurrentExecutable) return false;
        return status.RegisteredPath is null || !File.Exists(status.RegisteredPath);
    }

    public bool Register(out string? error)
    {
        if (_registry.WriteValue(ValueName, ExpectedValue))
        {
            error = null;
            return true;
        }
        error = "could not write HKCU\\...\\Run";
        return false;
    }

    public bool Unregister(out string? error)
    {
        if (_registry.DeleteValue(ValueName))
        {
            error = null;
            return true;
        }
        error = "could not delete the HKCU\\...\\Run entry";
        return false;
    }

    /// <summary>Re-registers a stale entry (used after the executable was moved).</summary>
    public bool Repair(out string? error) => Register(out error);

    /// <summary>Extracts the executable path from a Run value such as <c>"C:\dir\app.exe" --startup</c>.</summary>
    public static string? ExtractPath(string? commandLine)
    {
        var value = (commandLine ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value.Substring(1, end - 1) : null;
        }

        var space = value.IndexOf(' ');
        return space < 0 ? value : value[..space];
    }
}
