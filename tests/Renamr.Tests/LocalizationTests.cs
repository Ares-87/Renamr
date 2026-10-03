using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Renamr.Core.Abstractions;
using Renamr.Core.Localization;
using Renamr.Presentation.ViewModels;
using Renamr.Core.Templating;

namespace Renamr.Tests;

public partial class LocalizationTests
{
    private static readonly string[] Others = [.. Strings.SupportedLanguages.Where(l => l != "it")];

    public static TheoryData<string> Languages => [.. Strings.SupportedLanguages];

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_has_exactly_the_italian_keys(string language)
    {
        var italian = Strings.KeysOf("it").Order().ToArray();
        Assert.Equal(italian, Strings.KeysOf(language).Order().ToArray());
    }

    [Fact]
    public void Every_key_has_a_property_and_every_property_a_key()
    {
        var properties = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && p.Name != nameof(Strings.Language))
            .Select(p => p.Name).Order().ToArray();
        Assert.Equal(Strings.KeysOf("it").Order().ToArray(), properties);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Placeholders_match_italian_and_format_without_errors(string language)
    {
        var it = new Strings("it");
        var other = new Strings(language);
        foreach (var key in Strings.KeysOf("it"))
        {
            var expected = Placeholder().Matches(it[key]).Select(m => m.Value).Distinct().Order();
            Assert.Equal(expected, Placeholder().Matches(other[key]).Select(m => m.Value).Distinct().Order());
            if (Placeholder().IsMatch(other[key]))
            {
                _ = other.Format(key, "a", "b", "c");
            }
            Assert.False(string.IsNullOrWhiteSpace(other[key]), $"{language}: {key} vuoto");
        }
    }

    [Theory]
    [InlineData(null, "de-DE", "de")]
    [InlineData("", "pt-BR", "pt")]
    [InlineData("", "ja-JP", "en")]
    [InlineData("", "", "en")]
    [InlineData("fr", "it-IT", "fr")]
    [InlineData("xx", "es-MX", "es")]
    public void Language_follows_choice_then_system_then_english(string? preference, string system, string expected) =>
        Assert.Equal(expected, Strings.Resolve(preference, new CultureInfo(system)));

    [Fact]
    public void Texts_change_with_the_language()
    {
        Assert.Equal("Rinomina file", new Strings("it").ModeBatch);
        Assert.Equal("Rename files", new Strings("en-US").ModeBatch);
        Assert.Equal("Dateien umbenennen", new Strings("de").ModeBatch);
        Assert.Equal("Deutsch", Strings.NativeName("de"));
    }

    [Fact]
    public void Switching_language_notifies_every_binding_at_once()
    {
        var strings = new Strings("it");
        string? changed = null;
        strings.PropertyChanged += (_, e) => changed = e.PropertyName;
        strings.SetLanguage("fr");
        Assert.Equal(string.Empty, changed);
        Assert.Equal("Annuler", strings.Cancel);
    }

    [Fact]
    public void Linux_views_use_existing_keys() =>
        AssertKeysExist("src/Renamr.Linux", "*.axaml", TrKey());

    [Fact]
    public void Windows_views_use_existing_keys() =>
        AssertKeysExist("src/Renamr.App", "*.xaml", XBindKey());

    [Fact]
    public void Settings_show_the_movie_sections_only_in_media_mode()
    {
        var vm = new SettingsViewModel(new InMemorySettingsStore(), new NameTemplateEngine());
        Assert.True(vm.IsMediaMode);
        Assert.False(vm.IsBatchMode);
        vm.IsMediaMode = false;
        Assert.True(vm.IsBatchMode);
        Assert.Equal(Strings.SupportedLanguages.Count + 1, vm.UiLanguages.Count);
        Assert.Equal(string.Empty, vm.UiLanguage.Code); // di base segue il sistema
    }

    private static void AssertKeysExist(string folder, string pattern, Regex regex)
    {
        var keys = Strings.KeysOf("it").ToHashSet(StringComparer.Ordinal);
        var root = RepoRoot();
        var used = Directory.EnumerateFiles(Path.Combine(root, folder), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => regex.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToList();
        Assert.NotEmpty(used);
        Assert.All(used, key => Assert.Contains(key, keys));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Renamr.sln")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Renamr.sln");
    }

    [GeneratedRegex(@"\{\d\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{l:Tr (\w+)\}")]
    private static partial Regex TrKey();

    [GeneratedRegex(@"loc:Strings\.Current\.(\w+)")]
    private static partial Regex XBindKey();
}
