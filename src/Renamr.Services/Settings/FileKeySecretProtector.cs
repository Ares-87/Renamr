using System.Security.Cryptography;
using System.Text;
using Renamr.Services.Providers;

namespace Renamr.Services.Settings;

/// <summary>
/// Cifratura delle chiavi API dove DPAPI non esiste (Linux): AES-GCM con una chiave casuale da 256 bit salvata
/// accanto alle impostazioni e leggibile solo dall'utente (permessi 600). Chi copia settings.json senza la chiave
/// non legge le chiavi API; chi ha già accesso al tuo account sì, esattamente come con DPAPI.
/// </summary>
public sealed class FileKeySecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Associated = "Renamr.ProviderKeys.v1"u8.ToArray();

    private readonly string _keyFile;
    private readonly Lazy<byte[]> _key;

    public FileKeySecretProtector(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _keyFile = Path.Combine(paths.DataDirectory, "secret.key");
        _key = new Lazy<byte[]>(LoadOrCreateKey);
    }

    public string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var output = new byte[NonceSize + TagSize + data.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), Associated);
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string protectedValue)
    {
        var input = Convert.FromBase64String(protectedValue);
        if (input.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Valore cifrato troppo corto");
        }
        var plain = new byte[input.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plain, Associated);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyFile))
        {
            var existing = File.ReadAllBytes(_keyFile);
            if (existing.Length == 32)
            {
                return existing;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_keyFile)!);
        var key = RandomNumberGenerator.GetBytes(32);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            // Permessi decisi alla creazione: il file non è mai leggibile da altri, nemmeno per un istante.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(_keyFile, options))
        {
            stream.Write(key);
        }
        return key;
    }
}
