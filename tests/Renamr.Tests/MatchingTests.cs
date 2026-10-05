using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Matching;
using Renamr.Core.Models;
using Renamr.Services.Matching;

namespace Renamr.Tests;

public class MatchingTests
{
    [Theory]
    [InlineData("The Matrix", "Matrix", 0.99)]
    [InlineData("Amelie", "Amélie", 0.99)]
    [InlineData("Lord of the Rings Fellowship", "The Lord of the Rings: The Fellowship of the Ring", 0.6)]
    [InlineData("Fast and Furious", "Fast & Furious", 0.99)]
    public void Similarity_handles_articles_accents_and_ampersand(string a, string b, double atLeast) =>
        Assert.True(TitleSimilarity.Score(a, b) >= atLeast, $"{TitleSimilarity.Score(a, b)}");

    [Fact]
    public void Year_mismatch_penalizes_remakes()
    {
        var original = ConfidenceScorer.Score("Dune", 1984, "Dune", null, 1984);
        var remake = ConfidenceScorer.Score("Dune", 1984, "Dune", null, 2021);
        Assert.True(original >= 0.9);
        Assert.True(remake < 0.7);
    }

    [Fact]
    public async Task Cascade_falls_back_when_primary_fails()
    {
        var failing = new FakeProvider("A", 1, _ => throw new ProviderException("A", RenamrErrorCode.ProviderUnavailable, "down"));
        var working = new FakeProvider("B", 2, q => [Candidate("B", q.Title, 0.95)]);
        var resolver = new CascadingMetadataResolver([working, failing], new InMemorySettingsStore());

        var result = await resolver.ResolveAsync(Query("Matrix"), CancellationToken.None);

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal("B", result.Best!.Metadata.Provider);
        Assert.True(failing.Called);
        Assert.Contains(result.Trace, t => t.StartsWith("A:"));
    }

    [Fact]
    public async Task Cascade_stops_at_first_high_confidence()
    {
        var first = new FakeProvider("A", 1, q => [Candidate("A", q.Title, 0.9)]);
        var second = new FakeProvider("B", 2, q => [Candidate("B", q.Title, 0.99)]);
        var resolver = new CascadingMetadataResolver([first, second], new InMemorySettingsStore());

        var result = await resolver.ResolveAsync(Query("Matrix"), CancellationToken.None);

        Assert.Equal("A", result.Best!.Metadata.Provider);
        Assert.False(second.Called);
    }

    [Fact]
    public async Task Low_confidence_keeps_best_across_providers()
    {
        var a = new FakeProvider("A", 1, q => [Candidate("A", q.Title, 0.55)]);
        var b = new FakeProvider("B", 2, q => [Candidate("B", q.Title, 0.70)]);
        var resolver = new CascadingMetadataResolver([a, b], new InMemorySettingsStore());

        var result = await resolver.ResolveAsync(Query("Matrix"), CancellationToken.None);

        Assert.Equal(MatchOutcome.LowConfidence, result.Outcome);
        Assert.Equal("B", result.Best!.Metadata.Provider);
    }

    [Fact]
    public async Task All_providers_failing_is_a_provider_failure_not_no_match()
    {
        var a = new FakeProvider("A", 1, _ => throw new ProviderException("A", RenamrErrorCode.ProviderAuthFailed, "key"));
        var resolver = new CascadingMetadataResolver([a], new InMemorySettingsStore());
        var result = await resolver.ResolveAsync(Query("X"), CancellationToken.None);
        Assert.Equal(MatchOutcome.ProviderFailure, result.Outcome);
    }

    [Fact]
    public async Task No_match_keeps_the_approximate_results_for_the_manual_choice()
    {
        var a = new FakeProvider("A", 1, q => [Candidate("A", "Altro", 0.30)]);
        var resolver = new CascadingMetadataResolver([a], new InMemorySettingsStore());
        var result = await resolver.ResolveAsync(Query("X"), CancellationToken.None);
        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Best);
        Assert.Equal("Altro", Assert.Single(result.Alternatives).Metadata.Title);
    }

    [Fact]
    public async Task Search_all_asks_every_provider_and_sorts_the_results()
    {
        var first = new FakeProvider("A", 1, q => [Candidate("A", q.Title, 0.9)]);
        var failing = new FakeProvider("B", 2, _ => throw new ProviderException("B", RenamrErrorCode.ProviderUnavailable, "down"));
        var second = new FakeProvider("C", 3, q => [Candidate("C", q.Title, 0.99), Candidate("C", q.Title, 0.99)]);
        var resolver = new CascadingMetadataResolver([first, failing, second], new InMemorySettingsStore());

        var result = await resolver.SearchAllAsync(Query("Matrix"), CancellationToken.None);

        Assert.True(second.Called); // nessuno stop al primo risultato sicuro
        Assert.Equal(["C", "A"], result.Alternatives.Select(c => c.Metadata.Provider)); // doppioni tolti, dal più probabile
        Assert.Contains(result.Trace, t => t.StartsWith("B:"));
    }

    private static MediaQuery Query(string title) => new() { Kind = MediaKind.Movie, Title = title };

    private static MatchCandidate Candidate(string provider, string title, double confidence) =>
        new(new MediaMetadata { Kind = MediaKind.Movie, Provider = provider, ProviderId = "1", Title = title }, confidence);

    private sealed class FakeProvider(string name, int priority, Func<MediaQuery, IReadOnlyList<MatchCandidate>> search) : IMetadataProvider
    {
        public bool Called { get; private set; }
        public string Name => name;
        public int Priority => priority;
        public bool IsConfigured => true;
        public bool Supports(MediaKind kind) => true;

        public Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(search(query));
        }
    }
}
