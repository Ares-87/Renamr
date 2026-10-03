using Renamr.Core.Errors;

namespace Renamr.Services.IO;

/// <summary>Traduce le eccezioni di I/O (e gli HRESULT Win32) in codici leggibili.</summary>
public static class IoErrorClassifier
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorFileExists = 80;
    private const int ErrorDiskFull = 112;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorFilenameExcedRange = 206;

    public static RenamrError Classify(Exception ex, string? path = null)
    {
        var detail = path is null ? ex.Message : $"{path}: {ex.Message}";
        var code = ex switch
        {
            OperationCanceledException => RenamrErrorCode.Cancelled,
            UnauthorizedAccessException => RenamrErrorCode.AccessDenied,
            PathTooLongException => RenamrErrorCode.PathTooLong,
            FileNotFoundException or DirectoryNotFoundException => RenamrErrorCode.FileNotFound,
            IOException io => (io.HResult & 0xFFFF) switch
            {
                ErrorSharingViolation or ErrorLockViolation => RenamrErrorCode.FileInUse,
                ErrorDiskFull or ErrorHandleDiskFull => RenamrErrorCode.DiskFull,
                ErrorFileExists or ErrorAlreadyExists => RenamrErrorCode.TargetAlreadyExists,
                ErrorFileNotFound or ErrorPathNotFound => RenamrErrorCode.FileNotFound,
                ErrorAccessDenied => RenamrErrorCode.AccessDenied,
                ErrorFilenameExcedRange => RenamrErrorCode.PathTooLong,
                _ => RenamrErrorCode.IoFailure,
            },
            _ => RenamrErrorCode.Unexpected,
        };
        return RenamrError.From(code, detail);
    }

    /// <summary>Errori che tipicamente spariscono in pochi istanti (player che rilascia l'handle, antivirus).</summary>
    public static bool IsTransient(Exception ex) =>
        ex is IOException io && (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;
}
