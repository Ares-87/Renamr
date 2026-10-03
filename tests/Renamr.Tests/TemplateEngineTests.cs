using Renamr.Core.Models;
using Renamr.Core.Options;
using Renamr.Core.Templating;

namespace Renamr.Tests;

public class TemplateEngineTests
{
    private readonly NameTemplateEngine _engine = new();
    private readonly TemplateSettings _templates = new();

    private static MediaMetadata Movie(string title, DateOnly? date) => new()
    {
        Kind = MediaKind.Movie, Provider = "T", ProviderId = "1", Title = title, ReleaseDate = date,
    };

    [Fact]
    public void Movie_template_with_resolution()
    {
        var parsed = new Renamr.Core.Parsing.SceneCleaner().Parse("The.Matrix.1999.1080p.BluRay.x264-FGT.mkv");
        var name = _engine.Render(_templates.Movie, Movie("The Matrix", new DateOnly(1999, 3, 31)), parsed, ".MKV");
        Assert.Equal("The Matrix (1999) [1080p].mkv", name);
    }

    [Fact]
    public void Empty_placeholders_remove_their_brackets()
    {
        var name = _engine.Render(_templates.Movie, Movie("Inception", new DateOnly(2010, 7, 16)), parsed: null, ".mkv");
        Assert.Equal("Inception (2010).mkv", name);
    }

    [Fact]
    public void Episode_template_pads_numbers()
    {
        var md = new MediaMetadata
        {
            Kind = MediaKind.Episode, Provider = "T", ProviderId = "1", Title = "Breaking Bad",
            Season = 1, Episode = 2, EpisodeTitle = "Cat's in the Bag...",
        };
        Assert.Equal("Breaking Bad - S01E02 - Cat's in the Bag.mkv", _engine.Render(_templates.Episode, md, null, ".mkv"));
    }

    [Fact]
    public void Missing_episode_title_leaves_no_dangling_dash()
    {
        var md = new MediaMetadata { Kind = MediaKind.Episode, Provider = "T", ProviderId = "1", Title = "Show", Season = 2, Episode = 10 };
        Assert.Equal("Show - S02E10.mkv", _engine.Render(_templates.Episode, md, null, ".mkv"));
    }

    [Theory]
    [InlineData("Dune: Part Two", "Dune - Part Two")]
    [InlineData("What If...?", "What If")]
    [InlineData("CON", "_CON")]
    [InlineData("AC/DC <Live>", "AC-DC (Live)")]
    [InlineData("   ", "_")]
    public void Windows_invalid_names_are_sanitized(string input, string expected) =>
        Assert.Equal(expected, NameTemplateEngine.SanitizeFileName(input));

    [Fact]
    public void Folder_segments_cannot_escape_with_dot_dot()
    {
        var md = Movie("..", new DateOnly(2000, 1, 1));
        var path = _engine.Render("{Title}/{Title} ({Year})", md, null, ".mkv");
        Assert.DoesNotContain("..", path.Split(Path.DirectorySeparatorChar)[0]);
    }

    [Fact]
    public void Values_with_slashes_do_not_create_folders()
    {
        var md = new MediaMetadata { Kind = MediaKind.Music, Provider = "T", ProviderId = "1", Title = "Thunderstruck", Artist = "AC/DC", Album = "The Razors Edge", TrackNumber = 1 };
        var path = _engine.Render(_templates.Music, md, null, ".flac");
        Assert.Equal("AC-DC - The Razors Edge - 01 - Thunderstruck.flac", path);
    }
}
