using Renamr.Core.Errors;

namespace Renamr.Core.Models;

public enum PlanStatus
{
    Pending,
    Ready,
    LowConfidence,
    Unchanged,
    Error,
    Done,
    Simulated,
    Skipped,
}

/// <summary>
/// Una riga della Diff View: file originale ➔ nome proposto, più i metadata usati per il Deep Date Sync.
/// Immutabile: l'esecuzione produce un nuovo entry con lo stato aggiornato.
/// </summary>
public sealed record RenamePlanEntry
{
    public required string SourcePath { get; init; }
    public string? TargetPath { get; init; }
    public required PlanStatus Status { get; init; }
    public ParsedMediaName? Parsed { get; init; }
    public MediaMetadata? Metadata { get; init; }
    public double Confidence { get; init; }
    public RenamrError? Error { get; init; }

    /// <summary>Modalità "Rinomina file": cambia solo il nome, senza metadati né date (quindi senza <see cref="Metadata"/>).</summary>
    public bool NameOnly { get; init; }

    public string SourceName => Path.GetFileName(SourcePath);
    public string? TargetName => TargetPath is null ? null : Path.GetFileName(TargetPath);

    public bool IsActionable => Status is PlanStatus.Ready or PlanStatus.LowConfidence or PlanStatus.Unchanged
                                && TargetPath is not null && (Metadata is not null || NameOnly);
}
