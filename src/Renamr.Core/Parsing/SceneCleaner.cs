using System.Text.RegularExpressions;
using Renamr.Core.Abstractions;
using Renamr.Core.Models;

namespace Renamr.Core.Parsing;

/// <summary>
/// "Scene Cleaner": estrae titolo pulito, anno, stagione/episodio e informazioni tecniche
/// da nomi come <c>The.Matrix.1999.1080p.BluRay.x264.DTS-HD.MA.5.1-FGT.mkv</c>.
/// <para>
/// Strategia: tutte le informazioni tecniche vengono estratte dall'intero nome, ma il titolo
/// è solo la parte che precede il primo "marcatore" (anno, episodio o primo tag tecnico).
/// Questo è molto più robusto che cancellare i tag uno a uno e sperare che resti il titolo.
/// </para>
/// </summary>
public sealed class SceneCleaner : IFileNameParser
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wma", ".wav", ".aiff", ".ape", ".alac" };

    public ParsedMediaName Parse(string fileNameOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameOrPath);

        var fileName = Path.GetFileName(fileNameOrPath);
        var extension = Path.GetExtension(fileName);
        var name = extension.Length is > 1 and <= 6 ? fileName[..^extension.Length] : fileName;

        if (AudioExtensions.Contains(extension))
        {
            return ParseMusic(fileName, extension, name);
        }

        // 1) Rumore che non porta informazioni: hash CRC delle release anime.
        name = SceneTags.Crc32().Replace(name, " ");

        // 2) Gruppo fansub in testa ("[SubsPlease] Frieren - 05 (1080p).mkv").
        string? group = null;
        var leading = SceneTags.LeadingGroup().Match(name);
        if (leading.Success)
        {
            group = leading.Groups["g"].Value.Trim();
            name = name[leading.Length..];
        }

        // 3) Informazioni tecniche (cercate ovunque nel nome).
        var resolution = FirstValue(SceneTags.Resolution(), name);
        var source = FirstValue(SceneTags.Source(), name);
        var video = FirstValue(SceneTags.VideoCodec(), name);
        var audio = FirstValue(SceneTags.AudioCodec(), name);
        var hdr = FirstValue(SceneTags.DynamicRange(), name);

        // 4) Stagione / episodio.
        var (season, episodes, episodeIndex) = FindEpisode(name);
        int? absolute = null;
        var kind = episodes.Count > 0 ? MediaKind.Episode : MediaKind.Movie;

        if (episodes.Count == 0)
        {
            var abs = SceneTags.AbsoluteEpisode().Match(name);
            // La numerazione assoluta è plausibile solo se non è un anno e c'è un indizio "anime"
            // (gruppo fansub in testa) oppure il formato esplicito " - NN".
            if (abs.Success && !IsYearLike(abs.Groups["e"].Value) && (group is not null || abs.Value.StartsWith(" - ", StringComparison.Ordinal)))
            {
                absolute = Int(abs.Groups["e"].ValueSpan);
                episodeIndex = abs.Index;
                kind = MediaKind.Anime;
            }
        }
        else if (group is not null)
        {
            // "[Group] Show S01E05" è comunque anime: il provider anime ha la precedenza.
            kind = MediaKind.Anime;
        }

        // 5) Anno: l'ultimo anno "plausibile" che non sia all'inizio (gestisce "2001 A Space Odyssey 1968").
        var (year, yearIndex) = FindYear(name);

        // 6) Gruppo scene in coda ("-SPARKS"), solo se c'è almeno un tag tecnico: evita "Spider-Man".
        if (group is null && (resolution ?? source ?? video) is not null)
        {
            var trailing = SceneTags.TrailingGroup().Match(name);
            if (trailing.Success && !IsTechnicalToken(trailing.Groups["g"].Value))
            {
                group = trailing.Groups["g"].Value;
            }
        }

        // 7) Il titolo è ciò che precede il primo marcatore.
        var cut = MinPositive(episodeIndex, yearIndex,
            FirstIndex(SceneTags.Resolution(), name), FirstIndex(SceneTags.Source(), name),
            FirstIndex(SceneTags.VideoCodec(), name), FirstIndex(SceneTags.DynamicRange(), name),
            FirstIndex(SceneTags.Noise(), name, minIndex: 1));

        var rawTitle = cut > 0 ? name[..cut] : name;
        var title = CleanTitle(rawTitle);
        if (title.Length == 0)
        {
            // Nome tutto "tecnico" (es. "1080p.mkv"): meglio un titolo grezzo che nessuno.
            title = CleanTitle(name);
        }

        return new ParsedMediaName
        {
            OriginalFileName = fileName,
            Extension = extension,
            Kind = kind,
            Title = title,
            Year = year,
            Season = season ?? (kind == MediaKind.Anime && absolute is null ? 1 : null),
            Episodes = episodes,
            AbsoluteEpisode = absolute,
            Resolution = NormalizeResolution(resolution),
            Source = source,
            VideoCodec = video,
            AudioCodec = audio,
            DynamicRange = hdr,
            ReleaseGroup = group,
        };
    }

    /// <summary>Pulisce un titolo grezzo: separatori, parentesi vuote, spazi multipli.</summary>
    public static string CleanTitle(string raw)
    {
        // Se non ci sono spazi, puntini e underscore sono separatori di parola.
        var s = raw.Contains(' ', StringComparison.Ordinal) ? raw.Replace('_', ' ') : JoinDottedWords(raw);
        s = SceneTags.Brackets().Replace(s, " ");
        s = SceneTags.MultiSpace().Replace(s, " ");
        return s.Trim(' ', '-', '.', ',', '–', '+');
    }

    /// <summary>
    /// "2001.A.Space.Odyssey" -> "2001 A Space Odyssey", ma "Agents.of.S.H.I.E.L.D" -> "Agents of S.H.I.E.L.D":
    /// il punto resta solo tra due lettere singole (una sigla).
    /// </summary>
    private static string JoinDottedWords(string raw)
    {
        var tokens = raw.Split(['.', '_'], StringSplitOptions.None);
        var sb = new System.Text.StringBuilder(raw.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i > 0)
            {
                var acronym = IsSingleLetter(tokens[i - 1]) && IsSingleLetter(tokens[i]);
                sb.Append(acronym ? '.' : ' ');
            }
            sb.Append(tokens[i]);
        }
        return sb.ToString();

        static bool IsSingleLetter(string t) => t.Length == 1 && char.IsLetter(t[0]);
    }

    private static ParsedMediaName ParseMusic(string fileName, string extension, string name)
    {
        // I file musicali si identificano soprattutto da tag e impronta acustica:
        // dal nome ricaviamo solo indizi ("01 - Artista - Titolo").
        var rest = name;
        int? track = null;
        var m = SceneTags.LeadingTrack().Match(name);
        if (m.Success && !IsYearLike(m.Groups["track"].Value))
        {
            track = Int(m.Groups["track"].ValueSpan);
            rest = m.Groups["rest"].Value;
        }

        return new ParsedMediaName
        {
            OriginalFileName = fileName,
            Extension = extension,
            Kind = MediaKind.Music,
            Title = CleanTitle(rest),
            Episodes = track is null ? [] : [track.Value],
        };
    }

    private static (int? Season, IReadOnlyList<int> Episodes, int Index) FindEpisode(string name)
    {
        var m = SceneTags.SeasonEpisode().Match(name);
        if (m.Success)
        {
            var eps = new List<int> { Int(m.Groups["e"].ValueSpan) };
            foreach (Capture c in m.Groups["e2"].Captures)
            {
                eps.Add(Int(c.ValueSpan));
            }
            // "S01E01-E03" = range: espandiamo per il template multi-episodio.
            if (eps.Count == 2 && eps[1] > eps[0] && eps[1] - eps[0] <= 10)
            {
                eps = Enumerable.Range(eps[0], eps[1] - eps[0] + 1).ToList();
            }
            return (Int(m.Groups["s"].ValueSpan), eps, m.Index);
        }

        m = SceneTags.VerboseEpisode().Match(name);
        if (m.Success)
        {
            return (Int(m.Groups["s"].ValueSpan), [Int(m.Groups["e"].ValueSpan)], m.Index);
        }

        m = SceneTags.CrossFormat().Match(name);
        if (m.Success && !SceneTags.Resolution().IsMatch(m.Value))
        {
            return (Int(m.Groups["s"].ValueSpan), [Int(m.Groups["e"].ValueSpan)], m.Index);
        }

        return (null, [], -1);
    }

    private static (int? Year, int Index) FindYear(string name)
    {
        Match? best = null;
        foreach (Match m in SceneTags.Year().Matches(name))
        {
            if (m.Index == 0)
            {
                continue; // "1917.2019.1080p": il primo numero è il titolo.
            }
            best = m; // l'ultimo vince: "Blade Runner 2049 (2017)" -> 2017, titolo "Blade Runner 2049"
        }

        // Un anno solo all'inizio ("2012.mkv") resta parte del titolo.
        return best is null ? (null, -1) : (Int(best.Groups["y"].ValueSpan), best.Index);
    }

    private static int Int(ReadOnlySpan<char> digits) => int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsYearLike(string digits) =>
        digits.Length == 4 && int.TryParse(digits, out var n) && n is >= 1920 and <= 2049;

    private static bool IsTechnicalToken(string token) =>
        SceneTags.Resolution().IsMatch(token) || SceneTags.Source().IsMatch(token) ||
        SceneTags.VideoCodec().IsMatch(token) || SceneTags.AudioCodec().IsMatch(token) ||
        SceneTags.DynamicRange().IsMatch(token);

    private static string? FirstValue(Regex regex, string input)
    {
        var m = regex.Match(input);
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static int FirstIndex(Regex regex, string input, int minIndex = 0)
    {
        for (var m = regex.Match(input); m.Success; m = m.NextMatch())
        {
            if (m.Index >= minIndex)
            {
                return m.Index;
            }
        }
        return -1;
    }

    private static int MinPositive(params ReadOnlySpan<int> values)
    {
        var min = -1;
        foreach (var v in values)
        {
            if (v > 0 && (min < 0 || v < min))
            {
                min = v;
            }
        }
        return min;
    }

    private static string? NormalizeResolution(string? value) => value?.ToLowerInvariant() switch
    {
        null => null,
        "4k" or "uhd" => "2160p",
        "1080i" => "1080p",
        var v => v,
    };
}
