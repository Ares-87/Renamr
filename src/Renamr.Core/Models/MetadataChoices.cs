using System.Text.Json.Serialization;

namespace Renamr.Core.Models;

/// <summary>
/// Quali campi Renamr scrive dentro i file (tag). Ogni voce si sceglie nelle Impostazioni e resta per le volte dopo;
/// l'interruttore "Scrivi metadati interni" della finestra principale li spegne tutti in un colpo.
/// </summary>
public sealed record EmbeddedMetadataFields
{
    /// <summary>Titolo del film, della canzone o dell'episodio.</summary>
    public bool Title { get; init; } = true;

    /// <summary>Nome della serie, stagione ed episodio.</summary>
    public bool SeriesInfo { get; init; } = true;

    /// <summary>Anno e data di uscita nei tag, più la data nell'intestazione MKV/MP4 ("Supporto creato").</summary>
    public bool ReleaseDate { get; init; } = true;

    /// <summary>Trama.</summary>
    public bool Description { get; init; } = true;

    public bool Genres { get; init; } = true;

    /// <summary>Artista, album, traccia e disco (solo musica).</summary>
    public bool MusicInfo { get; init; } = true;

    public static EmbeddedMetadataFields All { get; } = new();

    [JsonIgnore]
    public bool Any => Title || SeriesInfo || ReleaseDate || Description || Genres || MusicInfo;
}

/// <summary>Quali date del file system diventano la data di uscita.</summary>
public sealed record FileDateChoices
{
    public bool Creation { get; init; } = true;
    public bool Modified { get; init; } = true;

    public static FileDateChoices All { get; } = new();

    [JsonIgnore]
    public bool Any => Creation || Modified;
}
