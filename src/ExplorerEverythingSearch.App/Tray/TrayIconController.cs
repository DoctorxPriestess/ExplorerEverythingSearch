using System.Drawing;
using System.Windows.Forms;
using ExplorerEverythingSearch.App.Localization;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Everything;

namespace ExplorerEverythingSearch.App.Tray;

/// <summary>
/// Notification area icon: status, enable/pause, settings, Everything reconnect, log management
/// and exit - as required by the specification.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly AppRoot _root;
    private readonly AppLogger _logger;
    private readonly Icon _icon;

    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _menu;
    private ToolStripMenuItem? _statusExplorer;
    private ToolStripMenuItem? _statusEverything;
    private ToolStripMenuItem? _toggleItem;
    private ToolStripMenuItem? _loggingItem;
    private bool _disposed;

    public TrayIconController(AppRoot root, AppLogger logger)
    {
        _root = root;
        _logger = logger;
        _icon = TrayIconFactory.Create();
    }

    public void Create()
    {
        if (_notifyIcon is not null) return;

        _menu = new ContextMenuStrip();
        _statusExplorer = new ToolStripMenuItem { Enabled = false };
        _statusEverything = new ToolStripMenuItem { Enabled = false };
        _menu.Items.Add(_statusExplorer);
        _menu.Items.Add(_statusEverything);
        _menu.Items.Add(new ToolStripSeparator());

        _toggleItem = new ToolStripMenuItem();
        _toggleItem.Click += (_, _) => _root.SetEnabled(!_root.IsEnabled);
        _menu.Items.Add(_toggleItem);

        var settings = new ToolStripMenuItem();
        settings.Click += (_, _) => _root.ShowSettings();
        _menu.Items.Add(settings);

        var reconnect = new ToolStripMenuItem();
        reconnect.Click += (_, _) =>
        {
            var state = _root.ReconnectEverything();
            var strings = Strings.Current;
            var body = state.Available
                ? Strings.Format(strings.StatusConnected, state.Version?.ToString() ?? "?")
                : strings.StatusUnavailable;
            ShowBalloon(strings.NotifyEverythingTitle, body, !state.Available);
        };
        _menu.Items.Add(reconnect);

        var openLogs = new ToolStripMenuItem();
        openLogs.Click += (_, _) => _root.OpenLogFolder();
        _menu.Items.Add(openLogs);

        var clearLogs = new ToolStripMenuItem();
        clearLogs.Click += (_, _) => _root.ClearLogs();
        _menu.Items.Add(clearLogs);

        _loggingItem = new ToolStripMenuItem { CheckOnClick = false };
        _loggingItem.Click += (_, _) => _root.SetLoggingEnabled(!_root.Config.LoggingEnabled);
        _menu.Items.Add(_loggingItem);

        _menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem();
        exit.Click += (_, _) => _root.RequestShutdown();
        _menu.Items.Add(exit);
        ExitItem = exit;
        SettingsItem = settings;
        ReconnectItem = reconnect;
        OpenLogsItem = openLogs;
        ClearLogsItem = clearLogs;

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => _root.ShowSettings();

        RefreshMenu();
        _logger.Debug("notification area icon created");
    }

    private ToolStripMenuItem? ExitItem { get; set; }

    private ToolStripMenuItem? SettingsItem { get; set; }

    private ToolStripMenuItem? ReconnectItem { get; set; }

    private ToolStripMenuItem? OpenLogsItem { get; set; }

    private ToolStripMenuItem? ClearLogsItem { get; set; }

    /// <summary>Updates every label to the current configuration and Everything state.</summary>
    public void RefreshMenu()
    {
        if (_menu is null) return;
        var strings = Strings.Current;
        var enabled = _root.IsEnabled;
        var state = _root.Everything.Probe();

        if (_toggleItem is not null)
        {
            _toggleItem.Text = enabled ? strings.MenuPause : strings.MenuEnable;
            _toggleItem.Checked = enabled;
        }
        if (_statusExplorer is not null)
            _statusExplorer.Text = Strings.Format(strings.TrayExplorerSearch, enabled ? strings.StatusEnabled : strings.StatusDisabled);
        if (_statusEverything is not null)
            _statusEverything.Text = Strings.Format(
                strings.TrayEverything,
                state.Available
                    ? Strings.Format(strings.StatusConnected, state.Version?.ToString() ?? "?")
                    : strings.StatusUnavailable);
        if (_loggingItem is not null)
        {
            _loggingItem.Text = _root.Config.LoggingEnabled ? strings.MenuLoggingOn : strings.MenuLoggingOff;
            _loggingItem.Checked = _root.Config.LoggingEnabled;
        }

        if (SettingsItem is not null) SettingsItem.Text = strings.MenuSettings;
        if (ReconnectItem is not null) ReconnectItem.Text = strings.MenuReconnect;
        if (OpenLogsItem is not null) OpenLogsItem.Text = strings.MenuOpenLogs;
        if (ClearLogsItem is not null) ClearLogsItem.Text = strings.MenuClearLogs;
        if (ExitItem is not null) ExitItem.Text = strings.MenuExit;

        if (_notifyIcon is not null)
        {
            // NotifyIcon.Text is limited to 63 characters, so the tooltip stays short.
            var monitoring = enabled ? "ON" : "OFF";
            var everything = state.Available ? "OK" : "N/A";
            var text = $"Explorer Everything Search [{monitoring}] Everything:{everything}";
            _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
        }
    }

    public void ShowBalloon(string title, string body, bool isError)
    {
        try
        {
            if (_notifyIcon is null) return;
            _notifyIcon.ShowBalloonTip(
                8000,
                title,
                body,
                isError ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _logger.Debug($"could not show a balloon notification: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }
        catch { }
        try { _menu?.Dispose(); } catch { }
        try { _icon.Dispose(); } catch { }
    }
}
