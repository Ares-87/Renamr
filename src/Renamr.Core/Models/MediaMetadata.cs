namespace Renamr.Core.Models;

/// <summary>
/// Metadata "ufficiali" restituiti da un provider online, normalizzati.
/// </summary>
public sealed record MediaMetadata
{
    public required MediaKind Kind { get; init; }
    public required string Provider { get; init; }
    public required string ProviderId { get; init; }

    /// <summary>Titolo del film, o titolo della serie per gli episodi.</summary>
    public required string Title { get; init; }
    public string? OriginalTitle { get; init; }

    /// <summary>Data di rilascio ufficiale (film: uscita; episodio: messa in onda; musica: release).</summary>
    public DateOnly? ReleaseDate { get; init; }
    public int? Year => ReleaseDate?.Year ?? YearOnly;
    public int? YearOnly { get; init; }

    // Serie / anime
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public int? AbsoluteEpisode { get; init; }
    public string? EpisodeTitle { get; init; }

    /// <summary>
    /// False quando il titolo dell'episodio non è nella lingua scelta (es. TVmaze: solo inglese).
    /// In quel caso, se il nome file ne contiene già uno, l'anteprima tiene quello.
    /// </summary>
    public bool EpisodeTitleLocalized { get; init; } = true;

    // Musica
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];

    public string? ImdbId { get; init; }
    public string? Overview { get; init; }
}
