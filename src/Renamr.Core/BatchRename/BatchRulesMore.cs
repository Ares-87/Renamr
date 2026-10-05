using System.Globalization;
using System.Text;

namespace Renamr.Core.BatchRename;

// Regole aggiunte nella v1.15: spostare, scambiare, rinumerare, rifilare, nomi da elenco e sostituzioni multiple.
// Come le altre lavorano sul nome senza estensione e non toccano il contenuto dei file.

// ---- Sposta testo -----------------------------------------------------------------------------------------

/// <summary>Cosa spostare: un tratto per posizione, oppure un testo (anche espressione regolare).</summary>
public enum MoveSource
{
    /// <summary>N caratteri a partire da una posizione (1 = primo carattere).</summary>
    Characters,

    /// <summary>La prima volta che compare un testo.</summary>
    Text,
}

/// <summary>Dove portarlo.</summary>
public enum MoveTarget
{
    Start,
    End,

    /// <summary>Dopo N caratteri del nome rimasto.</summary>
    AfterCharacters,
}

/// <summary>
/// Sposta una parte del nome: "2024 Mare Sardegna" con "2024" alla fine ➔ "Mare Sardegna 2024".
/// Il separatore va tra la parte spostata e il resto; gli spazi doppi che restano si tolgono.
/// </summary>
public sealed record MoveTextRule : BatchRule
{
    public MoveSource Source { get; init; } = MoveSource.Text;

    /// <summary>Per <see cref="MoveSource.Characters"/>: posizione del primo carattere, da 1.</summary>
    public int From { get; init; } = 1;

    /// <summary>Per <see cref="MoveSource.Characters"/>: quanti caratteri.</summary>
    public int Count { get; init; } = 1;

    /// <summary>Per <see cref="MoveSource.Text"/>: il testo da spostare.</summary>
    public string Find { get; init; } = string.Empty;

    public bool MatchCase { get; init; }
    public bool UseRegex { get; init; }

    public MoveTarget Target { get; init; } = MoveTarget.End;

    /// <summary>Per <see cref="MoveTarget.AfterCharacters"/>.</summary>
    public int TargetIndex { get; init; }

    public string Separator { get; init; } = " ";

    public override string Label => "Sposta testo";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        if (Cut(stem) is not { } cut)
        {
            return (stem, extension);
        }
        var piece = cut.Piece.Trim();
        var rest = BatchText.CollapseSpaces(cut.Remainder);
        if (piece.Length == 0)
        {
            return (stem, extension);
        }
        var separator = Separator ?? string.Empty;
        var moved = Target switch
        {
            MoveTarget.Start => rest.Length == 0 ? piece : piece + separator + rest,
            MoveTarget.End => rest.Length == 0 ? piece : rest + separator + piece,
            _ => InsertAt(rest, piece, separator),
        };
        return (separator.Trim().Length == 0 ? BatchText.CollapseSpaces(moved) : moved, extension);
    }

    private string InsertAt(string rest, string piece, string separator)
    {
        var at = Math.Clamp(TargetIndex, 0, rest.Length);
        var before = rest[..at];
        var after = rest[at..];
        return before + (before.Length > 0 && !before.EndsWith(' ') ? separator : string.Empty) + piece + (after.Length > 0 ? separator : string.Empty) + after;
    }

    private (string Piece, string Remainder)? Cut(string stem)
    {
        if (Source == MoveSource.Characters)
        {
            var start = Math.Clamp(From - 1, 0, stem.Length);
            var count = Math.Clamp(Count, 0, stem.Length - start);
            return count == 0 ? null : (stem.Substring(start, count), stem.Remove(start, count));
        }
        if (string.IsNullOrEmpty(Find))
        {
            return null;
        }
        if (UseRegex)
        {
            var match = BatchText.CreateRegex(Find, MatchCase).Match(stem);
            return match.Success && match.Length > 0 ? (match.Value, stem.Remove(match.Index, match.Length)) : null;
        }
        var at = stem.IndexOf(Find, MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : (stem.Substring(at, Find.Length), stem.Remove(at, Find.Length));
    }

    public string? Validate() => Source == MoveSource.Text && UseRegex ? BatchText.ValidateRegex(Find) : null;
}

// ---- Scambia parti ----------------------------------------------------------------------------------------

/// <summary>Scambia le due parti ai lati di un separatore: "Artista - Titolo" ➔ "Titolo - Artista".</summary>
public sealed record SwapPartsRule : BatchRule
{
    public string Separator { get; init; } = " - ";

    /// <summary>Quale volta del separatore usare se compare più volte: 1 = la prima.</summary>
    public int Occurrence { get; init; } = 1;

    /// <summary>Conta le volte dalla fine del nome (1 = l'ultima).</summary>
    public bool FromEnd { get; init; }

    public override string Label => "Scambia parti";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        if (string.IsNullOrEmpty(Separator))
        {
            return (stem, extension);
        }
        var positions = new List<int>();
        for (var at = stem.IndexOf(Separator, StringComparison.OrdinalIgnoreCase); at >= 0; at = stem.IndexOf(Separator, at + Separator.Length, StringComparison.OrdinalIgnoreCase))
        {
            positions.Add(at);
        }
        var n = Math.Max(1, Occurrence);
        if (positions.Count < n)
        {
            return (stem, extension);
        }
        var split = FromEnd ? positions[^n] : positions[n - 1];
        var left = stem[..split].Trim();
        var right = stem[(split + Separator.Length)..].Trim();
        // Il separatore originale (con le sue maiuscole) resta in mezzo.
        return (string.Concat(right, stem.AsSpan(split, Separator.Length), left), extension);
    }
}

// ---- Rinumera ---------------------------------------------------------------------------------------------

/// <summary>Come cambiare il numero trovato nel nome.</summary>
public enum RenumberMode
{
    /// <summary>Nuova sequenza: inizio + passo × posizione nell'elenco.</summary>
    Sequence,

    /// <summary>Somma (o toglie, se negativo) un valore al numero che c'è.</summary>
    Add,
}

/// <summary>
/// Cambia un numero già presente nel nome: "Episodio 07 parte 2" con il primo numero +10 ➔ "Episodio 17 parte 2".
/// I file senza quel numero restano com'erano.
/// </summary>
public sealed record RenumberRule : BatchRule
{
    /// <summary>Quale numero del nome: 1 = il primo.</summary>
    public int Which { get; init; } = 1;

    /// <summary>Conta i numeri dalla fine del nome (1 = l'ultimo).</summary>
    public bool FromEnd { get; init; }

    public RenumberMode Mode { get; init; } = RenumberMode.Sequence;

    /// <summary>Per <see cref="RenumberMode.Sequence"/>.</summary>
    public int Start { get; init; } = 1;

    /// <summary>Per <see cref="RenumberMode.Sequence"/>.</summary>
    public int Step { get; init; } = 1;

    /// <summary>Per <see cref="RenumberMode.Add"/>.</summary>
    public int Add { get; init; } = 1;

    /// <summary>Cifre con zeri iniziali; 0 = quante ne aveva il numero ("007" resta di tre cifre).</summary>
    public int Digits { get; init; }

    public override string Label => "Rinumera";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        var numbers = BatchText.Numbers().Matches(stem);
        var n = Math.Max(1, Which);
        if (numbers.Count < n)
        {
            return (stem, extension);
        }
        var match = FromEnd ? numbers[^n] : numbers[n - 1];
        if (!long.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var current))
        {
            return (stem, extension);
        }
        var value = Mode == RenumberMode.Add ? current + Add : Start + (long)Step * context.Index;
        var digits = Digits > 0 ? Digits : match.Length;
        var text = value < 0
            ? "-" + (-value).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')
            : value.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        return (string.Concat(stem.AsSpan(0, match.Index), text, stem.AsSpan(match.Index + match.Length)), extension);
    }
}

// ---- Rifila -----------------------------------------------------------------------------------------------

public enum TrimWhere
{
    Both,
    Start,
    End,
}

/// <summary>Toglie all'inizio e/o alla fine i caratteri scelti, finché ne trova: "__-Foto-__" ➔ "Foto".</summary>
public sealed record TrimRule : BatchRule
{
    /// <summary>I caratteri da togliere, scritti di seguito; maiuscole e minuscole indifferenti.</summary>
    public string Characters { get; init; } = " .-_";

    public TrimWhere Where { get; init; } = TrimWhere.Both;

    public override string Label => "Rifila";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        if (string.IsNullOrEmpty(Characters))
        {
            return (stem, extension);
        }
        var set = (Characters.ToLowerInvariant() + Characters.ToUpperInvariant()).ToCharArray();
        return Where switch
        {
            TrimWhere.Start => (stem.TrimStart(set), extension),
            TrimWhere.End => (stem.TrimEnd(set), extension),
            _ => (stem.Trim(set), extension),
        };
    }
}

// ---- Nomi da elenco ---------------------------------------------------------------------------------------

/// <summary>
/// Un nome per riga, nell'ordine dei file: la riga 1 va al primo file, la 2 al secondo… Le righe possono contenere
/// segnaposto ({n}, {data}…). Un file senza riga (o con la riga vuota) tiene il nome che ha.
/// </summary>
public sealed record NameListRule : BatchRule
{
    public string Names { get; init; } = string.Empty;

    public override string Label => "Nomi da elenco";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        var lines = Lines(Names);
        if (context.Index >= lines.Count || string.IsNullOrWhiteSpace(lines[context.Index]))
        {
            return (stem, extension);
        }
        return (BatchTokens.Expand(lines[context.Index].Trim(), stem, context), extension);
    }

    public static IReadOnlyList<string> Lines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    /// <summary>Il testo per "Riempi con i nomi attuali": un nome per riga.</summary>
    public static string FromNames(IEnumerable<string> names)
    {
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }
            sb.Append(name);
        }
        return sb.ToString();
    }
}

// ---- Sostituzioni multiple --------------------------------------------------------------------------------

/// <summary>Una coppia "cerca ➔ sostituisci con".</summary>
public sealed record ReplacePair(string Find, string Replacement);

/// <summary>Più sostituzioni in una regola sola, applicate dall'alto in basso: "è" ➔ "e", "&amp;" ➔ "e", "  " ➔ " "…</summary>
public sealed record ReplaceListRule : BatchRule
{
    public IReadOnlyList<ReplacePair> Pairs { get; init; } = [];
    public bool MatchCase { get; init; }
    public bool UseRegex { get; init; }

    public override string Label => "Sostituzioni multiple";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        var result = stem;
        foreach (var pair in Pairs)
        {
            result = BatchText.Replace(result, pair.Find, pair.Replacement, MatchCase, UseRegex, ReplaceOccurrence.All);
        }
        return (result, extension);
    }

    /// <summary>Il primo errore di espressione regolare tra le coppie; null se vanno tutte bene.</summary>
    public string? Validate() => UseRegex ? Pairs.Select(p => BatchText.ValidateRegex(p.Find)).FirstOrDefault(e => e is not null) : null;
}
