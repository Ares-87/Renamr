using System.Security.Cryptography;
using Renamr.Services.IO;
using Renamr.Services.Providers;
using Renamr.Services.Settings;

namespace Renamr.Tests;

/// <summary>Le parti che su Linux funzionano diversamente da Windows: date di creazione e cifratura delle chiavi.</summary>
public class LinuxPlatformTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    private readonly SafeFileOperations _io = new();

    [Fact]
    public void Creation_time_is_only_reported_as_settable_where_it_really_is()
    {
        var file = _lib.File("film.mkv");
        // La cartella temporanea dei test è ext4/tmpfs/overlay su Linux: nessun driver espone la data di nascita.
        Assert.Equal(!OperatingSystem.IsLinux(), FileCreationTime.CanSet(file));
        Assert.Equal(!OperatingSystem.IsLinux(), FileCreationTime.TrySet(file, new DateTime(2023, 10, 19, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Date_sync_succeeds_even_where_creation_time_is_fixed()
    {
        var file = _lib.File("film.mkv");
        var result = _io.SyncFileSystemDates(file, new DateOnly(2023, 10, 19));
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(new DateTime(2023, 10, 19, 12, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(file));
    }

    /// <summary>
    /// Disco NTFS montato con ntfs-3g (come un disco esterno di Windows su Linux): la data di creazione si scrive
    /// nella MFT. Gira solo se RENAMR_NTFS_TEST_DIR punta a una cartella su un volume del genere.
    /// </summary>
    [Fact]
    public void Creation_time_is_written_on_ntfs3g_volumes()
    {
        var dir = Environment.GetEnvironmentVariable("RENAMR_NTFS_TEST_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }
        var file = Path.Combine(dir, $"renamr-{Guid.NewGuid():N}.mkv");
        File.WriteAllText(file, "x");
        try
        {
            Assert.True(FileCreationTime.CanSet(dir));
            var result = _io.SyncFileSystemDates(file, new DateOnly(2023, 10, 19));
            Assert.True(result.Succeeded, result.Error?.ToString());
            var expected = new DateTime(2023, 10, 19, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(expected, FileCreationTime.TryGet(file));
            Assert.Equal(expected, File.GetLastWriteTimeUtc(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void File_key_protector_round_trips_and_keeps_the_key_private()
    {
        var paths = new DefaultAppPaths(_lib.Root);
        var protector = new FileKeySecretProtector(paths);
        var sealedValue = protector.Protect("tmdb-secret");

        Assert.DoesNotContain("tmdb-secret", sealedValue);
        Assert.Equal("tmdb-secret", new FileKeySecretProtector(paths).Unprotect(sealedValue));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_lib.Root, "secret.key")));
        }
    }

    [Fact]
    public void File_key_protector_rejects_values_sealed_with_another_key()
    {
        var sealedValue = new FileKeySecretProtector(new DefaultAppPaths(_lib.Root)).Protect("tmdb-secret");
        using var other = new TempLibrary();
        Assert.ThrowsAny<CryptographicException>(() => new FileKeySecretProtector(new DefaultAppPaths(other.Root)).Unprotect(sealedValue));
    }

    public void Dispose() => _lib.Dispose();
}
