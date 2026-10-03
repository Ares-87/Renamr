using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renamr.Core.Errors;

namespace Renamr.Services.IO;

/// <summary>
/// Primitive di I/O "difensive". Nessun metodo lancia eccezioni verso il chiamante:
/// tutto ritorna <see cref="OperationResult"/>, così un file problematico non ferma la coda.
/// </summary>
public sealed class SafeFileOperations(ILogger<SafeFileOperations>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<SafeFileOperations>.Instance;

    /// <summary>Tentativi per i lock transitori (player che chiude il file, antivirus che scansiona).</summary>
    public int TransientRetries { get; init; } = 3;
    public TimeSpan TransientDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Verifica preventiva del lock: apertura con <see cref="FileShare.None"/>. Se un player o un client torrent
    /// ha un handle aperto, Windows rifiuta con ERROR_SHARING_VIOLATION e lo sappiamo <i>prima</i> di toccare nulla.
    /// Si usa <see cref="FileAccess.Read"/> così il test funziona anche su file in sola lettura.
    /// </summary>
    public OperationResult ProbeExclusiveAccess(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1, FileOptions.None);
            return OperationResult.Ok();
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException)
        {
            // In questo contesto qualunque IOException "generica" significa: qualcun altro ha il file aperto.
            var classified = IoErrorClassifier.Classify(ex, path);
            return classified.Code == RenamrErrorCode.IoFailure
                ? OperationResult.Fail(RenamrErrorCode.FileInUse, $"{path}: {ex.Message}")
                : OperationResult.Fail(classified);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
        {
            return OperationResult.Fail(IoErrorClassifier.Classify(ex, path));
        }
    }

    /// <summary>
    /// Rimuove l'attributo ReadOnly (se presente) e restituisce gli attributi originali per poterli ripristinare.
    /// </summary>
    public OperationResult ClearReadOnly(string path, out FileAttributes original)
    {
        original = default;
        try
        {
            original = File.GetAttributes(path);
            if (original.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, original & ~FileAttributes.ReadOnly);
                _log.LogDebug("Rimosso ReadOnly da {Path}", path);
            }
            return OperationResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = IoErrorClassifier.Classify(ex, path);
            return error.Code == RenamrErrorCode.AccessDenied
                ? OperationResult.Fail(RenamrErrorCode.ReadOnlyNotCleared, error.Detail)
                : OperationResult.Fail(error);
        }
    }

    /// <summary>Ripristina gli attributi originali (best effort: un fallimento qui è solo un avviso nel log).</summary>
    public void RestoreAttributes(string path, FileAttributes original)
    {
        if (!original.HasFlag(FileAttributes.ReadOnly))
        {
            return;
        }
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Impossibile ripristinare ReadOnly su {Path}", path);
        }
    }

    /// <summary>
    /// Rinomina/sposta in modo atomico (MoveFileEx sullo stesso volume) senza mai sovrascrivere.
    /// I percorsi devono essere già validati da <see cref="PathBoundary"/>.
    /// </summary>
    public async Task<OperationResult> MoveAsync(string source, string target, CancellationToken ct)
    {
        if (!File.Exists(source))
        {
            return OperationResult.Fail(RenamrErrorCode.FileNotFound, source);
        }

        var caseOnlyRename = string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(source, target, StringComparison.Ordinal);

        if (!caseOnlyRename && (File.Exists(target) || Directory.Exists(target)))
        {
            return OperationResult.Fail(RenamrErrorCode.TargetAlreadyExists, target);
        }

        var targetDir = Path.GetDirectoryName(target);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }
                // overwrite:false => se nel frattempo è comparso un file con lo stesso nome, falliamo invece di distruggerlo.
                File.Move(source, target, overwrite: false);
                _log.LogInformation("Rinominato {Source} -> {Target}", source, target);
                return OperationResult.Ok();
            }
            catch (IOException ex) when (IoErrorClassifier.IsTransient(ex) && attempt < TransientRetries)
            {
                _log.LogDebug("Lock transitorio su {Path}, tentativo {Attempt}", source, attempt + 1);
                await Task.Delay(TransientDelay * (attempt + 1), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OperationResult.Fail(IoErrorClassifier.Classify(ex, source));
            }
        }
    }

    /// <summary>
    /// "Deep Date Sync" a livello di file system: CreationTime e LastWriteTime = data di rilascio.
    /// L'orario è fissato a mezzogiorno UTC così il giorno resta lo stesso in qualunque fuso orario
    /// (mezzanotte UTC diventerebbe il giorno prima a New York in Esplora File).
    /// La data di creazione si scrive solo dove il file system lo permette (vedi <see cref="FileCreationTime"/>):
    /// su Linux con ext4/Btrfs/exFAT cambia solo la data di modifica, e non è un errore.
    /// </summary>
    public OperationResult SyncFileSystemDates(string path, DateOnly releaseDate)
    {
        var utc = ToStableUtc(releaseDate);
        try
        {
            if (!FileCreationTime.TrySet(path, utc))
            {
                _log.LogDebug("Data di creazione non modificabile su questo file system: {Path}", path);
            }
            File.SetLastWriteTimeUtc(path, utc);
            return OperationResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or PlatformNotSupportedException)
        {
            var inner = ex is ArgumentOutOfRangeException ? RenamrError.From(RenamrErrorCode.IoFailure, ex.Message) : IoErrorClassifier.Classify(ex, path);
            return OperationResult.Fail(RenamrErrorCode.DateSyncFailed, inner.ToString());
        }
    }

    /// <summary>Ripristina le date (usato dal rollback quando un passo successivo fallisce).</summary>
    public void RestoreTimes(string path, DateTime creationUtc, DateTime lastWriteUtc)
    {
        try
        {
            FileCreationTime.TrySet(path, creationUtc);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Impossibile ripristinare le date di {Path}", path);
        }
    }

    public static DateTime ToStableUtc(DateOnly date) =>
        // NTFS accetta date dal 1601: qualunque data di rilascio reale è valida.
        date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
}
