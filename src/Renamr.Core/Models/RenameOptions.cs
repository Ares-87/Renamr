namespace Renamr.Core.Models;

/// <summary>Opzioni di una singola esecuzione (scelte dall'utente nella UI).</summary>
public sealed record RenameRunOptions
{
    /// <summary>Simulazione: calcola tutto, non tocca il disco.</summary>
    public bool DryRun { get; init; }

    /// <summary>Includere anche le voci a bassa confidenza (di default: no).</summary>
    public bool IncludeLowConfidence { get; init; }

    public bool SyncFileSystemDates { get; init; } = true;
    public bool WriteEmbeddedMetadata { get; init; } = true;
}
