using System.Text.Json;
using System.Text.Json.Serialization;
using Renamr.Core.Abstractions;
using Renamr.Core.Options;
using Renamr.Services.Providers;

namespace Renamr.Services.Settings;

/// <summary>Cifratura delle chiavi API a riposo. Su Windows: DPAPI legata all'utente (vedi app).</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}

/// <summary>
/// Impostazioni in <c>%LOCALAPPDATA%\Renamr\settings.json</c>. Le chiavi API non finiscono mai in chiaro su disco:
/// vengono serializzate a parte e cifrate con <see cref="ISecretProtector"/>. Scrittura atomica (temp + replace).
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _file;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(IAppPaths paths, ISecretProtector protector)
    {
        _file = Path.Combine(paths.DataDirectory, "settings.json");
        _protector = protector;
        Current = Load();
    }

    public RenamrSettings Current { get; private set; }

    public async Task SaveAsync(RenamrSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = new SettingsDocument
            {
                Templates = settings.Templates,
                Matching = settings.Matching,
                Output = settings.Output,
                ProtectedKeys = _protector.Protect(JsonSerializer.Serialize(settings.Keys)),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, Json), cancellationToken).ConfigureAwait(false);
            File.Move(temp, _file, overwrite: true);
            Current = settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    private RenamrSettings Load()
    {
        var settings = new RenamrSettings();
        if (!File.Exists(_file))
        {
            return settings;
        }
        try
        {
            var document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(_file), Json);
            if (document is null)
            {
                return settings;
            }
            settings.Templates = document.Templates ?? settings.Templates;
            settings.Matching = document.Matching ?? settings.Matching;
            settings.Output = document.Output ?? settings.Output;
            if (!string.IsNullOrEmpty(document.ProtectedKeys))
            {
                settings.Keys = JsonSerializer.Deserialize<ProviderKeys>(_protector.Unprotect(document.ProtectedKeys)) ?? settings.Keys;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            // File corrotto o creato da un altro utente Windows: si riparte dai default invece di non avviarsi.
        }
        return settings;
    }

    private sealed class SettingsDocument
    {
        public TemplateSettings? Templates { get; set; }
        public MatchingSettings? Matching { get; set; }
        public OutputSettings? Output { get; set; }
        public string? ProtectedKeys { get; set; }
    }

    public void Dispose() => _gate.Dispose();
}
