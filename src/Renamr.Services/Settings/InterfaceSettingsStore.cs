using System.Text.Json;
using Renamr.Services.Providers;

namespace Renamr.Services.Settings;

/// <summary>Preferenze dell'interfaccia (per ora la lingua). Vuota = lingua del sistema.</summary>
public sealed record InterfaceSettings
{
    /// <summary>"it", "en"… oppure vuoto per seguire la lingua del sistema.</summary>
    public string Language { get; init; } = string.Empty;
}

/// <summary>
/// <c>interface.json</c> accanto a settings.json: file separato, così cambiare lingua non riscrive mai le chiavi API
/// cifrate. Un file illeggibile fa ripartire dalla lingua del sistema.
/// </summary>
public sealed class InterfaceSettingsStore(IAppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private string FilePath => Path.Combine(paths.DataDirectory, "interface.json");

    public InterfaceSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<InterfaceSettings>(File.ReadAllText(FilePath), Json) ?? new InterfaceSettings()
                : new InterfaceSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new InterfaceSettings();
        }
    }

    public void Save(InterfaceSettings settings)
    {
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Si perde solo la scelta della lingua: al prossimo avvio vale quella del sistema.
        }
    }
}
