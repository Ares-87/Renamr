using System.Text.Json;
using System.Text.Json.Serialization;
using Renamr.Core.BatchRename;

namespace Renamr.Tests;

public class BatchRenameEngineTests
{
    private static readonly DateTime Noon = new(2024, 7, 14, 10, 30, 5, DateTimeKind.Utc);

    private static BatchFile F(string path, long size = 1, DateTime? modified = null) =>
        new(Path.Combine(Path.GetTempPath(), "lib", path), size, modified ?? Noon, modified ?? Noon);

    private static string[] Names(IEnumerable<BatchFile> files, params BatchRule[] rules) =>
        [.. BatchRenameEngine.Apply([.. files], rules).Select(r => r.NewName)];

    [Fact]
    public void Natural_sort_puts_2_before_10()
    {
        var sorted = BatchRenameEngine.Sort([F("IMG 10.jpg"), F("IMG 2.jpg"), F("img 1.jpg")], BatchSortBy.Name, descending: false);
        Assert.Equal(["img 1.jpg", "IMG 2.jpg", "IMG 10.jpg"], sorted.Select(f => f.Name));

        var descending = BatchRenameEngine.Sort([F("a 1.txt"), F("a 2.txt")], BatchSortBy.Name, descending: true);
        Assert.Equal("a 2.txt", descending[0].Name);
    }

    [Fact]
    public void Sort_by_size_and_date()
    {
        var bySize = BatchRenameEngine.Sort([F("big.txt", 300), F("small.txt", 3)], BatchSortBy.Size, false);
        Assert.Equal("small.txt", bySize[0].Name);

        var byDate = BatchRenameEngine.Sort([F("new.txt", modified: Noon), F("old.txt", modified: Noon.AddDays(-3))], BatchSortBy.Modified, false);
        Assert.Equal("old.txt", byDate[0].Name);
    }

    [Fact]
    public void Numbering_with_auto_digits_positions_and_step()
    {
        var files = Enumerable.Range(1, 12).Select(i => F($"foto{i}.JPG")).ToList();

        Assert.Equal("foto1 01.JPG", Names(files, new NumberingRule())[0]);
        Assert.Equal("foto12 12.JPG", Names(files, new NumberingRule())[11]);
        Assert.Equal("100_foto1.JPG", Names(files, new NumberingRule { Position = NumberPosition.Start, Start = 100, Step = 10, Separator = "_" })[0]);
        Assert.Equal("0210.JPG", Names(files, new NumberingRule { Position = NumberPosition.Replace, Start = 100, Step = 10, Digits = 4 })[11]);
    }

    [Fact]
    public void Numbering_can_restart_in_each_folder()
    {
        var files = new[] { F(Path.Combine("A", "x.txt")), F(Path.Combine("A", "y.txt")), F(Path.Combine("B", "z.txt")) };
        var names = Names(files, new NumberingRule { Position = NumberPosition.Replace, RestartInEachFolder = true });
        Assert.Equal(["1.txt", "2.txt", "1.txt"], names);
    }

    [Fact]
    public void Template_with_tokens_and_counter()
    {
        var files = new[] { F(Path.Combine("Mare 2024", "DSC0001.jpg")), F(Path.Combine("Mare 2024", "DSC0002.jpg")) };
        var names = Names(files, new NewNameRule { Pattern = "{cartella} - {n:000} ({originale})" });
        Assert.Equal(["Mare 2024 - 001 (DSC0001).jpg", "Mare 2024 - 002 (DSC0002).jpg"], names);

        var dated = Names([F("a.txt")], new NewNameRule { Pattern = "{data} {nome}" })[0];
        Assert.Equal(Noon.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.CurrentCulture) + " a.txt", dated);

        // L'ora non può contenere i due punti, vietati su Windows.
        var time = Names([F("a.txt")], new NewNameRule { Pattern = "{ora:HH:mm}" })[0];
        Assert.DoesNotContain(':', time);
    }

    [Fact]
    public void Rules_apply_in_order()
    {
        var files = new[] { F("The.Big_Lebowski [1998] (HD).avi"), F("other-file-name.AVI") };
        var names = Names(files,
            new CleanupRule { RemoveBracketed = true },
            new ChangeCaseRule { Mode = CaseMode.TitleCase },
            new ExtensionRule { Mode = ExtensionMode.Lower },
            new NumberingRule { Position = NumberPosition.Start, Separator = ". " });
        Assert.Equal(["1. The Big Lebowski.avi", "2. Other File Name.avi"], names);
    }

    [Fact]
    public void Replace_plain_and_regex()
    {
        Assert.Equal("Foto vacanze.jpg", Names([F("IMG vacanze.jpg")], new ReplaceTextRule { Find = "img", Replacement = "Foto" })[0]);
        Assert.Equal("IMG vacanze.jpg", Names([F("IMG vacanze.jpg")], new ReplaceTextRule { Find = "img", Replacement = "Foto", MatchCase = true })[0]);
        Assert.Equal("2024-07-14 viaggio.jpg",
            Names([F("14.07.2024 viaggio.jpg")], new ReplaceTextRule { Find = @"(\d+)\.(\d+)\.(\d{4})", Replacement = "$3-$2-$1", UseRegex = true })[0]);
    }

    [Fact]
    public void Invalid_regex_becomes_a_row_error()
    {
        var result = BatchRenameEngine.Apply([F("a.txt")], [new ReplaceTextRule { Find = "([", UseRegex = true }]);
        Assert.False(result[0].IsValid);
        Assert.Contains("Regola 1", result[0].Error);
        Assert.NotNull(new ReplaceTextRule { Find = "([", UseRegex = true }.Validate());
    }

    [Fact]
    public void Insert_and_remove_characters()
    {
        Assert.Equal("Viaggio - abc.txt", Names([F("abc.txt")], new InsertTextRule { Text = "Viaggio - " })[0]);
        Assert.Equal("ab-c.txt", Names([F("abc.txt")], new InsertTextRule { Text = "-", Position = InsertPosition.AfterCharacters, Index = 2 })[0]);
        Assert.Equal("cdef.txt", Names([F("abcdef.txt")], new RemoveCharactersRule { Mode = RemoveMode.FirstCharacters, Count = 2 })[0]);
        Assert.Equal("abcd.txt", Names([F("abcdef.txt")], new RemoveCharactersRule { Mode = RemoveMode.LastCharacters, Count = 2 })[0]);
        Assert.Equal("aef.txt", Names([F("abcdef.txt")], new RemoveCharactersRule { Mode = RemoveMode.Range, From = 2, Count = 3 })[0]);
        Assert.Equal("ab.txt", Names([F("abcdef.txt")], new RemoveCharactersRule { Mode = RemoveMode.Range, From = 3, Count = 99 })[0]);
    }

    [Fact]
    public void Case_and_cleanup_options()
    {
        Assert.Equal("L'Ultimo Dell'Anno.txt", Names([F("l'ultimo dell'anno.txt")], new ChangeCaseRule { Mode = CaseMode.TitleCase })[0]);
        Assert.Equal("Ciao mondo.txt", Names([F("CIAO MONDO.txt")], new ChangeCaseRule { Mode = CaseMode.SentenceCase })[0]);
        Assert.Equal("Perche cosi.txt", Names([F("Perché  così.txt")], new CleanupRule { SeparatorsToSpaces = false, RemoveAccents = true })[0]);
        Assert.Equal("Track - Song.txt", Names([F("01 Track - Song.txt")], new CleanupRule { RemoveDigits = true })[0]);
    }

    [Fact]
    public void Extension_can_be_replaced()
    {
        Assert.Equal("foto.jpg", Names([F("foto.jpeg")], new ExtensionRule { Mode = ExtensionMode.Replace, NewExtension = ".jpg" })[0]);
        Assert.Equal("foto.JPEG", Names([F("foto.jpeg")], new ExtensionRule { Mode = ExtensionMode.Upper })[0]);
    }

    [Fact]
    public void Disabled_rules_do_nothing()
    {
        Assert.Equal("a.txt", Names([F("a.txt")], new NumberingRule { Enabled = false })[0]);
    }

    [Theory]
    [InlineData("a?b", "Il carattere ? non è ammesso nei nomi dei file")]
    [InlineData("", "Il nome è vuoto")]
    [InlineData("CON", "Nome riservato da Windows")]
    public void Invalid_names_are_reported(string pattern, string expected)
    {
        var result = BatchRenameEngine.Apply([F("a.txt")], [new NewNameRule { Pattern = pattern }]);
        Assert.Equal(expected, result[0].Error);
    }

    [Theory]
    [InlineData("foto.JPG", "*.jpg; *.png", true)]
    [InlineData("foto.png", "jpg png", true)]
    [InlineData("foto.gif", "jpg, png", false)]
    [InlineData("IMG_001.heic", "IMG_*", true)]
    [InlineData("qualunque.bin", "", true)]
    public void Filter_matches_extensions_and_wildcards(string name, string filter, bool expected) =>
        Assert.Equal(expected, BatchRenameEngine.MatchesFilter(name, filter));

    [Fact]
    public void Options_round_trip_through_json()
    {
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var options = new BatchRenameOptions
        {
            Rules = [new NumberingRule { Start = 5 }, new ReplaceTextRule { Find = "x", Enabled = false }, new ExtensionRule { Mode = ExtensionMode.Upper }],
            SortBy = BatchSortBy.Size,
            Filter = "*.jpg",
        };
        var back = JsonSerializer.Deserialize<BatchRenameOptions>(JsonSerializer.Serialize(options, json), json)!;
        Assert.Equal(5, Assert.IsType<NumberingRule>(back.Rules[0]).Start);
        Assert.False(back.Rules[1].Enabled);
        Assert.Equal(ExtensionMode.Upper, Assert.IsType<ExtensionRule>(back.Rules[2]).Mode);
        Assert.Equal(BatchSortBy.Size, back.SortBy);
    }
}
