using Renamr.Core.Models;

namespace Renamr.Core.Abstractions;

/// <summary>
/// Punto di estensione per i database online. Aggiungere un provider = implementare questa interfaccia
/// e registrarla nel container DI: il resolver a cascata la ordina per <see cref="Priority"/>.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>Nome breve mostrato nella UI e nel log (es. "TMDb").</summary>
    string Name { get; }

    /// <summary>Più basso = interrogato prima.</summary>
    int Priority { get; }

    bool Supports(MediaKind kind);

    /// <summary>False se mancano chiavi API: il provider viene saltato senza errore.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Cerca candidati. Deve lanciare <see cref="ProviderException"/> per errori di servizio
    /// (così il resolver passa al provider successivo) e restituire lista vuota se non trova nulla.
    /// </summary>
    Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken);
}

public sealed class ProviderException(string provider, Errors.RenamrErrorCode code, string message, Exception? inner = null)
    : Exception($"{provider}: {message}", inner)
{
    public string Provider { get; } = provider;
    public Errors.RenamrErrorCode Code { get; } = code;
}
