namespace ExplorerEverythingSearch.Core.Diagnostics;

/// <summary>Severity of a structured log entry.</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,

    /// <summary>Logging disabled.</summary>
    None = 5,
}

public static class LogLevels
{
    /// <summary>
    /// Parses a configured level. The enum names are accepted, plus the short spellings people
    /// actually write into config.json (info/warn/err/off/...); anything unknown becomes Information.
    /// </summary>
    public static LogLevel Parse(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return LogLevel.Information;

        switch (text.ToLowerInvariant())
        {
            case "trace":
            case "verbose":
            case "all":
                return LogLevel.Trace;
            case "debug":
                return LogLevel.Debug;
            case "info":
            case "information":
            case "normal":
                return LogLevel.Information;
            case "warn":
            case "warning":
                return LogLevel.Warning;
            case "err":
            case "error":
            case "fatal":
                return LogLevel.Error;
            case "none":
            case "off":
            case "disabled":
            case "silent":
                return LogLevel.None;
            default:
                return Enum.TryParse<LogLevel>(text, ignoreCase: true, out var level) ? level : LogLevel.Information;
        }
    }

    /// <summary>Canonical spelling of a level, for writing back into config.json.</summary>
    public static string Normalize(string? value) => Parse(value).ToString();

    public static string ToTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => "NONE",
    };
}
