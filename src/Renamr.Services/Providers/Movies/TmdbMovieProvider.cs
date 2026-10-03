using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Matching;
using Renamr.Core.Models;
using Renamr.Services.Providers.Tmdb;
using Renamr.Services.Resilience;
using TMDbLib.Client;
using TMDbLib.Objects.Exceptions;
using TMDbLib.Objects.Movies;
using TMDbLib.Objects.Search;

namespace Renamr.Services.Providers.Movies;

/// <summary>
/// Provider primario per i film: TheMovieDB tramite TMDbLib.
/// <para>
/// Flusso di matching:
/// 1. ricerca con titolo + anno (se l'anno c'è);
/// 2. se nessun risultato, nuova ricerca senza anno (anni sbagliati di uno nei nomi file sono frequenti);
/// 3. punteggio di ogni candidato con <see cref="ConfidenceScorer"/>;
/// 4. dettaglio completo (IMDb id, generi, data esatta) solo per il migliore: una chiamata in più, non dieci.
/// </para>
/// </summary>
public sealed class TmdbMovieProvider : IMetadataProvider
{
    private const int MaxCandidates = 8;

    private readonly ISettingsStore _settings;
    private readonly ILogger _log;
    private readonly ResiliencePipeline _pipeline;
    private readonly TmdbClientAccessor _clients;

    public TmdbMovieProvider(ISettingsStore settings, TmdbClientAccessor clients, ILogger<TmdbMovieProvider>? logger = null)
    {
        _settings = settings;
        _clients = clients;
        _log = logger ?? NullLogger<TmdbMovieProvider>.Instance;
        _pipeline = ResiliencePipelines.CreateDefault(IsTransient);
    }

    public string Name => "TMDb";
    public int Priority => 10;
    public bool IsConfigured => _clients.IsConfigured;
    public bool Supports(MediaKind kind) => kind == MediaKind.Movie;

    public async Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var client = _clients.Get(Name);
        var language = _settings.Current.Matching.Language;

        try
        {
            var results = await SearchOnceAsync(client, query.Title, language, query.Year, cancellationToken).ConfigureAwait(false);
            if (results.Count == 0 && query.Year is not null)
            {
                _log.LogDebug("TMDb: nessun risultato per '{Title}' ({Year}), riprovo senza anno", query.Title, query.Year);
                results = await SearchOnceAsync(client, query.Title, language, null, cancellationToken).ConfigureAwait(false);
            }
            if (results.Count == 0)
            {
                return [];
            }

            var scored = results
                .Take(MaxCandidates)
                .Select((r, i) => (Movie: r, Score: ConfidenceScorer.Score(
                    query.Title, query.Year, r.Title ?? string.Empty, r.OriginalTitle, r.ReleaseDate?.Year,
                    ProviderHelpers.Rank(i, Math.Min(results.Count, MaxCandidates)))))
                .OrderByDescending(x => x.Score)
                .ToList();

            var candidates = new List<MatchCandidate>(scored.Count);
            for (var i = 0; i < scored.Count; i++)
            {
                var (movie, score) = scored[i];
                Movie? details = null;
                if (i == 0)
                {
                    details = await _pipeline.ExecuteAsync(
                        async ct => await client.GetMovieAsync(movie.Id, language, cancellationToken: ct).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                }
                candidates.Add(new MatchCandidate(ToMetadata(movie, details), score));
            }
            return candidates;
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

    private async Task<IReadOnlyList<SearchMovie>> SearchOnceAsync(TMDbClient client, string title, string language, int? year, CancellationToken cancellationToken)
    {
        var container = await _pipeline.ExecuteAsync(
            async ct => await client.SearchMovieAsync(title, language, page: 0, includeAdult: false, year: year ?? 0, cancellationToken: ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return container?.Results ?? [];
    }

    private MediaMetadata ToMetadata(SearchMovie movie, Movie? details) => new()
    {
        Kind = MediaKind.Movie,
        Provider = Name,
        ProviderId = movie.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Title = details?.Title ?? movie.Title ?? string.Empty,
        OriginalTitle = details?.OriginalTitle ?? movie.OriginalTitle,
        ReleaseDate = (details?.ReleaseDate ?? movie.ReleaseDate) is { } d ? DateOnly.FromDateTime(d) : null,
        ImdbId = details?.ImdbId,
        Overview = details?.Overview ?? movie.Overview,
        Genres = details?.Genres?.Select(g => g.Name).OfType<string>().ToList() ?? [],
    };

    internal static bool IsTransient(Exception ex) =>
        ex is RequestLimitExceededException or GeneralHttpException || ResiliencePipelines.IsTransientHttp(ex);
}
