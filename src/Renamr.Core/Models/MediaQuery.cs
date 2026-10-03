namespace Renamr.Core.Models;

/// <summary>Richiesta inviata ai provider: nasce dal parsing del nome e/o dai tag esistenti.</summary>
public sealed record MediaQuery
{
    public required MediaKind Kind { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public int? AbsoluteEpisode { get; init; }

    /// <summary>Percorso completo del file (serve ai provider acustici, es. AcoustID).</summary>
    public string? FilePath { get; init; }

    // Indizi musicali letti dai tag già presenti
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public TimeSpan? Duration { get; init; }

    public static MediaQuery FromParsed(ParsedMediaName parsed, string filePath) => new()
    {
        Kind = parsed.Kind,
        Title = parsed.Title,
        Year = parsed.Year,
        Season = parsed.Season,
        Episode = parsed.Episodes.Count > 0 ? parsed.Episodes[0] : null,
        AbsoluteEpisode = parsed.AbsoluteEpisode,
        FilePath = filePath,
    };
}
