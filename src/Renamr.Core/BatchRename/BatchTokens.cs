using System.Globalization;
using System.Text.RegularExpressions;
using Renamr.Core.Localization;

namespace Renamr.Core.BatchRename;

/// <summary>
/// Segnaposto dei modelli della modalità "Rinomina file" (in italiano, con alcuni sinonimi inglesi):
/// <list type="bullet">
/// <item><c>{nome}</c>: il nome com'è a questo punto delle regole; <c>{originale}</c>: il nome di partenza.</item>
/// <item><c>{n}</c>: numero progressivo da 1 nell'ordine scelto; <c>{n:000}</c> con zeri iniziali.</item>
/// <item><c>{cartella}</c>: la cartella che contiene il file.</item>
/// <item><c>{data}</c>, <c>{ora}</c>: data e ora di modifica; <c>{creazione}</c>: data di creazione.
/// Formato personalizzabile: <c>{data:dd-MM-yyyy}</c>.</item>
/// <item><c>{estensione}</c>: l'estensione originale senza punto.</item>
/// </list>
/// Un segnaposto sconosciuto resta scritto com'è, così l'errore si vede subito nell'anteprima.
/// </summary>
public static partial class BatchTokens
{
    /// <summary>
    /// Elenco per l'aiuto nell'interfaccia, con i nomi dei segnaposto nella lingua in uso
    /// (in italiano {nome}, {cartella}…; nelle altre lingue i sinonimi inglesi {name}, {folder}…, accettati sempre).
    /// </summary>
    public static IReadOnlyList<(string Token, string Description)> Help
    {
        get
        {
            var s = Strings.Current;
            return
            [
                ($"{{{s.TokenName}}}", s.TokenNameHelp),
                ($"{{{s.TokenOriginal}}}", s.TokenOriginalHelp),
                ("{n}", s.TokenNumberHelp),
                ($"{{{s.TokenFolder}}}", s.TokenFolderHelp),
                ($"{{{s.TokenDate}}}", s.TokenDateHelp),
                ($"{{{s.TokenTime}}}", s.TokenTimeHelp),
                ($"{{{s.TokenCreated}}}", s.TokenCreatedHelp),
                ($"{{{s.TokenExtension}}}", s.TokenExtensionHelp),
            ];
        }
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-zàèéìòù]+)(?::(?<fmt>[^}]+))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    public static string Expand(string pattern, string currentStem, BatchRuleContext context)
    {
        if (string.IsNullOrEmpty(pattern) || !pattern.Contains('{', StringComparison.Ordinal))
        {
            return pattern;
        }
        var file = context.File;
        return Token().Replace(pattern, m =>
        {
            var format = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            return m.Groups["name"].Value.ToLowerInvariant() switch
            {
                "nome" or "name" => currentStem,
                "originale" or "original" => file.Stem,
                "n" or "num" or "numero" => (context.Index + 1).ToString(format ?? "0", CultureInfo.InvariantCulture),
                "cartella" or "folder" => file.FolderName,
                "data" or "date" => Date(file.ModifiedUtc, format ?? "yyyy-MM-dd"),
                "ora" or "time" => Date(file.ModifiedUtc, format ?? "HH.mm.ss"),
                "creazione" or "created" => Date(file.CreatedUtc, format ?? "yyyy-MM-dd"),
                "estensione" or "ext" => file.Extension.TrimStart('.'),
                _ => m.Value,
            };
        });
    }

    private static string Date(DateTime utc, string format)
    {
        try
        {
            // I due punti non sono ammessi nei nomi di file Windows: "HH:mm" diventa "HH.mm".
            return utc.ToLocalTime().ToString(format, CultureInfo.CurrentCulture).Replace(':', '.');
        }
        catch (FormatException)
        {
            throw new BatchRuleException(Strings.Current.Format(nameof(Strings.DateFormatInvalid), format));
        }
    }
}
