using System.Collections.Concurrent;
using Renamr.Core.Errors;

namespace Renamr.Services.Matching;

/// <summary>
/// Problemi dei database durante un'analisi (chiave rifiutata, servizio giù). Il resolver passa in silenzio
/// al provider successivo, quindi senza questo registro l'utente vedrebbe solo titoli "sbagliati" senza sapere perché.
/// </summary>
public sealed class ProviderHealth
{
    private readonly ConcurrentDictionary<string, RenamrError> _failures = new(StringComparer.OrdinalIgnoreCase);

    public void Reset() => _failures.Clear();

    public void Report(string provider, RenamrErrorCode code, string? detail) =>
        _failures.TryAdd(provider, RenamrError.From(code, detail));

    public IReadOnlyDictionary<string, RenamrError> Failures => _failures;
}
