using Renamr.Core.Localization;

namespace Renamr.Core.Errors;

/// <summary>Errore leggibile dall'utente, con dettaglio tecnico opzionale per il log.</summary>
public sealed record RenamrError(RenamrErrorCode Code, string Message, string? Detail = null)
{
    /// <summary>Gli avvisi non impediscono la ridenominazione (es. data non scrivibile nei tag).</summary>
    public bool IsWarning => Code is RenamrErrorCode.LowConfidenceMatch
        or RenamrErrorCode.MetadataFormatUnsupported
        or RenamrErrorCode.DateSyncFailed
        or RenamrErrorCode.MetadataWriteFailed
        or RenamrErrorCode.MetadataSkippedLargeFile;

    public static RenamrError From(RenamrErrorCode code, string? detail = null) =>
        new(code, ErrorMessages.Describe(code), detail);

    public override string ToString() => $"[{(int)Code}] {Message}{(Detail is null ? "" : $" — {Detail}")}";
}

public static class ErrorMessages
{
    /// <summary>Messaggio dell'errore nella lingua dell'interfaccia (chiavi "Error" + nome del codice).</summary>
    public static string Describe(RenamrErrorCode code) =>
        Enum.IsDefined(code) && code != RenamrErrorCode.None
            ? Strings.Current["Error" + code]
            : Strings.Current.ErrorUnexpected;
}
