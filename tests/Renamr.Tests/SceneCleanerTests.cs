using Renamr.Core.Models;
using Renamr.Core.Parsing;

namespace Renamr.Tests;

public class SceneCleanerTests
{
    private readonly SceneCleaner _parser = new();

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264.DTS-HD.MA.5.1-FGT.mkv", "The Matrix", 1999, "1080p")]
    [InlineData("Blade Runner 2049 (2017) [2160p] [HDR] [x265].mkv", "Blade Runner 2049", 2017, "2160p")]
    [InlineData("2001.A.Space.Odyssey.1968.REMASTERED.720p.WEB-DL.AAC2.0.H.264.mp4", "2001 A Space Odyssey", 1968, "720p")]
    [InlineData("1917.2019.4K.UHD.HDR.DV.x265-GROUP.mkv", "1917", 2019, "2160p")]
    [InlineData("Dune.Part.Two.2024.WEBRip.x264-ION10.mp4", "Dune Part Two", 2024, null)]
    [InlineData("Spider-Man Across the Spider-Verse (2023).mkv", "Spider-Man Across the Spider-Verse", 2023, null)]
    [InlineData("La.vita.e.bella.1997.ITA.1080p.BluRay.x264.mkv", "La vita e bella", 1997, "1080p")]
    [InlineData("Inception.mkv", "Inception", null, null)]
    public void Movies_are_cleaned(string file, string title, int? year, string? resolution)
    {
        var p = _parser.Parse(file);
        Assert.Equal(MediaKind.Movie, p.Kind);
        Assert.Equal(title, p.Title);
        Assert.Equal(year, p.Year);
        Assert.Equal(resolution, p.Resolution);
    }

    [Theory]
    [InlineData("Breaking.Bad.S01E02.720p.HDTV.x264-CTU.mkv", "Breaking Bad", 1, 2)]
    [InlineData("The Office (US) - 3x07 - Branch Wars.avi", "The Office US", 3, 7)]
    [InlineData("Doctor.Who.2005.S10E01.1080p.WEB.h264-GROUP.mkv", "Doctor Who", 10, 1)]
    [InlineData("game_of_thrones_s08e03_1080p.mkv", "game of thrones", 8, 3)]
    [InlineData("Stranger Things - Season 4 Episode 9.mkv", "Stranger Things", 4, 9)]
    public void Episodes_are_detected(string file, string title, int season, int episode)
    {
        var p = _parser.Parse(file);
        Assert.Equal(MediaKind.Episode, p.Kind);
        Assert.Equal(title, p.Title);
        Assert.Equal(season, p.Season);
        Assert.Equal(episode, p.Episodes[0]);
    }

    [Fact]
    public void Multi_episode_range_is_expanded()
    {
        var p = _parser.Parse("Friends.S02E12-E13.DVDRip.XviD.avi");
        Assert.Equal([12, 13], p.Episodes);
        Assert.Equal("DVDRip", p.Source);
        Assert.Equal("XviD", p.VideoCodec);
    }

    [Fact]
    public void Release_group_and_codecs_are_extracted()
    {
        var p = _parser.Parse("The.Matrix.1999.1080p.BluRay.x264.DTS-HD.MA.5.1-FGT.mkv");
        Assert.Equal("FGT", p.ReleaseGroup);
        Assert.Equal("x264", p.VideoCodec);
        Assert.Equal("BluRay", p.Source);
    }

    [Theory]
    [InlineData("[SubsPlease] Sousou no Frieren - 05 (1080p) [A1B2C3D4].mkv", "Sousou no Frieren", 5, "SubsPlease")]
    [InlineData("[Erai-raws] One Piece - 1089 [720p][Multiple Subtitle].mkv", "One Piece", 1089, "Erai-raws")]
    public void Anime_absolute_numbering(string file, string title, int absolute, string group)
    {
        var p = _parser.Parse(file);
        Assert.Equal(MediaKind.Anime, p.Kind);
        Assert.Equal(title, p.Title);
        Assert.Equal(absolute, p.AbsoluteEpisode);
        Assert.Equal(group, p.ReleaseGroup);
    }

    [Fact]
    public void Anime_with_season_episode_keeps_anime_kind()
    {
        var p = _parser.Parse("[Judas] Shingeki no Kyojin S04E28 [1080p][HEVC x265 10bit].mkv");
        Assert.Equal(MediaKind.Anime, p.Kind);
        Assert.Equal(4, p.Season);
        Assert.Equal(28, p.Episodes[0]);
        Assert.Equal("Shingeki no Kyojin", p.Title);
    }

    [Fact]
    public void Music_track_number_is_a_hint()
    {
        var p = _parser.Parse(Path.Combine("Music", "Pink Floyd", "03 - Time.flac"));
        Assert.Equal(MediaKind.Music, p.Kind);
        Assert.Equal("Time", p.Title);
        Assert.Equal(3, p.Episodes[0]);
    }

    [Fact]
    public void Acronyms_with_dots_survive()
    {
        var p = _parser.Parse("Agents.of.S.H.I.E.L.D.S01E01.720p.mkv");
        Assert.StartsWith("Agents of S.H.I.E.L.D", p.Title);
        Assert.Equal(1, p.Season);
    }
}
