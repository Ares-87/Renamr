using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Matching;
using Renamr.Core.Models;
using Renamr.Services.Resilience;

namespace Renamr.Services.Providers.Tv;

/// <summary>
/// Provider anime: AniDB.
/// <para>
/// AniDB non ha un endpoint di ricerca: si scarica (max una volta al giorno, pena il ban) il dump dei titoli
/// <c>anime-titles.dat.gz</c> e si cerca in locale, confrontando titoli romaji, inglesi, italiani e sinonimi.
/// Il dettaglio (date di messa in onda per episodio) arriva dall'HTTP API, con 1 richiesta ogni 2 secondi
/// e cache su disco per 24 ore come chiedono le regole del servizio.
/// </para>
/// </summary>
public sealed class AniDbProvider : IMetadataProvider, IDisposable
{
    public const string HttpClientName = "anidb";
    private const string TitlesUrl = "https://anidb.net/api/anime-titles.dat.gz";
    private const string ApiUrl = "http://api.anidb.net:9001/httpapi"; // AniDB espone l'HTTP API solo su questa porta

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly ISettingsStore _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger _log;
    private readonly RequestThrottle _throttle = new(TimeSpan.FromSeconds(2.5));
    private readonly SemaphoreSlim _titlesGate = new(1, 1);
    private IReadOnlyList<AnimeTitle>? _titles;

    public AniDbProvider(HttpClient http, ISettingsStore settings, IAppPaths paths, ILogger<AniDbProvider>? logger = null)
    {
        _http = http;
        _settings = settings;
        _paths = paths;
        _log = logger ?? NullLogger<AniDbProvider>.Instance;
    }

    public string Name => "AniDB";
    public int Priority => 15;

    /// <summary>L'HTTP API richiede un "client" registrato su anidb.net.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Current.Keys.AniDbClientName);

    public bool Supports(MediaKind kind) => kind == MediaKind.Anime;

    public async Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var titles = await GetTitlesAsync(cancellationToken).ConfigureAwait(false);

            // Miglior titolo per ogni anime (aid), poi i primi 3.
            var best = titles
                .Select(t => (t.Aid, t.Title, Score: TitleSimilarity.Score(query.Title, t.Title)))
                .Where(x => x.Score >= 0.5)
                .GroupBy(x => x.Aid)
                .Select(g => g.MaxBy(x => x.Score))
                .OrderByDescending(x => x.Score)
                .Take(3)
                .ToList();

            var lang = LanguagePreference.From(_settings.Current.Matching.Language).TwoLetter;
            var result = new List<MatchCandidate>(best.Count);
            foreach (var (aid, _, titleScore) in best)
            {
                var anime = await GetAnimeAsync(aid, cancellationToken).ConfigureAwait(false);
                if (anime is null)
                {
                    continue;
                }

                var episodeNumber = query.AbsoluteEpisode ?? query.Episode;
                var episode = anime.Element("episodes")?.Elements("episode")
                    .FirstOrDefault(e => e.Element("epno") is { } n && (string?)n.Attribute("type") == "1" && n.Value == episodeNumber?.ToString(CultureInfo.InvariantCulture));

                var startYear = ProviderHelpers.ParseDate((string?)anime.Element("startdate"))?.Year;
                var score = titleScore * 0.8 + (query.Year is null || startYear is null ? 0.1 : (Math.Abs(query.Year.Value - startYear.Value) <= 1 ? 0.2 : 0));
                if (episodeNumber is not null && episode is null)
                {
                    score *= 0.6;
                }

                result.Add(new MatchCandidate(new MediaMetadata
                {
                    Kind = MediaKind.Anime,
                    Provider = Name,
                    ProviderId = aid.ToString(CultureInfo.InvariantCulture),
                    Title = PickTitle(anime.Element("titles"), lang, "official") ?? PickTitle(anime.Element("titles"), "x-jat", "main") ?? query.Title,
                    OriginalTitle = PickTitle(anime.Element("titles"), "ja", "official"),
                    Season = query.Season ?? 1,
                    Episode = episodeNumber,
                    AbsoluteEpisode = episodeNumber,
                    EpisodeTitle = PickTitle(episode, lang, null) ?? PickTitle(episode, "en", null) ?? PickTitle(episode, "x-jat", null),
                    EpisodeTitleLocalized = lang == "en" || PickTitle(episode, lang, null) is not null,
                    ReleaseDate = ProviderHelpers.ParseDate((string?)episode?.Element("airdate")) ?? ProviderHelpers.ParseDate((string?)anime.Element("startdate")),
                    YearOnly = startYear,
                }, Math.Round(Math.Clamp(score, 0, 1), 3)));
            }
            return result.OrderByDescending(c => c.Confidence).ToList();
        }
        catch (Exception ex) when (ProviderHelpers.ShouldWrap(ex, cancellationToken))
        {
            throw ProviderHelpers.Wrap(Name, ex);
        }
    }

    internal static string? PickTitle(XElement? parent, string lang, string? type) =>
        parent?.Elements("title").FirstOrDefault(t =>
            (string?)t.Attribute(XNamespace.Xml + "lang") == lang && (type is null || (string?)t.Attribute("type") == type))?.Value;

    private async Task<XElement?> GetAnimeAsync(int aid, CancellationToken ct)
    {
        var cacheFile = Path.Combine(_paths.CacheDirectory, "anidb", $"{aid}.xml");
        if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheLifetime)
        {
            return XElement.Load(cacheFile);
        }

        var keys = _settings.Current.Keys;
        var url = $"{ApiUrl}?request=anime&client={Uri.EscapeDataString(keys.AniDbClientName!)}&clientver={keys.AniDbClientVersion}&protover=1&aid={aid}";

        var xml = await _throttle.RunAsync(async t =>
        {
            using var response = await _http.GetAsync(url, t).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(t).ConfigureAwait(false);
            // L'API risponde sempre gzip, anche senza Accept-Encoding.
            await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            return await XElement.LoadAsync(gzip, LoadOptions.None, t).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (xml.Name == "error")
        {
            var code = xml.Value.Contains("banned", StringComparison.OrdinalIgnoreCase) ? RenamrErrorCode.ProviderRateLimited : RenamrErrorCode.ProviderAuthFailed;
            throw new ProviderException(Name, code, xml.Value);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        xml.Save(cacheFile);
        return xml;
    }

    private async Task<IReadOnlyList<AnimeTitle>> GetTitlesAsync(CancellationToken ct)
    {
        if (_titles is not null)
        {
            return _titles;
        }

        await _titlesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_titles is not null)
            {
                return _titles;
            }

            var file = Path.Combine(_paths.CacheDirectory, "anidb", "anime-titles.dat");
            if (!File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > CacheLifetime)
            {
                _log.LogInformation("Scarico il dump dei titoli AniDB");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temp = file + ".tmp";
                await using (var source = await _http.GetStreamAsync(TitlesUrl, ct).ConfigureAwait(false))
                await using (var gzip = new GZipStream(source, CompressionMode.Decompress))
                await using (var target = File.Create(temp))
                {
                    await gzip.CopyToAsync(target, ct).ConfigureAwait(false);
                }
                File.Move(temp, file, overwrite: true);
            }

            _titles = ParseTitles(await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false));
            return _titles;
        }
        finally
        {
            _titlesGate.Release();
        }
    }

    /// <summary>Formato: <c>aid|tipo|lingua|titolo</c>; tipo 1=principale, 2=sinonimo, 3=breve, 4=ufficiale.</summary>
    internal static IReadOnlyList<AnimeTitle> ParseTitles(IEnumerable<string> lines)
    {
        string[] languages = ["x-jat", "en", "it", "ja"];
        var list = new List<AnimeTitle>();
        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                continue;
            }
            var parts = line.Split('|', 4);
            if (parts.Length == 4 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var aid)
                && (parts[1] is "1" or "2" or "4") && (parts[1] == "1" || languages.Contains(parts[2])))
            {
                list.Add(new AnimeTitle(aid, parts[3]));
            }
        }
        return list;
    }

    internal sealed record AnimeTitle(int Aid, string Title);

    public void Dispose()
    {
        _throttle.Dispose();
        _titlesGate.Dispose();
    }
}
