using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Models;
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

    public async Task<IReadOnlyList<RenamePlanEntry>> PlanAsync(string rootFolder, IProgress<RenameProgress>? progress, CancellationToken ct)
    {
        var boundary = new PathBoundary(rootFolder);
        var files = scanner.Scan(boundary);
        var entries = new RenamePlanEntry[files.Count];
        var done = 0;
        progress?.Report(new RenameProgress(0, files.Count, null, "Analisi"));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, settings.Current.Matching.MaxParallelLookups), CancellationToken = ct },
            async (i, token) =>
            {
                entries[i] = await PlanOneAsync(boundary, files[i], token).ConfigureAwait(false);
                progress?.Report(new RenameProgress(Interlocked.Increment(ref done), files.Count, entries[i], "Analisi"));
            }).ConfigureAwait(false);

        return DetectConflicts(entries);
    }

    public async Task<RenamePlanEntry> PlanOneAsync(PathBoundary boundary, string path, CancellationToken ct)
    {
        ParsedMediaName? parsed = null;
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
                return Fail(path, parsed, code, string.Join(" · ", match.Trace));
            }

            var metadata = match.Best.Metadata;
            var template = settings.Current.Templates.For(TemplateKind(metadata, parsed));
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
            var low = match.Outcome == MatchOutcome.LowConfidence;
            return new RenamePlanEntry
            {
                SourcePath = path,
                TargetPath = target,
                Parsed = parsed,
                Metadata = metadata,
                Confidence = match.Best.Confidence,
                Status = low ? PlanStatus.LowConfidence : unchanged ? PlanStatus.Unchanged : PlanStatus.Ready,
                Error = low ? RenamrError.From(RenamrErrorCode.LowConfidenceMatch, string.Join(" · ", match.Trace)) : null,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Analisi fallita per {Path}", path);
            return Fail(path, parsed, RenamrErrorCode.Unexpected, ex.Message);
        }
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
                result.Add(e with { Status = PlanStatus.Error, Error = RenamrError.From(RenamrErrorCode.TargetAlreadyExists, "Un altro file del piano ha lo stesso nome di destinazione") });
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

    private static MediaKind TemplateKind(MediaMetadata md, ParsedMediaName parsed) => md.Kind switch
    {
        // Anime con "S01E05" nel nome: template episodi; con numerazione assoluta: template anime.
        MediaKind.Anime when parsed.AbsoluteEpisode is null => MediaKind.Episode,
        var k => k,
    };

    private static RenamePlanEntry Fail(string path, ParsedMediaName? parsed, RenamrErrorCode code, string? detail = null) => new()
    {
        SourcePath = path,
        Parsed = parsed,
        Status = PlanStatus.Error,
        Error = RenamrError.From(code, detail),
    };
}
