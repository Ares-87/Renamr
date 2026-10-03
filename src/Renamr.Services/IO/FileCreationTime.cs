using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Renamr.Services.IO;

/// <summary>
/// Data di creazione ("nascita") di un file, dove il file system permette davvero di cambiarla.
/// <list type="bullet">
/// <item>Windows e macOS: API native, via <see cref="File.SetCreationTimeUtc"/>.</item>
/// <item>Linux: il kernel non ha una chiamata per cambiare la data di nascita (utimensat tocca solo accesso e modifica),
/// quindi su ext4, Btrfs, XFS ed exFAT resta quella reale. Alcuni driver però la espongono come attributo esteso:
/// ntfs-3g (<c>system.ntfs_crtime</c>, scritta nella MFT e vista da Windows) e i dischi di rete SMB
/// (<c>user.cifs.creationtime</c>). Entrambi usano il FILETIME di Windows: 100 ns dal 1601, little-endian.</item>
/// </list>
/// Su Linux <see cref="File.SetCreationTimeUtc"/> non va usato: .NET lo ripiega sulla data di modifica senza dirlo.
/// </summary>
public static class FileCreationTime
{
    private const string NtfsAttribute = "system.ntfs_crtime";
    private const string CifsAttribute = "user.cifs.creationtime";

    /// <summary>Su questa piattaforma la data di creazione si cambia sempre con le API standard.</summary>
    public static bool IsNativelySupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Il file system che contiene <paramref name="path"/> (file o cartella) permette di cambiare la data di creazione?
    /// Su Linux si legge l'attributo sul percorso stesso: risponde solo un driver che sa anche scriverlo.
    /// </summary>
    public static bool CanSet(string path)
    {
        if (IsNativelySupported)
        {
            return true;
        }
        return OperatingSystem.IsLinux() && AttributeFor(path) is not null;
    }

    /// <summary>Imposta la data di creazione. <c>false</c> se il file system non lo permette (non è un errore).</summary>
    public static bool TrySet(string path, DateTime utc)
    {
        if (IsNativelySupported)
        {
            File.SetCreationTimeUtc(path, utc);
            return true;
        }
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        if (AttributeFor(path) is not { } name)
        {
            return false;
        }
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, utc.ToFileTimeUtc());
        if (setxattr(path, name, bytes, (nuint)bytes.Length, 0) == 0)
        {
            return true;
        }
        var errno = Marshal.GetLastPInvokeError();
        if (errno is Enotsup or Eperm or Einval)
        {
            return false;
        }
        // Permessi, file sparito, disco in sola lettura: lo stesso errore che darebbe la data di modifica.
        throw new IOException($"Data di creazione non impostata: {Marshal.GetPInvokeErrorMessage(errno)}", errno);
    }

    /// <summary>Data di creazione leggibile e modificabile (per ripristinarla), altrimenti <c>null</c>.</summary>
    public static DateTime? TryGet(string path)
    {
        if (IsNativelySupported)
        {
            return File.GetCreationTimeUtc(path);
        }
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        return AttributeFor(path) is { } name && TryGetAttribute(path, name, out var value)
            ? DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(value))
            : null;
    }

    /// <summary>
    /// L'attributo che porta la data di creazione su questo file system, se c'è. <c>system.*</c> lo inventa solo il
    /// driver (ntfs-3g), quindi basta che risponda. <c>user.*</c> invece si può salvare su qualunque ext4: lo si usa
    /// solo se il volume è davvero un disco di rete SMB, altrimenti si scriverebbe un attributo inutile nel file.
    /// </summary>
    private static string? AttributeFor(string path)
    {
        if (TryGetAttribute(path, NtfsAttribute, out _))
        {
            return NtfsAttribute;
        }
        return IsSmbVolume(path) && TryGetAttribute(path, CifsAttribute, out _) ? CifsAttribute : null;
    }

    private static bool IsSmbVolume(string path)
    {
        // struct statfs: f_type è il primo campo (long). 256 byte coprono la struttura su x64 e ARM64.
        var buffer = new byte[256];
        try
        {
            if (statfs(path, buffer) != 0)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        var type = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return type is CifsMagic or Smb2Magic;
    }

    private static bool TryGetAttribute(string path, string name, out byte[] value)
    {
        value = new byte[8];
        try
        {
            var length = getxattr(path, name, value, (nuint)value.Length);
            return length == 8;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // errno di Linux (asm-generic): attributo sconosciuto al file system, assente, vietato o valore rifiutato.
    private const int Eperm = 1;
    private const int Enotsup = 95;
    private const int Einval = 22;

    // Numeri "magici" dei file system di rete in statfs (linux/magic.h).
    private const uint CifsMagic = 0xFF534D42;
    private const uint Smb2Magic = 0xFE534D42;

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int statfs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int setxattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nuint size, int flags);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern nint getxattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nuint size);
}
