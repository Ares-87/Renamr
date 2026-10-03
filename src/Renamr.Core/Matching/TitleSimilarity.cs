using System.Globalization;
using System.Text;

namespace Renamr.Core.Matching;

/// <summary>
/// Similarità tra titoli robusta a maiuscole, accenti, punteggiatura, articoli e "&amp;"/"and".
/// Combina Levenshtein normalizzato (refusi) e Jaccard sui token (ordine diverso, sottotitoli).
/// </summary>
public static class TitleSimilarity
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        { "the", "a", "an", "il", "lo", "la", "i", "gli", "le", "l", "un", "una", "of", "di", "and", "e" };

    public static double Score(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0;
        }

        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0)
        {
            return 0;
        }
        if (na == nb)
        {
            return 1;
        }

        var lev = 1.0 - (double)Levenshtein(na, nb) / Math.Max(na.Length, nb.Length);
        var jac = Jaccard(Tokens(na), Tokens(nb));

        // "Dune Part Two" vs "Dune: Part Two" sono già uguali; per "Alien" vs "Aliens" conta il Levenshtein,
        // per "Lord of the Rings Fellowship" vs "The Lord of the Rings: The Fellowship of the Ring" il Jaccard.
        return Math.Max(lev, (lev + jac * 1.2) / 2.2);
    }

    public static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (ch == '&')
            {
                sb.Append(" and ");
            }
            else if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append(' ');
            }
        }

        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !StopWords.Contains(t));
        return string.Join(' ', tokens);
    }

    private static HashSet<string> Tokens(string normalized) =>
        new(normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }
        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    private static int Levenshtein(string s, string t)
    {
        Span<int> prev = t.Length < 256 ? stackalloc int[t.Length + 1] : new int[t.Length + 1];
        Span<int> curr = t.Length < 256 ? stackalloc int[t.Length + 1] : new int[t.Length + 1];
        for (var j = 0; j <= t.Length; j++)
        {
            prev[j] = j;
        }
        for (var i = 1; i <= s.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            curr.CopyTo(prev);
        }
        return prev[t.Length];
    }
}
