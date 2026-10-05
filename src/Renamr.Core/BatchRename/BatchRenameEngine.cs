using Renamr.Core.Localization;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Renamr.Core.BatchRename;

public enum BatchSortBy
{
    /// <summary>Ordine naturale: "Foto 2" prima di "Foto 10".</summary>
    Name,
    Modified,
    Created,
    Size,
    Extension,
}

/// <summary>Cosa fare con il nuovo nome.</summary>
public enum BatchAction
{
    /// <summary>Rinomina il file dov'è.</summary>
    Rename,

    /// <summary>Lo sposta nella cartella di destinazione (e nelle sottocartelle del modello) con il nuovo nome.</summary>
    Move,

    /// <summary>Ne crea una copia nella cartella di destinazione; l'originale resta com'è.</summary>
    Copy,
}

/// <summary>Cosa elencare: i file della cartella, oppure le sue cartelle.</summary>
public enum BatchItems
{
    Files,

    /// <summary>Le cartelle dentro quella aperta (solo il primo livello): si rinominano come i file, senza estensione.</summary>
    Folders,
}

/// <summary>Tutto ciò che l'utente sceglie nella modalità "Rinomina file". Viene salvato e riproposto all'avvio.</summary>
public sealed record BatchRenameOptions
{
    public IReadOnlyList<BatchRule> Rules { get; init; } = [];
    public BatchSortBy SortBy { get; init; } = BatchSortBy.Name;
    public bool Descending { get; init; }
    public bool IncludeSubfolders { get; init; }

    /// <summary>Quali file considerare: "*.jpg; *.png", "jpg png", "IMG_*". Vuoto = tutti.</summary>
    public string Filter { get; init; } = string.Empty;

    public BatchItems Items { get; init; } = BatchItems.Files;

    /// <summary>Le cartelle si possono solo rinominare: con <see cref="BatchItems.Folders"/> vale sempre <see cref="BatchAction.Rename"/>.</summary>
    public BatchAction Action { get; init; } = BatchAction.Rename;

    /// <summary>Per sposta e copia: cartella di destinazione (assoluta); vuota = la cartella aperta.</summary>
    public string DestinationFolder { get; init; } = string.Empty;

    /// <summary>Per sposta e copia: sottocartelle da creare nella destinazione, con segnaposto ("{scatto:yyyy}/{scatto:MM}").</summary>
    public string SubfolderPattern { get; init; } = string.Empty;

    /// <summary>Se il nome nuovo è già preso, aggiunge " (2)", " (3)"… invece di segnalare l'errore.</summary>
    public bool NumberDuplicates { get; init; }

    /// <summary>L'azione davvero applicata (le cartelle si possono solo rinominare).</summary>
    public BatchAction EffectiveAction => Items == BatchItems.Folders ? BatchAction.Rename : Action;

    /// <summary>Le regole proposte la prima volta: nome pulito e numerato.</summary>
    public static BatchRenameOptions Default { get; } = new()
    {
        Rules = [new NumberingRule()],
    };
}

/// <summary>
/// Calcola i nuovi nomi della modalità "Rinomina file": ordina i file, applica le regole in sequenza e controlla
/// che il risultato sia un nome valido anche su Windows (i dischi esterni sono spesso NTFS anche sotto Linux).
/// Puro calcolo: non legge né scrive il disco, quindi l'anteprima si aggiorna a ogni tasto.
/// </summary>
public static partial class BatchRenameEngine
{
    private const int MaxNameLength = 255;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Ordina i file come li numererà la regola di numerazione.</summary>
    public static IReadOnlyList<BatchFile> Sort(IEnumerable<BatchFile> files, BatchSortBy sortBy, bool descending)
    {
        Comparer<BatchFile> byKey = sortBy switch
        {
            BatchSortBy.Modified => Comparer<BatchFile>.Create((a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc)),
            BatchSortBy.Created => Comparer<BatchFile>.Create((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc)),
            BatchSortBy.Size => Comparer<BatchFile>.Create((a, b) => a.Size.CompareTo(b.Size)),
            BatchSortBy.Extension => Comparer<BatchFile>.Create((a, b) => NaturalComparer.Instance.Compare(a.Extension, b.Extension)),
            _ => Comparer<BatchFile>.Create((_, _) => 0),
        };
        // A parità di chiave conta il percorso (cartella, poi nome) in ordine naturale: risultato sempre stabile.
        var comparer = Comparer<BatchFile>.Create((a, b) =>
        {
            var c = byKey.Compare(a, b);
            if (c == 0)
            {
                c = NaturalComparer.Instance.Compare(a.Folder, b.Folder);
            }
            if (c == 0)
            {
                c = NaturalComparer.Instance.Compare(a.Name, b.Name);
            }
            return descending ? -c : c;
        });
        return files.Order(comparer).ToList();
    }

    /// <summary>
    /// Applica le regole attive ai file, già ordinati con <see cref="Sort"/>. Con <paramref name="subfolderPattern"/>
    /// calcola anche la sottocartella di destinazione di ogni file (stessi segnaposto delle regole).
    /// </summary>
    public static IReadOnlyList<BatchRenameResult> Apply(IReadOnlyList<BatchFile> sortedFiles, IReadOnlyList<BatchRule> rules, string? subfolderPattern = null)
    {
        ArgumentNullException.ThrowIfNull(sortedFiles);
        ArgumentNullException.ThrowIfNull(rules);

        var active = rules.Where(r => r.Enabled).ToList();
        var folderCounts = sortedFiles.GroupBy(f => f.Folder, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var folderSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var results = new List<BatchRenameResult>(sortedFiles.Count);

        for (var i = 0; i < sortedFiles.Count; i++)
        {
            var file = sortedFiles[i];
            folderSeen.TryGetValue(file.Folder, out var inFolder);
            folderSeen[file.Folder] = inFolder + 1;
            var context = new BatchRuleContext(file, i, sortedFiles.Count, inFolder, folderCounts[file.Folder]);

            var stem = file.Stem;
            var extension = file.Extension;
            string? error = null;
            for (var r = 0; r < active.Count; r++)
            {
                try
                {
                    (stem, extension) = active[r].Apply(stem, extension, context);
                }
                catch (BatchRuleException ex)
                {
                    error = $"Regola {r + 1} ({active[r].Label}): {ex.Message}";
                    break;
                }
            }

            // Spazi all'inizio e alla fine non sono mai voluti (restano da "Aggiungi testo" o dalla numerazione).
            stem = stem.Trim();
            var name = stem + extension;
            error ??= ValidateName(stem, name);
            var subfolder = string.Empty;
            if (error is null && !string.IsNullOrWhiteSpace(subfolderPattern))
            {
                try
                {
                    (subfolder, error) = Subfolder(BatchTokens.Expand(subfolderPattern, stem, context));
                }
                catch (BatchRuleException ex)
                {
                    error = ex.Message;
                }
            }
            results.Add(new BatchRenameResult(file, name, error) { Subfolder = subfolder });
        }
        return results;
    }

    /// <summary>
    /// Sottocartella relativa dal modello già espanso: "/" e "\" separano i livelli, i livelli vuoti si saltano
    /// (un dato mancante non crea "Foto//x"), ogni livello deve essere un nome valido e ".." non è ammesso.
    /// </summary>
    private static (string Path, string? Error) Subfolder(string expanded)
    {
        var parts = new List<string>();
        foreach (var raw in expanded.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }
            if (part is "." or "..")
            {
                return (string.Empty, Strings.Current.Format(nameof(Strings.SubfolderInvalid), part));
            }
            if (ValidateName(part, part) is { } error)
            {
                return (string.Empty, error);
            }
            parts.Add(part);
        }
        return (string.Join(System.IO.Path.DirectorySeparatorChar, parts), null);
    }

    /// <summary>Null se <paramref name="name"/> è un nome di file valido su Windows e Linux; altrimenti il motivo, nella lingua dell'interfaccia.</summary>
    public static string? ValidateName(string stem, string name)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return Strings.Current.NameEmpty;
        }
        var bad = name.FirstOrDefault(c => c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*');
        if (bad != default)
        {
            return bad < 32 ? Strings.Current.NameControlCharacter : Strings.Current.Format(nameof(Strings.NameBadCharacter), bad);
        }
        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return Strings.Current.NameTrailingDot;
        }
        if (ReservedNames.Contains(name.Split('.')[0].TrimEnd()))
        {
            return Strings.Current.NameReserved;
        }
        if (name.Length > MaxNameLength)
        {
            return Strings.Current.Format(nameof(Strings.NameTooLong), name.Length, MaxNameLength);
        }
        return null;
    }

    /// <summary>True se il nome del file rientra nel filtro ("*.jpg; *.png", "jpg png", "IMG_*").</summary>
    public static bool MatchesFilter(string fileName, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }
        foreach (var raw in filter.Split([';', ',', ' ', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "jpg" o ".jpg" = estensione; altrimenti un modello con * e ?.
            var pattern = raw.Contains('*', StringComparison.Ordinal) || raw.Contains('?', StringComparison.Ordinal)
                ? raw
                : "*." + raw.TrimStart('.');
            var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
            if (Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>Confronto "naturale": i numeri dentro il testo si confrontano per valore ("2" &lt; "10"), il resto senza maiuscole.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }
        if (x is null)
        {
            return -1;
        }
        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }
                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.SequenceCompareTo(b);
                if (c != 0)
                {
                    return c;
                }
                continue;
            }

            var si2 = i;
            var sj2 = j;
            while (i < x.Length && !char.IsAsciiDigit(x[i]))
            {
                i++;
            }
            while (j < y.Length && !char.IsAsciiDigit(y[j]))
            {
                j++;
            }
            var text = CultureInfo.CurrentCulture.CompareInfo.Compare(x[si2..i], y[sj2..j], CompareOptions.IgnoreCase);
            if (text != 0)
            {
                return text;
            }
        }
        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
