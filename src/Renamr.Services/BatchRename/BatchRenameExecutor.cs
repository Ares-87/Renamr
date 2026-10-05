using Renamr.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Models;
using Renamr.Services.IO;
using Renamr.Services.Pipeline;

namespace Renamr.Services.BatchRename;

/// <summary>
/// Modalità "Rinomina file", fase 3: cambia solo i nomi. Niente metadati interni, niente date.
/// <para>
/// Le rinumerazioni spostano spesso un file sul nome di un altro file dell'elenco ("2 ➔ 3, 3 ➔ 4"): si rinomina
/// prima chi ha la destinazione libera, così le catene si sciolgono da sole; i cicli veri ("A ➔ B, B ➔ A") passano
/// per un nome temporaneo. Ogni spostamento finisce subito nel journal, quindi "Annulla" funziona come per i film.
/// </para>
/// </summary>
public sealed class BatchRenameExecutor(SafeFileOperations io, RenameJournal journal, ILogger<BatchRenameExecutor>? logger = null)
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly ILogger _log = logger ?? NullLogger<BatchRenameExecutor>.Instance;

    public async Task<IReadOnlyList<RenamePlanEntry>> ExecuteAsync(
        string? rootFolder,
        IReadOnlyList<RenamePlanEntry> plan,
        bool dryRun,
        IProgress<RenameProgress>? progress,
        CancellationToken ct)
    {
        var boundaries = new BoundaryCache(rootFolder);
        var results = plan.ToArray();
        var todo = Enumerable.Range(0, results.Length).Where(i => results[i] is { Status: PlanStatus.Ready, NameOnly: true, TargetPath: not null }).ToList();
        var phase = dryRun ? Strings.Current.PhaseDryRun : Strings.Current.PhaseRename;
        var done = 0;
        progress?.Report(new RenameProgress(0, todo.Count, null, phase));

        void Finish(int i, RenamePlanEntry entry)
        {
            results[i] = entry;
            progress?.Report(new RenameProgress(++done, todo.Count, entry, phase));
        }

        // 1) Controlli su ogni file prima di toccare qualunque cosa: percorsi, lock, destinazioni occupate da estranei.
        var pending = new List<int>();
        var copies = new List<int>();
        // Con la copia l'originale resta dov'è: non libera il suo nome.
        var movingSources = todo.Where(i => !results[i].Copy).Select(i => results[i].SourcePath).ToHashSet(PathComparer);
        foreach (var i in todo)
        {
            var targetBoundary = results[i].TargetRoot is { } targetRoot ? boundaries.For(targetRoot) : boundaries.For(results[i]);
            var checkedEntry = boundaries.For(results[i]) is { } boundary && targetBoundary is not null
                ? Check(boundary, targetBoundary, results[i], movingSources)
                : Failed(results[i], RenamrError.From(RenamrErrorCode.PathOutsideRoot, results[i].SourcePath));
            if (checkedEntry.Status == PlanStatus.Error)
            {
                movingSources.Remove(results[i].SourcePath);
                Finish(i, checkedEntry);
            }
            else if (dryRun)
            {
                Finish(i, checkedEntry with { Status = PlanStatus.Simulated });
            }
            else
            {
                results[i] = checkedEntry;
                (checkedEntry.Copy ? copies : pending).Add(i);
            }
        }
        if (dryRun || pending.Count + copies.Count == 0)
        {
            return results;
        }

        journal.BeginSession(rootFolder ?? results[pending.Concat(copies).First()].Root ?? string.Empty);

        // 1b) Le copie: nessuna catena possibile, gli originali non si spostano.
        foreach (var i in copies)
        {
            if (ct.IsCancellationRequested)
            {
                Finish(i, results[i] with { Status = PlanStatus.Skipped, Error = RenamrError.From(RenamrErrorCode.Cancelled) });
                continue;
            }
            var entry = results[i];
            CreateFolders(entry, entry.TargetPath!);
            try
            {
                var copied = await io.CopyAsync(entry.SourcePath, entry.TargetPath!, ct).ConfigureAwait(false);
                if (copied.Succeeded)
                {
                    journal.RecordCopy(entry.SourcePath, entry.TargetPath!, RootOf(entry), entry.TargetRoot);
                    Finish(i, entry with { Status = PlanStatus.Done, Error = null });
                }
                else
                {
                    Finish(i, Failed(entry, copied.Error!));
                }
            }
            catch (OperationCanceledException)
            {
                Finish(i, entry with { Status = PlanStatus.Skipped, Error = RenamrError.From(RenamrErrorCode.Cancelled) });
            }
        }

        // 2) Chi ha la destinazione libera va subito; ogni rinomina può liberare la destinazione di un altro.
        bool progressMade;
        do
        {
            progressMade = false;
            foreach (var i in pending.ToList())
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                var entry = results[i];
                if (IsOccupied(entry.SourcePath, entry.TargetPath!))
                {
                    continue;
                }
                pending.Remove(i);
                movingSources.Remove(entry.SourcePath);
                progressMade = true;
                CreateFolders(entry, entry.TargetPath!);
                Finish(i, await MoveAsync(entry, entry.SourcePath, entry.TargetPath!, ct).ConfigureAwait(false));
            }
        }
        while (progressMade && pending.Count > 0 && !ct.IsCancellationRequested);

        // 3) Restano solo i cicli: tutti su un nome temporaneo, poi ognuno sul nome finale.
        var parked = new List<(int Index, string Temp)>();
        foreach (var i in pending)
        {
            if (ct.IsCancellationRequested)
            {
                Finish(i, results[i] with { Status = PlanStatus.Skipped, Error = RenamrError.From(RenamrErrorCode.Cancelled) });
                continue;
            }
            var entry = results[i];
            var temp = Path.Combine(Path.GetDirectoryName(entry.SourcePath)!, $".renamr-{Guid.NewGuid():N}{Path.GetExtension(entry.SourcePath)}");
            var moved = await io.MoveAsync(entry.SourcePath, temp, CancellationToken.None).ConfigureAwait(false);
            if (moved.Succeeded)
            {
                journal.RecordMove(entry.SourcePath, temp, RootOf(entry));
                parked.Add((i, temp));
            }
            else
            {
                Finish(i, entry with { Status = PlanStatus.Error, Error = moved.Error });
            }
        }
        foreach (var (i, temp) in parked)
        {
            // Una volta parcheggiato, il file va comunque riportato su un nome vero: niente annullamento a metà.
            var entry = results[i];
            CreateFolders(entry, entry.TargetPath!);
            var result = await MoveAsync(entry, temp, entry.TargetPath!, CancellationToken.None).ConfigureAwait(false);
            if (result.Status == PlanStatus.Error)
            {
                var back = await io.MoveAsync(temp, entry.SourcePath, CancellationToken.None).ConfigureAwait(false);
                if (back.Succeeded)
                {
                    journal.RecordMove(temp, entry.SourcePath, RootOf(entry));
                }
                else
                {
                    _log.LogError("File rimasto con il nome temporaneo {Temp}: {Error}", temp, back.Error);
                    result = result with { Error = RenamrError.From(RenamrErrorCode.IoFailure, Strings.Current.Format(nameof(Strings.BatchLeftAsTemporary), Path.GetFileName(temp), back.Error)) };
                }
            }
            Finish(i, result with { SourcePath = entry.SourcePath });
        }

        // Annullato durante il punto 2: i file non toccati restano com'erano.
        foreach (var i in todo.Where(i => results[i].Status == PlanStatus.Ready))
        {
            Finish(i, results[i] with { Status = PlanStatus.Skipped, Error = RenamrError.From(RenamrErrorCode.Cancelled) });
        }
        return results;

        string? RootOf(RenamePlanEntry entry) => entry.Root ?? rootFolder;

        // Le sottocartelle di destinazione create da noi finiscono nel diario: "Annulla" le toglie se restano vuote.
        void CreateFolders(RenamePlanEntry entry, string target)
        {
            var missing = new Stack<string>();
            for (var dir = Path.GetDirectoryName(target); !string.IsNullOrEmpty(dir) && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir))
            {
                missing.Push(dir);
            }
            foreach (var dir in missing)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    journal.RecordFolder(dir, entry.TargetRoot ?? RootOf(entry));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return; // lo spostamento fallirà con il messaggio giusto
                }
            }
        }

        bool IsOccupied(string source, string target) =>
            !PathComparer.Equals(source, target) && (File.Exists(target) || Directory.Exists(target));
    }

    private RenamePlanEntry Check(PathBoundary boundary, PathBoundary targetBoundary, RenamePlanEntry entry, HashSet<string> movingSources)
    {
        var check = boundary.Validate(entry.SourcePath, out var source);
        var target = string.Empty;
        if (check.Succeeded)
        {
            check = targetBoundary.Validate(entry.TargetPath!, out target);
        }
        if (!check.Succeeded)
        {
            return Failed(entry, check.Error!);
        }
        entry = entry with { SourcePath = source, TargetPath = target };

        // Le cartelle non si aprono come file: per loro decide Directory.Move (che rifiuta se qualcosa è in uso).
        var probe = Directory.Exists(source) ? OperationResult.Ok() : io.ProbeExclusiveAccess(source);
        if (!probe.Succeeded)
        {
            return Failed(entry, probe.Error!);
        }

        // Occupata da un file che non fa parte del piano (comparso dopo l'anteprima): non si sovrascrive mai.
        var self = PathComparer.Equals(source, target);
        if (!self && !movingSources.Contains(target) && (File.Exists(target) || Directory.Exists(target)))
        {
            return Failed(entry, RenamrError.From(RenamrErrorCode.TargetAlreadyExists, target));
        }
        return entry;
    }

    private async Task<RenamePlanEntry> MoveAsync(RenamePlanEntry entry, string from, string to, CancellationToken ct)
    {
        try
        {
            var moved = await io.MoveAsync(from, to, ct).ConfigureAwait(false);
            if (!moved.Succeeded)
            {
                return Failed(entry, moved.Error!);
            }
            journal.RecordMove(from, to, entry.Root, entry.TargetRoot);
            return entry with { Status = PlanStatus.Done, Error = null };
        }
        catch (OperationCanceledException)
        {
            return entry with { Status = PlanStatus.Skipped, Error = RenamrError.From(RenamrErrorCode.Cancelled) };
        }
    }

    private RenamePlanEntry Failed(RenamePlanEntry entry, RenamrError error)
    {
        _log.LogWarning("{Path}: {Error}", entry.SourcePath, error);
        return entry with { Status = PlanStatus.Error, Error = error };
    }
}
