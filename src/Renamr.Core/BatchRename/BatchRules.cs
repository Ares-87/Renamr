using Renamr.Core.Localization;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Renamr.Core.BatchRename;

/// <summary>Dove si trova un file nell'elenco ordinato: serve alla numerazione e al segnaposto {n}.</summary>
public sealed record BatchRuleContext(BatchFile File, int Index, int Count, int FolderIndex, int FolderCount);

/// <summary>Una regola non applicabile (es. espressione regolare non valida): il messaggio finisce sulla riga.</summary>
public sealed class BatchRuleException(string message) : Exception(message);

/// <summary>
/// Una regola della modalità "Rinomina file". Le regole si applicano in ordine, ognuna al risultato della precedente,
/// e lavorano sul nome senza estensione; solo <see cref="ExtensionRule"/> tocca l'estensione.
/// Sono record immutabili e serializzabili: l'elenco resta salvato per la volta successiva.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "regola")]
[JsonDerivedType(typeof(NewNameRule), "nuovoNome")]
[JsonDerivedType(typeof(NumberingRule), "numerazione")]
[JsonDerivedType(typeof(ReplaceTextRule), "sostituisci")]
[JsonDerivedType(typeof(InsertTextRule), "inserisci")]
[JsonDerivedType(typeof(RemoveCharactersRule), "rimuovi")]
[JsonDerivedType(typeof(ChangeCaseRule), "maiuscole")]
[JsonDerivedType(typeof(CleanupRule), "pulisci")]
[JsonDerivedType(typeof(LettersToDigitsRule), "lettereInNumeri")]
[JsonDerivedType(typeof(ExtensionRule), "estensione")]
public abstract record BatchRule
{
    /// <summary>Una regola spenta resta nell'elenco ma non cambia nulla.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Nome breve mostrato nell'interfaccia e nei messaggi di errore.</summary>
    [JsonIgnore]
    public abstract string Label { get; }

    /// <summary>Applica la regola: <paramref name="stem"/> è il nome corrente senza estensione, <paramref name="extension"/> include il punto.</summary>
    public abstract (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context);
}

// ---- Nuovo nome da modello --------------------------------------------------------------------------------

/// <summary>
/// Sostituisce il nome con un modello: <c>Vacanze {n:000}</c>, <c>{data:yyyy-MM-dd} {nome}</c>, <c>{cartella} - {n}</c>.
/// I segnaposto sono descritti in <see cref="BatchTokens"/>.
/// </summary>
public sealed record NewNameRule : BatchRule
{
    public string Pattern { get; init; } = "{nome}";

    public override string Label => "Nuovo nome";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context) =>
        (BatchTokens.Expand(Pattern, stem, context), extension);
}

// ---- Numerazione progressiva ------------------------------------------------------------------------------

public enum NumberPosition
{
    /// <summary>"001 Foto".</summary>
    Start,

    /// <summary>"Foto 001".</summary>
    End,

    /// <summary>Il nome diventa solo il numero: "001".</summary>
    Replace,
}

/// <summary>Numero progressivo nell'ordine scelto, con inizio, passo, cifre e separatore.</summary>
public sealed record NumberingRule : BatchRule
{
    public NumberPosition Position { get; init; } = NumberPosition.End;
    public int Start { get; init; } = 1;
    public int Step { get; init; } = 1;

    /// <summary>Cifre minime con zeri iniziali; 0 = automatico (quante ne servono al numero più alto).</summary>
    public int Digits { get; init; }

    public string Separator { get; init; } = " ";

    /// <summary>Ricomincia da capo in ogni cartella (utile con le sottocartelle).</summary>
    public bool RestartInEachFolder { get; init; }

    public override string Label => "Numerazione";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        var (index, count) = RestartInEachFolder ? (context.FolderIndex, context.FolderCount) : (context.Index, context.Count);
        var number = Start + (long)Step * index;
        var digits = Digits > 0 ? Digits : AutoDigits(count);
        var text = Format(number, digits);
        return Position switch
        {
            NumberPosition.Start => (text + Separator + stem, extension),
            NumberPosition.Replace => (text, extension),
            _ => (stem + Separator + text, extension),
        };
    }

    private int AutoDigits(int count)
    {
        var last = Math.Abs(Start + (long)Step * Math.Max(0, count - 1));
        var first = Math.Abs((long)Start);
        return Math.Max(Math.Max(last, first), 1).ToString(CultureInfo.InvariantCulture).Length;
    }

    private static string Format(long number, int digits) =>
        number < 0
            ? "-" + (-number).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')
            : number.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
}

// ---- Sostituisci testo ------------------------------------------------------------------------------------

public sealed record ReplaceTextRule : BatchRule
{
    public string Find { get; init; } = string.Empty;
    public string Replacement { get; init; } = string.Empty;
    public bool MatchCase { get; init; }

    /// <summary>Espressione regolare .NET; nella sostituzione si possono usare $1, $2…</summary>
    public bool UseRegex { get; init; }

    public override string Label => "Sostituisci testo";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        if (string.IsNullOrEmpty(Find))
        {
            return (stem, extension);
        }
        if (!UseRegex)
        {
            return (stem.Replace(Find, Replacement, MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase), extension);
        }
        try
        {
            var options = RegexOptions.CultureInvariant | (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            return (Regex.Replace(stem, Find, Replacement, options, TimeSpan.FromMilliseconds(250)), extension);
        }
        catch (ArgumentException)
        {
            throw new BatchRuleException("Espressione regolare non valida");
        }
        catch (RegexMatchTimeoutException)
        {
            throw new BatchRuleException("Espressione regolare troppo lenta");
        }
    }

    /// <summary>Messaggio da mostrare sotto la regola mentre la si scrive; null se va bene.</summary>
    public string? Validate()
    {
        if (!UseRegex || string.IsNullOrEmpty(Find))
        {
            return null;
        }
        try
        {
            _ = new Regex(Find, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            return null;
        }
        catch (ArgumentException ex)
        {
            return Strings.Current.Format(nameof(Strings.RegexInvalid), ex.Message);
        }
    }
}

// ---- Inserisci testo --------------------------------------------------------------------------------------

public enum InsertPosition
{
    Start,
    End,

    /// <summary>Dopo un certo numero di caratteri dall'inizio.</summary>
    AfterCharacters,
}

/// <summary>Aggiunge un testo (anche con segnaposto: "{data} ") all'inizio, alla fine o in mezzo al nome.</summary>
public sealed record InsertTextRule : BatchRule
{
    public string Text { get; init; } = string.Empty;
    public InsertPosition Position { get; init; } = InsertPosition.Start;

    /// <summary>Per <see cref="InsertPosition.AfterCharacters"/>: quanti caratteri lasciare prima del testo.</summary>
    public int Index { get; init; }

    public override string Label => "Aggiungi testo";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return (stem, extension);
        }
        var text = BatchTokens.Expand(Text, stem, context);
        return Position switch
        {
            InsertPosition.Start => (text + stem, extension),
            InsertPosition.End => (stem + text, extension),
            _ => (stem.Insert(Math.Clamp(Index, 0, stem.Length), text), extension),
        };
    }
}

// ---- Rimuovi caratteri ------------------------------------------------------------------------------------

public enum RemoveMode
{
    /// <summary>I primi N caratteri.</summary>
    FirstCharacters,

    /// <summary>Gli ultimi N caratteri.</summary>
    LastCharacters,

    /// <summary>N caratteri a partire da una posizione (1 = primo carattere).</summary>
    Range,
}

public sealed record RemoveCharactersRule : BatchRule
{
    public RemoveMode Mode { get; init; } = RemoveMode.FirstCharacters;
    public int Count { get; init; } = 1;

    /// <summary>Per <see cref="RemoveMode.Range"/>: posizione del primo carattere da togliere, da 1.</summary>
    public int From { get; init; } = 1;

    public override string Label => "Rimuovi caratteri";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        var count = Math.Max(0, Count);
        return Mode switch
        {
            RemoveMode.FirstCharacters => (stem[Math.Min(count, stem.Length)..], extension),
            RemoveMode.LastCharacters => (stem[..Math.Max(0, stem.Length - count)], extension),
            _ => RemoveRange(stem, count, extension),
        };
    }

    private (string, string) RemoveRange(string stem, int count, string extension)
    {
        var start = Math.Clamp(From - 1, 0, stem.Length);
        return (stem.Remove(start, Math.Min(count, stem.Length - start)), extension);
    }
}

// ---- Maiuscole e minuscole --------------------------------------------------------------------------------

public enum CaseMode
{
    /// <summary>tutto minuscolo.</summary>
    Lower,

    /// <summary>TUTTO MAIUSCOLO.</summary>
    Upper,

    /// <summary>Ogni Parola Con L'Iniziale Maiuscola.</summary>
    TitleCase,

    /// <summary>Solo la prima lettera maiuscola.</summary>
    SentenceCase,
}

public sealed record ChangeCaseRule : BatchRule
{
    public CaseMode Mode { get; init; } = CaseMode.TitleCase;

    public override string Label => "Maiuscole e minuscole";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context) =>
        (BatchText.ChangeCase(stem, Mode), extension);
}

// ---- Pulizia ----------------------------------------------------------------------------------------------

/// <summary>Le pulizie più comuni dei nomi scaricati: separatori, parentesi, accenti, spazi doppi.</summary>
public sealed record CleanupRule : BatchRule
{
    /// <summary>Punti, trattini bassi e trattini (non quelli circondati da spazi) diventano spazi.</summary>
    public bool SeparatorsToSpaces { get; init; } = true;

    /// <summary>Toglie il testo tra parentesi tonde, quadre e graffe, parentesi comprese.</summary>
    public bool RemoveBracketed { get; init; }

    /// <summary>"Perché" diventa "Perche".</summary>
    public bool RemoveAccents { get; init; }

    /// <summary>Toglie tutte le cifre.</summary>
    public bool RemoveDigits { get; init; }

    public override string Label => "Pulisci nome";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        var result = stem;
        if (RemoveBracketed)
        {
            result = BatchText.Bracketed().Replace(result, " ");
        }
        if (SeparatorsToSpaces)
        {
            result = BatchText.Separators().Replace(result, " ");
        }
        if (RemoveAccents)
        {
            result = BatchText.RemoveDiacritics(result);
        }
        if (RemoveDigits)
        {
            result = BatchText.Digits().Replace(result, string.Empty);
        }
        return (BatchText.CollapseSpaces(result), extension);
    }
}

// ---- Lettere in numeri -----------------------------------------------------------------------------------

/// <summary>
/// Sostituisce le lettere con i numeri che le somigliano ("Bone Lake" ➔ "B0n3 L4k3"), maiuscole e minuscole.
/// Solo sostituzioni che si leggono ancora come lettere; A, E, I, O, S, T sono attive di base,
/// B e G (meno immediate da leggere) si accendono a mano. Il riconoscimento dei titoli fa il contrario.
/// </summary>
public sealed record LettersToDigitsRule : BatchRule
{
    /// <summary>Le sostituzioni offerte, nell'ordine mostrato nella regola.</summary>
    public static IReadOnlyList<(char Letter, char Digit)> Substitutions { get; } =
        [('a', '4'), ('e', '3'), ('i', '1'), ('o', '0'), ('s', '5'), ('t', '7'), ('b', '8'), ('g', '6')];

    public bool A { get; init; } = true;
    public bool E { get; init; } = true;
    public bool I { get; init; } = true;
    public bool O { get; init; } = true;
    public bool S { get; init; } = true;
    public bool T { get; init; } = true;
    public bool B { get; init; }
    public bool G { get; init; }

    public override string Label => "Lettere in numeri";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context)
    {
        bool[] active = [A, E, I, O, S, T, B, G];
        var chars = stem.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var lower = char.ToLowerInvariant(chars[i]);
            for (var k = 0; k < Substitutions.Count; k++)
            {
                if (active[k] && Substitutions[k].Letter == lower)
                {
                    chars[i] = Substitutions[k].Digit;
                    break;
                }
            }
        }
        return (new string(chars), extension);
    }
}

// ---- Estensione -------------------------------------------------------------------------------------------

public enum ExtensionMode
{
    Lower,
    Upper,

    /// <summary>Sostituisce l'estensione (es. ".jpeg" ➔ ".jpg"). Non converte il file.</summary>
    Replace,
}

public sealed record ExtensionRule : BatchRule
{
    public ExtensionMode Mode { get; init; } = ExtensionMode.Lower;

    /// <summary>Per <see cref="ExtensionMode.Replace"/>: la nuova estensione, con o senza punto; vuota = nessuna estensione.</summary>
    public string NewExtension { get; init; } = string.Empty;

    public override string Label => "Estensione";

    public override (string Stem, string Extension) Apply(string stem, string extension, BatchRuleContext context) => Mode switch
    {
        ExtensionMode.Lower => (stem, extension.ToLowerInvariant()),
        ExtensionMode.Upper => (stem, extension.ToUpperInvariant()),
        _ => (stem, NormalizeExtension(NewExtension)),
    };

    private static string NormalizeExtension(string value)
    {
        var trimmed = value.Trim().TrimStart('.');
        return trimmed.Length == 0 ? string.Empty : "." + trimmed;
    }
}

// ---- Utilità di testo condivise ---------------------------------------------------------------------------

internal static partial class BatchText
{
    private static readonly System.Buffers.SearchValues<char> WordPunctuation = System.Buffers.SearchValues.Create(" ._-([");

    [GeneratedRegex(@"\s*(\([^()]*\)|\[[^\[\]]*\]|\{[^{}]*\})\s*", RegexOptions.CultureInvariant)]
    internal static partial Regex Bracketed();

    /// <summary>Punti e trattini bassi sempre; il trattino solo se attaccato alle parole ("Il-Signore" sì, "A - B" no).</summary>
    [GeneratedRegex(@"[._]+|(?<=\S)-(?=\S)", RegexOptions.CultureInvariant)]
    internal static partial Regex Separators();

    [GeneratedRegex(@"\d", RegexOptions.CultureInvariant)]
    internal static partial Regex Digits();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpace();

    internal static string CollapseSpaces(string value) => MultiSpace().Replace(value, " ").Trim();

    internal static string RemoveDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static string ChangeCase(string value, CaseMode mode)
    {
        var culture = CultureInfo.CurrentCulture;
        switch (mode)
        {
            case CaseMode.Lower:
                return value.ToLower(culture);
            case CaseMode.Upper:
                return value.ToUpper(culture);
            case CaseMode.SentenceCase:
            {
                var lower = value.ToLower(culture);
                var first = lower.AsSpan().IndexOfAnyExcept(WordPunctuation);
                return first < 0 ? lower : string.Concat(lower.AsSpan(0, first), char.ToUpper(lower[first], culture).ToString(), lower.AsSpan(first + 1));
            }
            default:
            {
                // Iniziale maiuscola dopo spazio, punto, trattino, parentesi o apostrofo ("L'Ultimo", "Dell'Anno").
                var chars = value.ToLower(culture).ToCharArray();
                var startOfWord = true;
                for (var i = 0; i < chars.Length; i++)
                {
                    if (char.IsLetterOrDigit(chars[i]))
                    {
                        if (startOfWord)
                        {
                            chars[i] = char.ToUpper(chars[i], culture);
                        }
                        startOfWord = false;
                    }
                    else
                    {
                        startOfWord = true;
                    }
                }
                return new string(chars);
            }
        }
    }
}
