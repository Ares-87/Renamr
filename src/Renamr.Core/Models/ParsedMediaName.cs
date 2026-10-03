namespace Renamr.Core.Models;

/// <summary>
/// Risultato del parsing "offline" di un nome file (nessuna chiamata di rete).
/// </summary>
public sealed record ParsedMediaName
{
    public required string OriginalFileName { get; init; }
    public required string Extension { get; init; }
    public required MediaKind Kind { get; init; }

    /// <summary>Titolo pulito (film, serie o anime).</summary>
    public required string Title { get; init; }

    public int? Year { get; init; }
    public int? Season { get; init; }
    public IReadOnlyList<int> Episodes { get; init; } = [];

    /// <summary>Numerazione assoluta tipica degli anime ("Title - 105").</summary>
    public int? AbsoluteEpisode { get; init; }

    public string? Resolution { get; init; }
    public string? Source { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? DynamicRange { get; init; }
    public string? ReleaseGroup { get; init; }

    public int? FirstEpisode => Episodes.Count > 0 ? Episodes[0] : AbsoluteEpisode;
}
