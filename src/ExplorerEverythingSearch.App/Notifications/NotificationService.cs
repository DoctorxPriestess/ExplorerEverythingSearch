using ExplorerEverythingSearch.App.Localization;
using ExplorerEverythingSearch.Core.Diagnostics;
using ExplorerEverythingSearch.Core.Shell;

namespace ExplorerEverythingSearch.App.Notifications;

/// <summary>
/// Turns failures into user visible messages.
///
/// Policy (agreed with the requirement that an unsupported search location must never fail silently):
/// - an unsupported / unresolvable search scope shows a modal message box the first time, and tray
///   balloons afterwards, so the user always learns that this search was not redirected;
/// - "Everything is unavailable" is reported the same way, because the tool cannot work without it;
/// - purely informational messages (logs cleared, read-only config, ...) follow the
///   <c>showNotifications</c> setting.
/// </summary>
public sealed class NotificationService
{
    private readonly AppLogger _logger;
    private readonly Func<bool> _showInformational;
    private readonly Action<string, string, bool> _balloon;
    private readonly Action<Action> _dispatch;
    private readonly object _sync = new();
    private readonly HashSet<string> _dialogShownFor = new();

    public NotificationService(
        AppLogger logger,
        Func<bool> showInformational,
        Action<string, string, bool> balloon,
        Action<Action> dispatch)
    {
        _logger = logger;
        _showInformational = showInformational;
        _balloon = balloon;
        _dispatch = dispatch;
    }

    /// <summary>Reports a scope that this version cannot redirect to Everything.</summary>
    public void ReportUnsupportedScope(ScopeResolution resolution, string searchText)
    {
        var strings = Strings.Current;
        var detail = Describe(resolution);
        var body = strings.NotifyUnsupportedBody + Environment.NewLine + Environment.NewLine
                   + detail + Environment.NewLine + strings.ScopeNotRedirectedNote;

        _logger.Warn($"search not redirected ({resolution.Failure}) text=\"{searchText}\" {detail.Replace(Environment.NewLine, " ")}");
        ShowError("unsupported-scope", strings.NotifyUnsupportedTitle, body);
    }

    /// <summary>Reports that Everything could not be located or driven.</summary>
    public void ReportEverythingUnavailable(string searchText, string error)
    {
        var strings = Strings.Current;
        var isMissing = error.Contains("not found", StringComparison.OrdinalIgnoreCase);
        var body = isMissing
            ? strings.NotifyEverythingNotFoundBody + Environment.NewLine + strings.EverythingNotConfiguredHint
            : Strings.Format(strings.NotifyEverythingFailedBody, error);

        _logger.Error($"Everything unavailable for search \"{searchText}\": {error}");
        ShowError(isMissing ? "everything-not-found" : "everything-failed", strings.NotifyEverythingTitle, body);
    }

    /// <summary>Informational message; suppressed unless notifications are enabled.</summary>
    public void ReportInformation(string title, string body, string dedupeKey, string? logMessage = null)
    {
        _logger.Info(logMessage ?? $"{title}: {body}");
        if (!_showInformational()) return;
        ShowBalloonOnce(dedupeKey, title, body);
    }

    private void ShowError(string key, string title, string body)
    {
        bool firstTime;
        lock (_sync) firstTime = _dialogShownFor.Add(key);

        if (firstTime)
        {
            _dispatch(() => System.Windows.MessageBox.Show(
                body,
                title,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning));
            return;
        }

        _balloon(title, body, true);
    }

    private void ShowBalloonOnce(string key, string title, string body)
    {
        bool firstTime;
        lock (_sync) firstTime = _dialogShownFor.Add(key);
        if (!firstTime) return;
        _balloon(title, body, false);
    }

    /// <summary>Builds the human readable explanation of a scope failure.</summary>
    public static string Describe(ScopeResolution resolution)
    {
        var strings = Strings.Current;
        return resolution.Failure switch
        {
            ScopeFailureReason.UnsupportedShellNamespace => Strings.Format(strings.ScopeUnsupportedTemplate, resolution.Detail),
            ScopeFailureReason.SearchScopeUnknown => Strings.Format(strings.ScopeSearchUnknownTemplate, resolution.RawLocation),
            ScopeFailureReason.DirectoryUnavailable => Strings.Format(strings.ScopeDirectoryMissingTemplate, resolution.Detail),
            ScopeFailureReason.ExplorerWindowNotFound => Strings.Format(strings.ScopeWindowGoneTemplate, resolution.RawLocation),
            _ => Strings.Format(strings.ScopeLocationUnavailableTemplate, resolution.Detail),
        };
    }

    /// <summary>Maps a ScopeResolution to the notification that has to be shown (or none).</summary>
    public void ReportScopeFailure(ScopeResolution resolution, string searchText)
        => ReportUnsupportedScope(resolution, searchText);
}
