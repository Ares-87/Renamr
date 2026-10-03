using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Renamr.Core.Localization;

/// <summary>
/// Testi dell'interfaccia nelle lingue supportate (file <c>Localization/*.json</c> incorporati nell'assembly).
/// <see cref="Current"/> è l'istanza usata da tutta l'app: all'avvio prende la lingua scelta nelle impostazioni
/// o quella del sistema; se manca un testo si usa l'inglese, poi l'italiano (la lingua di partenza).
/// </summary>
public sealed partial class Strings : INotifyPropertyChanged
{
    /// <summary>Lingue dell'interfaccia: codice ISO a due lettere e nome nella lingua stessa.</summary>
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["it", "en", "es", "fr", "de", "pt"];

    /// <summary>Lingua usata quando quella del sistema non è tra quelle supportate.</summary>
    public const string FallbackLanguage = "en";

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = new(StringComparer.Ordinal);
    private static readonly Lock TablesGate = new();

    private IReadOnlyDictionary<string, string> _table;

    public Strings(string language = "it")
    {
        Language = Normalize(language);
        _table = Table(Language);
    }

    /// <summary>L'istanza di tutta l'app. Parte in italiano finché l'app non sceglie la lingua (i test restano in italiano).</summary>
    public static Strings Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Codice a due lettere della lingua in uso ("it", "en"…).</summary>
    public string Language { get; private set; }

    public string this[string key] =>
        _table.TryGetValue(key, out var text) || Table(FallbackLanguage).TryGetValue(key, out text) || Table("it").TryGetValue(key, out text)
            ? text
            : key;

    /// <summary>Testo con segnaposto {0}, {1}… riempiti nella cultura corrente.</summary>
    public string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, this[key], args);

    /// <summary>Cambia lingua: chi è legato alle proprietà riceve un unico PropertyChanged per tutte.</summary>
    public void SetLanguage(string language)
    {
        var code = Normalize(language);
        if (code == Language)
        {
            return;
        }
        Language = code;
        _table = Table(code);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>
    /// Lingua da usare: quella scelta (<paramref name="preference"/>) se supportata, altrimenti quella del sistema
    /// (<paramref name="system"/>), altrimenti l'inglese.
    /// </summary>
    public static string Resolve(string? preference, CultureInfo system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (IsSupported(preference))
        {
            return Normalize(preference!);
        }
        for (var culture = system; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            if (IsSupported(culture.TwoLetterISOLanguageName))
            {
                return culture.TwoLetterISOLanguageName;
            }
        }
        return FallbackLanguage;
    }

    public static bool IsSupported(string? language) =>
        !string.IsNullOrWhiteSpace(language) && SupportedLanguages.Contains(Normalize(language));

    /// <summary>Nome di una lingua nella lingua stessa ("Deutsch"), per il selettore delle impostazioni.</summary>
    public static string NativeName(string language) => Table(Normalize(language)).TryGetValue("LanguageName", out var name) ? name : language;

    /// <summary>Tutte le chiavi di una lingua (per i test).</summary>
    public static IReadOnlyCollection<string> KeysOf(string language) => [.. Table(Normalize(language)).Keys];

    private string Get([CallerMemberName] string key = "") => this[key];

    private static string Normalize(string language)
    {
        var code = language.Trim();
        var dash = code.IndexOfAny(['-', '_']);
        return (dash > 0 ? code[..dash] : code).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string> Table(string language)
    {
        lock (TablesGate)
        {
            if (!Tables.TryGetValue(language, out var table))
            {
                table = Load(language);
                Tables[language] = table;
            }
            return table;
        }
    }

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream($"Renamr.Core.Localization.{language}.json");
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
