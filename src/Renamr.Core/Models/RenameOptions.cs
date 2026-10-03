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

    /// <summary>Campi scritti nei tag quando <see cref="WriteEmbeddedMetadata"/> è acceso.</summary>
    public EmbeddedMetadataFields EmbeddedFields { get; init; } = EmbeddedMetadataFields.All;

    /// <summary>Date del file system impostate quando <see cref="SyncFileSystemDates"/> è acceso.</summary>
    public FileDateChoices FileDates { get; init; } = FileDateChoices.All;
}
