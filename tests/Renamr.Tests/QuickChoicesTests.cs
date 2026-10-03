using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Renamr.Core.Abstractions;
using Renamr.Core.Models;
using Renamr.Core.Options;
using Renamr.Core.Templating;
using Renamr.Presentation.Services;
using Renamr.Presentation.ViewModels;
using Renamr.Services;
using Renamr.Services.Providers;
using Renamr.Services.Providers.Tv;

namespace Renamr.Tests;

/// <summary>Scelte rapide: lingua e formato al volo, versione nel titolo.</summary>
public class QuickChoicesTests : IDisposable
{
    private readonly TempLibrary _lib = new();

    private (MainViewModel Vm, ServiceProvider Sp, LanguageResolver Resolver, ISettingsStore Settings) Create(RenamrSettings? settings = null)
    {
        var store = new InMemorySettingsStore(settings);
        var resolver = new LanguageResolver(store);
        var services = new ServiceCollection()
            .AddSingleton<ISettingsStore>(store)
            .AddSingleton<IAppPaths>(new DefaultAppPaths(Path.Combine(_lib.Root, ".appdata")))
            .AddRenamrServices()
            .AddSingleton<IMetadataResolver>(resolver)
            .AddSingleton<IMessenger>(new StrongReferenceMessenger())
            .AddSingleton<IFolderPickerService>(new NoPicker())
            .AddSingleton<IssuesViewModel>()
            .AddTransient<MainViewModel>()
            .BuildServiceProvider();
        return (services.GetRequiredService<MainViewModel>(), services, resolver, store);
    }

    [Fact]
    public void Template_preset_from_menu_renames_without_new_searches() => UiThread.Run(async () =>
    {
        _lib.Fixture("sample.mkv", "The.Matrix.1999.1080p.mkv");
        var (vm, sp, resolver, settings) = Create();
        using var _ = sp;
        await vm.OpenFolderCommand.ExecuteAsync(_lib.Root);
        Assert.Equal("Matrix (1999) [1080p].mkv", vm.Items.Single().ProposedName);
        var searches = resolver.Calls;

        var preset = TemplatePresets.For(MediaKind.Movie).Single(p => p.Label == "Anno - Titolo");
        Assert.Equal("1999 - Matrix.mkv", vm.PreviewName(vm.Items.Single(), preset));
        await vm.ApplyTemplatePresetCommand.ExecuteAsync(preset);

        Assert.Equal("1999 - Matrix.mkv", vm.Items.Single().ProposedName);
        Assert.Equal(PlanStatus.Ready, vm.Items.Single().Status);
        Assert.Equal("{Year} - {Title}", settings.Current.Templates.Movie);
        Assert.Equal(preset.Pattern, vm.TemplateFor(MediaKind.Movie));
        Assert.Equal(searches, resolver.Calls); // nessuna nuova ricerca online
        Assert.True(vm.RunCommand.CanExecute(null));
    });

    [Fact]
    public void Changing_language_saves_it_and_searches_again() => UiThread.Run(async () =>
    {
        _lib.Fixture("sample.mkv", "The.Matrix.1999.1080p.mkv");
        var (vm, sp, resolver, settings) = Create();
        using var _ = sp;
        Assert.Equal("it-IT", vm.SelectedLanguage.Tag);
        await vm.OpenFolderCommand.ExecuteAsync(_lib.Root);
        var searches = resolver.Calls;

        vm.SelectedLanguage = vm.Languages.Single(l => l.Tag == "en-US");
        await vm.SetLanguageCommand.ExecutionTask!;

        Assert.Equal("en-US", settings.Current.Matching.Language);
        Assert.True(resolver.Calls > searches);
        Assert.Equal("The Matrix (1999) [1080p].mkv", vm.Items.Single().ProposedName);
        Assert.False(vm.IsLanguageHintOpen); // in inglese la chiave TMDb non serve per i titoli
    });

    [Fact]
    public void Settings_saved_with_new_template_refreshes_names() => UiThread.Run(async () =>
    {
        _lib.Fixture("sample.mkv", "The.Matrix.1999.1080p.mkv");
        var (vm, sp, resolver, settings) = Create();
        using var _ = sp;
        await vm.OpenFolderCommand.ExecuteAsync(_lib.Root);
        var searches = resolver.Calls;

        var current = settings.Current;
        await settings.SaveAsync(new RenamrSettings { Templates = current.Templates.With(MediaKind.Movie, "{Title}"), Matching = current.Matching, Keys = current.Keys });
        await vm.SettingsSavedCommand.ExecuteAsync("it-IT");

        Assert.Equal("Matrix.mkv", vm.Items.Single().ProposedName);
        Assert.Equal(searches, resolver.Calls);
    });

    [Fact]
    public void Hint_explains_missing_tmdb_key_for_translated_titles()
    {
        var (vm, sp, _, _) = Create();
        using var _ = sp;
        Assert.True(vm.IsLanguageHintOpen);
        Assert.Contains("TMDb", vm.LanguageHint, StringComparison.Ordinal);

        var (withKey, sp2, _, _) = Create(new RenamrSettings { Keys = new ProviderKeys { TmdbApiKey = "abc" } });
        using var __ = sp2;
        Assert.False(withKey.IsLanguageHintOpen);
    }

    [Fact]
    public void App_title_shows_version()
    {
        Assert.Matches(@"^Renamr v\d+\.\d+\.\d+$", AppInfo.Title);
        Assert.StartsWith(AppInfo.Title, AppInfo.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_preset_renders_a_name()
    {
        var engine = new NameTemplateEngine();
        var movie = new MediaMetadata { Kind = MediaKind.Movie, Provider = "T", ProviderId = "1", Title = "Matrix", OriginalTitle = "The Matrix", ReleaseDate = new DateOnly(1999, 3, 31) };
        var episode = new MediaMetadata { Kind = MediaKind.Episode, Provider = "T", ProviderId = "2", Title = "Il Trono di Spade", OriginalTitle = "Game of Thrones", Season = 1, Episode = 2, AbsoluteEpisode = 2, EpisodeTitle = "La strada del re" };
        var song = new MediaMetadata { Kind = MediaKind.Music, Provider = "T", ProviderId = "3", Title = "Time", Artist = "Pink Floyd", Album = "The Dark Side of the Moon", TrackNumber = 4 };
        foreach (var preset in TemplatePresets.All)
        {
            var md = preset.Kind switch { MediaKind.Movie => movie, MediaKind.Music => song, _ => episode };
            var name = engine.Render(preset.Pattern, md, null, ".mkv");
            Assert.False(string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)), preset.Label);
        }
        Assert.Equal("Il Trono di Spade - 1x02 - La strada del re.mkv",
            engine.Render(TemplatePresets.For(MediaKind.Episode).Single(p => p.Label.Contains("1x02", StringComparison.Ordinal)).Pattern, episode, null, ".mkv"));
        Assert.Equal("Matrix (1999).mkv", engine.Render("{Original Title} ({Year})", movie with { OriginalTitle = null }, null, ".mkv"));
        // Il primo preset di ogni tipo è il formato predefinito.
        var defaults = new TemplateSettings();
        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Episode, MediaKind.Anime, MediaKind.Music })
        {
            Assert.Equal(defaults.For(kind), TemplatePresets.For(kind).First().Pattern);
        }
    }

    [Fact]
    public void Template_kind_follows_episode_numbering()
    {
        var withSeason = new ParsedMediaName { OriginalFileName = "a", Extension = ".mkv", Kind = MediaKind.Anime, Title = "a", Season = 1, Episodes = [5] };
        var absolute = withSeason with { Season = null, Episodes = [], AbsoluteEpisode = 105 };
        Assert.Equal(MediaKind.Episode, TemplateSettings.KindFor(MediaKind.Anime, withSeason));
        Assert.Equal(MediaKind.Anime, TemplateSettings.KindFor(MediaKind.Anime, absolute));
        Assert.Equal(MediaKind.Movie, TemplateSettings.KindFor(MediaKind.Movie, withSeason));
    }

    [Fact]
    public void Tmdb_tv_maps_absolute_numbers_and_skips_placeholder_titles()
    {
        (int, int)[] seasons = [(0, 3), (1, 12), (2, 10)];
        Assert.Equal((1, 5), TmdbTvProvider.MapAbsolute(5, seasons));
        Assert.Equal((2, 3), TmdbTvProvider.MapAbsolute(15, seasons));
        Assert.Null(TmdbTvProvider.MapAbsolute(23, seasons));
        Assert.Null(TmdbTvProvider.MapAbsolute(0, seasons));

        Assert.Null(TmdbTvProvider.UsefulEpisodeTitle("Episodio 5"));
        Assert.Null(TmdbTvProvider.UsefulEpisodeTitle("Episode 12"));
        Assert.Equal("Il lupo e il leone", TmdbTvProvider.UsefulEpisodeTitle("Il lupo e il leone"));
        Assert.Equal("Episodio pilota", TmdbTvProvider.UsefulEpisodeTitle("Episodio pilota"));
    }

    public void Dispose() => _lib.Dispose();

    private sealed class NoPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    /// <summary>Finto database: "Matrix" in italiano, "The Matrix" nelle altre lingue.</summary>
    private sealed class LanguageResolver(ISettingsStore settings) : IMetadataResolver
    {
        private int _calls;
        public int Calls => _calls;

        public Task<MatchResult> ResolveAsync(MediaQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var title = LanguagePreference.From(settings.Current.Matching.Language).TwoLetter == "it" ? "Matrix" : "The Matrix";
            return Task.FromResult(new MatchResult
            {
                Outcome = MatchOutcome.Matched,
                Best = new MatchCandidate(new MediaMetadata { Kind = MediaKind.Movie, Provider = "Fake", ProviderId = "603", Title = title, OriginalTitle = "The Matrix", ReleaseDate = new DateOnly(1999, 3, 31) }, 0.97),
            });
        }
    }
}
