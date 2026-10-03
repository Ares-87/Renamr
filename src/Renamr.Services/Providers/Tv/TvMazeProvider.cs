using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Renamr.Core.Abstractions;
using Renamr.Core.Matching;
using Renamr.Core.Models;

namespace Renamr.Services.Providers.Tv;

/// <summary>Serie TV (e anime come fallback): TVmaze, API pubblica senza chiave.</summary>
/// <para>
/// TVmaze ha i nomi in inglese; i titoli localizzati della serie arrivano dagli AKA per paese (<c>/shows/{id}/akas</c>).
/// I titoli degli episodi esistono solo in inglese.
/// </para>
public sealed partial class TvMazeProvider(HttpClient http, ISettingsStore? settings = null) : IMetadataProvider
{
    public const string HttpClientName = "tvmaze";

    public string Name => "TVmaze";
    public int Priority => 25;
    public bool IsConfigured => true;
    public bool Supports(MediaKind kind) => kind is MediaKind.Episode or MediaKind.Anime;

    /// <summary>Un solo download degli AKA per serie: una stagione intera non ripete la stessa chiamata 20 volte.</summary>
    private readonly ConcurrentDictionary<(int ShowId, string Country), string?> _akaCache = new();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Html();

    public async Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var hits = await http.GetFromJsonAsync<List<SearchHit>>(
                $"search/shows?q={Uri.EscapeDataString(query.Title)}", cancellationToken).ConfigureAwait(false) ?? [];
            if (hits.Count == 0)
            {
                return [];
            }

            var shows = hits
                .Where(h => h.Show is not null)
                .Select((h, i) => (h.Show!, Score: ConfidenceScorer.Score(query.Title, query.Year, h.Show!.Name ?? "", null,
                    ProviderHelpers.ParseDate(h.Show!.Premiered)?.Year, ProviderHelpers.Rank(i, hits.Count))))
                .OrderByDescending(x => x.Score)
                .Take(5)
                .ToList();

            var result = new List<MatchCandidate>(shows.Count);
            foreach (var (show, showScore) in shows)
            {
                var score = showScore;
                Episode? episode = null;
                string? localizedTitle = null;
                if (result.Count == 0)
                {
                    localizedTitle = await GetLocalizedTitleAsync(show.Id, cancellationToken).ConfigureAwait(false);
                    episode = await GetEpisodeAsync(show.Id, query, cancellationToken).ConfigureAwait(false);
                    if (episode is null)
                    {
                        score *= 0.6; // serie giusta ma episodio inesistente: probabile errore di numerazione
                    }
                }

                result.Add(new MatchCandidate(new MediaMetadata
                {
                    Kind = query.Kind,
                    Provider = Name,
                    ProviderId = show.Id.ToString(CultureInfo.InvariantCulture),
                    Title = localizedTitle ?? show.Name ?? query.Title,
                    OriginalTitle = show.Name,
                    ImdbId = show.Externals?.Imdb,
                    Season = episode?.Season ?? query.Season,
                    Episode = episode?.Number ?? query.Episode,
                    AbsoluteEpisode = query.AbsoluteEpisode,
                    EpisodeTitle = episode?.Name,
                    EpisodeTitleLocalized = LanguagePreference.From(settings?.Current.Matching.Language).IsEnglish, // TVmaze: episodi solo in inglese
                    ReleaseDate = ProviderHelpers.ParseDate(episode?.Airdate),
                    YearOnly = ProviderHelpers.ParseDate(show.Premiered)?.Year,
                    Overview = episode?.Summary is null ? null : Html().Replace(episode.Summary, string.Empty),
                    Genres = show.Genres ?? [],
                }, Math.Round(score, 3)));
            }
            return result;
        }
        catch (Exception ex) when (ProviderHelpers.ShouldWrap(ex, cancellationToken))
        {
            throw ProviderHelpers.Wrap(Name, ex);
        }
    }

    private async Task<string?> GetLocalizedTitleAsync(int showId, CancellationToken ct)
    {
        var language = LanguagePreference.From(settings?.Current.Matching.Language);
        if (language.IsEnglish || language.Country is null)
        {
            return null;
        }
        if (_akaCache.TryGetValue((showId, language.Country), out var cached))
        {
            return cached;
        }
        List<Aka> akas;
        try
        {
            akas = await http.GetFromJsonAsync<List<Aka>>($"shows/{showId}/akas", ct).ConfigureAwait(false) ?? [];
        }
        catch (HttpRequestException)
        {
            return null; // il titolo localizzato è un di più: non deve far fallire il riconoscimento
        }
        var name = akas.FirstOrDefault(a => string.Equals(a.Country?.Code, language.Country, StringComparison.OrdinalIgnoreCase))?.Name;
        _akaCache[(showId, language.Country)] = name;
        return name;
    }

    private async Task<Episode?> GetEpisodeAsync(int showId, MediaQuery query, CancellationToken ct)
    {
        if (query.Season is { } season && query.Episode is { } number)
        {
            using var response = await http.GetAsync($"shows/{showId}/episodebynumber?season={season}&number={number}", ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<Episode>(ct).ConfigureAwait(false);
        }

        if (query.AbsoluteEpisode is { } absolute and > 0)
        {
            // Numerazione assoluta (anime): episodi regolari in ordine di messa in onda.
            var all = await http.GetFromJsonAsync<List<Episode>>($"shows/{showId}/episodes", ct).ConfigureAwait(false) ?? [];
            var regular = all.Where(e => e.Number is not null).ToList();
            return absolute <= regular.Count ? regular[absolute - 1] : null;
        }

        return null;
    }

    private sealed record SearchHit([property: JsonPropertyName("show")] Show? Show);

    private sealed record Show(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("premiered")] string? Premiered,
        [property: JsonPropertyName("genres")] List<string>? Genres,
        [property: JsonPropertyName("externals")] Externals? Externals);

    private sealed record Aka([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("country")] Country? Country);
    private sealed record Country([property: JsonPropertyName("code")] string? Code);

    private sealed record Externals([property: JsonPropertyName("imdb")] string? Imdb);

    private sealed record Episode(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("season")] int? Season,
        [property: JsonPropertyName("number")] int? Number,
        [property: JsonPropertyName("airdate")] string? Airdate,
        [property: JsonPropertyName("summary")] string? Summary);
}
