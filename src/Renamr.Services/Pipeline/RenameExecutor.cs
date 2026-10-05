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
        string? rootFolder,
        IReadOnlyList<RenamePlanEntry> plan,
        RenameRunOptions options,
        IProgress<RenameProgress>? progress,
        CancellationToken ct)
    {
        var results = plan.ToArray();
        // "Già corretto" resta fuori: nessun passaggio sul file e nessun posto nel conteggio dell'avanzamento.
        var todo = Enumerable.Range(0, results.Length)
            .Where(i => results[i].NeedsWork(options.IncludeLowConfidence))
            .ToList();
        var boundaries = new BoundaryCache(rootFolder);

        if (!options.DryRun && todo.Count > 0)
        {
            journal.BeginSession(rootFolder ?? results[todo[0]].Root ?? string.Empty);
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
                results[i] = boundaries.For(results[i]) is { } boundary
                    ? await processor.ProcessAsync(boundary, results[i], options, ct).ConfigureAwait(false)
                    : results[i] with { Status = PlanStatus.Error, Error = RenamrError.From(RenamrErrorCode.PathOutsideRoot, results[i].SourcePath) };
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
        var boundaries = new BoundaryCache(session.Source.Length > 0 ? session.Source : null);
        var errors = new List<RenamrError>();

        foreach (var move in entries.Where(e => e.Kind == "move").Reverse())
        {
            // Ogni spostamento ricorda la sua cartella-recinto (i file aggiunti a mano possono stare altrove);
            // i diari scritti prima valgono per la cartella della sessione.
            var boundary = boundaries.For(move.Root);
            if (boundary is null || !boundary.Validate(move.Target!, out var from).Succeeded || !boundary.Validate(move.Source, out var to).Succeeded)
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

/// <summary>Un recinto per cartella, creato una volta sola; null se la cartella non esiste più.</summary>
internal sealed class BoundaryCache(string? defaultRoot)
{
    private readonly Dictionary<string, PathBoundary?> _cache = new(StringComparer.Ordinal);

    public PathBoundary? For(RenamePlanEntry entry) => For(entry.Root);

    public PathBoundary? For(string? root)
    {
        root ??= defaultRoot;
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }
        if (!_cache.TryGetValue(root, out var boundary))
        {
            try
            {
                boundary = new PathBoundary(root);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                boundary = null;
            }
            _cache[root] = boundary;
        }
        return boundary;
    }
}
