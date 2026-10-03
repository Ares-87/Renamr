using Renamr.Core.Localization;
using Renamr.Core.Models;

namespace Renamr.Core.Templating;

/// <summary>Un formato pronto da scegliere al volo (menu contestuale della lista).</summary>
public sealed record TemplatePreset(MediaKind Kind, string Label, string Pattern);

/// <summary>Formati più comuni per ogni tipo: il primo di ogni gruppo è il predefinito. Nomi nella lingua dell'interfaccia.</summary>
public static class TemplatePresets
{
    public static IReadOnlyList<TemplatePreset> All
    {
        get
        {
            var s = Strings.Current;
            var season = s.SeasonFolder;
            return
            [
                new(MediaKind.Movie, s.PresetTitleYearResolution, "{Title} ({Year}) [{Resolution}]"),
                new(MediaKind.Movie, s.PresetTitleYear, "{Title} ({Year})"),
                new(MediaKind.Movie, s.PresetOriginalTitleYear, "{Original Title} ({Year})"),
                new(MediaKind.Movie, s.PresetYearTitle, "{Year} - {Title}"),
                new(MediaKind.Movie, s.PresetMovieFolder, "{Title} ({Year})/{Title} ({Year})"),

                new(MediaKind.Episode, s.PresetShowSxxExxTitle, "{Show Title} - S{Season:00}E{Episode:00} - {Episode Title}"),
                new(MediaKind.Episode, s.PresetShow1x02Title, "{Show Title} - {Season}x{Episode:00} - {Episode Title}"),
                new(MediaKind.Episode, s.PresetShowSxxExx, "{Show Title} - S{Season:00}E{Episode:00}"),
                new(MediaKind.Episode, s.PresetOriginalSxxExxTitle, "{Original Title} - S{Season:00}E{Episode:00} - {Episode Title}"),
                new(MediaKind.Episode, s.Format(nameof(Strings.PresetShowSeasonFolders), season),
                    $"{{Show Title}}/{season} {{Season:00}}/{{Show Title}} - S{{Season:00}}E{{Episode:00}} - {{Episode Title}}"),

                new(MediaKind.Anime, s.PresetAnimeAbsoluteTitle, "{Show Title} - {Absolute:000} - {Episode Title}"),
                new(MediaKind.Anime, s.PresetAnimeAbsolute, "{Show Title} - {Absolute:000}"),
                new(MediaKind.Anime, s.PresetAnimeOriginalAbsoluteTitle, "{Original Title} - {Absolute:000} - {Episode Title}"),

                new(MediaKind.Music, s.PresetArtistAlbumTrackTitle, "{Artist} - {Album} - {Track:00} - {Title}"),
                new(MediaKind.Music, s.PresetArtistTitle, "{Artist} - {Title}"),
                new(MediaKind.Music, s.PresetTrackTitle, "{Track:00} - {Title}"),
                new(MediaKind.Music, s.PresetArtistAlbumFolders, "{Artist}/{Album}/{Track:00} - {Title}"),
            ];
        }
    }

    public static IEnumerable<TemplatePreset> For(MediaKind kind) => All.Where(p => p.Kind == kind);
}
