using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Options;
using Renamr.Services;
using Renamr.Services.IO;
using Renamr.Services.Matching;
using Renamr.Services.Pipeline;
using Renamr.Services.Providers;
using Renamr.Services.Providers.Tmdb;
using Renamr.Services.Providers.Tv;

namespace Renamr.Tests;

/// <summary>
/// Dal nome file al nome proposto con il vero grafo di servizi e risposte finte dei database (niente rete):
/// TMDbLib vero con TMDb finto, HttpClient vero con TVmaze finto.
/// </summary>
public class EndToEndTitleTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    private readonly ConcurrentBag<string> _requests = [];
    private ServiceProvider? _sp;

    private RenamePlanner Planner(string? tmdbKey = "test-key", bool rejectKey = false, string language = "it-IT")
    {
        var settings = new InMemorySettingsStore(new RenamrSettings
        {
            Matching = new MatchingSettings { Language = language },
            Keys = new ProviderKeys { TmdbApiKey = tmdbKey },
        });
        var services = new ServiceCollection()
            .AddSingleton<ISettingsStore>(settings)
            .AddSingleton<IAppPaths>(new DefaultAppPaths(Path.Combine(_lib.Root, ".appdata")))
            .AddRenamrServices();
        services.Replace(ServiceDescriptor.Singleton(new TmdbClientAccessor(settings) { Factory = k => FakeClient(k, new FakeTmdb(_requests, rejectKey)) }));
        services.AddHttpClient(TvMazeProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeTvMaze(_requests));
        _sp = services.BuildServiceProvider();
        return _sp.GetRequiredService<RenamePlanner>();
    }

    private Task<Core.Models.RenamePlanEntry> PlanAsync(RenamePlanner planner, string fileName) =>
        planner.PlanOneAsync(new PathBoundary(_lib.Root), _lib.File(fileName), CancellationToken.None);

    private string Requests => string.Join(" | ", _requests);

    [Fact]
    public async Task Movie_title_comes_back_in_italian()
    {
        var entry = await PlanAsync(Planner(), "The.Lord.of.the.Rings.The.Fellowship.of.the.Ring.2001.1080p.BluRay.x264.mkv");

        Assert.True(entry.Error is null, $"{entry.Error} :: {Requests}");
        Assert.Equal("Il Signore degli Anelli - La compagnia dell'anello (2001) [1080p].mkv", entry.TargetName);
        Assert.Contains(_requests, r => r.Contains("search/movie", StringComparison.Ordinal) && r.Contains("language=it-IT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Episode_titles_come_back_in_italian_with_tmdb()
    {
        var entry = await PlanAsync(Planner(), "Game.of.Thrones.S01E02.720p.HDTV.x264.mkv");

        Assert.True(entry.Error is null, $"{entry.Error} :: {Requests}");
        Assert.Equal("Il Trono di Spade - S01E02 - La strada del re.mkv", entry.TargetName);
    }

    /// <summary>Il caso di Daniele: nessuna chiave, TVmaze ha l'episodio solo in inglese, il nome file ce l'ha in italiano.</summary>
    [Theory]
    [InlineData("Silo S03E01 Chi sei tu 2160p DVHDR10.mkv", "Silo - S03E01 - Chi sei tu.mkv")]
    [InlineData("Silo S03E03 Dark Web 2160p DVHDR10.mkv", "Silo - S03E03 - Dark Web.mkv")]
    [InlineData("Silo S03E04 L’importante è che non torni a casa 2160p DVHDR10.mkv", "Silo - S03E04 - L’importante è che non torni a casa.mkv")]
    [InlineData("Silo.S03E01.2160p.WEB-DL.DV.HDR10.mkv", "Silo - S03E01 - Who Are You.mkv")]
    public async Task Without_key_the_italian_episode_title_in_the_file_name_wins_over_english(string fileName, string expected)
    {
        var entry = await PlanAsync(Planner(tmdbKey: null), fileName);

        Assert.True(entry.Error is null, $"{entry.Error} :: {Requests}");
        Assert.Equal("TVmaze", entry.Metadata!.Provider);
        Assert.Equal(expected, entry.TargetName);
    }

    [Fact]
    public async Task In_english_the_database_title_is_kept()
    {
        var entry = await PlanAsync(Planner(tmdbKey: null, language: "en-US"), "Silo S03E01 Chi sei tu 2160p DVHDR10.mkv");
        Assert.Equal("Silo - S03E01 - Who Are You.mkv", entry.TargetName);
    }

    [Fact]
    public async Task Read_access_token_is_accepted_in_place_of_the_api_key()
    {
        // Il "Token di accesso in lettura" di TMDb è un JWT con la chiave API nel campo "aud".
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = B64("{\"alg\":\"HS256\"}") + "." + B64("{\"aud\":\"test-key\",\"sub\":\"abc\",\"scopes\":[\"api_read\"],\"version\":1}") + ".firma";
        Assert.Equal("test-key", TmdbClientAccessor.ApiKeyFrom(token));
        Assert.Equal("abc123", TmdbClientAccessor.ApiKeyFrom("  abc123 \n"));

        var entry = await PlanAsync(Planner(tmdbKey: token), "Game.of.Thrones.S01E02.720p.HDTV.x264.mkv");

        Assert.Equal("Il Trono di Spade - S01E02 - La strada del re.mkv", entry.TargetName);
        Assert.All(_requests.Where(r => r.Contains("themoviedb", StringComparison.Ordinal)), r => Assert.Contains("api_key=test-key", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejected_key_is_reported_instead_of_silently_falling_back()
    {
        var entry = await PlanAsync(Planner(rejectKey: true), "Silo S03E01 Chi sei tu 2160p DVHDR10.mkv");

        Assert.Equal("TVmaze", entry.Metadata!.Provider); // si continua comunque, con la fonte successiva
        var failures = _sp!.GetRequiredService<ProviderHealth>().Failures;
        Assert.Equal(RenamrErrorCode.ProviderAuthFailed, failures["TMDb"].Code);
    }

    /// <summary>TMDbLib accetta un HttpMessageHandler solo dal costruttore interno.</summary>
    private static TMDbLib.Client.TMDbClient FakeClient(string key, HttpMessageHandler handler) =>
        (TMDbLib.Client.TMDbClient)Activator.CreateInstance(typeof(TMDbLib.Client.TMDbClient),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [key, true, "api.themoviedb.org", null, null, handler], null)!;

    public void Dispose()
    {
        _sp?.Dispose();
        _lib.Dispose();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Risponde come api.themoviedb.org: italiano se la richiesta ha language=it-IT, inglese altrimenti.</summary>
    private sealed class FakeTmdb(ConcurrentBag<string> log, bool rejectKey) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            log.Add(uri);
            if (rejectKey)
            {
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"status_code":7,"status_message":"Invalid API key: You must be granted a valid key.","success":false}"""));
            }
            var it = uri.Contains("language=it", StringComparison.Ordinal);
            var path = request.RequestUri.AbsolutePath;
            string? json = path switch
            {
                _ when path.EndsWith("/search/movie", StringComparison.Ordinal) =>
                    $$"""{"page":1,"total_pages":1,"total_results":1,"results":[{"id":120,"title":"{{(it ? "Il Signore degli Anelli - La compagnia dell'anello" : "The Lord of the Rings: The Fellowship of the Ring")}}","original_title":"The Lord of the Rings: The Fellowship of the Ring","release_date":"2001-12-18"}]}""",
                _ when path.EndsWith("/movie/120", StringComparison.Ordinal) =>
                    $$"""{"id":120,"title":"{{(it ? "Il Signore degli Anelli - La compagnia dell'anello" : "The Lord of the Rings: The Fellowship of the Ring")}}","original_title":"The Lord of the Rings: The Fellowship of the Ring","release_date":"2001-12-18","imdb_id":"tt0120737","genres":[]}""",
                _ when path.EndsWith("/search/tv", StringComparison.Ordinal) =>
                    $$"""{"page":1,"total_pages":1,"total_results":1,"results":[{"id":1399,"name":"{{(it ? "Il Trono di Spade" : "Game of Thrones")}}","original_name":"Game of Thrones","first_air_date":"2011-04-17"}]}""",
                _ when path.EndsWith("/tv/1399", StringComparison.Ordinal) =>
                    $$"""{"id":1399,"name":"{{(it ? "Il Trono di Spade" : "Game of Thrones")}}","original_name":"Game of Thrones","first_air_date":"2011-04-17","seasons":[{"season_number":1,"episode_count":10}],"genres":[]}""",
                _ when path.EndsWith("/tv/1399/season/1/episode/2", StringComparison.Ordinal) =>
                    $$"""{"id":63057,"name":"{{(it ? "La strada del re" : "The Kingsroad")}}","season_number":1,"episode_number":2,"air_date":"2011-04-24"}""",
                _ => null,
            };
            return Task.FromResult(json is null
                ? Json(HttpStatusCode.NotFound, """{"status_code":34,"status_message":"not found"}""")
                : Json(HttpStatusCode.OK, json));
        }
    }

    /// <summary>TVmaze: "Silo" senza AKA italiano, episodi solo in inglese (come il servizio vero).</summary>
    private sealed class FakeTvMaze(ConcurrentBag<string> log) : HttpMessageHandler
    {
        private static readonly Dictionary<int, string> Silo3 = new()
        {
            [1] = "Who Are You", [2] = "It's All Good", [3] = "A Dark Web", [4] = "Whatever You Do, Don't Go Home",
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            log.Add(uri.ToString());
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            string? json = uri.AbsolutePath switch
            {
                "/search/shows" => """[{"show":{"id":44933,"name":"Silo","premiered":"2023-05-05","genres":["Drama"]}}]""",
                "/shows/44933/akas" => "[]",
                "/shows/44933/episodebynumber" when int.TryParse(query["number"], out var n) && Silo3.TryGetValue(n, out var name) =>
                    $$"""{"name":"{{name}}","season":3,"number":{{n}},"airdate":"2026-07-0{{n}}"}""",
                _ => null,
            };
            return Task.FromResult(json is null ? Json(HttpStatusCode.NotFound, "{}") : Json(HttpStatusCode.OK, json));
        }
    }
}
