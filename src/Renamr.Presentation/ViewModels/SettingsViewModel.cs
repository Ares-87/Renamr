using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Renamr.Core.Abstractions;
using Renamr.Core.Models;
using Renamr.Core.Options;

namespace Renamr.Presentation.ViewModels;

/// <summary>Finestra Impostazioni: template con anteprima dal vivo, chiavi API, soglia di confidenza.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly MediaMetadata SampleMovie = new()
    {
        Kind = MediaKind.Movie, Provider = "TMDb", ProviderId = "603", Title = "The Matrix", ReleaseDate = new DateOnly(1999, 3, 31),
    };

    private static readonly ParsedMediaName SampleParsed = new()
    {
        OriginalFileName = "The.Matrix.1999.1080p.BluRay.x264-FGT.mkv", Extension = ".mkv", Kind = MediaKind.Movie,
        Title = "The Matrix", Year = 1999, Resolution = "1080p", Source = "BluRay", VideoCodec = "x264", ReleaseGroup = "FGT",
    };

    private static readonly MediaMetadata SampleEpisode = new()
    {
        Kind = MediaKind.Episode, Provider = "TVmaze", ProviderId = "169", Title = "Breaking Bad", Season = 1, Episode = 2,
        EpisodeTitle = "Cat's in the Bag...", ReleaseDate = new DateOnly(2008, 1, 27),
    };

    private static readonly MediaMetadata SampleAnime = new()
    {
        Kind = MediaKind.Anime, Provider = "AniDB", ProviderId = "17617", Title = "Sousou no Frieren", Season = 1, Episode = 5,
        AbsoluteEpisode = 5, EpisodeTitle = "Phantoms of the Dead", ReleaseDate = new DateOnly(2023, 10, 6),
    };

    private static readonly MediaMetadata SampleSong = new()
    {
        Kind = MediaKind.Music, Provider = "MusicBrainz", ProviderId = "x", Title = "Time", Artist = "Pink Floyd",
        Album = "The Dark Side of the Moon", TrackNumber = 4, ReleaseDate = new DateOnly(1973, 3, 1),
    };

    private readonly ISettingsStore _store;
    private readonly INameTemplateEngine _engine;

    public SettingsViewModel(ISettingsStore store, INameTemplateEngine engine)
    {
        _store = store;
        _engine = engine;
        Load(store.Current);
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(MoviePreview))]
    public partial string MovieTemplate { get; set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(EpisodePreview))]
    public partial string EpisodeTemplate { get; set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AnimePreview))]
    public partial string AnimeTemplate { get; set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(MusicPreview))]
    public partial string MusicTemplate { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? TmdbApiKey { get; set; }

    [ObservableProperty]
    public partial string? OmdbApiKey { get; set; }

    [ObservableProperty]
    public partial string? TheTvdbApiKey { get; set; }

    [ObservableProperty]
    public partial string? TheTvdbPin { get; set; }

    [ObservableProperty]
    public partial string? AcoustIdClientKey { get; set; }

    [ObservableProperty]
    public partial string? AniDbClientName { get; set; }

    /// <summary>Soglia "Pronto" in percentuale (50–99).</summary>
    [ObservableProperty]
    public partial double HighConfidencePercent { get; set; }

    [ObservableProperty]
    public partial string Language { get; set; } = "it-IT";

    public IReadOnlyList<string> Languages { get; } = [.. LanguageOption.All.Select(l => l.Tag)];

    public string MoviePreview => Preview(MovieTemplate, SampleMovie, SampleParsed, ".mkv");
    public string EpisodePreview => Preview(EpisodeTemplate, SampleEpisode, null, ".mkv");
    public string AnimePreview => Preview(AnimeTemplate, SampleAnime, null, ".mkv");
    public string MusicPreview => Preview(MusicTemplate, SampleSong, null, ".flac");

    [RelayCommand]
    private Task SaveAsync()
    {
        var current = _store.Current;
        var updated = new RenamrSettings
        {
            Templates = new TemplateSettings { Movie = MovieTemplate, Episode = EpisodeTemplate, Anime = AnimeTemplate, Music = MusicTemplate },
            Matching = new MatchingSettings
            {
                HighConfidenceThreshold = Math.Clamp(HighConfidencePercent / 100.0, 0.5, 0.99),
                MinimumConfidence = current.Matching.MinimumConfidence,
                MaxParallelLookups = current.Matching.MaxParallelLookups,
                Language = Language,
            },
            Keys = new ProviderKeys
            {
                TmdbApiKey = Clean(TmdbApiKey),
                OmdbApiKey = Clean(OmdbApiKey),
                TheTvdbApiKey = Clean(TheTvdbApiKey),
                TheTvdbPin = Clean(TheTvdbPin),
                AcoustIdClientKey = Clean(AcoustIdClientKey),
                AniDbClientName = Clean(AniDbClientName),
                AniDbClientVersion = current.Keys.AniDbClientVersion,
            },
            VideoExtensions = current.VideoExtensions,
            AudioExtensions = current.AudioExtensions,
            CompanionExtensions = current.CompanionExtensions,
        };
        return _store.SaveAsync(updated);
    }

    [RelayCommand]
    private void RestoreDefaultTemplates()
    {
        var defaults = new TemplateSettings();
        MovieTemplate = defaults.Movie;
        EpisodeTemplate = defaults.Episode;
        AnimeTemplate = defaults.Anime;
        MusicTemplate = defaults.Music;
    }

    private void Load(RenamrSettings s)
    {
        MovieTemplate = s.Templates.Movie;
        EpisodeTemplate = s.Templates.Episode;
        AnimeTemplate = s.Templates.Anime;
        MusicTemplate = s.Templates.Music;
        TmdbApiKey = s.Keys.TmdbApiKey;
        OmdbApiKey = s.Keys.OmdbApiKey;
        TheTvdbApiKey = s.Keys.TheTvdbApiKey;
        TheTvdbPin = s.Keys.TheTvdbPin;
        AcoustIdClientKey = s.Keys.AcoustIdClientKey;
        AniDbClientName = s.Keys.AniDbClientName;
        HighConfidencePercent = Math.Round(s.Matching.HighConfidenceThreshold * 100);
        Language = s.Matching.Language;
    }

    private string Preview(string pattern, MediaMetadata md, ParsedMediaName? parsed, string ext)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Template vuoto";
        }
        try
        {
            return _engine.Render(pattern, md, parsed, ext);
        }
        catch (ArgumentException ex)
        {
            return $"Template non valido: {ex.Message}";
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
