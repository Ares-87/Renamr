using Hqub.MusicBrainz;
using Hqub.MusicBrainz.Entities;
using Renamr.Core.Abstractions;
using Renamr.Core.Matching;
using Renamr.Core.Models;
using Renamr.Services.Resilience;

namespace Renamr.Services.Providers.Music;

/// <summary>
/// Fallback testuale per la musica: ricerca su MusicBrainz a partire dai tag ID3/Vorbis esistenti
/// (o dal nome file). MusicBrainz impone 1 richiesta al secondo e uno User-Agent identificativo.
/// </summary>
public sealed class MusicBrainzProvider : IMetadataProvider, IDisposable
{
    public const string HttpClientName = "musicbrainz";

    private readonly MusicBrainzClient _client;
    private readonly RequestThrottle _throttle = new(TimeSpan.FromSeconds(1.1));

    public MusicBrainzProvider(HttpClient http)
    {
        _client = new MusicBrainzClient(http);
    }

    public string Name => "MusicBrainz";
    public int Priority => 20;
    public bool IsConfigured => true;
    public bool Supports(MediaKind kind) => kind == MediaKind.Music;

    public async Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var parameters = new QueryParameters<Recording> { { "recording", query.Title } };
        if (!string.IsNullOrWhiteSpace(query.Artist))
        {
            parameters.Add("artist", query.Artist);
        }
        if (!string.IsNullOrWhiteSpace(query.Album))
        {
            parameters.Add("release", query.Album);
        }

        try
        {
            var found = await _throttle.RunAsync(
                ct => _client.Recordings.SearchAsync(parameters, 10, 0).WaitAsync(ct), cancellationToken).ConfigureAwait(false);

            var candidates = new List<MatchCandidate>();
            foreach (var rec in found.Items)
            {
                var artist = rec.Credits is { Count: > 0 } c ? string.Concat(c.Select(x => (x.Name ?? x.Artist?.Name) + x.JoinPhrase)) : null;
                var release = PickRelease(rec, query.Album);
                var date = ProviderHelpers.ParseDate(release?.Date);

                var title = TitleSimilarity.Score(query.Title, rec.Title);
                var artistScore = query.Artist is null ? 0.5 : TitleSimilarity.Score(query.Artist, artist);
                var score = rec.Score / 100.0 * 0.4 + title * 0.4 + artistScore * 0.2;

                candidates.Add(new MatchCandidate(new MediaMetadata
                {
                    Kind = MediaKind.Music,
                    Provider = Name,
                    ProviderId = rec.Id ?? string.Empty,
                    Title = rec.Title ?? query.Title,
                    Artist = artist,
                    Album = release?.Title,
                    TrackNumber = release?.Media?.FirstOrDefault()?.Tracks?.FirstOrDefault()?.Position ?? query.Episode,
                    ReleaseDate = date,
                    YearOnly = date?.Year,
                    Genres = rec.Genres?.Select(g => g.Name).OfType<string>().ToList() ?? [],
                }, Math.Round(score, 3)));
            }
            return candidates.OrderByDescending(c => c.Confidence).Take(5).ToList();
        }
        catch (Exception ex) when (ProviderHelpers.ShouldWrap(ex, cancellationToken))
        {
            throw ProviderHelpers.Wrap(Name, ex);
        }
    }

    /// <summary>Release dell'album indicato nei tag, altrimenti la release ufficiale più antica.</summary>
    private static Release? PickRelease(Recording rec, string? album)
    {
        var releases = rec.Releases ?? [];
        if (album is not null && releases.FirstOrDefault(r => TitleSimilarity.Score(r.Title, album) > 0.85) is { } byAlbum)
        {
            return byAlbum;
        }
        return releases
            .Where(r => !string.IsNullOrEmpty(r.Date))
            .OrderBy(r => r.Status == "Official" ? 0 : 1)
            .ThenBy(r => r.Date, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public void Dispose()
    {
        _client.Dispose();
        _throttle.Dispose();
    }
}
