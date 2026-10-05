using Renamr.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Models;
using Renamr.Core.Options;
using Renamr.Services.IO;

namespace Renamr.Services.Pipeline;

/// <summary>
/// Fase 2 della UI ("Anteprima intelligente"): scansione, parsing, matching online e calcolo del nuovo nome.
/// Non modifica nulla sul disco. Le ricerche online girano in parallelo (limitato), l'ordine delle righe è stabile.
/// </summary>
public sealed class RenamePlanner(
    MediaScanner scanner,
    IFileNameParser parser,
    IMetadataResolver resolver,
    INameTemplateEngine templates,
    IEmbeddedMetadataReader tagReader,
    ISettingsStore settings,
    ILogger<RenamePlanner>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<RenamePlanner>.Instance;

    public Task<IReadOnlyList<RenamePlanEntry>> PlanAsync(string rootFolder, IProgress<RenameProgress>? progress, CancellationToken ct)
    {
        var boundary = new PathBoundary(rootFolder);
        return PlanFilesAsync([.. scanner.Scan(boundary).Select(f => new PlanSource(f, rootFolder))], progress, ct);
    }

    /// <summary>
    /// Analizza un elenco di file, ognuno con la sua cartella-recinto: quelli della cartella aperta e quelli aggiunti a mano.
    /// </summary>
    public async Task<IReadOnlyList<RenamePlanEntry>> PlanFilesAsync(IReadOnlyList<PlanSource> files, IProgress<RenameProgress>? progress, CancellationToken ct)
    {
        var boundaries = new Dictionary<string, PathBoundary>(StringComparer.Ordinal);
        foreach (var root in files.Select(f => f.Root).Distinct(StringComparer.Ordinal))
        {
            boundaries[root] = new PathBoundary(root);
        }
        var entries = new RenamePlanEntry[files.Count];
        var done = 0;
        progress?.Report(new RenameProgress(0, files.Count, null, Strings.Current.PhaseAnalysis));

        // Annulla non passa da ParallelOptions.CancellationToken: ogni file controlla ct e chiude subito come annullato,
        // così nessuna eccezione esce dal corpo verso Parallel.ForEachAsync (Visual Studio fermerebbe il debug).
        // Chi chiama controlla ct dopo l'attesa e scarta il piano parziale.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, settings.Current.Matching.MaxParallelLookups) },
            async (i, _) =>
            {
                var boundary = boundaries[files[i].Root];
                // Un file aggiunto a mano che non sta dentro il suo recinto (link, percorso strano) non si analizza nemmeno.
                entries[i] = boundary.IsStrictDescendant(files[i].Path)
                    ? await PlanOneAsync(boundary, files[i].Path, ct).ConfigureAwait(false) with { Root = files[i].Root }
                    : Fail(files[i].Path, null, RenamrErrorCode.PathOutsideRoot, files[i].Path) with { Root = files[i].Root };
                progress?.Report(new RenameProgress(Interlocked.Increment(ref done), files.Count, entries[i], Strings.Current.PhaseAnalysis));
            }).ConfigureAwait(false);

        return DetectConflicts(entries);
    }

    public async Task<RenamePlanEntry> PlanOneAsync(PathBoundary boundary, string path, CancellationToken ct)
    {
        ParsedMediaName? parsed = null;
        if (ct.IsCancellationRequested)
        {
            return Cancelled(path, parsed);
        }
        try
        {
            parsed = parser.Parse(path);
            var query = parsed.Kind == MediaKind.Music
                ? tagReader.TryReadMusicQuery(path) ?? MediaQuery.FromParsed(parsed, path)
                : MediaQuery.FromParsed(parsed, path);

            if (string.IsNullOrWhiteSpace(query.Title) && parsed.Kind != MediaKind.Music)
            {
                return Fail(path, parsed, RenamrErrorCode.UnrecognizedFileName);
            }

            var match = await resolver.ResolveAsync(query, ct).ConfigureAwait(false);
            if (match.Best is null)
            {
                var code = match.Outcome == MatchOutcome.ProviderFailure ? RenamrErrorCode.ProviderUnavailable : RenamrErrorCode.NoDatabaseMatch;
                return Fail(path, parsed, code, string.Join(" · ", match.Trace)) with { Candidates = match.Alternatives };
            }

            var low = match.Outcome == MatchOutcome.LowConfidence;
            var entry = BuildEntry(boundary, path, parsed, match.Best.Metadata, match.Best.Confidence,
                low ? RenamrError.From(RenamrErrorCode.LowConfidenceMatch, string.Join(" · ", match.Trace)) : null);
            return entry with { Candidates = [match.Best, .. match.Alternatives] };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Cancelled(path, parsed);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Analisi fallita per {Path}", path);
            return Fail(path, parsed, RenamrErrorCode.Unexpected, ex.Message);
        }
    }

    /// <summary>
    /// Ricalcola i nomi proposti con i template correnti, senza rifare le ricerche online:
    /// serve quando l'utente cambia formato al volo dal menu contestuale, sceglie un risultato o toglie un file.
    /// </summary>
    public IReadOnlyList<RenamePlanEntry> Rerender(string? rootFolder, IReadOnlyList<RenamePlanEntry> plan)
    {
        var boundaries = new Dictionary<string, PathBoundary>(StringComparer.Ordinal);
        var threshold = settings.Current.Matching.HighConfidenceThreshold;
        var entries = plan.Select(e =>
        {
            if (e.Metadata is null || e.Parsed is null || e.Status is not (PlanStatus.Ready or PlanStatus.LowConfidence or PlanStatus.Unchanged or PlanStatus.Error))
            {
                return e;
            }
            var root = e.Root ?? rootFolder;
            if (root is null)
            {
                return e;
            }
            if (!boundaries.TryGetValue(root, out var boundary))
            {
                boundaries[root] = boundary = new PathBoundary(root);
            }
            RenamrError? lowError = e.ManualMatch || e.Confidence >= threshold
                ? null
                : e.Error is { Code: RenamrErrorCode.LowConfidenceMatch } previous ? previous : RenamrError.From(RenamrErrorCode.LowConfidenceMatch);
            return BuildEntry(boundary, e.SourcePath, e.Parsed, e.Metadata, e.Confidence, lowError) with
            {
                Root = e.Root,
                Candidates = e.Candidates,
                ManualMatch = e.ManualMatch,
            };
        }).ToList();
        return DetectConflicts(entries);
    }

    /// <summary>
    /// L'utente ha scelto un risultato per un file: la riga prende quei dati e vale come confermata
    /// (si rinomina anche senza "Includi bassa confidenza"). Le altre righe si ricontrollano per i conflitti di nome.
    /// </summary>
    public IReadOnlyList<RenamePlanEntry> ApplyMatch(string? rootFolder, IReadOnlyList<RenamePlanEntry> plan, string sourcePath, MatchCandidate chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var updated = plan.Select(e =>
        {
            if (!string.Equals(e.SourcePath, sourcePath, StringComparison.Ordinal))
            {
                return e;
            }
            var candidates = e.Candidates.Any(c => c.Metadata.IsSameEntry(chosen.Metadata)) ? e.Candidates : [chosen, .. e.Candidates];
            return e with
            {
                Parsed = e.Parsed ?? parser.Parse(e.SourcePath),
                Metadata = chosen.Metadata,
                Confidence = chosen.Confidence,
                ManualMatch = true,
                Candidates = candidates,
                // Rerender ricalcola nome e stato: si parte da "Pronto", i conflitti li ritrova lui.
                Status = PlanStatus.Ready,
                Error = null,
            };
        }).ToList();
        return Rerender(rootFolder, updated);
    }

    private RenamePlanEntry BuildEntry(PathBoundary boundary, string path, ParsedMediaName parsed, MediaMetadata metadata, double confidence, RenamrError? lowConfidence)
    {
        // Il database ha l'episodio solo in un'altra lingua (TVmaze: inglese) ma il nome file ne ha già uno:
        // "Silo S03E01 Chi sei tu" resta "Chi sei tu" invece di diventare "Who Are You".
        if (!metadata.EpisodeTitleLocalized && parsed.EpisodeTitle is { } fromFileName)
        {
            metadata = metadata with { EpisodeTitle = fromFileName, EpisodeTitleLocalized = true };
        }

        var template = settings.Current.Templates.For(TemplateSettings.KindFor(metadata.Kind, parsed));
        var relative = templates.Render(template, metadata, parsed, parsed.Extension);

        // Template con cartelle => organizzazione a partire dalla radice; altrimenti rinomina sul posto.
        var baseDir = relative.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? boundary.Root
            : Path.GetDirectoryName(path)!;

        var check = boundary.ResolveTarget(baseDir, relative, out var target);
        if (!check.Succeeded)
        {
            return Fail(path, parsed, check.Error!.Code, check.Error.Detail);
        }

        var unchanged = string.Equals(path, target, StringComparison.Ordinal);
        return new RenamePlanEntry
        {
            SourcePath = path,
            TargetPath = target,
            Parsed = parsed,
            Metadata = metadata,
            Confidence = confidence,
            Status = lowConfidence is not null ? PlanStatus.LowConfidence : unchanged ? PlanStatus.Unchanged : PlanStatus.Ready,
            Error = lowConfidence,
        };
    }

    /// <summary>Due file non possono finire sullo stesso nome; un file esistente non viene mai sovrascritto.</summary>
    internal static IReadOnlyList<RenamePlanEntry> DetectConflicts(IReadOnlyList<RenamePlanEntry> entries)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RenamePlanEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (!e.IsActionable || e.Status == PlanStatus.Unchanged)
            {
                result.Add(e);
                continue;
            }

            var sameFile = string.Equals(e.SourcePath, e.TargetPath, StringComparison.OrdinalIgnoreCase);
            if (!claimed.Add(e.TargetPath!))
            {
                result.Add(e with { Status = PlanStatus.Error, Error = RenamrError.From(RenamrErrorCode.TargetAlreadyExists, Strings.Current.PlanDuplicateTarget) });
            }
            else if (!sameFile && File.Exists(e.TargetPath))
            {
                result.Add(e with { Status = PlanStatus.Error, Error = RenamrError.From(RenamrErrorCode.TargetAlreadyExists, e.TargetPath) });
            }
            else
            {
                result.Add(e);
            }
        }
        return result;
    }

    private static RenamePlanEntry Cancelled(string path, ParsedMediaName? parsed) => new()
    {
        SourcePath = path,
        Parsed = parsed,
        Status = PlanStatus.Skipped,
        Error = RenamrError.From(RenamrErrorCode.Cancelled),
    };

    private static RenamePlanEntry Fail(string path, ParsedMediaName? parsed, RenamrErrorCode code, string? detail = null) => new()
    {
        SourcePath = path,
        Parsed = parsed,
        Status = PlanStatus.Error,
        Error = RenamrError.From(code, detail),
    };
}

/// <summary>Un file da analizzare e la cartella che gli fa da recinto.</summary>
public sealed record PlanSource(string Path, string Root);
