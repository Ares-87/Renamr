using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Models;
using Renamr.Services.IO;

namespace Renamr.Services.Pipeline;

/// <summary>
/// Esegue <b>un</b> file come una piccola transazione. Ordine dei passi (non casuale):
/// <list type="number">
/// <item>Boundary check di sorgente e destinazione (percorsi normalizzati, niente link).</item>
/// <item>Probe del lock con FileShare.None: se un player/torrent tiene il file, ci fermiamo prima di toccarlo.</item>
/// <item>Rimozione di ReadOnly (ripristinato alla fine).</item>
/// <item>Tag interni via TagLib su copia temporanea + scambio atomico: l'originale è integro anche se si fallisce qui.</item>
/// <item>Rinomina atomica senza sovrascrittura, registrata subito nel journal.</item>
/// <item>Sottotitoli/NFO con lo stesso nome seguono il file.</item>
/// <item>Date del file system per ULTIME: ogni scrittura precedente aggiornerebbe LastWriteTime.</item>
/// </list>
/// Gli errori dei passi 4, 6 e 7 sono avvisi: il file è comunque rinominato correttamente.
/// </summary>
public sealed class MediaFileProcessor(
    SafeFileOperations io,
    IEmbeddedMetadataWriter metadataWriter,
    RenameJournal journal,
    ISettingsStore settings,
    ILogger<MediaFileProcessor>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<MediaFileProcessor>.Instance;

    public async Task<RenamePlanEntry> ProcessAsync(PathBoundary boundary, RenamePlanEntry entry, RenameRunOptions options, CancellationToken ct)
    {
        if (entry.TargetPath is null || entry.Metadata is null)
        {
            return entry;
        }

        // 1) Boundary check: da qui in avanti si usano SOLO i percorsi normalizzati restituiti.
        var check = boundary.Validate(entry.SourcePath, out var source);
        if (check.Succeeded)
        {
            check = boundary.Validate(entry.TargetPath, out var normalizedTarget);
            entry = entry with { TargetPath = normalizedTarget };
        }
        if (!check.Succeeded)
        {
            return Failed(entry, check.Error!);
        }
        var target = entry.TargetPath!;

        // 2) Lock preventivo.
        var probe = io.ProbeExclusiveAccess(source);
        if (!probe.Succeeded)
        {
            return Failed(entry, probe.Error!);
        }

        var isRename = !string.Equals(source, target, StringComparison.Ordinal);
        var caseOnly = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        if (isRename && !caseOnly && File.Exists(target))
        {
            return Failed(entry, RenamrError.From(RenamrErrorCode.TargetAlreadyExists, target));
        }

        if (options.DryRun)
        {
            return entry with { Status = PlanStatus.Simulated };
        }

        // 3) ReadOnly.
        var ro = io.ClearReadOnly(source, out var originalAttributes);
        if (!ro.Succeeded)
        {
            return Failed(entry, ro.Error!);
        }

        var warnings = new List<RenamrError>();
        var current = source;
        try
        {
            // 4) Metadati interni (copy-on-write).
            if (options.WriteEmbeddedMetadata && options.EmbeddedFields.Any && metadataWriter.CanWrite(source))
            {
                var written = metadataWriter.Write(source, entry.Metadata, options.EmbeddedFields);
                warnings.AddRange(written.Warnings);
                if (written.Error is { } writeError)
                {
                    warnings.Add(writeError);
                }
            }

            // 5) Rinomina atomica.
            if (isRename)
            {
                var moved = await io.MoveAsync(source, target, ct).ConfigureAwait(false);
                if (!moved.Succeeded)
                {
                    return Failed(entry, moved.Error!);
                }
                current = target;
                journal.RecordMove(source, target);

                // 6) File accessori.
                warnings.AddRange(await MoveCompanionsAsync(boundary, source, target, ct).ConfigureAwait(false));
            }

            // 7) Deep Date Sync sul file system, per ultimo.
            if (options.SyncFileSystemDates && options.FileDates.Any && entry.Metadata.ReleaseDate is { } date)
            {
                var synced = io.SyncFileSystemDates(current, date, options.FileDates);
                if (!synced.Succeeded)
                {
                    warnings.Add(synced.Error!);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Mai far crollare la coda: l'errore resta confinato a questo file.
            _log.LogError(ex, "Errore imprevisto su {Path}", source);
            return Failed(entry, IoErrorClassifier.Classify(ex, source));
        }
        finally
        {
            io.RestoreAttributes(current, originalAttributes);
        }

        return entry with
        {
            SourcePath = source,
            Status = PlanStatus.Done,
            Error = warnings.FirstOrDefault(),
        };
    }

    private async Task<IReadOnlyList<RenamrError>> MoveCompanionsAsync(PathBoundary boundary, string source, string target, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(source)!;
        var stem = Path.GetFileNameWithoutExtension(source);
        var newStem = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target));
        var companions = settings.Current.CompanionExtensions;
        var warnings = new List<RenamrError>();

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(dir, stem + ".*").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [IoErrorClassifier.Classify(ex, dir)];
        }

        foreach (var file in candidates)
        {
            if (!companions.Contains(Path.GetExtension(file)))
            {
                continue;
            }
            // "Film.it.forced.srt" -> "Nuovo Nome.it.forced.srt"
            var suffix = Path.GetFileName(file)[stem.Length..];
            if (!boundary.Validate(newStem + suffix, out var companionTarget).Succeeded)
            {
                continue;
            }
            var moved = await io.MoveAsync(file, companionTarget, ct).ConfigureAwait(false);
            if (moved.Succeeded)
            {
                journal.RecordMove(file, companionTarget);
            }
            else
            {
                warnings.Add(moved.Error!);
            }
        }
        return warnings;
    }

    private RenamePlanEntry Failed(RenamePlanEntry entry, RenamrError error)
    {
        _log.LogWarning("{Path}: {Error}", entry.SourcePath, error);
        return entry with { Status = PlanStatus.Error, Error = error };
    }
}
