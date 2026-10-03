namespace Renamr.Tests;

/// <summary>Cartella temporanea che fa da "libreria" per i test di I/O.</summary>
public sealed class TempLibrary : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("renamr-test-").FullName;

    public string File(string relative, string content = "x")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string Fixture(string fixtureName, string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName), path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            System.IO.File.SetAttributes(f, FileAttributes.Normal);
        }
        Directory.Delete(Root, recursive: true);
    }
}
