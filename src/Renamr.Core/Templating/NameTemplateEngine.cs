using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Renamr.Core.Abstractions;
using Renamr.Core.Models;

namespace Renamr.Core.Templating;

/// <summary>
/// Motore di template: <c>{Title} ({Year}) [{Resolution}]</c>, <c>S{Season:00}E{Episode:00}</c>.
/// <list type="bullet">
/// <item>I segnaposto sono case-insensitive e ammettono un formato .NET dopo i due punti.</item>
/// <item>Un segnaposto vuoto fa sparire anche le parentesi/separatori che lo racchiudono:
/// "Film (2020) []" diventa "Film (2020)".</item>
/// <item>Il risultato è sempre un nome file valido per Windows.</item>
/// </list>
/// </summary>
public sealed partial class NameTemplateEngine : INameTemplateEngine
{
    private const int MaxFileNameLength = 200; // margine sotto i 255 per estensione e sottotitoli

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z ]*?)(?::(?<fmt>[^}]+))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>Gruppi rimasti vuoti dopo la sostituzione: "()", "[ ]", "{}".</summary>
    [GeneratedRegex(@"\s*(\(\s*\)|\[\s*\]|\{\s*\})", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyGroups();

    /// <summary>Separatori orfani: "Show -  - Title", " - " finale.</summary>
    [GeneratedRegex(@"\s+-(?:\s+-)+(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DanglingDashes();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpace();

    public string Render(string pattern, MediaMetadata metadata, ParsedMediaName? parsed, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(metadata);

        // "{Show Title}/Season {Season:00}/{Show Title} - S{Season:00}E{Episode:00}" organizza anche in cartelle:
        // ogni segmento è reso e sanificato separatamente, così un valore con "/" (es. "AC/DC") non crea cartelle
        // e nessun segmento può diventare ".." (i punti finali vengono rimossi). Il PathBoundary ricontrolla comunque.
        var segments = pattern.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => RenderSegment(segment, metadata, parsed))
            .ToArray();

        return Path.Combine(segments) + NormalizeExtension(extension);
    }

    private static string RenderSegment(string segment, MediaMetadata metadata, ParsedMediaName? parsed)
    {
        var rendered = Placeholder().Replace(segment, m =>
        {
            var value = Resolve(m.Groups["name"].Value, metadata, parsed);
            var format = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            return Format(value, format);
        });

        rendered = EmptyGroups().Replace(rendered, string.Empty);
        rendered = DanglingDashes().Replace(rendered, " -");
        rendered = MultiSpace().Replace(rendered, " ").Trim().Trim('-').Trim();

        return SanitizeFileName(rendered);
    }

    private static object? Resolve(string name, MediaMetadata md, ParsedMediaName? parsed) =>
        name.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "title" => md.Title,
            "showtitle" or "show" or "series" => md.Title,
            "originaltitle" => md.OriginalTitle ?? md.Title,
            "year" => md.Year,
            "releasedate" or "date" => md.ReleaseDate,
            "season" => md.Season,
            "episode" => md.Episode,
            "absolute" => md.AbsoluteEpisode ?? md.Episode,
            "episodetitle" => md.EpisodeTitle,
            "resolution" => parsed?.Resolution,
            "source" => parsed?.Source,
            "videocodec" or "vc" => parsed?.VideoCodec,
            "audiocodec" or "ac" => parsed?.AudioCodec,
            "hdr" => parsed?.DynamicRange,
            "group" => parsed?.ReleaseGroup,
            "artist" => md.Artist,
            "albumartist" => md.AlbumArtist ?? md.Artist,
            "album" => md.Album,
            "track" => md.TrackNumber,
            "disc" => md.DiscNumber,
            "imdb" => md.ImdbId,
            "provider" => md.Provider,
            "id" => md.ProviderId,
            _ => null, // segnaposto sconosciuto: sparisce invece di finire nel nome
        };

    private static string Format(object? value, string? format) => value switch
    {
        null => string.Empty,
        string s => SanitizeValue(s),
        int i => i.ToString(format ?? "0", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>I valori dai database non possono introdurre separatori di cartella.</summary>
    private static string SanitizeValue(string value) => value.Replace('/', '-').Replace('\\', '-');

    /// <summary>Rende il nome valido su NTFS: caratteri vietati, nomi riservati, punti/spazi finali, lunghezza.</summary>
    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(ch switch
            {
                ':' => " -",             // "Dune: Part Two" -> "Dune - Part Two"
                '/' or '\\' or '|' => '-',
                '?' or '*' => '\0',
                '"' => '\'',
                '<' => '(',
                '>' => ')',
                _ when ch < 32 => '\0',
                _ => ch,
            });
        }

        var result = MultiSpace().Replace(sb.ToString().Replace("\0", string.Empty, StringComparison.Ordinal), " ")
            .Replace(" - -", " -", StringComparison.Ordinal)
            .Trim()
            .TrimEnd('.', ' ');

        if (result.Length > MaxFileNameLength)
        {
            result = result[..MaxFileNameLength].TrimEnd('.', ' ');
        }

        if (result.Length == 0)
        {
            result = "_";
        }

        var stem = result.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            result = "_" + result;
        }

        return result;
    }

    private static string NormalizeExtension(string extension) =>
        string.IsNullOrEmpty(extension) ? string.Empty
        : (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant();
}
