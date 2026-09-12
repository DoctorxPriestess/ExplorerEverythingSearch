using System.Diagnostics;
using System.Globalization;
using System.Windows;
using ExplorerEverythingSearch.App.Localization;
using ExplorerEverythingSearch.App.Notifications;
using ExplorerEverythingSearch.App.Tray;
using ExplorerEverythingSearch.App.Views;
using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Everything;
using ExplorerEverythingSearch.Core.Explorer;
using ExplorerEverythingSearch.Core.Search;
using ExplorerEverythingSearch.Core.Shell;
using ExplorerEverythingSearch.Core.Startup;
using ExplorerEverythingSearch.Core.Threading;

namespace ExplorerEverythingSearch.App;

/// <summary>
/// Composition root and application controller: owns the configuration, the structured logger, the
/// STA dispatcher used by UI Automation and the Shell, the Explorer monitor, the Everything bridge
/// and the notification area icon.
/// </summary>
public sealed class AppRoot : IDisposable
{
    private readonly CommandLineOptions _options;
    private readonly ConfigStore _configStore;
    private readonly AppLogger _logger;
    private readonly StaDispatcher _sta;
    private readonly WindowScopeTracker _scopeTracker = new();
    private readonly NotificationService _notifications;
    private readonly EverythingLocator _everythingLocator;
    private readonly EverythingBridge _everythingBridge;
    private readonly SearchCoordinator _coordinator;
    private readonly ExplorerWindowMonitor _monitor;
    private readonly StartupRegistration _startupRegistration;
    private readonly SingleInstanceGuard? _singleInstance;

    private TrayIconController? _tray;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;

    public AppRoot(CommandLineOptions options, SingleInstanceGuard? singleInstance = null)
    {
        _options = options;
        _singleInstance = singleInstance;
        RootDirectory = AppPaths.ResolveRoot(options.RootDirectory);

        // The logger starts without a log file: the configuration decides whether a log file may be
        // created at all, and until it is loaded nothing is written to disk.
        _logger = AppLogger.Create(RootDirectory, new AppConfig(), fileLogging: false);
        _configStore = new ConfigStore(RootDirectory, _logger);
        var config = _configStore.Load();

        _logger.SetLevel(LogLevels.Parse(config.LogLevel));
        _logger.SetFileLogging(config.LoggingEnabled);
        _logger.Info($"configuration loaded from {_configStore.ConfigPath}"
                     + (_configStore.LastError is { Length: > 0 } error
                         ? $" (defaults are used in memory: {error})"
                         : string.Empty));

        Strings.Initialize(config.Language, CultureInfo.CurrentUICulture);
        ApplicationDirectory = RootDirectory;
        ConfigPath = _configStore.ConfigPath;

        _sta = new StaDispatcher("EES-STA");
        var locationResolver = new ShellAutomationLocationResolver();
        var scopeResolver = new SearchScopeResolver(locationResolver, new KnownFolderResolver(), _scopeTracker, _logger);
        _everythingLocator = new EverythingLocator(() => Config, _logger);
        _everythingBridge = new EverythingBridge(_everythingLocator, _logger, () => Config, hwnd => _monitor?.FocusSearchBox(hwnd));
        _coordinator = new SearchCoordinator(() => Config, _logger, scopeResolver, _sta, _everythingBridge);
        _notifications = new NotificationService(
            _logger,
            () => Config.ShowNotifications,
            (title, body, isError) => _tray?.ShowBalloon(title, body, isError),
            DispatchToUi);
        _coordinator.ScopeNotResolved += (resolution, text) => _notifications.ReportUnsupportedScope(resolution, text);
        _coordinator.EverythingUnavailable += (text, error) => _notifications.ReportEverythingUnavailable(text, error);

        _monitor = new ExplorerWindowMonitor(
            () => Config,
            _logger,
            _sta,
            _coordinator,
            hwnd => _coordinator.Forget(hwnd),
            scopeResolver);
        _startupRegistration = new StartupRegistration(new HkcuRunRegistry(), AppPaths.ExecutablePath);
    }

    public AppConfig Config => _configStore.Current;

    public string RootDirectory { get; }

    public string ApplicationDirectory { get; }

    public string ConfigPath { get; }

    public AppLogger Logger => _logger;

    public EverythingBridge Everything => _everythingBridge;

    public ExplorerWindowMonitor Monitor => _monitor;

    public bool IsEnabled => Config.Enabled;

    /// <summary>Creates and wires the tray icon, then starts monitoring according to the configuration.</summary>
    public void Start()
    {
        _logger.Info($"Explorer Everything Search {typeof(AppRoot).Assembly.GetName().Version} starting");
        _logger.Info($"application directory: {RootDirectory}");
        _logger.Info($"configuration: {ConfigPath}");
        _logger.Info($"OS: {Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture})");

        var everythingState = _everythingBridge.Probe();
        _logger.Info($"Everything: {everythingState.Display}"
                     + (everythingState.ExecutablePath is null ? string.Empty : $" path={everythingState.ExecutablePath}"));

        _tray = new TrayIconController(this, _logger);
        _tray.Create();

        ApplyStartWithWindows(logAlways: true);
        ApplyMonitoringState();

        if (_configStore.IsPersistDisabled)
        {
            _notifications.ReportInformation(
                Strings.Current.NotifyConfigNotWritableTitle,
                Strings.Format(Strings.Current.NotifyConfigNotWritableBody, _configStore.LastError ?? "unknown error"),
                "config-readonly",
                logMessage: $"configuration is not writable ({_configStore.LastError}); changes are kept in memory only");
        }

        _singleInstance?.StartListening(ShowSettings, RequestShutdown);

        if (_options.OpenSettings) ShowSettings();
    }

    // ------------------------------------------------------------------ configuration

    /// <summary>Persists a new configuration and applies everything that can change at runtime.</summary>
    public bool SaveConfig(AppConfig config, out string? error)
    {
        var languageBefore = Config.Language;
        if (!_configStore.Save(config, out error)) return false;

        var current = Config;
        _logger.SetLevel(LogLevels.Parse(current.LogLevel));
        _logger.SetFileLogging(current.LoggingEnabled);
        if (!string.Equals(languageBefore, current.Language, StringComparison.OrdinalIgnoreCase))
            Strings.Initialize(current.Language, CultureInfo.CurrentUICulture);

        _everythingLocator.Invalidate();
        ApplyStartWithWindows(logAlways: false);
        ApplyMonitoringState();
        _tray?.RefreshMenu();
        return true;
    }

    public void SetEnabled(bool enabled)
    {
        var config = Config.Clone();
        config.Enabled = enabled;
        SaveConfig(config, out _);
        _logger.Info(enabled ? "monitoring enabled by the user" : "monitoring paused by the user");
    }

    public bool SetLoggingEnabled(bool enabled)
    {
        var config = Config.Clone();
        config.LoggingEnabled = enabled;
        _logger.Info(enabled ? "logging enabled" : "logging disabled");
        var saved = SaveConfig(config, out _);
        _logger.SetFileLogging(enabled);
        _tray?.RefreshMenu();
        return saved;
    }

    // ------------------------------------------------------------------ tray actions

    public EverythingConnectionState ReconnectEverything()
    {
        var state = _everythingBridge.Reconnect();
        _tray?.RefreshMenu();
        return state;
    }

    public string LogDirectory => _logger.LogDirectory;

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_logger.LogDirectory);
            Process.Start(new ProcessStartInfo(_logger.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error("could not open the log folder", ex);
        }
    }

    public void ClearLogs()
    {
        _logger.Info("clearing log files");
        var cleared = _logger.ClearLogs(out var error);
        if (cleared)
        {
            _notifications.ReportInformation(
                Strings.Current.NotifyLogsClearedTitle,
                Strings.Current.NotifyLogsClearedBody,
                "logs-cleared",
                logMessage: "log files cleared; logging continues");
        }
        else
        {
            _logger.Warn($"clearing logs failed: {error}");
            _notifications.ReportInformation(
                Strings.Current.NotifyLogsClearedTitle,
                Strings.Format(Strings.Current.NotifyLogsClearFailedBody, error ?? "unknown error"),
                "logs-clear-failed",
                logMessage: $"clearing logs failed: {error}");
        }
        _logger.Info(cleared ? "log files cleared" : "log files could not be cleared");
    }

    public void ShowSettings()
    {
        DispatchToUi(() =>
        {
            try
            {
                if (_settingsWindow is { IsLoaded: true })
                {
                    if (_settingsWindow.WindowState == WindowState.Minimized)
                        _settingsWindow.WindowState = WindowState.Normal;
                    _settingsWindow.Activate();
                    return;
                }

                _settingsWindow = new SettingsWindow(this);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
                _settingsWindow.Activate();
            }
            catch (Exception ex)
            {
                _logger.Error("could not open the settings window", ex);
            }
        });
    }

    public void RequestShutdown() => DispatchToUi(() => Application.Current?.Shutdown());

    // ------------------------------------------------------------------ internals

    private void ApplyMonitoringState()
    {
        if (Config.Enabled && !_monitor.IsRunning) _monitor.Start();
        else if (!Config.Enabled && _monitor.IsRunning) _monitor.Stop();
        _tray?.RefreshMenu();
    }

    private void ApplyStartWithWindows(bool logAlways)
    {
        var status = _startupRegistration.GetStatus();
        if (Config.StartWithWindows)
        {
            if (!status.Registered || !status.PointsToCurrentExecutable)
            {
                var repaired = status.Registered;
                if (_startupRegistration.Register(out var error))
                {
                    if (logAlways || repaired)
                        _logger.Info(repaired
                            ? $"start with Windows entry repaired: {_startupRegistration.ExpectedValue}"
                            : $"start with Windows entry created: {_startupRegistration.ExpectedValue}");

                    if (repaired)
                    {
                        _notifications.ReportInformation(
                            Strings.Current.NotifyStartupRepairedTitle,
                            Strings.Format(Strings.Current.NotifyStartupRepairedBody, AppPaths.ExecutablePath),
                            "startup-repaired",
                            logMessage: $"start with Windows entry repaired to \"{AppPaths.ExecutablePath}\" (the previous entry pointed elsewhere)");
                    }
                }
                else
                {
                    _logger.Warn($"could not register start with Windows: {error}");
                }
            }
            else if (logAlways)
            {
                _logger.Info("start with Windows entry is valid");
            }
        }
        else if (status.Registered)
        {
            if (_startupRegistration.Unregister(out var error))
                _logger.Info("start with Windows entry removed");
            else
                _logger.Warn($"could not remove the start with Windows entry: {error}");
        }
    }

    /// <summary>True when a start-up entry exists but points somewhere else (the EXE was moved).</summary>
    public bool IsStartupEntryStale() => _startupRegistration.IsStale();

    public bool RepairStartupEntry(out string? error) => _startupRegistration.Repair(out error);

    public StartupStatus GetStartupStatus() => _startupRegistration.GetStatus();

    private static void DispatchToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _monitor.Dispose(); } catch { }
        try { _coordinator.Dispose(); } catch { }
        try { _sta.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        _logger.Info("Explorer Everything Search stopped");
        try { _logger.Dispose(); } catch { }
    }
}
