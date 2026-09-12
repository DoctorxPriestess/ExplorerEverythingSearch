using System.Text.Json;
using System.Text.Json.Serialization;
using ExplorerEverythingSearch.Core.Diagnostics;

namespace ExplorerEverythingSearch.Core.Configuration;

/// <summary>
/// User configuration, persisted as <c>config.json</c> in the application root directory
/// (never in %AppData%). Property names are camelCase on purpose: the file is meant to be
/// hand-editable and is documented in the README.
/// </summary>
public sealed class AppConfig
{
    public const int DefaultAutoSearchDelayMs = 1000;

    /// <summary>Master switch for the Explorer search monitoring.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Delay (ms) of the idle search trigger: submit when the user stops typing.</summary>
    [JsonPropertyName("autoSearchDelay")]
    public int AutoSearchDelay { get; set; } = DefaultAutoSearchDelayMs;

    /// <summary>Explicit path to Everything.exe. Empty = auto-detect.</summary>
    [JsonPropertyName("everythingPath")]
    public string EverythingPath { get; set; } = string.Empty;

    /// <summary>Explicit path to es.exe (optional; used for diagnostics and verification). Empty = auto-detect.</summary>
    [JsonPropertyName("esPath")]
    public string EsPath { get; set; } = string.Empty;

    /// <summary>Register the application in HKCU\...\Run.</summary>
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Show tray balloon notifications (unsupported locations, Everything problems).</summary>
    [JsonPropertyName("showNotifications")]
    public bool ShowNotifications { get; set; }

    /// <summary>Trace | Debug | Information | Warning | Error | None</summary>
    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Information";

    /// <summary>Write structured logs to &lt;root&gt;\logs. When false no log file is produced at all.</summary>
    [JsonPropertyName("loggingEnabled")]
    public bool LoggingEnabled { get; set; } = true;

    /// <summary>Reuse the existing Everything search window instead of creating a new one per search.</summary>
    [JsonPropertyName("reuseEverythingWindow")]
    public bool ReuseEverythingWindow { get; set; } = true;

    /// <summary>
    /// Enter detection part 1 (primary): while an Explorer search box has the keyboard focus a low
    /// level keyboard hook watches for the Enter key. That is the only signal which is exact - it also
    /// works when the search finds nothing - and the hook exists only for as long as a search box is
    /// focused. Advanced option; the settings window does not expose it.
    /// </summary>
    [JsonPropertyName("detectEnterByKeyboardHook")]
    public bool DetectEnterByKeyboardHook { get; set; } = true;

    /// <summary>
    /// Enter detection part 2 (secondary), for the case where the hook is unavailable: the keyboard
    /// focus leaving the search box for the result area of the same window means Enter was pressed.
    /// Explorer's own view updates move the focus into its input site instead, which is never Enter.
    /// </summary>
    [JsonPropertyName("detectEnterByFocusChange")]
    public bool DetectEnterByFocusChange { get; set; } = true;

    /// <summary>
    /// Enter detection part 3 (fallback): an Explorer search commit that happens within
    /// <see cref="EnterCommitWindowMs"/> of the last keystroke and whose window title carries the text
    /// of the search box is treated as Enter. Explorer's own automatic commit happens ~800 ms after
    /// the last keystroke, hence the default of 400 ms.
    /// </summary>
    [JsonPropertyName("detectEnterByCommitTiming")]
    public bool DetectEnterByCommitTiming { get; set; } = true;

    /// <summary>Upper bound (ms) for the commit-timing Enter heuristic.</summary>
    [JsonPropertyName("enterCommitWindowMs")]
    public int EnterCommitWindowMs { get; set; } = 400;

    /// <summary>
    /// Low frequency self-healing rescan of Explorer windows (seconds). Event driven monitoring is
    /// primary; this only repairs a missed window event. 0 disables it.
    /// </summary>
    [JsonPropertyName("explorerRescanSeconds")]
    public int ExplorerRescanSeconds { get; set; } = 60;

    /// <summary>auto | en-US | zh-CN</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "auto";

    /// <summary>Rotate app.log after this many megabytes.</summary>
    [JsonPropertyName("maxLogFileSizeMb")]
    public int MaxLogFileSizeMb { get; set; } = 5;

    /// <summary>Number of rotated log files to keep.</summary>
    [JsonPropertyName("maxLogFiles")]
    public int MaxLogFiles { get; set; } = 5;

    /// <summary>Shown as a tray balloon at most once per session; suppressed by <see cref="ShowNotifications"/>.</summary>
    [JsonIgnore]
    public string? LastError { get; set; }

    [JsonIgnore]
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Clamps out-of-range values so a hand-edited config can never break the runtime.</summary>
    public AppConfig Normalize()
    {
        AutoSearchDelay = Math.Clamp(AutoSearchDelay, 100, 60_000);
        EnterCommitWindowMs = Math.Clamp(EnterCommitWindowMs, 50, 5_000);
        ExplorerRescanSeconds = Math.Clamp(ExplorerRescanSeconds, 0, 3_600);
        MaxLogFileSizeMb = Math.Clamp(MaxLogFileSizeMb, 1, 1_024);
        MaxLogFiles = Math.Clamp(MaxLogFiles, 1, 100);
        EverythingPath = (EverythingPath ?? string.Empty).Trim().Trim('"');
        EsPath = (EsPath ?? string.Empty).Trim().Trim('"');
        LogLevel = LogLevels.Normalize(LogLevel);
        Language = NormalizeLanguage(Language);
        return this;
    }

    private static string NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "auto";
        var v = value.Trim();
        if (v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        if (v.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        if (v.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en-US";
        return "auto";
    }

    public AppConfig Clone() => (AppConfig)MemberwiseClone();
}
