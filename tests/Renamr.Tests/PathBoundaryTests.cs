using Renamr.Core.Errors;
using Renamr.Services.IO;

namespace Renamr.Tests;

public class PathBoundaryTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    private readonly PathBoundary _boundary;

    public PathBoundaryTests() => _boundary = new PathBoundary(_lib.Root);

    [Fact]
    public void Root_is_normalized_with_trailing_separator() =>
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), _boundary.Root);

    [Fact]
    public void Child_paths_are_accepted() =>
        Assert.True(_boundary.IsStrictDescendant(Path.Combine(_lib.Root, "Film", "a.mkv")));

    [Fact]
    public void Root_itself_is_not_a_strict_descendant() =>
        Assert.False(_boundary.IsStrictDescendant(_lib.Root));

    [Theory]
    [InlineData("../evil.mkv")]
    [InlineData("Film/../../evil.mkv")]
    [InlineData("./Film/../..")]
    public void Traversal_is_rejected(string relative)
    {
        var candidate = Path.Combine(_lib.Root, relative);
        Assert.False(_boundary.IsStrictDescendant(candidate));
        Assert.Equal(RenamrErrorCode.PathOutsideRoot, _boundary.Validate(candidate, out _).Error!.Code);
    }

    [Fact]
    public void Sibling_with_common_prefix_is_rejected()
    {
        // "/tmp/renamr-test-abc" non deve contenere "/tmp/renamr-test-abcEVIL/x.mkv"
        var sibling = _lib.Root.TrimEnd(Path.DirectorySeparatorChar) + "EVIL" + Path.DirectorySeparatorChar + "x.mkv";
        Assert.False(_boundary.IsStrictDescendant(sibling));
    }

    [Fact]
    public void Absolute_template_output_is_rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "x.mkv");
        Assert.False(_boundary.ResolveTarget(_lib.Root, outside, out _).Succeeded);
    }

    [Fact]
    public void Symlinked_folder_inside_root_is_rejected()
    {
        var outside = Directory.CreateTempSubdirectory("renamr-outside-").FullName;
        try
        {
            var link = Path.Combine(_lib.Root, "link");
            Directory.CreateSymbolicLink(link, outside);
            var result = _boundary.Validate(Path.Combine(link, "file.mkv"), out _);
            Assert.Equal(RenamrErrorCode.ReparsePointRejected, result.Error!.Code);
        }
        finally
        {
            Directory.Delete(Path.Combine(_lib.Root, "link"));
            Directory.Delete(outside);
        }
    }

    [Fact]
    public void Relative_root_is_refused() =>
        Assert.Throws<ArgumentException>(() => new PathBoundary("relative/folder"));

    public void Dispose() => _lib.Dispose();
}
