using Renamr.Core.Localization;
using Renamr.Core.BatchRename;
using Renamr.Core.Errors;
using Renamr.Core.Models;
using Renamr.Services.IO;

namespace Renamr.Services.BatchRename;

/// <summary>
/// Modalità "Rinomina file", fasi 1 e 2: elenca i file della cartella (di qualunque tipo) e calcola i nuovi nomi
/// con le regole dell'utente. Nessuna ricerca online, nessun metadato: solo il nome. Non modifica nulla sul disco.
/// </summary>
public sealed class BatchRenamePlanner
{
    /// <summary>Tutti i file sotto la radice (o solo quelli della cartella), con i dati che servono a ordinare e ai segnaposto.</summary>
    public IReadOnlyList<BatchFile> Scan(string rootFolder, BatchRenameOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var boundary = new PathBoundary(rootFolder);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = options.IncludeSubfolders,
            IgnoreInaccessible = true,
            // Come per i film: niente file nascosti o di sistema, e nessun link che porti fuori dalla radice.
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        var files = new List<BatchFile>();
        if (options.Items == BatchItems.Folders)
        {
            // Solo il primo livello: rinominare una cartella cambierebbe il percorso di quelle che contiene.
            var folderEnumeration = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            };
            foreach (var info in new DirectoryInfo(boundary.Root).EnumerateDirectories("*", folderEnumeration))
            {
                if (!info.Name.StartsWith('.') && !info.Name.Contains(".renamr-", StringComparison.OrdinalIgnoreCase) && boundary.IsStrictDescendant(info.FullName))
                {
                    files.Add(new BatchFile(info.FullName, 0, info.LastWriteTimeUtc, info.CreationTimeUtc) { IsFolder = true, Details = FileDetails.None });
                }
            }
            return files;
        }

        var details = BatchTokens.NeedsDetails(options.Rules);
        foreach (var info in new DirectoryInfo(boundary.Root).EnumerateFiles("*", enumeration))
        {
            if (info.Name.Contains(".renamr-", StringComparison.OrdinalIgnoreCase) // temporanei nostri
                || info.Name.StartsWith('.') // nascosti su Linux
                || !BatchRenameEngine.MatchesFilter(info.Name, options.Filter)
                || !boundary.IsStrictDescendant(info.FullName))
            {
                continue;
            }
            var file = new BatchFile(info.FullName, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
            files.Add(details ? file with { Details = FileDetailsReader.Read(file.Path) } : file);
        }
        return files;
    }

    /// <summary>
    /// Una regola ora usa {scatto}, {artista}…: legge il contenuto dei file che non l'hanno ancora letto.
    /// Restituisce lo stesso elenco se non serve niente, così l'anteprima resta immediata.
    /// </summary>
    public static IReadOnlyList<BatchFile> WithDetails(IReadOnlyList<BatchFile> files, BatchRenameOptions options)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        if (!BatchTokens.NeedsDetails(options.Rules) || files.All(f => f.Details is not null))
        {
            return files;
        }
        return [.. files.Select(f => f.Details is null ? f with { Details = FileDetailsReader.Read(f.Path) } : f)];
    }

    /// <summary>
    /// Un file aggiunto a mano: nessun filtro (l'ha scelto l'utente), recinto = la cartella aperta se ci sta dentro,
    /// altrimenti la sua cartella. Null se il file non c'è più o non si può leggere.
    /// </summary>
    public static BatchFile? Describe(string path, string root)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new BatchFile(info.FullName, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) { Root = root } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Applica regole e ordine ai file già elencati: si richiama a ogni modifica delle regole, senza rileggere la cartella.</summary>
    public IReadOnlyList<RenamePlanEntry> Plan(string? rootFolder, IReadOnlyList<BatchFile> files, BatchRenameOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var boundaries = new Dictionary<string, PathBoundary?>(StringComparer.Ordinal);
        var sorted = BatchRenameEngine.Sort(files, options.SortBy, options.Descending);
        var action = options.EffectiveAction;
        var relocating = action != BatchAction.Rename;
        var results = BatchRenameEngine.Apply(sorted, options.Rules, relocating ? options.SubfolderPattern : null);
        var destination = options.DestinationFolder?.Trim() ?? string.Empty;

        var entries = results.Select(r =>
        {
            var root = r.File.Root ?? rootFolder;
            if (r.Error is { } reason)
            {
                return Fail(r.File.Path, new RenamrError(RenamrErrorCode.InvalidFileName, reason)) with { Root = r.File.Root };
            }
            if (root is null)
            {
                return Fail(r.File.Path, RenamrError.From(RenamrErrorCode.PathOutsideRoot, r.File.Path));
            }

            // Rinomina: stessa cartella, stesso recinto. Sposta e copia: la destinazione (vuota = la cartella aperta)
            // è il recinto delle destinazioni, la sorgente resta controllata con la sua cartella.
            var targetRoot = relocating && destination.Length > 0 ? destination : root;
            if (Boundary(targetRoot) is not { } boundary)
            {
                return Fail(r.File.Path, new RenamrError(RenamrErrorCode.FileNotFound, Strings.Current.Format(nameof(Strings.DestinationNotFound), targetRoot))) with { Root = r.File.Root };
            }
            var check = relocating
                ? boundary.ResolveTarget(boundary.Root, Path.Combine(r.Subfolder, r.NewName), out var target)
                : boundary.ResolveTarget(r.File.Folder, r.NewName, out target);
            if (!check.Succeeded)
            {
                return Fail(r.File.Path, check.Error!) with { Root = r.File.Root };
            }
            var same = string.Equals(r.File.Path, target, StringComparison.Ordinal);
            return new RenamePlanEntry
            {
                SourcePath = r.File.Path,
                TargetPath = target,
                Root = r.File.Root,
                TargetRoot = relocating && destination.Length > 0 ? boundary.Root : null,
                Copy = action == BatchAction.Copy,
                NameOnly = true,
                // Copiare un file su sé stesso non ha senso: resta "già corretto".
                Status = same ? PlanStatus.Unchanged : PlanStatus.Ready,
            };
        }).ToList();

        if (options.NumberDuplicates)
        {
            NumberDuplicateTargets(entries, sourcesStay: action == BatchAction.Copy);
        }
        return DetectConflicts(entries, sourcesStay: action == BatchAction.Copy);

        PathBoundary? Boundary(string folder)
        {
            if (!boundaries.TryGetValue(folder, out var boundary))
            {
                try
                {
                    boundary = new PathBoundary(folder);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException)
                {
                    boundary = null;
                }
                boundaries[folder] = boundary;
            }
            return boundary;
        }
    }

    /// <summary>
    /// "Aggiungi (2)": un nome già preso (da un altro file dell'elenco o da un file che c'è già) diventa
    /// "Nome (2).ext", "Nome (3).ext"… nell'ordine dell'elenco. Con la copia gli originali restano al loro posto.
    /// </summary>
    internal static void NumberDuplicateTargets(List<RenamePlanEntry> entries, bool sourcesStay)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var moving = sourcesStay
            ? new HashSet<string>(comparer)
            : entries.Where(e => e.Status == PlanStatus.Ready).Select(e => e.SourcePath).ToHashSet(comparer);
        var claimed = entries.Where(e => sourcesStay || e.Status != PlanStatus.Ready).Select(e => e.SourcePath).ToHashSet(comparer);

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Status != PlanStatus.Ready)
            {
                continue;
            }
            var target = e.TargetPath!;
            if (IsFree(target, e.SourcePath))
            {
                claimed.Add(target);
                continue;
            }
            var folder = Path.GetDirectoryName(target)!;
            var isFolder = Directory.Exists(e.SourcePath);
            var stem = isFolder ? Path.GetFileName(target) : Path.GetFileNameWithoutExtension(target);
            var extension = isFolder ? string.Empty : Path.GetExtension(target);
            for (var n = 2; n < 10_000; n++)
            {
                var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
                if (IsFree(candidate, e.SourcePath))
                {
                    claimed.Add(candidate);
                    entries[i] = e with { TargetPath = candidate, Status = comparer.Equals(candidate, e.SourcePath) ? PlanStatus.Unchanged : PlanStatus.Ready };
                    break;
                }
            }
        }

        bool IsFree(string path, string source) =>
            !claimed.Contains(path)
            && (comparer.Equals(path, source) || moving.Contains(path) || !(File.Exists(path) || Directory.Exists(path)));
    }

    /// <summary>
    /// Due file non possono avere lo stesso nome finale, e un file che resta com'è (invariato o in errore) occupa il suo nome.
    /// Un file che verrà rinominato invece libera il suo: "1 ➔ 2, 2 ➔ 3" è lecito, l'esecuzione ne gestisce l'ordine.
    /// I confronti ignorano maiuscole e minuscole perché i dischi Windows non le distinguono.
    /// </summary>
    internal static IReadOnlyList<RenamePlanEntry> DetectConflicts(List<RenamePlanEntry> entries, bool sourcesStay = false)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        bool changed;
        do
        {
            changed = false;
            // Con la copia ogni originale resta dov'è e occupa il suo nome.
            var staying = entries.Where(e => sourcesStay || e.Status != PlanStatus.Ready).Select(e => e.SourcePath).ToHashSet(comparer);
            var moving = sourcesStay
                ? new HashSet<string>(comparer)
                : entries.Where(e => e.Status == PlanStatus.Ready).Select(e => e.SourcePath).ToHashSet(comparer);
            var claimed = new HashSet<string>(staying, comparer);

            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Status != PlanStatus.Ready)
                {
                    continue;
                }
                var target = e.TargetPath!;
                var self = comparer.Equals(e.SourcePath, target);
                RenamrError? conflict = null;
                if (!claimed.Add(target) && !self)
                {
                    conflict = new RenamrError(RenamrErrorCode.TargetAlreadyExists, Strings.Current.BatchDuplicateTarget);
                }
                else if (!self && !moving.Contains(target) && (File.Exists(target) || Directory.Exists(target)))
                {
                    conflict = RenamrError.From(RenamrErrorCode.TargetAlreadyExists, target);
                }

                if (conflict is not null)
                {
                    entries[i] = e with { Status = PlanStatus.Error, Error = conflict };
                    changed = true; // ora questo file resta dov'è: può bloccare altri, si ricontrolla
                }
            }
        }
        while (changed);
        return entries;
    }

    private static RenamePlanEntry Fail(string path, RenamrError error) => new()
    {
        SourcePath = path,
        NameOnly = true,
        Status = PlanStatus.Error,
        Error = error,
    };
}
