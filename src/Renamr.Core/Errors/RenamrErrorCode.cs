namespace Renamr.Core.Errors;

/// <summary>Codici di errore stabili: usati nel log, nel pannello Errori/Avvisi e nei test.</summary>
public enum RenamrErrorCode
{
    None = 0,

    // I/O e permessi
    FileInUse = 100,
    AccessDenied = 101,
    ReadOnlyNotCleared = 102,
    FileNotFound = 103,
    TargetAlreadyExists = 104,
    PathTooLong = 105,
    DiskFull = 106,
    PathOutsideRoot = 107,
    ReparsePointRejected = 108,
    InvalidFileName = 109,
    IoFailure = 199,

    // Matching / provider
    NoDatabaseMatch = 200,
    LowConfidenceMatch = 201,
    ProviderUnavailable = 202,
    ProviderAuthFailed = 203,
    ProviderRateLimited = 204,
    UnrecognizedFileName = 205,

    // Metadata embedded
    MetadataFormatUnsupported = 300,
    MetadataWriteFailed = 301,
    DateSyncFailed = 302,
    MetadataSkippedLargeFile = 303,

    Cancelled = 900,
    Unexpected = 999,
}
