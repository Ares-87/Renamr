using System.Globalization;
using Polly;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Matching;
using Renamr.Core.Models;
using Renamr.Services.Providers.Movies;
using Renamr.Services.Providers.Tmdb;
using Renamr.Services.Resilience;
using TMDbLib.Client;
using TMDbLib.Objects.Exceptions;
using TMDbLib.Objects.Search;
using TMDbLib.Objects.TvShows;

namespace Renamr.Services.Providers.Tv;

/// <summary>
/// Serie TV e anime da TheMovieDB (stessa chiave gratuita dei film).
/// <para>
/// È l'unica fonte che ha quasi sempre sia il titolo della serie sia i titoli degli episodi tradotti in italiano:
/// TVmaze ha gli episodi solo in inglese. Se la traduzione dell'episodio manca, TMDb restituisce "Episodio 5":
/// in quel caso si usa il titolo inglese.
/// </para>
/// </summary>
public sealed class TmdbTvProvider(ISettingsStore settings, TmdbClientAccessor clients) : IMetadataProvider
{
    private const int MaxCandidates = 5;
    private readonly ResiliencePipeline _pipeline = ResiliencePipelines.CreateDefault(TmdbMovieProvider.IsTransient);

    public string Name => "TMDb";
    public int Priority => 18;
    public bool IsConfigured => clients.IsConfigured;
    public bool Supports(MediaKind kind) => kind is MediaKind.Episode or MediaKind.Anime;

    public async Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var client = clients.Get(Name);
        var language = LanguagePreference.From(settings.Current.Matching.Language).Tag;

        try
        {
            var container = await _pipeline.ExecuteAsync(
                async ct => await AbandonOnCancel.RunAsync(t => client.SearchTvShowAsync(query.Title, language, page: 0, includeAdult: false, firstAirDateYear: 0, cancellationToken: t), ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            var hits = container?.Results ?? [];
            if (hits.Count == 0)
            {
                return [];
            }

            var scored = hits
                .Take(MaxCandidates)
                .Select((s, i) => (Show: s, Score: ConfidenceScorer.Score(
                    query.Title, query.Year, s.Name ?? string.Empty, s.OriginalName, s.FirstAirDate?.Year,
                    ProviderHelpers.Rank(i, Math.Min(hits.Count, MaxCandidates)))))
                .OrderByDescending(x => x.Score)
                .ToList();

            var result = new List<MatchCandidate>(scored.Count);
            foreach (var (show, showScore) in scored)
            {
                var score = showScore;
                TvShow? details = null;
                TvEpisode? episode = null;
                string? episodeTitle = null;
                var translated = true;
                (int Season, int Episode)? number = null;
                if (result.Count == 0)
                {
                    details = await _pipeline.ExecuteAsync(
                        async ct => await AbandonOnCancel.RunAsync(t => client.GetTvShowAsync(show.Id, TvShowMethods.ExternalIds, language, cancellationToken: t), ct).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    number = ResolveNumber(query, details);
                    if (number is { } n)
                    {
                        episode = await GetEpisodeAsync(client, show.Id, n.Season, n.Episode, language, cancellationToken).ConfigureAwait(false);
                        episodeTitle = UsefulEpisodeTitle(episode?.Name);
                        if (episode is not null && episodeTitle is null && language != "en-US")
                        {
                            // Episodio non ancora tradotto: meglio il titolo inglese che "Episodio 5".
                            var english = await GetEpisodeAsync(client, show.Id, n.Season, n.Episode, "en-US", cancellationToken).ConfigureAwait(false);
                            episodeTitle = UsefulEpisodeTitle(english?.Name);
                            translated = false;
                        }
                    }
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
                    Title = details?.Name ?? show.Name ?? query.Title,
                    OriginalTitle = details?.OriginalName ?? show.OriginalName,
                    ImdbId = details?.ExternalIds?.ImdbId,
                    Season = number?.Season ?? query.Season,
                    Episode = number?.Episode ?? query.Episode,
                    AbsoluteEpisode = query.AbsoluteEpisode,
                    EpisodeTitle = episodeTitle,
                    EpisodeTitleLocalized = translated,
                    ReleaseDate = episode?.AirDate is { } aired ? DateOnly.FromDateTime(aired) : null,
                    YearOnly = (details?.FirstAirDate ?? show.FirstAirDate)?.Year,
                    Overview = string.IsNullOrWhiteSpace(episode?.Overview) ? details?.Overview : episode.Overview,
                    Genres = details?.Genres?.Select(g => g.Name).OfType<string>().ToList() ?? [],
                }, Math.Round(score, 3)));
            }
            return result;
        }
        catch (Exception ex) when (ProviderHelpers.ShouldWrap(ex, cancellationToken))
        {
            throw ex switch
            {
                RequestLimitExceededException => new ProviderException(Name, RenamrErrorCode.ProviderRateLimited, ex.Message, ex),
                _ => ProviderHelpers.Wrap(Name, ex),
            };
        }
    }

    private async Task<TvEpisode?> GetEpisodeAsync(TMDbClient client, int showId, int season, int number, string language, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(
                async ct => await AbandonOnCancel.RunAsync(t => client.GetTvEpisodeAsync(showId, season, number, language: language, cancellationToken: t), ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private static (int Season, int Episode)? ResolveNumber(MediaQuery query, TvShow? show)
    {
        if (query.Season is { } season && query.Episode is { } episode)
        {
            return (season, episode);
        }
        if (query.AbsoluteEpisode is { } absolute && show?.Seasons is { } seasons)
        {
            return MapAbsolute(absolute, seasons.Select(s => (s.SeasonNumber, s.EpisodeCount)));
        }
        return query.Episode is { } single ? (1, single) : null;
    }

    /// <summary>Numerazione assoluta (anime) ➔ stagione/episodio, saltando gli speciali (stagione 0).</summary>
    internal static (int Season, int Episode)? MapAbsolute(int absolute, IEnumerable<(int Season, int EpisodeCount)> seasons)
    {
        if (absolute <= 0)
        {
            return null;
        }
        var remaining = absolute;
        foreach (var (season, count) in seasons.Where(s => s.Season > 0).OrderBy(s => s.Season))
        {
            if (remaining <= count)
            {
                return (season, remaining);
            }
            remaining -= count;
        }
        return null;
    }

    /// <summary>Senza traduzione TMDb restituisce un segnaposto ("Episodio 5", "Episode 5"): meglio nessun titolo.</summary>
    internal static string? UsefulEpisodeTitle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
               parts[0] is "Episodio" or "Episode" or "Épisode" or "Folge"
            ? null
            : name.Trim();
    }
}
