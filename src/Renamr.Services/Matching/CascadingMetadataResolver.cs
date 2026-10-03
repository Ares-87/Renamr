using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renamr.Core.Abstractions;
using Renamr.Core.Models;

namespace Renamr.Services.Matching;

/// <summary>
/// Fallback a cascata tra provider.
/// <list type="number">
/// <item>Provider che supportano il tipo e sono configurati, in ordine di <see cref="IMetadataProvider.Priority"/>.</item>
/// <item>Primo candidato sopra la soglia "alta" = risultato immediato (niente chiamate inutili).</item>
/// <item>Errore di servizio o confidenza bassa = si prova il successivo, tenendo il migliore visto finora.</item>
/// <item>Per gli anime, se i provider anime non bastano, si scende ai provider TV generici.</item>
/// </list>
/// </summary>
public sealed class CascadingMetadataResolver(
    IEnumerable<IMetadataProvider> providers,
    ISettingsStore settings,
    ILogger<CascadingMetadataResolver>? logger = null,
    ProviderHealth? health = null) : IMetadataResolver
{
    private readonly IReadOnlyList<IMetadataProvider> _providers = [.. providers.OrderBy(p => p.Priority)];
    private readonly ILogger _log = logger ?? NullLogger<CascadingMetadataResolver>.Instance;

    public async Task<MatchResult> ResolveAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var matching = settings.Current.Matching;
        var trace = new List<string>();
        MatchCandidate? best = null;
        var alternatives = new List<MatchCandidate>();
        var anySucceeded = false;

        foreach (var provider in ProvidersFor(query.Kind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provider.IsConfigured)
            {
                trace.Add($"{provider.Name}: non configurato");
                continue;
            }

            IReadOnlyList<MatchCandidate> found;
            try
            {
                found = await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                anySucceeded = true;
            }
            catch (ProviderException ex)
            {
                _log.LogWarning("{Provider} non disponibile per '{Title}': {Message}", provider.Name, query.Title, ex.Message);
                trace.Add($"{provider.Name}: {Core.Errors.ErrorMessages.Describe(ex.Code)}");
                health?.Report(provider.Name, ex.Code, ex.Message);
                continue;
            }

            var top = found.MaxBy(c => c.Confidence);
            trace.Add(top is null ? $"{provider.Name}: nessun risultato" : $"{provider.Name}: {top.Metadata.Title} ({top.Confidence:P0})");
            alternatives.AddRange(found);

            if (top is not null && (best is null || top.Confidence > best.Confidence))
            {
                best = top;
            }
            if (best is not null && best.Confidence >= matching.HighConfidenceThreshold)
            {
                break;
            }
        }

        if (best is null || best.Confidence < matching.MinimumConfidence)
        {
            return new MatchResult
            {
                Outcome = anySucceeded ? MatchOutcome.NoMatch : MatchOutcome.ProviderFailure,
                Trace = trace,
            };
        }

        return new MatchResult
        {
            Outcome = best.Confidence >= matching.HighConfidenceThreshold ? MatchOutcome.Matched : MatchOutcome.LowConfidence,
            Best = best,
            Alternatives = [.. alternatives.Where(a => !ReferenceEquals(a, best)).OrderByDescending(a => a.Confidence).Take(5)],
            Trace = trace,
        };
    }

    private IEnumerable<IMetadataProvider> ProvidersFor(MediaKind kind)
    {
        var primary = _providers.Where(p => p.Supports(kind));
        if (kind != MediaKind.Anime)
        {
            return primary;
        }
        // Anime: prima i provider specifici, poi quelli TV generici (che trattano l'anime come serie).
        var tv = _providers.Where(p => !p.Supports(MediaKind.Anime) && p.Supports(MediaKind.Episode));
        return primary.Concat(tv);
    }
}
