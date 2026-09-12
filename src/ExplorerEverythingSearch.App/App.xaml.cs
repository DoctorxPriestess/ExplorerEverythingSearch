using System.Diagnostics;
using System.Windows;
using ExplorerEverythingSearch.App.Localization;

namespace ExplorerEverythingSearch.App;

/// <summary>
/// Application entry point. Runs in the notification area without a main window; the settings window
/// is created on demand.
/// </summary>
public partial class App : Application
{
    private AppRoot? _root;
    private SingleInstanceGuard? _guard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = CommandLineOptions.Parse(e.Args);
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        if (options.ShowVersion)
        {
            MessageBox.Show($"Explorer Everything Search {version}", "Explorer Everything Search", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        if (options.ShowHelp)
        {
            MessageBox.Show(CommandLineOptionsHelp(), "Explorer Everything Search", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // An explicit --root produces an independent instance (used by the automated tests).
        var root = AppPaths.ResolveRoot(options.RootDirectory);
        var instanceKey = string.Equals(root, AppPaths.ExecutableDirectory, StringComparison.OrdinalIgnoreCase) ? null : root;

        _guard = new SingleInstanceGuard(instanceKey);
        if (!_guard.IsPrimary)
        {
            if (options.RequestExit) SingleInstanceGuard.RequestExistingInstanceExit(instanceKey);
            else SingleInstanceGuard.RequestExistingInstanceOpenSettings(instanceKey);
            Shutdown(0);
            return;
        }

        try
        {
            _root = new AppRoot(options, _guard);
            _root.Start();
        }
        catch (Exception ex)
        {
            var strings = Strings.Current;
            MessageBox.Show(
                $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{Environment.NewLine}{ex.StackTrace}",
                "Explorer Everything Search - startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _root?.Dispose();
            _root = null;
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _root?.Dispose(); } catch { }
        try { _guard?.Dispose(); } catch { }
        base.OnExit(e);
    }

    private static string CommandLineOptionsHelp()
    {
        var lines = new[]
        {
            Strings.Current.HelpText,
            string.Empty,
            "--startup           start minimised in the notification area (used by the Run entry)",
            "--settings          open the settings window",
            "--exit              stop the running instance",
            "--root <directory>  store config.json and logs in <directory>",
            "--version           show the version",
        };
        return string.Join(Environment.NewLine, lines);
    }
}
