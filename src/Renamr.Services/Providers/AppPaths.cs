namespace Renamr.Services.Providers;

/// <summary>Cartelle dell'applicazione (cache dei provider, journal, impostazioni).</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
    string CacheDirectory { get; }
    string JournalDirectory { get; }
}

public sealed class DefaultAppPaths : IAppPaths
{
    public DefaultAppPaths(string? baseDirectory = null)
    {
        DataDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "Renamr");
        CacheDirectory = Path.Combine(DataDirectory, "cache");
        JournalDirectory = Path.Combine(DataDirectory, "journal");
    }

    public string DataDirectory { get; }
    public string CacheDirectory { get; }
    public string JournalDirectory { get; }
}
