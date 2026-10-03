using Renamr.Core.Abstractions;
using Renamr.Core.Templating;
using Renamr.Presentation.ViewModels;
using Renamr.Services.Providers;
using Renamr.Services.Settings;

namespace Renamr.Tests;

public class SettingsTests : IDisposable
{
    private readonly TempLibrary _lib = new();

    private sealed class ReverseProtector : ISecretProtector
    {
        public string Protect(string plain) => new(plain.Reverse().ToArray());
        public string Unprotect(string protectedValue) => new(protectedValue.Reverse().ToArray());
    }

    [Fact]
    public async Task Api_keys_are_never_written_in_clear()
    {
        var paths = new DefaultAppPaths(_lib.Root);
        using var store = new JsonSettingsStore(paths, new ReverseProtector());
        var settings = store.Current;
        settings.Keys.TmdbApiKey = "secret-tmdb-key";
        settings.Templates.Movie = "{Title} [{Year}]";
        settings.Output.WriteEmbeddedMetadata = false;
        await store.SaveAsync(settings);

        var json = await File.ReadAllTextAsync(Path.Combine(_lib.Root, "settings.json"));
        Assert.DoesNotContain("secret-tmdb-key", json);

        using var reloaded = new JsonSettingsStore(paths, new ReverseProtector());
        Assert.Equal("secret-tmdb-key", reloaded.Current.Keys.TmdbApiKey);
        Assert.Equal("{Title} [{Year}]", reloaded.Current.Templates.Movie);
        Assert.False(reloaded.Current.Output.WriteEmbeddedMetadata);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        File.WriteAllText(Path.Combine(_lib.Root, "settings.json"), "{ not json");
        using var store = new JsonSettingsStore(new DefaultAppPaths(_lib.Root), new ReverseProtector());
        Assert.Equal("{Title} ({Year}) [{Resolution}]", store.Current.Templates.Movie);
    }

    [Fact]
    public void Settings_view_model_previews_templates_live()
    {
        var vm = new SettingsViewModel(new InMemorySettingsStore(), new NameTemplateEngine());
        Assert.Equal("The Matrix (1999) [1080p].mkv", vm.MoviePreview);
        Assert.Equal("Breaking Bad - S01E02 - Cat's in the Bag.mkv", vm.EpisodePreview);
        Assert.Equal("Sousou no Frieren - 005 - Phantoms of the Dead.mkv", vm.AnimePreview);

        vm.MovieTemplate = "Film/{Title} ({Year})/{Title}";
        Assert.Equal(Path.Combine("Film", "The Matrix (1999)", "The Matrix.mkv"), vm.MoviePreview);
    }

    [Fact]
    public async Task Settings_view_model_saves_to_store()
    {
        var store = new InMemorySettingsStore();
        var vm = new SettingsViewModel(store, new NameTemplateEngine()) { TmdbApiKey = "  abc  ", HighConfidencePercent = 90 };
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("abc", store.Current.Keys.TmdbApiKey);
        Assert.Equal(0.9, store.Current.Matching.HighConfidenceThreshold, 3);
    }

    public void Dispose() => _lib.Dispose();
}
