using Renamr.Core.Abstractions;
using Renamr.Core.Matching;
using Renamr.Core.Models;
using Renamr.Core.Parsing;
using Renamr.Services.Matching;

namespace Renamr.Tests;

public class LeetSpeakTests
{
    [Fact]
    public void Obfuscated_file_name_decodes_to_the_real_title()
    {
        var parsed = new SceneCleaner().Parse("B0N3.L4K3.W.24.4k.mkv");
        Assert.Equal("2160p", parsed.Resolution);

        var alternatives = LeetSpeak.Alternatives(parsed.Title);

        Assert.Equal("Bone Lake W 24", alternatives[0]);
        Assert.Contains("Bone Lake", alternatives);
        Assert.Equal("Bone Lake W 24", LeetSpeak.Decode(parsed.Title));
    }

    [Theory]
    [InlineData("N1GHTM4R3", "Nightmare")]
    [InlineData("Th3 M4tr1x", "The Matrix")]
    [InlineData("TH3 G0DF4TH3R", "The Godfather")]
    [InlineData("M3t4ll1c4", "Metallica")]
    [InlineData("H4LL0W33N", "Halloween")]
    public void Mixed_words_are_decoded(string title, string expected)
    {
        var alternatives = LeetSpeak.Alternatives(title);
        Assert.Contains(expected, alternatives);
    }

    [Theory]
    [InlineData("2012")]
    [InlineData("300")]
    [InlineData("Ocean's 11")]
    [InlineData("Blade Runner 2049")]
    [InlineData("The 2nd Best Exotic Marigold Hotel")]
    [InlineData("Up 3D")]
    [InlineData("Matrix")]
    [InlineData("Fantastic 4")]
    public void Real_titles_with_numbers_are_left_alone(string title) =>
        Assert.Empty(LeetSpeak.Alternatives(title));

    [Theory]
    [InlineData("R0cky 2")]
    [InlineData("R0cky II")]
    public void Sequel_numbers_are_never_trimmed(string title)
    {
        var alternatives = LeetSpeak.Alternatives(title);
        Assert.DoesNotContain("Rocky", alternatives);
        Assert.Contains(alternatives, a => a.StartsWith("Rocky ", StringComparison.Ordinal));
    }

    [Fact]
    public void One_can_be_i_or_l()
    {
        var alternatives = LeetSpeak.Alternatives("K1LL B1LL");
        Assert.Contains("Kill Bill", alternatives);
        Assert.Contains("Klll Blll", alternatives);
    }

    [Fact]
    public async Task Resolver_searches_the_decoded_title_when_the_original_finds_nothing()
    {
        var provider = new TitleProvider("Bone Lake", 2024);
        var resolver = new CascadingMetadataResolver([provider], new InMemorySettingsStore());

        var result = await resolver.ResolveAsync(new MediaQuery { Kind = MediaKind.Movie, Title = "B0N3 L4K3 W 24" }, CancellationToken.None);

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal("Bone Lake", result.Best!.Metadata.Title);
        Assert.Contains("B0N3 L4K3 W 24", provider.Queries);
        Assert.Contains("Bone Lake", provider.Queries);
        Assert.Contains(result.Trace, t => t.Contains("Bone Lake", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolver_keeps_the_original_title_when_it_already_matches()
    {
        var provider = new TitleProvider("Se7en", 1995);
        var resolver = new CascadingMetadataResolver([provider], new InMemorySettingsStore());

        var result = await resolver.ResolveAsync(new MediaQuery { Kind = MediaKind.Movie, Title = "Se7en", Year = 1995 }, CancellationToken.None);

        Assert.Equal("Se7en", result.Best!.Metadata.Title);
        Assert.Equal(["Se7en"], provider.Queries); // nessuna ricerca in più
    }

    /// <summary>Un database con un solo titolo, che risponde solo se la ricerca gli somiglia, con il punteggio vero.</summary>
    private sealed class TitleProvider(string title, int year) : IMetadataProvider
    {
        public List<string> Queries { get; } = [];
        public string Name => "Fake";
        public int Priority => 1;
        public bool IsConfigured => true;
        public bool Supports(MediaKind kind) => true;

        public Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query.Title);
            var score = ConfidenceScorer.Score(query.Title, query.Year, title, null, year, 1);
            IReadOnlyList<MatchCandidate> found = TitleSimilarity.Score(query.Title, title) < 0.5
                ? []
                : [new MatchCandidate(new MediaMetadata { Kind = query.Kind, Provider = Name, ProviderId = "1", Title = title, ReleaseDate = new DateOnly(year, 1, 1) }, score)];
            return Task.FromResult(found);
        }
    }
}
