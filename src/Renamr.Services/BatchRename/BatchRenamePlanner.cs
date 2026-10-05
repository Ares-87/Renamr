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
        foreach (var info in new DirectoryInfo(boundary.Root).EnumerateFiles("*", enumeration))
        {
            if (info.Name.Contains(".renamr-", StringComparison.OrdinalIgnoreCase) // temporanei nostri
                || info.Name.StartsWith('.') // nascosti su Linux
                || !BatchRenameEngine.MatchesFilter(info.Name, options.Filter)
                || !boundary.IsStrictDescendant(info.FullName))
            {
                continue;
            }
            files.Add(new BatchFile(info.FullName, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc));
        }
        return files;
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
        var boundaries = new Dictionary<string, PathBoundary>(StringComparer.Ordinal);
        var sorted = BatchRenameEngine.Sort(files, options.SortBy, options.Descending);
        var results = BatchRenameEngine.Apply(sorted, options.Rules);

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
            if (!boundaries.TryGetValue(root, out var boundary))
            {
                boundaries[root] = boundary = new PathBoundary(root);
            }
            var check = boundary.ResolveTarget(r.File.Folder, r.NewName, out var target);
            if (!check.Succeeded)
            {
                return Fail(r.File.Path, check.Error!) with { Root = r.File.Root };
            }
            return new RenamePlanEntry
            {
                SourcePath = r.File.Path,
                TargetPath = target,
                Root = r.File.Root,
                NameOnly = true,
                Status = string.Equals(r.File.Path, target, StringComparison.Ordinal) ? PlanStatus.Unchanged : PlanStatus.Ready,
            };
        }).ToList();

        return DetectConflicts(entries);
    }

    /// <summary>
    /// Due file non possono avere lo stesso nome finale, e un file che resta com'è (invariato o in errore) occupa il suo nome.
    /// Un file che verrà rinominato invece libera il suo: "1 ➔ 2, 2 ➔ 3" è lecito, l'esecuzione ne gestisce l'ordine.
    /// I confronti ignorano maiuscole e minuscole perché i dischi Windows non le distinguono.
    /// </summary>
    internal static IReadOnlyList<RenamePlanEntry> DetectConflicts(List<RenamePlanEntry> entries)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        bool changed;
        do
        {
            changed = false;
            var staying = entries.Where(e => e.Status != PlanStatus.Ready).Select(e => e.SourcePath).ToHashSet(comparer);
            var moving = entries.Where(e => e.Status == PlanStatus.Ready).Select(e => e.SourcePath).ToHashSet(comparer);
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
