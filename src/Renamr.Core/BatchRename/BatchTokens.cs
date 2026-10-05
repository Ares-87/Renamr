using System.Globalization;
using System.Text.RegularExpressions;
using Renamr.Core.Localization;

namespace Renamr.Core.BatchRename;

/// <summary>
/// Segnaposto dei modelli della modalità "Rinomina file" (in italiano, con alcuni sinonimi inglesi):
/// <list type="bullet">
/// <item><c>{nome}</c>: il nome com'è a questo punto delle regole; <c>{originale}</c>: il nome di partenza.</item>
/// <item><c>{n}</c>: numero progressivo da 1 nell'ordine scelto; <c>{n:000}</c> con zeri iniziali.</item>
/// <item><c>{cartella}</c>: la cartella che contiene il file; <c>{cartella:2}</c> quella sopra.</item>
/// <item><c>{data}</c>, <c>{ora}</c>: data e ora di modifica; <c>{creazione}</c>: data di creazione.
/// Formato personalizzabile: <c>{data:dd-MM-yyyy}</c>.</item>
/// <item><c>{estensione}</c>: l'estensione originale senza punto.</item>
/// <item><c>{parola:2}</c>: la seconda parola del nome (-1 = l'ultima); <c>{parte:1:4}</c>: 4 caratteri dal primo.</item>
/// <item><c>{dimensione}</c>: "1.5 MB"; <c>{casuale}</c>: 4 cifre a caso, sempre le stesse per lo stesso file.</item>
/// <item>Dal contenuto, solo letti: <c>{scatto}</c>, <c>{fotocamera}</c>, <c>{risoluzione}</c>, <c>{larghezza}</c>,
/// <c>{altezza}</c>, <c>{durata}</c>, <c>{artista}</c>, <c>{album}</c>, <c>{titolo}</c>, <c>{traccia}</c>, <c>{anno}</c>.</item>
/// </list>
/// Un segnaposto sconosciuto resta scritto com'è, così l'errore si vede subito nell'anteprima;
/// un dato che il file non ha (una foto senza EXIF) diventa vuoto.
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

    /// <summary>Gli altri segnaposto, mostrati a richiesta: parti del nome, dimensione, e i dati letti da foto, musica e video.</summary>
    public static IReadOnlyList<(string Token, string Description)> MoreHelp
    {
        get
        {
            var s = Strings.Current;
            return
            [
                ($"{{{s.TokenWord}:2}}", s.TokenWordHelp),
                ($"{{{s.TokenPart}:1:4}}", s.TokenPartHelp),
                ($"{{{s.TokenFolder}:2}}", s.TokenParentFolderHelp),
                ($"{{{s.TokenSize}}}", s.TokenSizeHelp),
                ($"{{{s.TokenRandom}}}", s.TokenRandomHelp),
                ($"{{{s.TokenTaken}}}", s.TokenTakenHelp),
                ($"{{{s.TokenCamera}}}", s.TokenCameraHelp),
                ($"{{{s.TokenResolution}}}", s.TokenResolutionHelp),
                ($"{{{s.TokenDuration}}}", s.TokenDurationHelp),
                ($"{{{s.TokenArtist}}}", s.TokenArtistHelp),
                ($"{{{s.TokenAlbum}}}", s.TokenAlbumHelp),
                ($"{{{s.TokenTitle}}}", s.TokenTitleHelp),
                ($"{{{s.TokenTrack}}}", s.TokenTrackHelp),
                ($"{{{s.TokenYear}}}", s.TokenYearHelp),
            ];
        }
    }

    /// <summary>Segnaposto che leggono il contenuto del file: solo se una regola li usa la cartella viene letta più a fondo.</summary>
    private static readonly HashSet<string> DetailTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "scatto", "taken", "fotocamera", "camera", "risoluzione", "resolution", "larghezza", "width", "altezza", "height",
        "durata", "duration", "artista", "artist", "album", "titolo", "title", "traccia", "track", "anno", "year",
    };

    [GeneratedRegex(@"\{(?<name>[A-Za-zàèéìòù]+)(?::(?<fmt>[^}]+))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"[^\s._\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    /// <summary>True se almeno una regola attiva usa un segnaposto che richiede di leggere il contenuto dei file.</summary>
    public static bool NeedsDetails(IEnumerable<BatchRule> rules) =>
        rules.Where(r => r.Enabled).Select(TextOf).Any(text =>
            !string.IsNullOrEmpty(text) && Token().Matches(text).Any(m => DetailTokens.Contains(m.Groups["name"].Value)));

    private static string? TextOf(BatchRule rule) => rule switch
    {
        NewNameRule r => r.Pattern,
        InsertTextRule r => r.Text,
        NameListRule r => r.Names,
        _ => null,
    };

    public static string Expand(string pattern, string currentStem, BatchRuleContext context)
    {
        if (string.IsNullOrEmpty(pattern) || !pattern.Contains('{', StringComparison.Ordinal))
        {
            return pattern;
        }
        var file = context.File;
        var details = file.Details ?? FileDetails.None;
        return Token().Replace(pattern, m =>
        {
            var format = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            return m.Groups["name"].Value.ToLowerInvariant() switch
            {
                "nome" or "name" => currentStem,
                "originale" or "original" => file.Stem,
                "n" or "num" or "numero" => (context.Index + 1).ToString(format ?? "0", CultureInfo.InvariantCulture),
                "cartella" or "folder" => Folder(file, format),
                "data" or "date" => Date(file.ModifiedUtc, format ?? "yyyy-MM-dd"),
                "ora" or "time" => Date(file.ModifiedUtc, format ?? "HH.mm.ss"),
                "creazione" or "created" => Date(file.CreatedUtc, format ?? "yyyy-MM-dd"),
                "estensione" or "ext" => file.Extension.TrimStart('.'),
                "parola" or "word" => WordOf(currentStem, format),
                "parte" or "part" => PartOf(currentStem, format),
                "dimensione" or "size" => Size(file.Size),
                "casuale" or "random" => Random(file.Path, format),
                "scatto" or "taken" => details.DateTaken is { } taken ? Format(taken, format ?? "yyyy-MM-dd") : string.Empty,
                "fotocamera" or "camera" => details.Camera ?? string.Empty,
                "risoluzione" or "resolution" => details is { Width: { } w, Height: { } h } ? $"{w}x{h}" : string.Empty,
                "larghezza" or "width" => Number(details.Width, format),
                "altezza" or "height" => Number(details.Height, format),
                "durata" or "duration" => details.Duration is { } d ? Duration(d) : string.Empty,
                "artista" or "artist" => details.Artist ?? string.Empty,
                "album" => details.Album ?? string.Empty,
                "titolo" or "title" => details.Title ?? string.Empty,
                "traccia" or "track" => Number((int?)details.Track, format ?? "00"),
                "anno" or "year" => Number((int?)details.Year, format),
                _ => m.Value,
            };
        });
    }

    /// <summary>{cartella} = la cartella del file; {cartella:2} = quella che la contiene, e così via.</summary>
    private static string Folder(BatchFile file, string? format)
    {
        var level = int.TryParse(format, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? Math.Max(1, l) : 1;
        var folder = Path.TrimEndingDirectorySeparator(file.Folder);
        for (var i = 1; i < level && !string.IsNullOrEmpty(folder); i++)
        {
            folder = Path.GetDirectoryName(folder);
        }
        return string.IsNullOrEmpty(folder) ? string.Empty : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
    }

    /// <summary>{parola:2} = la seconda parola; {parola:-1} = l'ultima. Separano le parole spazi, punti, trattini e trattini bassi.</summary>
    private static string WordOf(string stem, string? format)
    {
        var words = Word().Matches(stem);
        var n = int.TryParse(format, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 1;
        var index = n < 0 ? words.Count + n : n - 1;
        return index >= 0 && index < words.Count ? words[index].Value : string.Empty;
    }

    /// <summary>{parte:3:4} = 4 caratteri dal terzo; {parte:3} = dal terzo alla fine; {parte:-4} = gli ultimi 4.</summary>
    private static string PartOf(string stem, string? format)
    {
        var bits = (format ?? "1").Split(':');
        if (!int.TryParse(bits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from) || from == 0)
        {
            from = 1;
        }
        var start = Math.Clamp(from < 0 ? stem.Length + from : from - 1, 0, stem.Length);
        var count = bits.Length > 1 && int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? Math.Max(0, c) : stem.Length;
        return stem.Substring(start, Math.Min(count, stem.Length - start));
    }

    /// <summary>"512 B", "34 KB", "1.5 MB", "4.2 GB" (sempre col punto: la virgola sembrerebbe un elenco).</summary>
    private static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var text = unit <= 1 ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{text} {units[unit]}";
    }

    /// <summary>Cifre "a caso" ma stabili: calcolate dal percorso, così l'anteprima non cambia a ogni tasto.</summary>
    private static string Random(string path, string? format)
    {
        var digits = int.TryParse(format, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? Math.Clamp(d, 1, 12) : 4;
        ulong hash = 14695981039346656037; // FNV-1a
        foreach (var ch in path)
        {
            hash = (hash ^ ch) * 1099511628211;
        }
        var modulo = (ulong)Math.Pow(10, digits);
        return (hash % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    private static string Number(int? value, string? format) =>
        value is { } v ? v.ToString(format ?? "0", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>"3.45" o "1.02.03": i due punti non sono ammessi nei nomi.</summary>
    private static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\.mm\.ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\.ss", CultureInfo.InvariantCulture);

    private static string Date(DateTime utc, string format) => Format(utc.ToLocalTime(), format);

    private static string Format(DateTime value, string format)
    {
        try
        {
            // I due punti non sono ammessi nei nomi di file Windows: "HH:mm" diventa "HH.mm".
            return value.ToString(format, CultureInfo.CurrentCulture).Replace(':', '.');
        }
        catch (FormatException)
        {
            throw new BatchRuleException(Strings.Current.Format(nameof(Strings.DateFormatInvalid), format));
        }
    }
}
