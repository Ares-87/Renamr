using Renamr.Core.BatchRename;
using Renamr.Services.BatchRename;

namespace Renamr.Tests;

/// <summary>I segnaposto aggiunti nella v1.16: parti del nome, cartelle superiori, dimensione, casuale e dati letti dai file.</summary>
public class BatchTokensMoreTests : IDisposable
{
    private static readonly DateTime Noon = new(2024, 7, 14, 10, 30, 5, DateTimeKind.Utc);
    private readonly TempLibrary _lib = new();

    private static BatchFile F(string relative, long size = 1, FileDetails? details = null) =>
        new(Path.Combine(Path.GetTempPath(), "lib", relative), size, Noon, Noon) { Details = details };

    private static string N(BatchFile file, string pattern) =>
        BatchRenameEngine.Apply([file], [new NewNameRule { Pattern = pattern }])[0].NewName;

    [Theory]
    [InlineData("{parola}", "Vacanze")]
    [InlineData("{parola:2}", "Sardegna")]
    [InlineData("{word:-1}", "Agosto")]
    [InlineData("{parola:9} x", "x")]
    [InlineData("{parte:1:4}", "Vaca")]
    [InlineData("{parte:9}", "Sardegna_2024 Agosto")]
    [InlineData("{part:-6}", "Agosto")]
    public void Words_and_parts_of_the_name(string pattern, string expected) =>
        Assert.Equal(expected + ".jpg", N(F("Vacanze.Sardegna_2024 Agosto.jpg"), pattern));

    [Fact]
    public void Parent_folders_size_and_random()
    {
        var file = F(Path.Combine("Foto", "2024", "Mare", "a.jpg"), size: 1536 * 1024);
        Assert.Equal("Mare.jpg", N(file, "{cartella}"));
        Assert.Equal("2024 - Mare.jpg", N(file, "{cartella:2} - {cartella}"));
        Assert.Equal("Foto.jpg", N(file, "{folder:3}"));
        Assert.Equal("1.5 MB.jpg", N(file, "{dimensione}"));
        Assert.Equal("512 B.jpg", N(F("a.jpg", 512), "{size}"));

        var random = N(file, "{casuale}");
        Assert.Matches(@"^\d{4}\.jpg$", random);
        Assert.Equal(random, N(file, "{casuale}")); // stabile: l'anteprima non cambia a ogni tasto
        Assert.Matches(@"^\d{6}\.jpg$", N(file, "{random:6}"));
    }

    [Fact]
    public void File_details_fill_their_placeholders()
    {
        var details = new FileDetails
        {
            DateTaken = new DateTime(2023, 6, 29, 18, 4, 0),
            Camera = "Canon EOS 80D",
            Width = 6000,
            Height = 4000,
            Duration = TimeSpan.FromSeconds(225),
            Artist = "Queen",
            Album = "A Night at the Opera",
            Title = "Bohemian Rhapsody",
            Track = 11,
            Year = 1975,
        };
        var file = F("x.jpg", details: details);
        Assert.Equal("2023-06-29 18.04 Canon EOS 80D 6000x4000.jpg", N(file, "{scatto} {scatto:HH:mm} {fotocamera} {risoluzione}"));
        Assert.Equal("Queen - 11 - Bohemian Rhapsody (1975) 3.45.jpg", N(file, "{artista} - {traccia} - {titolo} ({anno}) {durata}"));
        Assert.Equal("A Night at the Opera 6000 4000.jpg", N(file, "{album} {width} {height}"));
        Assert.Equal("1.01.05.jpg", N(F("y.jpg", details: new FileDetails { Duration = TimeSpan.FromSeconds(3665) }), "{duration}"));

        // Un file senza quei dati: il segnaposto resta vuoto.
        Assert.Equal("Foto.jpg", N(F("z.jpg", details: FileDetails.None), "Foto {scatto}"));
    }

    [Fact]
    public void Only_rules_using_file_data_need_reading_the_content()
    {
        Assert.False(BatchTokens.NeedsDetails([new NewNameRule { Pattern = "{nome} {data}" }]));
        Assert.True(BatchTokens.NeedsDetails([new NewNameRule { Pattern = "{scatto} {nome}" }]));
        Assert.True(BatchTokens.NeedsDetails([new InsertTextRule { Text = "{Artist} - " }]));
        Assert.True(BatchTokens.NeedsDetails([new NameListRule { Names = "a\n{traccia}" }]));
        Assert.False(BatchTokens.NeedsDetails([new NewNameRule { Pattern = "{scatto}", Enabled = false }]));
    }

    [Fact]
    public void Reader_gets_exif_and_music_tags_without_changing_the_file()
    {
        var photo = _lib.Fixture("sample.jpg", "foto.jpg");
        using (var file = (TagLib.Image.File)TagLib.File.Create(photo))
        {
            file.EnsureAvailableTags();
            file.ImageTag.DateTime = new DateTime(2023, 6, 29, 18, 4, 0);
            file.ImageTag.Make = "Canon";
            file.ImageTag.Model = "Canon EOS 80D";
            file.Save();
        }
        var before = File.ReadAllBytes(photo);
        var details = FileDetailsReader.Read(photo);
        Assert.Equal(new DateTime(2023, 6, 29, 18, 4, 0), details.DateTaken);
        Assert.Equal("Canon EOS 80D", details.Camera);
        Assert.Equal(64, details.Width);
        Assert.Equal(48, details.Height);
        Assert.Equal(before, File.ReadAllBytes(photo));

        var song = _lib.Fixture("sample.mp3", "song.mp3");
        using (var file = TagLib.File.Create(song))
        {
            file.Tag.Performers = ["AC/DC"];
            file.Tag.Title = "Thunderstruck";
            file.Tag.Track = 1;
            file.Save();
        }
        var music = FileDetailsReader.Read(song);
        Assert.Equal("AC-DC", music.Artist); // la barra non è ammessa nei nomi
        Assert.Equal("Thunderstruck", music.Title);
        Assert.Equal(1u, music.Track);
        Assert.NotNull(music.Duration);

        var text = Path.Combine(_lib.Root, "nota.txt");
        File.WriteAllText(text, "ciao");
        Assert.Same(FileDetails.None, FileDetailsReader.Read(text));
    }

    [Fact]
    public void Planner_reads_details_only_when_a_rule_needs_them()
    {
        var song = _lib.Fixture("sample.mp3", "song.mp3");
        var planner = new BatchRenamePlanner();
        var plain = planner.Scan(_lib.Root, new BatchRenameOptions { Rules = [new NewNameRule { Pattern = "{nome}" }] });
        Assert.All(plain, f => Assert.Null(f.Details));

        var options = new BatchRenameOptions { Rules = [new NewNameRule { Pattern = "{durata} {nome}" }] };
        Assert.All(planner.Scan(_lib.Root, options), f => Assert.NotNull(f.Details));
        Assert.All(BatchRenamePlanner.WithDetails(plain, options), f => Assert.NotNull(f.Details));
        Assert.Same(plain, BatchRenamePlanner.WithDetails(plain, new BatchRenameOptions()));
        Assert.Contains(song, plain.Select(f => f.Path));
    }

    public void Dispose() => _lib.Dispose();
}
