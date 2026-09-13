using System.Globalization;

namespace ExplorerEverythingSearch.App.Localization;

/// <summary>
/// All user visible strings. English and Simplified Chinese are built in; the language is chosen from
/// config.json ("auto" follows the Windows UI language).
/// </summary>
public sealed record LocalizedStrings
{
    public required string LanguageCode { get; init; }

    // tray
    public required string TrayStatusHeader { get; init; }
    public required string TrayExplorerSearch { get; init; }
    public required string TrayEverything { get; init; }
    public required string StatusEnabled { get; init; }
    public required string StatusDisabled { get; init; }
    public required string StatusConnected { get; init; }
    public required string StatusUnavailable { get; init; }
    public required string MenuEnable { get; init; }
    public required string MenuPause { get; init; }
    public required string MenuSettings { get; init; }
    public required string MenuReconnect { get; init; }
    public required string MenuOpenLogs { get; init; }
    public required string MenuClearLogs { get; init; }
    public required string MenuLoggingOn { get; init; }
    public required string MenuLoggingOff { get; init; }
    public required string MenuExit { get; init; }

    // notifications
    public required string NotifyUnsupportedTitle { get; init; }
    public required string NotifyUnsupportedBody { get; init; }
    public required string NotifyEverythingTitle { get; init; }
    public required string NotifyEverythingNotFoundBody { get; init; }
    public required string NotifyEverythingFailedBody { get; init; }
    public required string NotifyStartupRepairedTitle { get; init; }
    public required string NotifyStartupRepairedBody { get; init; }
    public required string NotifyLogsClearedTitle { get; init; }
    public required string NotifyLogsClearedBody { get; init; }
    public required string NotifyLogsClearFailedBody { get; init; }
    public required string NotifyConfigNotWritableTitle { get; init; }
    public required string NotifyConfigNotWritableBody { get; init; }

    // scope failure explanations
    public required string ScopeUnsupportedTemplate { get; init; }
    public required string ScopeSearchUnknownTemplate { get; init; }
    public required string ScopeDirectoryMissingTemplate { get; init; }
    public required string ScopeWindowGoneTemplate { get; init; }
    public required string ScopeLocationUnavailableTemplate { get; init; }
    public required string ScopeNotRedirectedNote { get; init; }

    // settings window
    public required string SettingsTitle { get; init; }
    public required string SettingsEnabled { get; init; }
    public required string SettingsAutoSearchDelay { get; init; }
    public required string SettingsEverythingPath { get; init; }
    public required string SettingsEsPath { get; init; }
    public required string SettingsStartWithWindows { get; init; }
    public required string SettingsShowNotifications { get; init; }
    public required string SettingsLoggingEnabled { get; init; }
    public required string SettingsLogLevel { get; init; }
    public required string SettingsLanguage { get; init; }
    public required string SettingsReuseWindow { get; init; }
    public required string SettingsDetectEnterFocus { get; init; }
    public required string SettingsDetectEnterCommit { get; init; }
    public required string SettingsDetectEnterCommitWindow { get; init; }
    public required string SettingsExplorerRescan { get; init; }
    public required string SettingsMaxLogSize { get; init; }
    public required string SettingsBrowse { get; init; }
    public required string SettingsSave { get; init; }
    public required string SettingsCancel { get; init; }
    public required string SettingsTestConnection { get; init; }
    public required string SettingsOpenLogs { get; init; }
    public required string SettingsClearLogs { get; init; }
    public required string SettingsSaveFailed { get; init; }
    public required string SettingsSaveSucceeded { get; init; }
    public required string SettingsStartupStale { get; init; }
    public required string SettingsStartupRepair { get; init; }
    public required string SettingsEverythingDetection { get; init; }
    public required string SettingsLanguageAuto { get; init; }
    public required string SettingsConfigPath { get; init; }
    public required string SettingsRootDirectory { get; init; }

    // misc
    public required string AlreadyRunning { get; init; }
    public required string StartupStaleRepaired { get; init; }
    public required string HelpText { get; init; }
    public required string EverythingNotConfiguredHint { get; init; }

    public static LocalizedStrings English { get; } = new()
    {
        LanguageCode = "en-US",
        TrayStatusHeader = "Explorer Everything Search",
        TrayExplorerSearch = "Explorer Search: {0}",
        TrayEverything = "Everything: {0}",
        StatusEnabled = "Enabled",
        StatusDisabled = "Disabled",
        StatusConnected = "Connected ({0})",
        StatusUnavailable = "Unavailable",
        MenuEnable = "Enable monitoring",
        MenuPause = "Pause monitoring",
        MenuSettings = "Settings...",
        MenuReconnect = "Reconnect Everything",
        MenuOpenLogs = "Open log folder",
        MenuClearLogs = "Clear logs",
        MenuLoggingOn = "Logging: on",
        MenuLoggingOff = "Logging: off",
        MenuExit = "Exit",
        NotifyUnsupportedTitle = "Search not redirected to Everything",
        NotifyUnsupportedBody = "This Explorer location is not a search scope supported by this version, so this search was not redirected to Everything.",
        NotifyEverythingTitle = "Everything is not available",
        NotifyEverythingNotFoundBody = "Everything not found. Set the path to Everything.exe in the settings.",
        NotifyEverythingFailedBody = "Everything could not be driven: {0}",
        NotifyStartupRepairedTitle = "Start with Windows repaired",
        NotifyStartupRepairedBody = "The previous start-up entry pointed to a different location. It has been updated to {0}.",
        NotifyLogsClearedTitle = "Logs",
        NotifyLogsClearedBody = "Log files were removed and logging continues.",
        NotifyLogsClearFailedBody = "Log files could not be removed: {0}",
        NotifyConfigNotWritableTitle = "Configuration is read-only",
        NotifyConfigNotWritableBody = "config.json cannot be written ({0}). Settings changed in this session are only kept in memory.",
        ScopeUnsupportedTemplate = "Location: {0}",
        ScopeSearchUnknownTemplate = "Explorer is showing search results, but the folder the search was started from is unknown (window {0}).",
        ScopeDirectoryMissingTemplate = "The resolved directory no longer exists: {0}",
        ScopeWindowGoneTemplate = "The Explorer window ({0}) was closed before the search could be resolved.",
        ScopeLocationUnavailableTemplate = "The Explorer location could not be read: {0}",
        ScopeNotRedirectedNote = "This search was not redirected to Everything.",
        SettingsTitle = "Explorer Everything Search - Settings",
        SettingsEnabled = "Enable Explorer search monitoring",
        SettingsAutoSearchDelay = "Auto search delay (ms)",
        SettingsEverythingPath = "Everything.exe path (empty = auto detect)",
        SettingsEsPath = "es.exe path (optional)",
        SettingsStartWithWindows = "Start with Windows (current user)",
        SettingsShowNotifications = "Show status notifications",
        SettingsLoggingEnabled = "Write log files",
        SettingsLogLevel = "Log level",
        SettingsLanguage = "Language",
        SettingsReuseWindow = "Reuse the Everything search window",
        SettingsDetectEnterFocus = "Detect Enter by focus change",
        SettingsDetectEnterCommit = "Detect Enter by Explorer commit timing",
        SettingsDetectEnterCommitWindow = "Commit timing window (ms)",
        SettingsExplorerRescan = "Explorer self-healing rescan (seconds, 0 = off)",
        SettingsMaxLogSize = "Maximum log file size (MB)",
        SettingsBrowse = "Browse...",
        SettingsSave = "Save",
        SettingsCancel = "Cancel",
        SettingsTestConnection = "Test / reconnect",
        SettingsOpenLogs = "Open logs",
        SettingsClearLogs = "Clear logs",
        SettingsSaveFailed = "The configuration could not be saved: {0}",
        SettingsSaveSucceeded = "Configuration saved.",
        SettingsStartupStale = "The start-up entry points to another location.",
        SettingsStartupRepair = "Repair start-up entry",
        SettingsEverythingDetection = "Detected",
        SettingsLanguageAuto = "Automatic (Windows language)",
        SettingsConfigPath = "Configuration file",
        SettingsRootDirectory = "Application directory",
        AlreadyRunning = "Explorer Everything Search is already running (see the notification area).",
        StartupStaleRepaired = "The start-up entry was stale and has been updated.",
        HelpText = "ExplorerEverythingSearch [--startup] [--settings] [--exit] [--root <directory>] [--version] [--help]",
        EverythingNotConfiguredHint = "Set the path to Everything.exe in the settings if it is not detected automatically.",
    };

    public static LocalizedStrings Chinese { get; } = new()
    {
        LanguageCode = "zh-CN",
        TrayStatusHeader = "Explorer Everything Search",
        TrayExplorerSearch = "Explorer 搜索监听：{0}",
        TrayEverything = "Everything：{0}",
        StatusEnabled = "已启用",
        StatusDisabled = "已暂停",
        StatusConnected = "已连接（{0}）",
        StatusUnavailable = "不可用",
        MenuEnable = "启用监听",
        MenuPause = "暂停监听",
        MenuSettings = "设置...",
        MenuReconnect = "重新连接 Everything",
        MenuOpenLogs = "打开日志目录",
        MenuClearLogs = "清理日志",
        MenuLoggingOn = "日志记录：开",
        MenuLoggingOff = "日志记录：关",
        MenuExit = "退出",
        NotifyUnsupportedTitle = "本次搜索未重定向到 Everything",
        NotifyUnsupportedBody = "当前位置属于当前版本暂不支持的搜索范围，因此本次搜索未重定向到 Everything。",
        NotifyEverythingTitle = "Everything 不可用",
        NotifyEverythingNotFoundBody = "未找到 Everything。请在设置中指定 Everything.exe 的路径。",
        NotifyEverythingFailedBody = "无法驱动 Everything：{0}",
        NotifyStartupRepairedTitle = "开机启动已修复",
        NotifyStartupRepairedBody = "原开机启动项指向了其他位置，已更新为 {0}。",
        NotifyLogsClearedTitle = "日志",
        NotifyLogsClearedBody = "日志文件已清理，日志记录继续运行。",
        NotifyLogsClearFailedBody = "日志文件无法删除：{0}",
        NotifyConfigNotWritableTitle = "配置不可写",
        NotifyConfigNotWritableBody = "无法写入 config.json（{0}）。本次会话中的设置修改只保留在内存中。",
        ScopeUnsupportedTemplate = "位置：{0}",
        ScopeSearchUnknownTemplate = "Explorer 正处于搜索结果视图，但无法确定该搜索是从哪个目录发起的（窗口 {0}）。",
        ScopeDirectoryMissingTemplate = "解析出的目录已不存在：{0}",
        ScopeWindowGoneTemplate = "Explorer 窗口（{0}）在解析搜索范围前已关闭。",
        ScopeLocationUnavailableTemplate = "无法读取 Explorer 当前位置：{0}",
        ScopeNotRedirectedNote = "本次搜索没有重定向到 Everything。",
        SettingsTitle = "Explorer Everything Search - 设置",
        SettingsEnabled = "启用 Explorer 搜索监听",
        SettingsAutoSearchDelay = "停止输入后自动搜索延迟（毫秒）",
        SettingsEverythingPath = "Everything.exe 路径（留空则自动检测）",
        SettingsEsPath = "es.exe 路径（可选）",
        SettingsStartWithWindows = "开机启动（当前用户）",
        SettingsShowNotifications = "显示状态通知",
        SettingsLoggingEnabled = "写入日志文件",
        SettingsLogLevel = "日志级别",
        SettingsLanguage = "界面语言",
        SettingsReuseWindow = "复用 Everything 搜索窗口",
        SettingsDetectEnterFocus = "按焦点变化识别 Enter",
        SettingsDetectEnterCommit = "按 Explorer 提交时序识别 Enter",
        SettingsDetectEnterCommitWindow = "提交时序判定窗口（毫秒）",
        SettingsExplorerRescan = "Explorer 自愈重扫间隔（秒，0 = 关闭）",
        SettingsMaxLogSize = "单个日志文件上限（MB）",
        SettingsBrowse = "浏览...",
        SettingsSave = "保存",
        SettingsCancel = "取消",
        SettingsTestConnection = "测试 / 重新连接",
        SettingsOpenLogs = "打开日志",
        SettingsClearLogs = "清理日志",
        SettingsSaveFailed = "配置保存失败：{0}",
        SettingsSaveSucceeded = "配置已保存。",
        SettingsStartupStale = "开机启动项指向了其他位置。",
        SettingsStartupRepair = "修复开机启动项",
        SettingsEverythingDetection = "检测结果",
        SettingsLanguageAuto = "自动（跟随 Windows 语言）",
        SettingsConfigPath = "配置文件",
        SettingsRootDirectory = "程序目录",
        AlreadyRunning = "Explorer Everything Search 已在运行（请查看通知区域）。",
        StartupStaleRepaired = "开机启动项已失效，已自动更新。",
        HelpText = "ExplorerEverythingSearch [--startup] [--settings] [--exit] [--root <目录>] [--version] [--help]",
        EverythingNotConfiguredHint = "如果未能自动检测到 Everything，请在设置中指定 Everything.exe 路径。",
    };
}

/// <summary>Current language selection.</summary>
public static class Strings
{
    private static LocalizedStrings _current = LocalizedStrings.English;

    public static LocalizedStrings Current => _current;

    public static void Initialize(string? configuredLanguage, CultureInfo? uiCulture = null)
    {
        var culture = uiCulture ?? CultureInfo.CurrentUICulture;
        var code = (configuredLanguage ?? "auto").Trim().ToLowerInvariant();
        _current = code switch
        {
            "zh-cn" => LocalizedStrings.Chinese,
            "en-us" => LocalizedStrings.English,
            _ => culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? LocalizedStrings.Chinese
                : LocalizedStrings.English,
        };
    }

    public static string Format(string template, params object?[] values)
    {
        try { return string.Format(CultureInfo.CurrentCulture, template, values); }
        catch (FormatException) { return template; }
    }
}
