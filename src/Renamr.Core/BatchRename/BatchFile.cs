namespace Renamr.Core.BatchRename;

/// <summary>Un file qualsiasi della modalità "Rinomina file": solo ciò che serve alle regole, nessun metadato multimediale.</summary>
public sealed record BatchFile(string Path, long Size, DateTime ModifiedUtc, DateTime CreatedUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Cartella-recinto del file: vuota = la cartella aperta (vedi RenamePlanEntry.Root).</summary>
    public string? Root { get; init; }

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
