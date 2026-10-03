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
    public static string Describe(RenamrErrorCode code) => code switch
    {
        RenamrErrorCode.FileInUse => "File in uso da un altro processo",
        RenamrErrorCode.AccessDenied => "Accesso negato",
        RenamrErrorCode.ReadOnlyNotCleared => "Impossibile rimuovere l'attributo Sola lettura",
        RenamrErrorCode.FileNotFound => "File non trovato (spostato o eliminato)",
        RenamrErrorCode.TargetAlreadyExists => "Esiste già un file con il nuovo nome",
        RenamrErrorCode.PathTooLong => "Percorso troppo lungo",
        RenamrErrorCode.DiskFull => "Spazio su disco insufficiente",
        RenamrErrorCode.PathOutsideRoot => "Destinazione fuori dalla cartella selezionata",
        RenamrErrorCode.ReparsePointRejected => "Collegamento simbolico o junction non consentito",
        RenamrErrorCode.InvalidFileName => "Il nome proposto non è valido per Windows",
        RenamrErrorCode.IoFailure => "Errore di lettura/scrittura",
        RenamrErrorCode.NoDatabaseMatch => "Nessun riscontro nei database",
        RenamrErrorCode.LowConfidenceMatch => "Riscontro a bassa confidenza, verificare",
        RenamrErrorCode.ProviderUnavailable => "Servizio online non raggiungibile",
        RenamrErrorCode.ProviderAuthFailed => "Chiave API mancante o non valida",
        RenamrErrorCode.ProviderRateLimited => "Troppe richieste al servizio online, riprovare più tardi",
        RenamrErrorCode.UnrecognizedFileName => "Nome file non riconosciuto",
        RenamrErrorCode.MetadataFormatUnsupported => "Formato che non supporta la data nei metadati",
        RenamrErrorCode.MetadataWriteFailed => "Scrittura dei metadati interni non riuscita",
        RenamrErrorCode.DateSyncFailed => "Aggiornamento delle date del file non riuscito",
        RenamrErrorCode.MetadataSkippedLargeFile => "Metadati interni non scritti: file oltre 4 GB",
        RenamrErrorCode.Cancelled => "Operazione annullata",
        _ => "Errore imprevisto",
    };
}
