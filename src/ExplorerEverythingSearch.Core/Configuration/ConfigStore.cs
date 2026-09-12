using System.Text.Json;
using ExplorerEverythingSearch.Core.Diagnostics;

namespace ExplorerEverythingSearch.Core.Configuration;

/// <summary>
/// Loads and saves <c>config.json</c> next to the executable (portable layout, never %AppData%).
/// All IO problems are reported through <see cref="LastError"/>; the store never throws, so a
/// read-only install directory degrades to in-memory defaults instead of breaking the tool.
/// </summary>
public sealed class ConfigStore
{
    private readonly AppLogger _logger;
    private readonly object _sync = new();

    public ConfigStore(string rootDirectory, AppLogger logger)
    {
        RootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ConfigPath = Path.Combine(RootDirectory, "config.json");
        Current = new AppConfig();
    }

    public string RootDirectory { get; }

    public string ConfigPath { get; }

    public AppConfig Current { get; private set; }

    /// <summary>True when the configuration could not be persisted (read-only media, ACLs).</summary>
    public bool IsPersistDisabled { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Reads the configuration file, creating it with defaults when missing.</summary>
    public AppConfig Load()
    {
        lock (_sync)
        {
            LastError = null;
            if (!File.Exists(ConfigPath))
            {
                Current = new AppConfig().Normalize();
                if (!SaveInternal(Current, out var createError))
                {
                    IsPersistDisabled = true;
                    LastError = createError;
                    _logger.Warn($"could not create {ConfigPath}: {createError}");
                }
                else
                {
                    _logger.Info($"configuration created at {ConfigPath}");
                }
                return Current;
            }

            try
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions);
                Current = (config ?? new AppConfig()).Normalize();
            }
            catch (Exception ex)
            {
                // A corrupt or unreadable config must never prevent startup.
                Current = new AppConfig().Normalize();
                LastError = ex.Message;
                _logger.Error($"could not read {ConfigPath}, using defaults", ex);
            }
            return Current;
        }
    }

    /// <summary>Persists the configuration atomically (temp file + replace).</summary>
    public bool Save(AppConfig config, out string? error)
    {
        lock (_sync)
        {
            var normalized = config.Normalize();
            if (!SaveInternal(normalized, out error))
            {
                IsPersistDisabled = true;
                LastError = error;
                _logger.Warn($"could not write {ConfigPath}: {error}");
                return false;
            }

            IsPersistDisabled = false;
            LastError = null;
            Current = normalized;
            return true;
        }
    }

    private bool SaveInternal(AppConfig config, out string? error)
    {
        error = null;
        var tempPath = ConfigPath + ".tmp";
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var json = JsonSerializer.Serialize(config, AppConfig.SerializerOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return false;
        }
    }
}
