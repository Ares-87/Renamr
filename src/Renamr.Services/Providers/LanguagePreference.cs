using System.Globalization;

namespace Renamr.Services.Providers;

/// <summary>
/// La lingua scelta nelle impostazioni ("it-IT") nei formati che ogni servizio si aspetta:
/// TMDb vuole "it-IT", TheTVDB "ita" (ISO 639-2), AniDB "it" (ISO 639-1), TVmaze il paese "IT" per gli AKA.
/// </summary>
public sealed record LanguagePreference(string Tag, string TwoLetter, string ThreeLetter, string? Country)
{
    public bool IsEnglish => TwoLetter == "en";

    public static LanguagePreference From(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            tag = "en-US";
        }
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            var country = tag.Contains('-', StringComparison.Ordinal) ? tag[(tag.IndexOf('-', StringComparison.Ordinal) + 1)..].ToUpperInvariant() : null;
            return new LanguagePreference(tag, culture.TwoLetterISOLanguageName, culture.ThreeLetterISOLanguageName, country);
        }
        catch (CultureNotFoundException)
        {
            return new LanguagePreference("en-US", "en", "eng", "US");
        }
    }
}
