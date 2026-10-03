using Renamr.Core.Models;

namespace Renamr.Core.Templating;

/// <summary>Un formato pronto da scegliere al volo (menu contestuale della lista).</summary>
public sealed record TemplatePreset(MediaKind Kind, string Label, string Pattern);

/// <summary>Formati più comuni per ogni tipo: il primo di ogni gruppo è il predefinito.</summary>
public static class TemplatePresets
{
    public static IReadOnlyList<TemplatePreset> All { get; } =
    [
        new(MediaKind.Movie, "Titolo (Anno) [Risoluzione]", "{Title} ({Year}) [{Resolution}]"),
        new(MediaKind.Movie, "Titolo (Anno)", "{Title} ({Year})"),
        new(MediaKind.Movie, "Titolo originale (Anno)", "{Original Title} ({Year})"),
        new(MediaKind.Movie, "Anno - Titolo", "{Year} - {Title}"),
        new(MediaKind.Movie, "Cartella Titolo (Anno)", "{Title} ({Year})/{Title} ({Year})"),

        new(MediaKind.Episode, "Serie - S01E02 - Titolo episodio", "{Show Title} - S{Season:00}E{Episode:00} - {Episode Title}"),
        new(MediaKind.Episode, "Serie - 1x02 - Titolo episodio", "{Show Title} - {Season}x{Episode:00} - {Episode Title}"),
        new(MediaKind.Episode, "Serie - S01E02", "{Show Title} - S{Season:00}E{Episode:00}"),
        new(MediaKind.Episode, "Titolo originale - S01E02 - Titolo episodio", "{Original Title} - S{Season:00}E{Episode:00} - {Episode Title}"),
        new(MediaKind.Episode, "Cartelle Serie / Stagione 01", "{Show Title}/Stagione {Season:00}/{Show Title} - S{Season:00}E{Episode:00} - {Episode Title}"),

        new(MediaKind.Anime, "Serie - 001 - Titolo episodio", "{Show Title} - {Absolute:000} - {Episode Title}"),
        new(MediaKind.Anime, "Serie - 001", "{Show Title} - {Absolute:000}"),
        new(MediaKind.Anime, "Titolo originale - 001 - Titolo episodio", "{Original Title} - {Absolute:000} - {Episode Title}"),

        new(MediaKind.Music, "Artista - Album - 01 - Titolo", "{Artist} - {Album} - {Track:00} - {Title}"),
        new(MediaKind.Music, "Artista - Titolo", "{Artist} - {Title}"),
        new(MediaKind.Music, "01 - Titolo", "{Track:00} - {Title}"),
        new(MediaKind.Music, "Cartelle Artista / Album", "{Artist}/{Album}/{Track:00} - {Title}"),
    ];

    public static IEnumerable<TemplatePreset> For(MediaKind kind) => All.Where(p => p.Kind == kind);
}
