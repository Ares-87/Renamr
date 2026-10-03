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
        string rootFolder,
        IReadOnlyList<RenamePlanEntry> plan,
        bool dryRun,
        IProgress<RenameProgress>? progress,
        CancellationToken ct)
    {
        var boundary = new PathBoundary(rootFolder);
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
        var movingSources = todo.Select(i => results[i].SourcePath).ToHashSet(PathComparer);
        foreach (var i in todo)
        {
            var checkedEntry = Check(boundary, results[i], movingSources);
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
                pending.Add(i);
            }
        }
        if (dryRun || pending.Count == 0)
        {
            return results;
        }

        journal.BeginSession(boundary.Root);

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
                journal.RecordMove(entry.SourcePath, temp);
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
            var result = await MoveAsync(entry, temp, entry.TargetPath!, CancellationToken.None).ConfigureAwait(false);
            if (result.Status == PlanStatus.Error)
            {
                var back = await io.MoveAsync(temp, entry.SourcePath, CancellationToken.None).ConfigureAwait(false);
                if (back.Succeeded)
                {
                    journal.RecordMove(temp, entry.SourcePath);
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

        bool IsOccupied(string source, string target) =>
            !PathComparer.Equals(source, target) && (File.Exists(target) || Directory.Exists(target));
    }

    private RenamePlanEntry Check(PathBoundary boundary, RenamePlanEntry entry, HashSet<string> movingSources)
    {
        var check = boundary.Validate(entry.SourcePath, out var source);
        var target = string.Empty;
        if (check.Succeeded)
        {
            check = boundary.Validate(entry.TargetPath!, out target);
        }
        if (!check.Succeeded)
        {
            return Failed(entry, check.Error!);
        }
        entry = entry with { SourcePath = source, TargetPath = target };

        var probe = io.ProbeExclusiveAccess(source);
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
            journal.RecordMove(from, to);
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
