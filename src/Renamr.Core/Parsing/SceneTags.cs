using System.Text.RegularExpressions;

namespace Renamr.Core.Parsing;

/// <summary>
/// Dizionario dei tag "di release" riconosciuti. Ogni regex è delimitata da separatori
/// (spazio . _ - [ ] ( )) così "Hevcland" o "Webster" non vengono mutilati.
/// </summary>
internal static partial class SceneTags
{
    private const string L = @"(?<=^|[\s._\-\[\]()])";
    private const string R = @"(?=$|[\s._\-\[\]()])";
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(L + @"(?<v>2160p|1440p|1080[pi]|720p|576[pi]|480[pi]|4k|uhd)" + R, Opt)]
    public static partial Regex Resolution();

    [GeneratedRegex(L + @"(?<v>blu-?ray|bd-?remux|remux|bdrip|brrip|bd|web-?dl|web-?rip|web|hdtv|pdtv|dvd-?rip|dvd(?:5|9)?|hd-?rip|hdcam|cam|telesync|ts-?rip|amzn|nf|dsnp|hmax|atvp|pcok|hulu|cr)" + R, Opt)]
    public static partial Regex Source();

    [GeneratedRegex(L + @"(?<v>[xh]\.?26[45]|hevc|avc|av1|xvid|divx|vp9|mpeg-?2)" + R, Opt)]
    public static partial Regex VideoCodec();

    [GeneratedRegex(L + @"(?<v>e?-?ac-?3|ddp?\+?(?:[.\s]?[257]\.[01])?|dts(?:-?hd)?(?:[.\-]?ma)?(?:[.\s]?[257]\.[01])?|truehd|atmos|aac(?:[.\s]?[257]\.[01])?|flac|opus|mp3|lpcm|[257]\.[01])" + R, Opt)]
    public static partial Regex AudioCodec();

    [GeneratedRegex(L + @"(?<v>hdr10\+?|hdr10plus|hdr|dolby[.\s-]?vision|dovi|dv|sdr|1[02]-?bit|8-?bit|hi10p?)" + R, Opt)]
    public static partial Regex DynamicRange();

    [GeneratedRegex(L + @"(?:proper|repack|rerip|real|extended|unrated|uncut|remastered|directors?[.\s]?cut|theatrical|imax|internal|limited|multi(?:sub)?|dual[.\s-]?audio|dubbed|subbed|subs?|ita|eng|jpn|fre|ger|spa|complete|hc|3d|sbs|hsbs|ou)" + R, Opt)]
    public static partial Regex Noise();

    /// <summary>Hash CRC32 tipico delle release anime: [1A2B3C4D].</summary>
    /// <summary>
    /// Dove finisce il titolo dell'episodio scritto nel nome ("Silo S03E03 Dark Web 2160p"): solo tag inequivocabili,
    /// perché parole come "web", "real" o "complete" possono far parte del titolo.
    /// </summary>
    [GeneratedRegex(L + @"(?:blu-?ray|bd-?remux|remux|bdrip|brrip|web-?dl|web-?rip|hdtv|pdtv|dvd-?rip|hd-?rip|amzn|dsnp|hmax|atvp|proper|repack|rerip|internal|multi(?:sub)?|dual[.\s-]?audio|ita|eng|jpn|fre|ger|spa|subs?|subbed|dubbed)" + R, Opt)]
    public static partial Regex EpisodeTitleStop();

    [GeneratedRegex(@"\[[0-9A-F]{8}\]", Opt)]
    public static partial Regex Crc32();

    /// <summary>Gruppo di release scene in coda: "...x264-SPARKS".</summary>
    [GeneratedRegex(@"-(?<g>[A-Za-z0-9]{2,20})$", RegexOptions.CultureInvariant)]
    public static partial Regex TrailingGroup();

    /// <summary>Gruppo fansub in testa: "[SubsPlease] ...".</summary>
    [GeneratedRegex(@"^\s*\[(?<g>[^\]]{1,40})\]\s*", RegexOptions.CultureInvariant)]
    public static partial Regex LeadingGroup();

    // --- Episodi -------------------------------------------------------------

    /// <summary>S01E02, s1e2, S01E02E03, S01E02-E03, S01E02-03.</summary>
    [GeneratedRegex(@"(?<=^|[\s._\-\[(])S(?<s>\d{1,2})[\s._]?E(?<e>\d{1,4})(?:(?:[\-_]?E|-)(?<e2>\d{1,4}))*(?=$|[^\d])", Opt)]
    public static partial Regex SeasonEpisode();

    /// <summary>1x02, 01x02.</summary>
    [GeneratedRegex(@"(?<=^|[\s._\-\[(])(?<s>\d{1,2})x(?<e>\d{2,3})(?=$|[^\d])", Opt)]
    public static partial Regex CrossFormat();

    /// <summary>"Season 1 Episode 2" / "Stagione 1 Episodio 2".</summary>
    [GeneratedRegex(@"(?:season|stagione)[\s._]?(?<s>\d{1,2})[\s._\-]*(?:episode|episodio|ep)[\s._]?(?<e>\d{1,4})", Opt)]
    public static partial Regex VerboseEpisode();

    /// <summary>Solo stagione (cartelle o pack): "S02".</summary>
    [GeneratedRegex(@"(?<=^|[\s._\-\[(])S(?<s>\d{1,2})(?=$|[\s._\-\])])", Opt)]
    public static partial Regex SeasonOnly();

    /// <summary>Numerazione assoluta anime: "Title - 05", "Title - 105v2", "Title Ep 12".</summary>
    [GeneratedRegex(@"(?:\s-\s|[\s._](?:ep|e|#)[\s._]?)(?<e>\d{1,4})(?:v\d)?(?=$|[\s._\[(])", Opt)]
    public static partial Regex AbsoluteEpisode();

    // --- Anno e titolo -------------------------------------------------------

    [GeneratedRegex(@"(?<=^|[\s._\-\[(])(?<y>19[2-9]\d|20[0-4]\d)(?=$|[\s._\-\])])", RegexOptions.CultureInvariant)]
    public static partial Regex Year();

    [GeneratedRegex(@"[\[\](){}]+", RegexOptions.CultureInvariant)]
    public static partial Regex Brackets();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    public static partial Regex MultiSpace();

    /// <summary>Musica: "01 - Artista - Titolo" o "01. Titolo".</summary>
    [GeneratedRegex(@"^(?<track>\d{1,3})(?:\s*[-.]\s*|\s+)(?<rest>.+)$", RegexOptions.CultureInvariant)]
    public static partial Regex LeadingTrack();
}
