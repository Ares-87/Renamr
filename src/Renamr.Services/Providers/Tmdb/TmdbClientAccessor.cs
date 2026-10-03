using System.Text.Json;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using TMDbLib.Client;

namespace Renamr.Services.Providers.Tmdb;

/// <summary>
/// Un solo <see cref="TMDbClient"/> per film e serie. Se l'utente cambia la chiave nelle impostazioni, ne creiamo uno nuovo.
/// </summary>
public sealed class TmdbClientAccessor(ISettingsStore settings) : IDisposable
{
    /// <summary>Solo per i test: client con risposte TMDb finte, senza rete.</summary>
    internal Func<string, TMDbClient>? Factory { get; init; }

    private readonly Lock _gate = new();
    private TMDbClient? _client;
    private string? _clientKey;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Current.Keys.TmdbApiKey);

    public TMDbClient Get(string provider)
    {
        var key = ApiKeyFrom(settings.Current.Keys.TmdbApiKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ProviderException(provider, RenamrErrorCode.ProviderAuthFailed, "Chiave API TMDb non impostata");
        }

        lock (_gate)
        {
            if (_client is null || _clientKey != key)
            {
                _client?.Dispose();
                _client = Factory?.Invoke(key) ?? new TMDbClient(key);
                _client.MaxRetryCount = 0; // i retry li gestisce Polly
                _clientKey = key;
            }
            return _client;
        }
    }

    /// <summary>
    /// La pagina API di TMDb mostra due valori: la "Chiave API" (32 caratteri) e il lungo "Token di accesso in lettura"
    /// (un JWT che inizia con "eyJ"). TMDbLib accetta solo la prima, ma il token la contiene nel campo "aud":
    /// così funziona qualunque dei due venga incollato.
    /// </summary>
    internal static string? ApiKeyFrom(string? value)
    {
        var key = value?.Trim();
        if (string.IsNullOrEmpty(key) || !key.StartsWith("eyJ", StringComparison.Ordinal))
        {
            return key;
        }
        var parts = key.Split('.');
        if (parts.Length != 3)
        {
            return key;
        }
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return json.RootElement.TryGetProperty("aud", out var aud) && aud.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(aud.GetString())
                ? aud.GetString()
                : key;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return key;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
