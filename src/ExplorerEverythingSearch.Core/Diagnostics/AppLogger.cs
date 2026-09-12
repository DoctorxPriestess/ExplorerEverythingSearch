using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using ExplorerEverythingSearch.Core.Configuration;

namespace ExplorerEverythingSearch.Core.Diagnostics;

/// <summary>
/// Structured, asynchronous logger writing to <c>&lt;root&gt;\logs\app.log</c>.
///
/// Design constraints taken from the specification:
/// - must never block the caller (Explorer UI / UIA event thread),
/// - must not cause high frequency disk writes (batched, flushed once per batch),
/// - must be fully switchable off (no file is produced at all while disabled),
/// - must support safe log cleanup while running (flush + close + delete + reopen),
/// - must never throw into the application.
/// </summary>
public sealed class AppLogger : IDisposable
{
    private readonly string _logDirectory;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _writer;
    private readonly object _maintenanceLock = new();
    /// <summary>
    /// Completion of the maintenance request that is currently in flight. A fresh event is used per
    /// request and it is only ever set, never reset: a reset pulse can be missed by the waiter (it may
    /// be spinning or sleeping inside Wait) and the caller would then block for the whole timeout.
    /// </summary>
    private ManualResetEventSlim? _maintenanceCompletion;
    private readonly int _maxFileBytes;
    private readonly int _maxFiles;

    private int _maintenanceRequested; // 0 = none
    private volatile bool _fileLoggingEnabled;

    /// <summary>What the configuration asked for; the level can switch file logging off independently.</summary>
    private volatile bool _fileLoggingRequested;
    private volatile LogLevel _level;
    private volatile bool _disposed;
    private string? _currentLogPath;

    private AppLogger(string logDirectory, LogLevel level, bool fileLoggingEnabled, int maxFileSizeMb, int maxFiles)
    {
        _logDirectory = logDirectory;
        _level = level;
        _fileLoggingRequested = fileLoggingEnabled;
        _fileLoggingEnabled = fileLoggingEnabled && level != LogLevel.None;
        _maxFileBytes = Math.Max(1, maxFileSizeMb) * 1024 * 1024;
        _maxFiles = Math.Max(1, maxFiles);
        _writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "EES-LogWriter",
        };
        _writer.Start();
    }

    /// <summary>
    /// Creates the logger. <paramref name="fileLogging"/> is false while the configuration is being
    /// loaded, so a configuration that disables logging never produces a log file at all.
    /// </summary>
    public static AppLogger Create(string rootDirectory, AppConfig config, bool fileLogging = true)
    {
        var logDirectory = Path.Combine(rootDirectory, "logs");
        return new AppLogger(
            logDirectory,
            LogLevels.Parse(config.LogLevel),
            fileLogging && config.LoggingEnabled,
            config.MaxLogFileSizeMb,
            config.MaxLogFiles);
    }

    /// <summary>Creates a logger that is completely disabled (used by unit tests / --no-log runs).</summary>
    public static AppLogger CreateDisabled(string rootDirectory)
        => new(Path.Combine(rootDirectory, "logs"), LogLevel.None, fileLoggingEnabled: false, maxFileSizeMb: 1, maxFiles: 1);

    public string LogDirectory => _logDirectory;

    public string CurrentLogPath => Path.Combine(_logDirectory, "app.log");

    public bool FileLoggingEnabled => _fileLoggingEnabled;

    public LogLevel Level => _level;

    /// <summary>Enables/disables writing to disk. When disabled, no further log file is produced or written.</summary>
    public void SetFileLogging(bool enabled)
    {
        _fileLoggingRequested = enabled;
        _fileLoggingEnabled = enabled && _level != LogLevel.None;
        if (!_fileLoggingEnabled)
        {
            // Ask the writer to close the file handle so nothing keeps the file open while disabled.
            RequestMaintenance(closeOnly: true);
        }
    }

    public void SetLevel(LogLevel level)
    {
        _level = level;
        _fileLoggingEnabled = _fileLoggingRequested && level != LogLevel.None;
    }

    public bool IsEnabled(LogLevel level) => _fileLoggingEnabled && level >= _level;

    public void Trace(string message) => Write(LogLevel.Trace, message);
    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Information, message);
    public void Warn(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);
    public void Error(string message, Exception exception)
        => Write(LogLevel.Error, $"{message} | {exception.GetType().Name}: {exception.Message}");

    /// <summary>Queues one structured line. Never blocks and never throws.</summary>
    public void Write(LogLevel level, string message)
    {
        if (_disposed || !_fileLoggingEnabled || level < _level) return;
        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LogLevels.ToTag(level)}] {message}");
            _queue.Enqueue(line);
            _signal.Release();
        }
        catch
        {
            // Logging must never break the application.
        }
    }

    /// <summary>Waits until everything queued so far has been written and flushed.</summary>
    public void Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!_queue.IsEmpty && DateTime.UtcNow < deadline) Thread.Sleep(10);
    }

    /// <summary>
    /// Deletes the produced log files. Flushes and closes the current file first, deletes the
    /// files, and lets the writer reopen lazily. Never throws.
    /// </summary>
    public bool ClearLogs(out string? error)
    {
        error = null;
        if (!RequestMaintenance(closeOnly: false))
        {
            error = "log writer did not become idle in time";
            return false;
        }

        var failures = new List<string>();
        try
        {
            if (Directory.Exists(_logDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_logDirectory, "*.log"))
                {
                    var deleted = TryDelete(file);
                    if (!deleted) failures.Add(Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }

        if (failures.Count > 0)
        {
            error = "could not remove: " + string.Join(", ", failures);
            return false;
        }
        return true;
    }

    private static bool TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
        return !File.Exists(path);
    }

    /// <summary>Signals the writer thread to close the file (and optionally wait for it).</summary>
    private bool RequestMaintenance(bool closeOnly)
    {
        var completed = new ManualResetEventSlim(false);
        lock (_maintenanceLock)
        {
            if (_disposed)
            {
                completed.Dispose();
                return true;
            }
            _maintenanceCompletion = completed;
            Interlocked.Exchange(ref _maintenanceRequested, closeOnly ? 1 : 2);
        }

        _signal.Release();
        try
        {
            return completed.Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            lock (_maintenanceLock)
            {
                if (ReferenceEquals(_maintenanceCompletion, completed)) _maintenanceCompletion = null;
            }
            completed.Dispose();
        }
    }

    /// <summary>Called by the writer thread once it has closed the file for a maintenance request.</summary>
    private void CompleteMaintenance()
    {
        ManualResetEventSlim? completed;
        lock (_maintenanceLock) completed = _maintenanceCompletion;
        try { completed?.Set(); } catch (ObjectDisposedException) { }
    }

    private void WriterLoop()
    {
        StreamWriter? writer = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                _signal.Wait(_shutdown.Token);
                if (_shutdown.IsCancellationRequested) break;

                var wrote = false;
                while (_queue.TryDequeue(out var line))
                {
                    if (!_fileLoggingEnabled) continue;
                    writer ??= TryOpenWriter();
                    if (writer is null) continue;
                    try
                    {
                        writer.WriteLine(line);
                        wrote = true;
                    }
                    catch (Exception)
                    {
                        CloseWriter(ref writer);
                    }
                }

                // Drop any surplus signals so the next Wait() blocks until new work arrives.
                while (_signal.Wait(0)) { }

                if (wrote) FlushWriter(writer);
                if (writer is not null && writer.BaseStream.Length >= _maxFileBytes)
                {
                    CloseWriter(ref writer);
                    Rotate();
                }

                var request = Interlocked.Exchange(ref _maintenanceRequested, 0);
                if (request != 0)
                {
                    CloseWriter(ref writer);
                    CompleteMaintenance();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch
        {
            // never surface logger failures
        }
        finally
        {
            CloseWriter(ref writer);
        }
    }

    private void FlushWriter(StreamWriter? writer)
    {
        try { writer?.Flush(); }
        catch { }
    }

    private void CloseWriter(ref StreamWriter? writer)
    {
        if (writer is null) return;
        try { writer.Flush(); } catch { }
        try { writer.Dispose(); } catch { }
        writer = null;
    }

    private StreamWriter? TryOpenWriter()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            RotateIfNeeded();
            var path = CurrentLogPath;
            _currentLogPath = path;
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
        }
        catch
        {
            return null;
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var path = CurrentLogPath;
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length < _maxFileBytes) return;
            Rotate();
        }
        catch { }
    }

    private void Rotate()
    {
        try
        {
            var path = CurrentLogPath;
            if (!File.Exists(path)) return;
            var oldest = Path.Combine(_logDirectory, $"app.{_maxFiles - 1}.log");
            if (File.Exists(oldest)) File.Delete(oldest);
            for (var i = _maxFiles - 2; i >= 1; i--)
            {
                var from = Path.Combine(_logDirectory, $"app.{i}.log");
                if (File.Exists(from)) File.Move(from, Path.Combine(_logDirectory, $"app.{i + 1}.log"), overwrite: true);
            }
            File.Move(path, Path.Combine(_logDirectory, "app.1.log"), overwrite: true);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _signal.Release();
            _shutdown.Cancel();
            _writer.Join(TimeSpan.FromSeconds(3));
        }
        catch { }
        try { _signal.Dispose(); } catch { }
        try { _shutdown.Dispose(); } catch { }
    }
}
