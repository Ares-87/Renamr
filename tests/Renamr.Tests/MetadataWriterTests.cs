using System.Buffers.Binary;
using Renamr.Core.Models;
using Renamr.Services.Metadata;

namespace Renamr.Tests;

public class MetadataWriterTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    private readonly TagLibMetadataWriter _writer = new();

    private static readonly MediaMetadata Song = new()
    {
        Kind = MediaKind.Music, Provider = "T", ProviderId = "1", Title = "Time", Artist = "Pink Floyd",
        Album = "The Dark Side of the Moon", TrackNumber = 4, ReleaseDate = new DateOnly(1973, 3, 1),
    };

    private static readonly MediaMetadata Film = new()
    {
        Kind = MediaKind.Movie, Provider = "T", ProviderId = "603", Title = "The Matrix", ReleaseDate = new DateOnly(1999, 3, 31),
    };

    [Fact]
    public void Mp3_gets_full_date_in_id3v24()
    {
        var path = _lib.Fixture("sample.mp3", "a.mp3");
        var result = _writer.Write(path, Song);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Warnings);

        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2);
        Assert.Equal(4, id3.Version);
        Assert.Equal("1973-03-01", TagLib.Id3v2.TextInformationFrame.Get(id3, "TDRC", false)?.ToString());
        Assert.Equal(1973u, file.Tag.Year);
        Assert.Equal("Pink Floyd", file.Tag.FirstPerformer);
    }

    [Fact]
    public void Flac_gets_vorbis_date()
    {
        var path = _lib.Fixture("sample.flac", "a.flac");
        Assert.Empty(_writer.Write(path, Song).Warnings);

        using var file = TagLib.File.Create(path);
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph);
        Assert.Equal("1973-03-01", xiph.GetFirstField("DATE"));
    }

    [Fact]
    public void Mp4_gets_day_atom_and_movie_header_date()
    {
        var path = _lib.Fixture("sample.mp4", "a.mp4");
        Assert.Empty(_writer.Write(path, Film).Warnings);

        using (var file = TagLib.File.Create(path))
        {
            Assert.Equal("The Matrix", file.Tag.Title);
            Assert.Equal(1999u, file.Tag.Year);
        }

        Assert.Equal(new DateTime(1999, 3, 31, 12, 0, 0, DateTimeKind.Utc), ReadMvhdCreation(path));
    }

    [Fact]
    public void Mkv_gets_date_released_tag_and_segment_date()
    {
        var path = _lib.Fixture("sample.mkv", "a.mkv");
        var result = _writer.Write(path, Film);
        Assert.True(result.Succeeded);

        using (var file = TagLib.File.Create(path))
        {
            var mkv = (TagLib.Matroska.Tag)file.GetTag(TagLib.TagTypes.Matroska);
            Assert.Equal("1999-03-31", mkv.Get("DATE_RELEASED", null).FirstOrDefault());
        }
        Assert.Equal(new DateTime(1999, 3, 31, 12, 0, 0, DateTimeKind.Utc), ReadMatroskaDate(path));
    }

    [Fact]
    public void Unsupported_extension_is_a_warning_not_an_error()
    {
        var path = _lib.File("notes.txt");
        var result = _writer.Write(path, Film);
        Assert.True(result.Succeeded);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Corrupt_file_is_left_untouched()
    {
        var path = _lib.File("broken.mp4", "this is not an mp4");
        var result = _writer.Write(path, Film);
        Assert.True(result.Succeeded);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal("this is not an mp4", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_lib.Root)); // nessun temporaneo rimasto
    }

    [Fact]
    public void Mp4_patcher_rejects_garbage_without_writing()
    {
        var path = _lib.File("garbage.mp4", new string('z', 64));
        Assert.False(Mp4HeaderDatePatcher.TryPatch(path, DateTime.UtcNow, out _));
        Assert.Equal(new string('z', 64), File.ReadAllText(path));
    }

    private static DateTime ReadMvhdCreation(string path)
    {
        using var fs = File.OpenRead(path);
        Assert.True(Mp4HeaderDatePatcher.TryFindBox(fs, 0, fs.Length, "moov", out var moov, out var moovEnd));
        Assert.True(Mp4HeaderDatePatcher.TryFindBox(fs, moov, moovEnd, "mvhd", out var mvhd, out _));
        fs.Position = mvhd;
        var buf = new byte[20];
        fs.ReadExactly(buf);
        var seconds = buf[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4));
        return new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
    }

    private static DateTime ReadMatroskaDate(string path)
    {
        using var fs = File.OpenRead(path);
        Assert.True(MatroskaDatePatcher.TryReadElementHeader(fs, out _, out var ebmlSize));
        fs.Position += ebmlSize;
        Assert.True(MatroskaDatePatcher.TryReadElementHeader(fs, out _, out var segSize));
        var segEnd = segSize < 0 ? fs.Length : fs.Position + segSize;
        Assert.True(MatroskaDatePatcher.TryFindChild(fs, segEnd, 0x1549A966, out var infoSize));
        Assert.True(MatroskaDatePatcher.TryFindChild(fs, fs.Position + infoSize, 0x4461, out _));
        var buf = new byte[8];
        fs.ReadExactly(buf);
        return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(BinaryPrimitives.ReadInt64BigEndian(buf) / 100);
    }

    /// <summary>Episodi 2160p oltre 4 GB: niente riscrittura dei tag, ma con la data nell'intestazione a posto nessun avviso.</summary>
    [Fact]
    public void Large_mkv_with_header_date_gets_no_warning()
    {
        var path = _lib.Fixture("sample.mkv", "a.mkv");
        var writer = new TagLibMetadataWriter { CopyOnWriteMaxBytes = 1 };

        var result = writer.Write(path, Film);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Warnings);
        Assert.Equal(new DateTime(1999, 3, 31, 12, 0, 0, DateTimeKind.Utc), ReadMatroskaDate(path));
        using var file = TagLib.File.Create(path);
        Assert.NotEqual("The Matrix", file.Tag.Title); // tag non riscritti
    }

    [Fact]
    public void Large_file_without_header_date_explains_why()
    {
        var path = _lib.Fixture("sample.mkv", "a.mkv");
        var writer = new TagLibMetadataWriter { CopyOnWriteMaxBytes = 1 };

        var result = writer.Write(path, Film with { ReleaseDate = null, YearOnly = 1999 });

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(Core.Errors.RenamrErrorCode.MetadataSkippedLargeFile, warning.Code);
        Assert.Contains("4 GB", warning.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _lib.Dispose();
}
