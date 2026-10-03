using Renamr.Core.Errors;

namespace Renamr.Services.IO;

/// <summary>
/// "Sandbox" della cartella radice scelta dall'utente.
/// <para>
/// Ogni percorso viene normalizzato con <see cref="Path.GetFullPath(string)"/> (che risolve "..", ".", separatori
/// misti e, su Windows, punti/spazi finali dei segmenti) e poi confrontato con la radice <b>terminata dal separatore</b>:
/// così <c>D:\Media</c> non "contiene" <c>D:\MediaPrivati</c>. Il chiamante deve sempre usare il percorso
/// <i>restituito</i> dalla validazione, mai quello originale: ciò che è stato verificato è ciò che viene usato.
/// </para>
/// <para>
/// Inoltre ogni cartella intermedia esistente viene controllata: una junction o un symlink dentro la radice
/// potrebbe puntare fuori (es. <c>D:\Media\link -> C:\Windows</c>) pur superando il confronto testuale.
/// </para>
/// </summary>
public sealed class PathBoundary
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Radice normalizzata, sempre terminata dal separatore.</summary>
    public string Root { get; }

    public PathBoundary(string rootFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolder);

        if (!Path.IsPathFullyQualified(rootFolder))
        {
            throw new ArgumentException("La cartella radice deve essere un percorso assoluto.", nameof(rootFolder));
        }
        if (IsDevicePath(rootFolder))
        {
            throw new ArgumentException("I percorsi di dispositivo (\\\\.\\ o \\\\?\\GLOBALROOT) non sono ammessi.", nameof(rootFolder));
        }

        var full = Path.GetFullPath(rootFolder);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Cartella non trovata: {full}");
        }

        // Sempre un solo separatore finale. Attenzione alla radice di un volume: TrimEndingDirectorySeparator
        // lascia "I:\" com'è, quindi aggiungere il separatore produceva "I:\\" e nessun file risultava dentro la radice.
        Root = Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }

    /// <summary>True se <paramref name="candidate"/> è <b>strettamente</b> discendente della radice (la radice stessa no).</summary>
    public bool IsStrictDescendant(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || IsDevicePath(candidate))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(candidate, Root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return full.Length > Root.Length && full.StartsWith(Root, PathComparison);
    }

    /// <summary>
    /// Valida un percorso (sorgente o destinazione) e restituisce la sua forma normalizzata.
    /// </summary>
    public OperationResult Validate(string candidate, out string normalized)
    {
        normalized = string.Empty;

        if (!IsStrictDescendant(candidate))
        {
            return OperationResult.Fail(RenamrErrorCode.PathOutsideRoot, candidate);
        }

        normalized = Path.GetFullPath(candidate, Root);

        // Alternate Data Stream ("film.mkv:stream") o due punti fuori dalla lettera di unità.
        var afterDrive = OperatingSystem.IsWindows() && normalized.Length > 2 && normalized[1] == ':' ? normalized[2..] : normalized;
        if (OperatingSystem.IsWindows() && afterDrive.Contains(':', StringComparison.Ordinal))
        {
            return OperationResult.Fail(RenamrErrorCode.InvalidFileName, normalized);
        }

        // Nessun reparse point (symlink/junction) tra la radice e il file.
        var relative = normalized[Root.Length..];
        var current = Root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
            {
                break; // il resto del percorso verrà creato da noi: non può essere un link
            }
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            {
                return OperationResult.Fail(RenamrErrorCode.ReparsePointRejected, current);
            }
        }

        return OperationResult.Ok();
    }

    /// <summary>Combina una cartella con un percorso relativo generato dal template e lo valida.</summary>
    public OperationResult ResolveTarget(string baseDirectory, string relativePath, out string normalized)
    {
        normalized = string.Empty;
        if (Path.IsPathRooted(relativePath))
        {
            return OperationResult.Fail(RenamrErrorCode.PathOutsideRoot, relativePath);
        }
        return Validate(Path.Combine(baseDirectory, relativePath), out normalized);
    }

    private static bool IsDevicePath(string path) =>
        path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("//./", StringComparison.Ordinal);
}
