using System.Net;
using System.Text;
using System.Xml.Linq;
using Renamr.Core.Abstractions;
using Renamr.Core.Models;
using Renamr.Core.Options;
using Renamr.Services.Providers;
using Renamr.Services.Providers.Tv;

namespace Renamr.Tests;

/// <summary>I provider TV restituiscono i titoli nella lingua scelta nelle impostazioni (niente rete: risposte finte).</summary>
public class LocalizedTitleTests
{
    private static InMemorySettingsStore Settings(string language) =>
        new(new RenamrSettings { Matching = new MatchingSettings { Language = language } });

    private static MediaQuery Query => new() { Kind = MediaKind.Episode, Title = "Breaking Bad", Season = 1, Episode = 2 };

    [Theory]
    [InlineData("it-IT", "it", "ita", "IT")]
    [InlineData("de-DE", "de", "deu", "DE")]
    [InlineData("ja-JP", "ja", "jpn", "JP")]
    public void Language_codes_for_each_service(string tag, string two, string three, string country)
    {
        var p = LanguagePreference.From(tag);
        Assert.Equal((two, three, country), (p.TwoLetter, p.ThreeLetter, p.Country));
    }

    [Fact]
    public async Task TvMaze_uses_italian_aka_for_show_title()
    {
        var http = FakeHttp("https://api.tvmaze.com/", new()
        {
            ["search/shows"] = """[{"show":{"id":169,"name":"Breaking Bad","premiered":"2008-01-20"}}]""",
            ["shows/169/episodebynumber"] = """{"name":"Cat's in the Bag...","season":1,"number":2,"airdate":"2008-01-27"}""",
            ["shows/169/akas"] = """[{"name":"Breaking Bad - Reazioni collaterali","country":{"code":"IT"}},{"name":"Breaking Bad: Totál szívás","country":{"code":"HU"}}]""",
        });

        var result = await new TvMazeProvider(http, Settings("it-IT")).SearchAsync(Query, CancellationToken.None);

        var md = result[0].Metadata;
        Assert.Equal("Breaking Bad - Reazioni collaterali", md.Title);
        Assert.Equal("Breaking Bad", md.OriginalTitle);
        Assert.Equal(new DateOnly(2008, 1, 27), md.ReleaseDate);
    }

    [Fact]
    public async Task TvMaze_without_aka_keeps_original_title()
    {
        var http = FakeHttp("https://api.tvmaze.com/", new()
        {
            ["search/shows"] = """[{"show":{"id":169,"name":"Breaking Bad","premiered":"2008-01-20"}}]""",
            ["shows/169/episodebynumber"] = """{"name":"Cat's in the Bag...","season":1,"number":2,"airdate":"2008-01-27"}""",
            ["shows/169/akas"] = "[]",
        });
        var result = await new TvMazeProvider(http, Settings("it-IT")).SearchAsync(Query, CancellationToken.None);
        Assert.Equal("Breaking Bad", result[0].Metadata.Title);
    }

    [Fact]
    public async Task TheTvdb_uses_italian_translations_for_series_and_episode()
    {
        var http = FakeHttp("https://api4.thetvdb.com/v4/", new()
        {
            ["login"] = """{"data":{"token":"t"}}""",
            ["search"] = """{"data":[{"tvdb_id":"81189","name":"Breaking Bad","year":"2008"}]}""",
            ["series/81189/episodes/default"] = """{"data":{"episodes":[{"id":349232,"name":"Cat's in the Bag...","aired":"2008-01-27","seasonNumber":1,"number":2}]}}""",
            ["series/81189/translations/ita"] = """{"data":{"name":"Breaking Bad - Reazioni collaterali"}}""",
            ["episodes/349232/translations/ita"] = """{"data":{"name":"Il gatto è nel sacco...","overview":"Walt e Jesse..."}}""",
        });
        var store = Settings("it-IT");
        store.Current.Keys.TheTvdbApiKey = "key";

        var result = await new TheTvdbProvider(http, store).SearchAsync(Query, CancellationToken.None);

        var md = result[0].Metadata;
        Assert.Equal("Breaking Bad - Reazioni collaterali", md.Title);
        Assert.Equal("Il gatto è nel sacco...", md.EpisodeTitle);
    }

    [Fact]
    public async Task TheTvdb_missing_translation_falls_back_to_original()
    {
        var http = FakeHttp("https://api4.thetvdb.com/v4/", new()
        {
            ["login"] = """{"data":{"token":"t"}}""",
            ["search"] = """{"data":[{"tvdb_id":"81189","name":"Breaking Bad","year":"2008"}]}""",
            ["series/81189/episodes/default"] = """{"data":{"episodes":[{"id":349232,"name":"Cat's in the Bag...","aired":"2008-01-27","seasonNumber":1,"number":2}]}}""",
            // nessuna traduzione: il server risponde 404
        });
        var store = Settings("it-IT");
        store.Current.Keys.TheTvdbApiKey = "key";

        var md = (await new TheTvdbProvider(http, store).SearchAsync(Query, CancellationToken.None))[0].Metadata;
        Assert.Equal(("Breaking Bad", "Cat's in the Bag..."), (md.Title, md.EpisodeTitle));
    }

    [Fact]
    public void AniDb_prefers_official_title_in_chosen_language()
    {
        var titles = XElement.Parse("""
            <titles>
              <title xml:lang="x-jat" type="main">Sousou no Frieren</title>
              <title xml:lang="en" type="official">Frieren: Beyond Journey's End</title>
              <title xml:lang="it" type="official">Frieren - Oltre la fine del viaggio</title>
            </titles>
            """);
        Assert.Equal("Frieren - Oltre la fine del viaggio", AniDbProvider.PickTitle(titles, "it", "official"));
        Assert.Null(AniDbProvider.PickTitle(titles, "de", "official"));
    }

    /// <summary>HttpClient che risponde in base al percorso (query string esclusa); 404 per tutto il resto.</summary>
    private static HttpClient FakeHttp(string baseAddress, Dictionary<string, string> routes) =>
        new(new RouteHandler(new Uri(baseAddress), routes)) { BaseAddress = new Uri(baseAddress) };

    private sealed class RouteHandler(Uri baseAddress, Dictionary<string, string> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath[baseAddress.AbsolutePath.Length..];
            return Task.FromResult(routes.TryGetValue(path, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
