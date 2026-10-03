using Renamr.Core.Localization;
using Renamr.Core.Errors;
using Renamr.Core.Models;
using Renamr.Services.IO;

namespace Renamr.Services.Pipeline;

/// <summary>
/// Fase 3 della UI: esegue il piano in sequenza (le scritture su disco non si parallelizzano: stesso disco,
/// nessun guadagno e conflitti più difficili da diagnosticare). Un errore su un file non ferma la coda.
/// </summary>
public sealed class RenameExecutor(MediaFileProcessor processor, RenameJournal journal, SafeFileOperations io)
{
    public async Task<IReadOnlyList<RenamePlanEntry>> ExecuteAsync(
        string rootFolder,
        IReadOnlyList<RenamePlanEntry> plan,
        RenameRunOptions options,
        IProgress<RenameProgress>? progress,
        CancellationToken ct)
    {
        var boundary = new PathBoundary(rootFolder);
        var results = plan.ToArray();
        var todo = Enumerable.Range(0, results.Length)
            .Where(i => results[i].IsActionable && (results[i].Status != PlanStatus.LowConfidence || options.IncludeLowConfidence))
            .ToList();

        if (!options.DryRun)
        {
            journal.BeginSession(boundary.Root);
        }

        var phase = options.DryRun ? Strings.Current.PhaseDryRun : Strings.Current.PhaseRename;
        progress?.Report(new RenameProgress(0, todo.Count, null, phase));

        for (var n = 0; n < todo.Count; n++)
        {
            var i = todo[n];
            if (ct.IsCancellationRequested)
            {
                // Le voci non ancora toccate restano come erano: nessuno stato a metà.
                results[i] = results[i] with { Status = PlanStatus.Skipped, Error = RenamrError.From(RenamrErrorCode.Cancelled) };
                continue;
            }

            try
            {
                results[i] = await processor.ProcessAsync(boundary, results[i], options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results[i] = results[i] with { Status = PlanStatus.Skipped, Error = RenamrError.From(RenamrErrorCode.Cancelled) };
            }
            progress?.Report(new RenameProgress(n + 1, todo.Count, results[i], phase));
        }

        return results;
    }

    /// <summary>Annulla una sessione leggendo il journal al contrario. Le date originali non sono ripristinabili.</summary>
    public async Task<IReadOnlyList<RenamrError>> UndoAsync(string journalFile, CancellationToken ct)
    {
        var entries = RenameJournal.Read(journalFile);
        var session = entries.FirstOrDefault(e => e.Kind == "session")
                      ?? throw new InvalidDataException("Journal senza intestazione di sessione");
        var boundary = new PathBoundary(session.Source);
        var errors = new List<RenamrError>();

        foreach (var move in entries.Where(e => e.Kind == "move").Reverse())
        {
            if (!boundary.Validate(move.Target!, out var from).Succeeded || !boundary.Validate(move.Source, out var to).Succeeded)
            {
                errors.Add(RenamrError.From(RenamrErrorCode.PathOutsideRoot, move.Target));
                continue;
            }
            var result = await io.MoveAsync(from, to, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                errors.Add(result.Error!);
            }
        }
        return errors;
    }
}
