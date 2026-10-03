using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Matching;
using Renamr.Core.Models;

namespace Renamr.Services.Providers.Movies;

/// <summary>Fallback per i film: OMDb (dati IMDb). HttpClient con resilienza standard (Polly) dal container DI.</summary>
public sealed class OmdbProvider(HttpClient http, ISettingsStore settings) : IMetadataProvider
{
    public const string HttpClientName = "omdb";

    public string Name => "OMDb";
    public int Priority => 20;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Current.Keys.OmdbApiKey);
    public bool Supports(MediaKind kind) => kind == MediaKind.Movie;

    public async Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var key = Uri.EscapeDataString(settings.Current.Keys.OmdbApiKey ?? string.Empty);
        try
        {
            var yearPart = query.Year is { } y ? $"&y={y}" : string.Empty;
            var search = await http.GetFromJsonAsync<OmdbSearch>(
                $"?apikey={key}&type=movie&s={Uri.EscapeDataString(query.Title)}{yearPart}", cancellationToken).ConfigureAwait(false);

            if (search is null || !string.Equals(search.Response, "True", StringComparison.OrdinalIgnoreCase))
            {
                if (search?.Error?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true)
                {
                    throw new ProviderException(Name, RenamrErrorCode.ProviderAuthFailed, search.Error);
                }
                return []; // "Movie not found!"
            }

            var items = search.Search ?? [];
            var scored = items
                .Select((s, i) => (Item: s, Score: ConfidenceScorer.Score(query.Title, query.Year, s.Title ?? "", null, ParseYear(s.Year),
                    Providers.ProviderHelpers.Rank(i, items.Count))))
                .OrderByDescending(x => x.Score)
                .Take(5)
                .ToList();

            var result = new List<MatchCandidate>(scored.Count);
            foreach (var (item, score) in scored)
            {
                // Il dettaglio (data completa "Released") solo per il migliore.
                OmdbDetail? detail = null;
                if (result.Count == 0 && item.ImdbId is not null)
                {
                    detail = await http.GetFromJsonAsync<OmdbDetail>($"?apikey={key}&i={item.ImdbId}&plot=short", cancellationToken).ConfigureAwait(false);
                }
                result.Add(new MatchCandidate(new MediaMetadata
                {
                    Kind = MediaKind.Movie,
                    Provider = Name,
                    ProviderId = item.ImdbId ?? string.Empty,
                    ImdbId = item.ImdbId,
                    Title = detail?.Title ?? item.Title ?? string.Empty,
                    ReleaseDate = ProviderHelpers.ParseDate(detail?.Released, "dd MMM yyyy"),
                    YearOnly = ParseYear(item.Year),
                    Overview = detail?.Plot,
                    Genres = detail?.Genre?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [],
                }, score));
            }
            return result;
        }
        catch (Exception ex) when (ProviderHelpers.ShouldWrap(ex, cancellationToken))
        {
            throw ProviderHelpers.Wrap(Name, ex);
        }
    }

    private static int? ParseYear(string? value) =>
        value is { Length: >= 4 } && int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;

    private sealed record OmdbSearch(
        [property: JsonPropertyName("Search")] List<OmdbItem>? Search,
        [property: JsonPropertyName("Response")] string? Response,
        [property: JsonPropertyName("Error")] string? Error);

    private sealed record OmdbItem(
        [property: JsonPropertyName("Title")] string? Title,
        [property: JsonPropertyName("Year")] string? Year,
        [property: JsonPropertyName("imdbID")] string? ImdbId);

    private sealed record OmdbDetail(
        [property: JsonPropertyName("Title")] string? Title,
        [property: JsonPropertyName("Released")] string? Released,
        [property: JsonPropertyName("Genre")] string? Genre,
        [property: JsonPropertyName("Plot")] string? Plot);
}
