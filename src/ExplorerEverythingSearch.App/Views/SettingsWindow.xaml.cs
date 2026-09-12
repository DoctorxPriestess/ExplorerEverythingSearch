using System.Windows;
using ExplorerEverythingSearch.App.Localization;
using ExplorerEverythingSearch.Core.Configuration;
using ExplorerEverythingSearch.Core.Diagnostics;

namespace ExplorerEverythingSearch.App.Views;

/// <summary>
/// Settings window. Kept as plain code-behind on purpose: the tool has a single small dialog and no
/// data binding infrastructure is warranted.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppRoot _root;

    public SettingsWindow(AppRoot root)
    {
        _root = root;
        InitializeComponent();
        LoadFromConfig();
    }

    private static LocalizedStrings Strings => Localization.Strings.Current;

    private void LoadFromConfig()
    {
        var strings = Strings;
        var config = _root.Config;

        Title = strings.SettingsTitle;
        RootLabel.Text = strings.SettingsRootDirectory;
        RootValue.Text = _root.RootDirectory;
        ConfigLabel.Text = strings.SettingsConfigPath;
        ConfigValue.Text = _root.ConfigPath;

        EnabledCheck.Content = strings.SettingsEnabled;
        EnabledCheck.IsChecked = config.Enabled;

        ReuseWindowCheck.Content = strings.SettingsReuseWindow;
        ReuseWindowCheck.IsChecked = config.ReuseEverythingWindow;

        EnterFocusCheck.Content = strings.SettingsDetectEnterFocus;
        EnterFocusCheck.IsChecked = config.DetectEnterByFocusChange;

        EnterCommitCheck.Content = strings.SettingsDetectEnterCommit;
        EnterCommitCheck.IsChecked = config.DetectEnterByCommitTiming;

        DelayLabel.Text = strings.SettingsAutoSearchDelay;
        DelayBox.Text = config.AutoSearchDelay.ToString();

        EnterWindowLabel.Text = strings.SettingsDetectEnterCommitWindow;
        EnterWindowBox.Text = config.EnterCommitWindowMs.ToString();

        RescanLabel.Text = strings.SettingsExplorerRescan;
        RescanBox.Text = config.ExplorerRescanSeconds.ToString();

        EverythingPathLabel.Text = strings.SettingsEverythingPath;
        EverythingPathBox.Text = config.EverythingPath;

        EsPathLabel.Text = strings.SettingsEsPath;
        EsPathBox.Text = config.EsPath;

        BrowseEverythingButton.Content = strings.SettingsBrowse;
        BrowseEsButton.Content = strings.SettingsBrowse;

        DetectionLabel.Text = strings.SettingsEverythingDetection;
        UpdateDetectionText();

        StartupCheck.Content = strings.SettingsStartWithWindows;
        StartupCheck.IsChecked = config.StartWithWindows;
        StartupRepairButton.Content = strings.SettingsStartupRepair;
        UpdateStartupWarning();

        NotificationsCheck.Content = strings.SettingsShowNotifications;
        NotificationsCheck.IsChecked = config.ShowNotifications;

        LoggingCheck.Content = strings.SettingsLoggingEnabled;
        LoggingCheck.IsChecked = config.LoggingEnabled;

        LogLevelLabel.Text = strings.SettingsLogLevel;
        LogLevelCombo.ItemsSource = new[] { "Trace", "Debug", "Information", "Warning", "Error", "None" };
        LogLevelCombo.SelectedItem = LogLevels.Normalize(config.LogLevel);

        MaxLogSizeLabel.Text = strings.SettingsMaxLogSize;
        MaxLogSizeBox.Text = config.MaxLogFileSizeMb.ToString();

        LanguageLabel.Text = strings.SettingsLanguage;
        LanguageCombo.ItemsSource = new[] { strings.SettingsLanguageAuto, "English", "简体中文" };
        LanguageCombo.SelectedIndex = config.Language switch
        {
            "zh-CN" => 2,
            "en-US" => 1,
            _ => 0,
        };

        TestButton.Content = strings.SettingsTestConnection;
        OpenLogsButton.Content = strings.SettingsOpenLogs;
        ClearLogsButton.Content = strings.SettingsClearLogs;
        SaveButton.Content = strings.SettingsSave;
        CancelButton.Content = strings.SettingsCancel;
        StatusText.Text = string.Empty;
    }

    private void UpdateDetectionText()
    {
        var state = _root.Everything.Probe();
        DetectionValue.Text = state.Available
            ? $"Everything {state.Version?.ToString() ?? "?"} - {state.ExecutablePath ?? "?"}"
            : $"{Strings.StatusUnavailable} - {state.Error ?? "Everything not found"}";
    }

    private void UpdateStartupWarning()
    {
        var stale = _root.IsStartupEntryStale();
        StartupStaleText.Text = stale ? Strings.SettingsStartupStale : string.Empty;
        StartupStaleText.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        StartupRepairButton.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
    }

    private AppConfig CollectConfig()
    {
        var config = _root.Config.Clone();
        config.Enabled = EnabledCheck.IsChecked == true;
        config.ReuseEverythingWindow = ReuseWindowCheck.IsChecked == true;
        config.DetectEnterByFocusChange = EnterFocusCheck.IsChecked == true;
        config.DetectEnterByCommitTiming = EnterCommitCheck.IsChecked == true;
        config.AutoSearchDelay = ParseInt(DelayBox.Text, config.AutoSearchDelay);
        config.EnterCommitWindowMs = ParseInt(EnterWindowBox.Text, config.EnterCommitWindowMs);
        config.ExplorerRescanSeconds = ParseInt(RescanBox.Text, config.ExplorerRescanSeconds);
        config.EverythingPath = EverythingPathBox.Text.Trim();
        config.EsPath = EsPathBox.Text.Trim();
        config.StartWithWindows = StartupCheck.IsChecked == true;
        config.ShowNotifications = NotificationsCheck.IsChecked == true;
        config.LoggingEnabled = LoggingCheck.IsChecked == true;
        config.LogLevel = LogLevelCombo.SelectedItem as string ?? config.LogLevel;
        config.MaxLogFileSizeMb = ParseInt(MaxLogSizeBox.Text, config.MaxLogFileSizeMb);
        config.Language = LanguageCombo.SelectedIndex switch
        {
            1 => "en-US",
            2 => "zh-CN",
            _ => "auto",
        };
        return config.Normalize();
    }

    private static int ParseInt(string? text, int fallback)
        => int.TryParse((text ?? string.Empty).Trim(), out var value) ? value : fallback;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var config = CollectConfig();
        if (_root.SaveConfig(config, out var error))
        {
            StatusText.Text = Strings.SettingsSaveSucceeded;
            LoadFromConfig();
            return;
        }

        StatusText.Text = Localization.Strings.Format(Strings.SettingsSaveFailed, error ?? "unknown error");
        MessageBox.Show(StatusText.Text, Strings.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BrowseEverything_Click(object sender, RoutedEventArgs e) => BrowseFor(EverythingPathBox, "Everything.exe");

    private void BrowseEs_Click(object sender, RoutedEventArgs e) => BrowseFor(EsPathBox, "es.exe");

    private void BrowseFor(System.Windows.Controls.TextBox target, string suggestedName)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            FileName = suggestedName,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName;
    }

    private void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var state = _root.ReconnectEverything();
        UpdateDetectionText();
        UpdateStartupWarning();
        StatusText.Text = state.Available
            ? Localization.Strings.Format(Strings.StatusConnected, state.Version?.ToString() ?? "?")
            : Strings.StatusUnavailable;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => _root.OpenLogFolder();

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        _root.ClearLogs();
        StatusText.Text = Strings.NotifyLogsClearedBody;
    }

    private void RepairStartup_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = _root.RepairStartupEntry(out var error)
            ? Strings.StartupStaleRepaired
            : Localization.Strings.Format(Strings.SettingsSaveFailed, error ?? "unknown error");
        UpdateStartupWarning();
    }
}
