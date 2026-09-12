using System.IO;

namespace ExplorerEverythingSearch.Tests.TestSupport;

/// <summary>Polling helpers. The production code is asynchronous (log writer thread, timers, worker
/// thread), so tests wait for observable state instead of sleeping a fixed amount of time.</summary>
internal static class Wait
{
    public static bool Until(Func<bool> condition, int timeoutMs = 5_000, int pollMs = 10)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (condition()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(pollMs);
        }
    }

    /// <summary>Waits until the file exists and its content satisfies <paramref name="predicate"/>.</summary>
    public static string ForFileText(string path, Func<string, bool> predicate, int timeoutMs = 10_000)
    {
        string text = string.Empty;
        var ok = Until(() =>
        {
            text = TryRead(path);
            return predicate(text);
        }, timeoutMs);
        if (!ok) throw new TimeoutException($"'{path}' did not reach the expected content within {timeoutMs} ms. Last content:\n{text}");
        return text;
    }

    /// <summary>
    /// Reads a file that another thread may still have open (the log writer keeps its own handle), and
    /// throws when the content cannot be read at all - "must not contain X" assertions depend on that.
    /// </summary>
    public static string ReadShared(string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(20);
            }
        }
        throw new IOException($"could not read '{path}'", last);
    }

    public static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? ReadShared(path) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
