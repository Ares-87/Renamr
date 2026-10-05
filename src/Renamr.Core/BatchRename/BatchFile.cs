namespace Renamr.Core.BatchRename;

/// <summary>Un file qualsiasi della modalità "Rinomina file": solo ciò che serve alle regole, nessun metadato multimediale.</summary>
public sealed record BatchFile(string Path, long Size, DateTime ModifiedUtc, DateTime CreatedUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Cartella-recinto del file: vuota = la cartella aperta (vedi RenamePlanEntry.Root).</summary>
    public string? Root { get; init; }

    /// <summary>Dati letti dal contenuto (foto, musica, video); null = non ancora letti, perché nessuna regola li usa.</summary>
    public FileDetails? Details { get; init; }

    /// <summary>Nome senza estensione ("Foto 01" di "Foto 01.JPG").</summary>
    public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Estensione con il punto (".JPG"), vuota se manca.</summary>
    public string Extension => System.IO.Path.GetExtension(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>Nome della cartella che contiene il file (per il segnaposto {cartella}).</summary>
    public string FolderName => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Folder));
}

/// <summary>Esito delle regole su un file: il nuovo nome, oppure il motivo per cui non è valido.</summary>
public sealed record BatchRenameResult(BatchFile File, string NewName, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Dati letti (mai scritti) dal contenuto del file per i segnaposto {scatto}, {artista}, {durata}…
/// Tutti facoltativi: un documento qualsiasi li ha tutti vuoti (<see cref="None"/>).
/// </summary>
public sealed record FileDetails
{
    public static FileDetails None { get; } = new();

    /// <summary>Data e ora di scatto delle foto (EXIF), ora locale della fotocamera.</summary>
    public DateTime? DateTaken { get; init; }

    public string? Camera { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Title { get; init; }
    public uint? Track { get; init; }
    public uint? Year { get; init; }
}
