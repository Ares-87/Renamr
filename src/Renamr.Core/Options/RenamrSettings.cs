using Renamr.Core.Models;

namespace Renamr.Core.Options;

/// <summary>Impostazioni persistenti (salvate in %LOCALAPPDATA%\Renamr\settings.json).</summary>
public sealed class RenamrSettings
{
    public TemplateSettings Templates { get; set; } = new();
    public MatchingSettings Matching { get; set; } = new();
    public ProviderKeys Keys { get; set; } = new();
    public OutputSettings Output { get; set; } = new();

    /// <summary>Estensioni considerate, per tipo.</summary>
    public HashSet<string> VideoExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".webm" };
    public HashSet<string> AudioExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wma", ".wav", ".aiff" };
    public HashSet<string> CompanionExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".ass", ".ssa", ".sub", ".idx", ".nfo" };
}

public sealed class TemplateSettings
{
    public string Movie { get; set; } = "{Title} ({Year}) [{Resolution}]";
    public string Episode { get; set; } = "{Show Title} - S{Season:00}E{Episode:00} - {Episode Title}";
    public string Anime { get; set; } = "{Show Title} - {Absolute:000} - {Episode Title}";
    public string Music { get; set; } = "{Artist} - {Album} - {Track:00} - {Title}";

    public string For(MediaKind kind) => kind switch
    {
        MediaKind.Movie => Movie,
        MediaKind.Episode => Episode,
        MediaKind.Anime => Anime,
        MediaKind.Music => Music,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Nessun template per questo tipo."),
    };

    /// <summary>Copia con il template di un solo tipo sostituito.</summary>
    public TemplateSettings With(MediaKind kind, string pattern) => kind switch
    {
        MediaKind.Movie => new TemplateSettings { Movie = pattern, Episode = Episode, Anime = Anime, Music = Music },
        MediaKind.Episode => new TemplateSettings { Movie = Movie, Episode = pattern, Anime = Anime, Music = Music },
        MediaKind.Anime => new TemplateSettings { Movie = Movie, Episode = Episode, Anime = pattern, Music = Music },
        MediaKind.Music => new TemplateSettings { Movie = Movie, Episode = Episode, Anime = Anime, Music = pattern },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Nessun template per questo tipo."),
    };

    /// <summary>
    /// Quale template usa un file: anime con "S01E05" nel nome usano quello degli episodi,
    /// con numerazione assoluta quello degli anime.
    /// </summary>
    public static MediaKind KindFor(MediaKind kind, ParsedMediaName? parsed) =>
        kind == MediaKind.Anime && parsed is not null && parsed.AbsoluteEpisode is null ? MediaKind.Episode : kind;
}

/// <summary>Cosa fa la ridenominazione oltre a cambiare il nome.</summary>
public sealed class OutputSettings
{
    /// <summary>Titolo, stagione/episodio e data scritti dentro il file (tag MKV/MP4/MP3). Si può spegnere.</summary>
    public bool WriteEmbeddedMetadata { get; set; } = true;
}

public sealed class MatchingSettings
{
    /// <summary>Sopra questa soglia la riga è "Pronto"; sotto è "Bassa confidenza".</summary>
    public double HighConfidenceThreshold { get; set; } = 0.80;

    /// <summary>Sotto questa soglia il candidato è scartato del tutto.</summary>
    public double MinimumConfidence { get; set; } = 0.45;

    public string Language { get; set; } = "it-IT";

    /// <summary>Ricerche in parallelo durante l'analisi (le scritture su disco restano sequenziali).</summary>
    public int MaxParallelLookups { get; set; } = 4;
}

/// <summary>Le chiavi sono cifrate a riposo con DPAPI dal SettingsService dell'app.</summary>
public sealed class ProviderKeys
{
    public string? TmdbApiKey { get; set; }
    public string? OmdbApiKey { get; set; }
    public string? TheTvdbApiKey { get; set; }
    public string? TheTvdbPin { get; set; }
    public string? AcoustIdClientKey { get; set; }
    public string? AniDbClientName { get; set; }
    public int AniDbClientVersion { get; set; } = 1;
}
