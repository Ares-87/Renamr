namespace Renamr.Core.Errors;

/// <summary>
/// Result type per le operazioni su file: niente eccezioni che attraversano la coda di lavoro.
/// </summary>
public readonly record struct OperationResult
{
    public RenamrError? Error { get; }
    public IReadOnlyList<RenamrError> Warnings { get; }
    public bool Succeeded => Error is null;

    private OperationResult(RenamrError? error, IReadOnlyList<RenamrError>? warnings)
    {
        Error = error;
        Warnings = warnings ?? [];
    }

    public static OperationResult Ok(IReadOnlyList<RenamrError>? warnings = null) => new(null, warnings);
    public static OperationResult Fail(RenamrError error) => new(error, null);
    public static OperationResult Fail(RenamrErrorCode code, string? detail = null) => new(RenamrError.From(code, detail), null);

    public static implicit operator OperationResult(RenamrError error) => Fail(error);
}
