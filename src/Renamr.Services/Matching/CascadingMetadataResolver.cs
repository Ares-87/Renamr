using Renamr.Core.Localization;
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
                trace.Add(Strings.Current.Format(nameof(Strings.ProviderNotConfigured), provider.Name));
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
            // Nessun risultato abbastanza sicuro, ma quelli approssimativi restano: l'utente può sceglierli dalla riga.
            return new MatchResult
            {
                Outcome = anySucceeded ? MatchOutcome.NoMatch : MatchOutcome.ProviderFailure,
                Alternatives = Distinct(alternatives).Take(MaxCandidates).ToList(),
                Trace = trace,
            };
        }

        return new MatchResult
        {
            Outcome = best.Confidence >= matching.HighConfidenceThreshold ? MatchOutcome.Matched : MatchOutcome.LowConfidence,
            Best = best,
            Alternatives = [.. Distinct(alternatives.Where(a => !ReferenceEquals(a, best))).Take(MaxCandidates)],
            Trace = trace,
        };
    }

    /// <summary>Quanti risultati al massimo si mostrano nella scelta manuale.</summary>
    public const int MaxCandidates = 15;

    /// <summary>Ricerca dell'utente: tutti i database adatti, senza fermarsi al primo risultato sicuro.</summary>
    public async Task<MatchResult> SearchAllAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var trace = new List<string>();
        var found = new List<MatchCandidate>();
        var anySucceeded = false;
        foreach (var provider in ProvidersFor(query.Kind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provider.IsConfigured)
            {
                continue;
            }
            try
            {
                var results = await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                anySucceeded = true;
                found.AddRange(results);
            }
            catch (ProviderException ex)
            {
                _log.LogWarning("{Provider} non disponibile per '{Title}': {Message}", provider.Name, query.Title, ex.Message);
                trace.Add($"{provider.Name}: {Core.Errors.ErrorMessages.Describe(ex.Code)}");
                health?.Report(provider.Name, ex.Code, ex.Message);
            }
        }

        var all = Distinct(found).Take(MaxCandidates).ToList();
        return new MatchResult
        {
            Outcome = all.Count > 0 ? MatchOutcome.Matched : anySucceeded ? MatchOutcome.NoMatch : MatchOutcome.ProviderFailure,
            Best = all.FirstOrDefault(),
            Alternatives = all,
            Trace = trace,
        };
    }

    /// <summary>Dal più probabile; lo stesso titolo dello stesso database compare una volta sola.</summary>
    private static IEnumerable<MatchCandidate> Distinct(IEnumerable<MatchCandidate> candidates) =>
        candidates
            .OrderByDescending(c => c.Confidence)
            .DistinctBy(c => (c.Metadata.Provider, c.Metadata.ProviderId, c.Metadata.Season, c.Metadata.Episode));

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
