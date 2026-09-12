using System.IO;

namespace ExplorerEverythingSearch.Tests.TestSupport;

/// <summary>
/// A unique, disposable directory below <see cref="Path.GetTempPath()"/>. Every test that touches the
/// file system uses one, so no test artifact is ever produced inside the repository and runs cannot
/// interfere with each other.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string label = "ws")
    {
        Root = Path.Combine(Path.GetTempPath(), "ees-tests", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string name) => System.IO.Path.Combine(Root, name);

    public string CreateDirectory(string name)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string name, string content = "content")
    {
        var path = PathOf(name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
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
        // Deliberately swallowed: a leftover temp directory must never fail a test run.
    }
}
