using Renamr.Core.Errors;
using Renamr.Services.IO;

namespace Renamr.Tests;

public class SafeFileOperationsTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    private readonly SafeFileOperations _io = new() { TransientRetries = 0 };

    [Fact]
    public void Lock_probe_detects_file_held_open_exclusively()
    {
        var file = _lib.File("in-use.mkv");
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = _io.ProbeExclusiveAccess(file);
            Assert.Equal(RenamrErrorCode.FileInUse, result.Error!.Code);
        }
        Assert.True(_io.ProbeExclusiveAccess(file).Succeeded);
    }

    [Fact]
    public void Lock_probe_reports_missing_file()
    {
        var result = _io.ProbeExclusiveAccess(Path.Combine(_lib.Root, "nope.mkv"));
        Assert.Equal(RenamrErrorCode.FileNotFound, result.Error!.Code);
    }

    [Fact]
    public void ReadOnly_is_cleared_and_restored()
    {
        var file = _lib.File("ro.mkv");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Assert.True(_io.ClearReadOnly(file, out var original).Succeeded);
        Assert.False(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));

        _io.RestoreAttributes(file, original);
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task Move_never_overwrites()
    {
        var a = _lib.File("a.mkv", "A");
        var b = _lib.File("b.mkv", "B");

        var result = await _io.MoveAsync(a, b, CancellationToken.None);

        Assert.Equal(RenamrErrorCode.TargetAlreadyExists, result.Error!.Code);
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
    }

    [Fact]
    public async Task Move_creates_target_folders()
    {
        var a = _lib.File("a.mkv");
        var target = Path.Combine(_lib.Root, "Show", "Season 01", "Show - S01E01.mkv");
        Assert.True((await _io.MoveAsync(a, target, CancellationToken.None)).Succeeded);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(a));
    }

    [Fact]
    public void Dates_are_set_to_release_date_at_noon_utc()
    {
        var file = _lib.File("m.mkv");
        var result = _io.SyncFileSystemDates(file, new DateOnly(1999, 3, 31));

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(new DateTime(1999, 3, 31, 12, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(file));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(new DateTime(1999, 3, 31, 12, 0, 0, DateTimeKind.Utc), File.GetCreationTimeUtc(file));
        }
    }

    [Fact]
    public void Io_errors_are_classified()
    {
        Assert.Equal(RenamrErrorCode.AccessDenied, IoErrorClassifier.Classify(new UnauthorizedAccessException()).Code);
        Assert.Equal(RenamrErrorCode.FileInUse, IoErrorClassifier.Classify(new IOException("x", unchecked((int)0x80070020))).Code);
        Assert.Equal(RenamrErrorCode.DiskFull, IoErrorClassifier.Classify(new IOException("x", unchecked((int)0x80070070))).Code);
        Assert.Equal(RenamrErrorCode.PathTooLong, IoErrorClassifier.Classify(new PathTooLongException()).Code);
    }

    public void Dispose() => _lib.Dispose();
}
