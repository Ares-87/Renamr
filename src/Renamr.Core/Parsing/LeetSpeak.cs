using System.Text;
using System.Text.RegularExpressions;

namespace Renamr.Core.Parsing;

/// <summary>
/// Titoli "offuscati" con numeri al posto delle lettere ("B0N3.L4K3" = "Bone Lake", 3 = e, 4 = a, 0 = o…).
/// <para>
/// Si decodificano solo le parole che mescolano lettere e numeri: un titolo fatto di numeri veri
/// ("2012", "300", "Ocean's 11") resta com'è. Il titolo decodificato è solo una ricerca in più:
/// l'originale viene cercato per primo e vince il risultato con il punteggio migliore,
/// così "Se7en" o "Fast5" continuano a funzionare.
/// </para>
/// </summary>
public static partial class LeetSpeak
{
    private static readonly HashSet<string> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
        { "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

    /// <summary>
    /// Titoli alternativi da cercare, dal più fedele al più libero; vuoto se il titolo non sembra offuscato.
    /// <list type="bullet">
    /// <item>la decodifica intera ("B0N3 L4K3 W 24" ➔ "Bone Lake W 24");</item>
    /// <item>lo stesso con 1 = l invece di 1 = i, se il titolo contiene un 1 da decodificare;</item>
    /// <item>la decodifica senza i pezzetti finali che non sono parole ("W 24" ➔ "Bone Lake").</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> Alternatives(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }
        var tokens = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var decodable = tokens.Select(IsObfuscated).ToArray();
        if (!decodable.Any(d => d))
        {
            return [];
        }

        var result = new List<string>();
        var oneAsI = Decode(tokens, decodable, oneAs: 'i');
        Add(result, string.Join(' ', oneAsI));
        var oneAsL = Decode(tokens, decodable, oneAs: 'l');
        Add(result, string.Join(' ', oneAsL));

        // Coda di sigle e numeretti dopo l'ultima parola decodificata ("W 24"): spesso è rumore
        // aggiunto per offuscare. Si toglie solo se contiene una lettera singola che non è un numero
        // romano, così "R0cky 2" o "R0cky II" restano interi e non diventano il primo "Rocky".
        var last = Array.LastIndexOf(decodable, true);
        var tail = tokens[(last + 1)..];
        if (tail.Length > 0 && tail.All(t => t.Length <= 2) &&
            tail.Any(t => t.Length == 1 && char.IsLetter(t[0]) && !RomanNumerals.Contains(t)))
        {
            Add(result, string.Join(' ', oneAsI[..(last + 1)]));
            Add(result, string.Join(' ', oneAsL[..(last + 1)]));
        }

        return result;

        void Add(List<string> list, string value)
        {
            if (!string.Equals(value, title, StringComparison.OrdinalIgnoreCase) &&
                !list.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(value);
            }
        }
    }

    /// <summary>La decodifica più probabile, oppure il titolo stesso se non è offuscato.</summary>
    public static string Decode(string title) => Alternatives(title) is [var first, ..] ? first : title;

    /// <summary>
    /// Una parola è offuscata se ha almeno due lettere e una cifra (o @, $) che, decodificate,
    /// lasciano solo lettere: "B0N3", "L4K3", "Se7en" sì; "4K", "3D", "x264", "2nd", "1080p" no.
    /// </summary>
    private static bool IsObfuscated(string token)
    {
        var letters = 0;
        var substitutes = 0;
        foreach (var ch in token)
        {
            if (char.IsLetter(ch))
            {
                letters++;
            }
            else if (Map(ch, 'i') is not null)
            {
                substitutes++;
            }
            else if (ch is not ('\'' or '-' or ':' or ',' or '!' or '?'))
            {
                return false;
            }
        }
        if (letters < 2 || substitutes == 0)
        {
            return false;
        }
        // Ordinali e decenni: "2nd", "21st", "3rd", "4th", "90s".
        return !Ordinal().IsMatch(token);
    }

    [GeneratedRegex(@"^\d+(st|nd|rd|th|s)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Ordinal();

    private static string[] Decode(string[] tokens, bool[] decodable, char oneAs)
    {
        var output = new string[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            output[i] = decodable[i] ? DecodeWord(tokens[i], oneAs) : tokens[i];
        }
        return output;
    }

    /// <summary>"B0N3" ➔ "Bone": le parole tutte maiuscole tornano con l'iniziale maiuscola, le altre tengono le maiuscole.</summary>
    private static string DecodeWord(string token, char oneAs)
    {
        var allUpper = token.Where(char.IsLetter).All(char.IsUpper);
        var sb = new StringBuilder(token.Length);
        foreach (var ch in token)
        {
            sb.Append(Map(ch, oneAs) ?? ch);
        }
        var word = sb.ToString();
        if (allUpper)
        {
            var lower = word.ToLowerInvariant().ToCharArray();
            var first = Array.FindIndex(lower, char.IsLetter);
            lower[first] = char.ToUpperInvariant(lower[first]);
            return new string(lower);
        }
        // Lettere decodificate in minuscolo dentro una parola mista ("Se7en" ➔ "Seven").
        return word;
    }

    private static char? Map(char ch, char oneAs) => ch switch
    {
        '0' => 'o',
        '1' => oneAs,
        '2' => 'z',
        '3' => 'e',
        '4' => 'a',
        '5' => 's',
        '6' => 'g',
        '7' => 't',
        '8' => 'b',
        '9' => 'g',
        '@' => 'a',
        '$' => 's',
        _ => null,
    };
}
