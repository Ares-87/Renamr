using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Matching;
using Renamr.Core.Models;

namespace Renamr.Services.Providers.Tv;

/// <summary>
/// TheTVDB API v4. Richiede una chiave (ed eventualmente il PIN dell'abbonato).
/// Il token JWT dura un mese: lo teniamo in memoria e lo rinnoviamo su 401.
/// </summary>
public sealed class TheTvdbProvider(HttpClient http, ISettingsStore settings) : IMetadataProvider, IDisposable
{
    public const string HttpClientName = "thetvdb";

    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string? _token;
    private string? _tokenKey;

    public string Name => "TheTVDB";
    public int Priority => 20;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Current.Keys.TheTvdbApiKey);
    public bool Supports(MediaKind kind) => kind is MediaKind.Episode or MediaKind.Anime;

    public async Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var yearPart = query.Year is { } y ? $"&year={y}" : string.Empty;
            var search = await GetAsync<Envelope<List<SearchResult>>>($"search?type=series&query={Uri.EscapeDataString(query.Title)}{yearPart}", cancellationToken).ConfigureAwait(false);
            var hits = search?.Data ?? [];

            var series = hits
                .Select((s, i) => (Series: s, Score: ConfidenceScorer.Score(query.Title, query.Year, s.Name ?? "", s.Translations?.GetValueOrDefault("eng"),
                    int.TryParse(s.Year, NumberStyles.None, CultureInfo.InvariantCulture, out var yy) ? yy : null, ProviderHelpers.Rank(i, hits.Count))))
                .OrderByDescending(x => x.Score)
                .Take(5)
                .ToList();

            var result = new List<MatchCandidate>(series.Count);
            foreach (var (s, seriesScore) in series)
            {
                var score = seriesScore;
                EpisodeDto? ep = null;
                if (result.Count == 0)
                {
                    ep = await GetEpisodeAsync(s.TvdbId, query, cancellationToken).ConfigureAwait(false);
                    if (ep is null)
                    {
                        score *= 0.6;
                    }
                }
                result.Add(new MatchCandidate(new MediaMetadata
                {
                    Kind = query.Kind,
                    Provider = Name,
                    ProviderId = s.TvdbId ?? string.Empty,
                    Title = s.Name ?? query.Title,
                    Season = ep?.SeasonNumber ?? query.Season,
                    Episode = ep?.Number ?? query.Episode,
                    AbsoluteEpisode = ep?.AbsoluteNumber ?? query.AbsoluteEpisode,
                    EpisodeTitle = ep?.Name,
                    ReleaseDate = ProviderHelpers.ParseDate(ep?.Aired),
                    YearOnly = int.TryParse(s.Year, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null,
                    Overview = ep?.Overview,
                }, Math.Round(score, 3)));
            }
            return result;
        }
        catch (Exception ex) when (ProviderHelpers.ShouldWrap(ex, cancellationToken))
        {
            throw ProviderHelpers.Wrap(Name, ex);
        }
    }

    private async Task<EpisodeDto?> GetEpisodeAsync(string? seriesId, MediaQuery query, CancellationToken ct)
    {
        if (seriesId is null)
        {
            return null;
        }
        // Ordine "default" per S/E, ordine "absolute" per la numerazione assoluta degli anime.
        var url = query switch
        {
            { Season: { } s, Episode: { } e } => $"series/{seriesId}/episodes/default?page=0&season={s}&episodeNumber={e}",
            { AbsoluteEpisode: { } a } => $"series/{seriesId}/episodes/absolute?page=0&season=1&episodeNumber={a}",
            _ => null,
        };
        if (url is null)
        {
            return null;
        }
        var page = await GetAsync<Envelope<EpisodePage>>(url, ct).ConfigureAwait(false);
        return page?.Data?.Episodes?.FirstOrDefault();
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await EnsureTokenAsync(forceRefresh: attempt > 0, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                continue; // token scaduto: un nuovo login e si riprova una volta
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
        }
        throw new ProviderException(Name, RenamrErrorCode.ProviderAuthFailed, "Token TheTVDB rifiutato");
    }

    private async Task<string> EnsureTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var key = settings.Current.Keys.TheTvdbApiKey
                  ?? throw new ProviderException(Name, RenamrErrorCode.ProviderAuthFailed, "Chiave TheTVDB non impostata");

        await _loginGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _token is not null && _tokenKey == key)
            {
                return _token;
            }
            using var response = await http.PostAsJsonAsync("login", new LoginRequest(key, settings.Current.Keys.TheTvdbPin), ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ProviderException(Name, RenamrErrorCode.ProviderAuthFailed, "Login TheTVDB rifiutato");
            }
            response.EnsureSuccessStatusCode();
            var login = await response.Content.ReadFromJsonAsync<Envelope<LoginData>>(ct).ConfigureAwait(false);
            _token = login?.Data?.Token ?? throw new ProviderException(Name, RenamrErrorCode.ProviderAuthFailed, "Token assente");
            _tokenKey = key;
            return _token;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    public void Dispose() => _loginGate.Dispose();

    private sealed record LoginRequest([property: JsonPropertyName("apikey")] string ApiKey, [property: JsonPropertyName("pin")] string? Pin);
    private sealed record LoginData([property: JsonPropertyName("token")] string? Token);
    private sealed record Envelope<T>([property: JsonPropertyName("data")] T? Data);

    private sealed record SearchResult(
        [property: JsonPropertyName("tvdb_id")] string? TvdbId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("year")] string? Year,
        [property: JsonPropertyName("translations")] Dictionary<string, string>? Translations);

    private sealed record EpisodePage([property: JsonPropertyName("episodes")] List<EpisodeDto>? Episodes);

    private sealed record EpisodeDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("aired")] string? Aired,
        [property: JsonPropertyName("seasonNumber")] int? SeasonNumber,
        [property: JsonPropertyName("number")] int? Number,
        [property: JsonPropertyName("absoluteNumber")] int? AbsoluteNumber,
        [property: JsonPropertyName("overview")] string? Overview);
}
