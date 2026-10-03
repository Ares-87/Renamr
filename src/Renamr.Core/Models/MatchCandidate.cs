namespace Renamr.Core.Models;

/// <summary>Un candidato restituito da un provider, con il punteggio di confidenza [0..1].</summary>
public sealed record MatchCandidate(MediaMetadata Metadata, double Confidence)
{
    public bool IsHighConfidence(double threshold) => Confidence >= threshold;
}

public sealed record MatchResult
{
    public required MatchOutcome Outcome { get; init; }
    public MatchCandidate? Best { get; init; }
    public IReadOnlyList<MatchCandidate> Alternatives { get; init; } = [];

    /// <summary>Provider interrogati in ordine, con l'esito di ciascuno (diagnostica nel pannello errori).</summary>
    public IReadOnlyList<string> Trace { get; init; } = [];

    public static MatchResult NoMatch(IReadOnlyList<string> trace) => new() { Outcome = MatchOutcome.NoMatch, Trace = trace };
}

public enum MatchOutcome
{
    Matched,
    LowConfidence,
    NoMatch,
    ProviderFailure,
}
