using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using TMDbLib.Client;

namespace Renamr.Services.Providers.Tmdb;

/// <summary>
/// Un solo <see cref="TMDbClient"/> per film e serie. Se l'utente cambia la chiave nelle impostazioni, ne creiamo uno nuovo.
/// </summary>
public sealed class TmdbClientAccessor(ISettingsStore settings) : IDisposable
{
    private readonly Lock _gate = new();
    private TMDbClient? _client;
    private string? _clientKey;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Current.Keys.TmdbApiKey);

    public TMDbClient Get(string provider)
    {
        var key = settings.Current.Keys.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ProviderException(provider, RenamrErrorCode.ProviderAuthFailed, "Chiave API TMDb non impostata");
        }

        lock (_gate)
        {
            if (_client is null || _clientKey != key)
            {
                _client?.Dispose();
                _client = new TMDbClient(key) { MaxRetryCount = 0 }; // i retry li gestisce Polly
                _clientKey = key;
            }
            return _client;
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
